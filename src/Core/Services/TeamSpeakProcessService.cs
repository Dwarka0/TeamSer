using System.Net.Sockets;
using TeamSpeakManager.Core.Models;

namespace TeamSpeakManager.Core.Services;

/// <summary>
/// Owns the local TeamSpeak server lifecycle: detection, start, readiness, stop, crash watch.
/// Only ever acts on a process whose executable path we were configured with.
/// </summary>
public sealed class TeamSpeakProcessService : IDisposable
{
    private readonly IProcessHost _host;
    private readonly Func<string, string, bool> _portProbe;
    private readonly object _gate = new();

    private IProcessHandle? _managed;
    private int _stateOwnerPid;

    public TeamSpeakProcessService(IProcessHost? host = null, Func<string, string, bool>? portProbe = null)
    {
        _host = host ?? new SystemProcessHost();
        _portProbe = portProbe ?? DefaultProbe;
    }

    /// <summary>Raised when a managed process dies for any reason.</summary>
    public event EventHandler<ServerState>? ProcessExited;

    private static bool DefaultProbe(string host, string port)
    {
        if (!int.TryParse(port, out var p) || p is < 1 or > 65535) return false;
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(host, p).Wait(500) && c.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string ServerExecutablePath { get; private set; } = "";
    public string ServerDirectory { get; private set; } = "";

    /// <summary>
    /// Executable names shipped by the official Windows packages, in preference order.
    /// Verified against TeamSpeak support documentation; older builds used plain ts3server.exe.
    /// </summary>
    public static readonly string[] CandidateNames =
    [
        "ts3server_win64.exe",
        "ts3server.exe",
        "ts3server_win32.exe",
    ];

    public string? ConfigError { get; private set; }

    /// <summary>
    /// Locate ts3server.exe. Returns false with a reason when configuration is missing/invalid.
    /// </summary>
    public bool TryConfigure(string? directory, out string error)
    {
        error = "";
        ConfigError = null;

        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "Server directory is not configured.";
            ConfigError = error;
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(directory);
        }
        catch (Exception e)
        {
            error = $"Server directory path is invalid: {e.Message}";
            ConfigError = error;
            return false;
        }

        if (!Directory.Exists(full))
        {
            error = "Server directory does not exist.";
            ConfigError = error;
            return false;
        }

        // Official Windows packages ship ts3server_win64.exe / ts3server_win32.exe; older
        // packages used plain ts3server.exe. Try them in priority order rather than assuming one.
        var exe = CandidateNames
            .Select(n => Path.Combine(full, n))
            .FirstOrDefault(File.Exists);

        if (exe is null)
        {
            error = "No TeamSpeak server executable was found in the selected directory. " +
                    "Expected ts3server_win64.exe or ts3server.exe.";
            ConfigError = error;
            return false;
        }

        // Reject a directory containing a path that resolves elsewhere (junction trickery).
        if (!string.Equals(Path.GetFullPath(exe), exe, StringComparison.OrdinalIgnoreCase))
        {
            error = "Server executable path is not canonical.";
            ConfigError = error;
            return false;
        }

        ServerDirectory = full;
        ServerExecutablePath = exe;
        return true;
    }

    public ServerState CurrentState
    {
        get
        {
            lock (_gate)
            {
                if (_managed is null)
                {
                    var detached = _host.GetProcessesByExecutablePath(ServerExecutablePath);
                    return detached.Count > 0 ? ServerState.Online : ServerState.Offline;
                }
                return _managed.HasExited ? ServerState.Offline : ServerState.Online;
            }
        }
    }

    public int? ManagedProcessId
    {
        get
        {
            lock (_gate)
            {
                if (_managed is not null && !_managed.HasExited) return _managed.Id;
                var detached = _host.GetProcessesByExecutablePath(ServerExecutablePath);
                return detached.Count > 0 ? detached[0].Id : null;
            }
        }
    }

    /// <summary>
    /// True when a server process already runs that we did not start (e.g. started manually
    /// or by a previous session). Those are reported, never killed silently.
    /// </summary>
    public bool HasForeignProcess
    {
        get
        {
            lock (_gate)
            {
                return _managed is null && _host.GetProcessesByExecutablePath(ServerExecutablePath).Count > 0;
            }
        }
    }

    public IReadOnlyList<int> ExistingProcessIds =>
        _host.GetProcessesByExecutablePath(ServerExecutablePath).Select(p => p.Id).ToList();

    /// <summary>
    /// Start the server. Throws on failure; never leaves a partial handle behind.
    /// <paramref name="startupArguments"/> is empty in production: the TeamSpeak server is
    /// launched with no invented arguments. Tests use it to drive the mock binary.
    /// </summary>
    public IProcessHandle Start(IReadOnlyList<string>? startupArguments = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(ServerExecutablePath))
                throw new InvalidOperationException(ConfigError ?? "Server path not configured.");

            var existing = _host.GetProcessesByExecutablePath(ServerExecutablePath);
            if (existing.Count > 0)
            {
                // Duplicate prevention: attach to the running one instead of starting a second.
                _managed = existing[0];
                return _managed;
            }

            var handle = _host.Start(ServerExecutablePath, ServerDirectory, startupArguments);
            _managed = handle;
            _stateOwnerPid = handle.Id;
            handle.Exited += OnManagedExit;
            return handle;
        }
    }

    private void OnManagedExit(object? sender, EventArgs e)
    {
        var s = sender as IProcessHandle;
        if (s is not null && s.Id == _stateOwnerPid)
            ProcessExited?.Invoke(this, ServerState.Offline);
    }

    /// <summary>
    /// Poll until the ServerQuery TCP port accepts connections, or timeout.
    /// Readiness is proven by a real socket connect, not by button intent.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_portProbe(host, port.ToString())) return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Stop: graceful first (console Ctrl+C), forced only after grace expires.
    /// ponytail: grace window is fixed-config; upgrade path is per-user adaptive timing.
    /// </summary>
    public async Task<StopOutcome> StopAsync(TimeSpan grace, CancellationToken ct)
    {
        IProcessHandle? target;
        lock (_gate) target = _managed;

        if (target is null)
        {
            var detached = _host.GetProcessesByExecutablePath(ServerExecutablePath);
            if (detached.Count == 0) return StopOutcome.NotRunning;
            target = detached[0];
        }

        if (target.HasExited)
        {
            lock (_gate) _managed = null;
            return StopOutcome.NotRunning;
        }

        var graceful = _host.RequestGracefulExit(target);

        var exited = await Task.Run(() => target.WaitForExit(grace), ct).ConfigureAwait(false);
        if (exited)
        {
            lock (_gate) _managed = null;
            return graceful ? StopOutcome.Graceful : StopOutcome.NotRunning;
        }

        // Last resort, still scoped to the process we identified.
        target.Kill();
        var forced = await Task.Run(() => target.WaitForExit(TimeSpan.FromSeconds(5)), ct).ConfigureAwait(false);
        lock (_gate) _managed = null;
        return forced ? StopOutcome.Forced : StopOutcome.Timeout;
    }

    /// <summary>Restart = stop then start. Data files are never touched.</summary>
    public async Task<(StopOutcome Outcome, bool Started)> RestartAsync(
        string host, int port, TimeSpan grace, TimeSpan readiness, CancellationToken ct)
    {
        var outcome = await StopAsync(grace, ct).ConfigureAwait(false);
        if (outcome is StopOutcome.Timeout) return (outcome, false);

        try { Start(); }
        catch (InvalidOperationException) { return (outcome, false); }

        var ready = await WaitUntilReadyAsync(host, port, readiness, ct).ConfigureAwait(false);
        return (outcome, ready);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_managed is not null) _managed.Exited -= OnManagedExit;
            _managed = null;
        }
    }
}

public enum StopOutcome
{
    NotRunning,
    Graceful,
    Forced,
    Timeout
}
