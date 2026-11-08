using System.Diagnostics;
using System.Net.Sockets;

namespace TeamSpeakManager.Core.Services;

/// <summary>
/// Minimal seam over a running OS process. Lets the service logic be tested against
/// mock executables instead of a real TeamSpeak binary.
/// </summary>
public interface IProcessHandle
{
    int Id { get; }
    bool HasExited { get; }
    string ExecutablePath { get; }
    string WorkingDirectory { get; }
    event EventHandler? Exited;

    /// <summary>Wait for exit. Returns false on timeout.</summary>
    bool WaitForExit(TimeSpan timeout);

    void Kill();
}

public interface IProcessHost
{
    /// <summary>Enumerate candidate processes that could be the TeamSpeak server.</summary>
    IReadOnlyList<IProcessHandle> GetProcessesByExecutablePath(string executablePath);

    /// <summary>Launch with working directory set to the server directory.</summary>
    IProcessHandle Start(string executablePath, string workingDirectory, IReadOnlyList<string>? arguments = null);

    /// <summary>
    /// Ask a console-attached process to exit the way Ctrl+C does. Returns false when the
    /// mechanism is unavailable (non-console process, access denied).
    /// </summary>
    bool RequestGracefulExit(IProcessHandle process);
}/// <summary>System.Diagnostics implementation used in production.</summary>
public sealed class SystemProcessHost : IProcessHost
{
    public IReadOnlyList<IProcessHandle> GetProcessesByExecutablePath(string executablePath)
    {
        var normalized = Path.GetFullPath(executablePath);
        var results = new List<IProcessHandle>();
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 0 || !seen.Add(p.Id)) continue;
                // Must match the exact binary, not just the process name. Never touch
                // an unrelated ts3server.exe from a different install.
                if (!string.Equals(p.MainModule?.FileName, normalized, StringComparison.OrdinalIgnoreCase))
                    continue;
                results.Add(new SystemProcessHandle(p));
            }
            catch (InvalidOperationException)
            {
                // Process exited between enumeration and access.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access denied (elevated or protected process). Skip, do not crash.
            }
            catch (NotSupportedException) { }
        }

        return results;
    }

    public IProcessHandle Start(string executablePath, string workingDirectory, IReadOnlyList<string>? arguments = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,               // must inherit a real cwd, not shell defaults
            CreateNoWindow = false,                // keeps the console so Ctrl+C-style stop works
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };
        // ArgumentList quotes each argument independently; no string concatenation, so a
        // value with spaces or quotes can never become an extra argument.
        if (arguments is not null)
            foreach (var a in arguments) psi.ArgumentList.Add(a);

        psi.Environment["TS3_SERVER_MANAGED_BY"] = AppIdentifier; // provenance marker for the child

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch server process.");
        return new SystemProcessHandle(proc);
    }

    public const string AppIdentifier = "teamspeak-server-manager";

    public bool RequestGracefulExit(IProcessHandle process)
    {
        if (process.HasExited) return true;
        return ConsoleCtrlRequest.Send(process.Id);
    }
}

internal sealed class SystemProcessHandle : IProcessHandle
{
    private readonly Process _proc;

    public SystemProcessHandle(Process proc)
    {
        _proc = proc;
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public int Id => _proc.Id;
    public bool HasExited => _proc.HasExited;

    /// <summary>
    /// MainModule throws Win32Exception for processes we cannot query (access denied, or the
    /// process already exited). Fall back to the path we launched with, which is authoritative
    /// for processes this app owns.
    /// </summary>
    public string ExecutablePath
    {
        get
        {
            try
            {
                var name = _proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception)
            {
                // Win32Exception / InvalidOperationException: not queryable.
            }
            return _proc.StartInfo.FileName;
        }
    }

    public string WorkingDirectory => _proc.StartInfo.WorkingDirectory ?? "";

    public event EventHandler? Exited;

    public bool WaitForExit(TimeSpan timeout) => _proc.WaitForExit((int)timeout.TotalMilliseconds);

    public void Kill()
    {
        if (!_proc.HasExited) _proc.Kill(entireProcessTree: false); // scoped: only this process
    }
}

/// <summary>
/// Sends a console control event to the process group of a process we own.
/// ponytail: covers the single-console case only. Processes in a service session or with no
/// console need Job-Object/TerminateProcess; upgrade path is a Job Object kill-on-close handle.
/// </summary>
internal static class ConsoleCtrlRequest
{
    public const uint CtrlCEvent = 0;

    // Detach our own console so attaching to the target's console is unambiguous.
    // 0 means "no console".
    private const uint AttachParentProcess = unchecked((uint)-1);

    public static bool Send(int processId)
    {
        if (processId <= 0) return false;

        // Remember our own console so we can restore it; otherwise simply probing a
        // process would silently detach TeamSer from the terminal it was launched from.
        var ourConsole = NativeMethods.GetConsoleWindow();

        NativeMethods.FreeConsole();
        if (!NativeMethods.AttachConsole((uint)processId))
        {
            Restore(ourConsole);
            return false;
        }

        try
        {
            // Returning true marks the event as handled, so the echo does not end TeamSer.
            NativeMethods.SetConsoleCtrlHandler(NativeMethods.IgnoreHandler, true);
            try
            {
                // Group 0 = every process attached to this console, which is exactly the
                // server we just attached to (it owns this console).
                return NativeMethods.GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            }
            finally
            {
                NativeMethods.SetConsoleCtrlHandler(NativeMethods.IgnoreHandler, false);
            }
        }
        finally
        {
            NativeMethods.FreeConsole();
            Restore(ourConsole);
        }
    }

    private static void Restore(nint ourConsole)
    {
        // GetConsoleWindow is a best-effort indicator of whether we had one.
        if (ourConsole == nint.Zero) return;
        if (NativeMethods.AttachConsole(AttachParentProcess)) return;
        _ = NativeMethods.AttachConsole((uint)Environment.ProcessId);
    }
}

internal static partial class NativeMethods
{
    public delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate HandlerRoutine, bool Add);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GetConsoleWindow();

    // Returning true marks the event as handled = ignored. Must not be GC'd.
    public static readonly ConsoleCtrlDelegate IgnoreHandler = _ => true;
}
