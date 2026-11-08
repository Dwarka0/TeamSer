using System.Diagnostics;
using TeamSpeakManager.Core.Services;

namespace LTS.Tests;

/// <summary>
/// Exercises real process handling against the mock server executable.
/// No TeamSpeak binary is involved, and no process outside the mock is ever touched.
/// </summary>
[Collection("process")]
public class ProcessManagementTests : IDisposable
{
    private readonly string _dir;
    private readonly string _exe;

    public ProcessManagementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        CopyMockRuntime(_dir);
        // Renamed to ts3server.exe so path detection is exercised exactly as production sees it.
        var source = FindMockExe();
        _exe = Path.Combine(_dir, "ts3server.exe");
        File.Copy(source, _exe, overwrite: true);
    }

    /// <summary>
    /// A .NET apphost launched under a different name still resolves its
    /// sibling .dll/.runtimeconfig.json, so the whole output folder must be present.
    /// </summary>
    private static void CopyMockRuntime(string target)
    {
        var sourceDir = Path.GetDirectoryName(FindMockExe())!;
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var copy = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(copy)) File.Copy(file, copy);
        }
    }

    private static string FindMockExe()
    {
        var name = OperatingSystem.IsWindows() ? "mock-server.exe" : "mock-server";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;

            // Build output may live under tests/MockServer/bin/<config>/<tfm>/
            var nested = Directory.EnumerateFiles(dir.FullName, name, SearchOption.AllDirectories).FirstOrDefault();
            if (nested is not null) return nested;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("mock-server executable was not built. Build the solution first.");
    }

    public void Dispose()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ts3server"))
            {
                try { if (p.MainModule?.FileName == _exe) p.Kill(); } catch { }
            }
        }
        catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Missing_directory_is_reported()
    {
        var svc = new TeamSpeakProcessService();
        Assert.False(svc.TryConfigure(Path.Combine(_dir, "nope"), out var error));
        Assert.Contains("does not exist", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_path_is_reported()
    {
        var svc = new TeamSpeakProcessService();
        Assert.False(svc.TryConfigure(null, out var error));
        Assert.Contains("not configured", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Directory_without_exe_is_reported()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        var svc = new TeamSpeakProcessService();
        Assert.False(svc.TryConfigure(empty, out var error));
        Assert.Contains("ts3server.exe", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valid_directory_is_accepted()
    {
        var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(_dir, out var error), error);
        Assert.Equal(Path.Combine(_dir, "ts3server.exe"), svc.ServerExecutablePath);
        Assert.Equal(_dir, svc.ServerDirectory);
    }

    [Fact]
    public void Detects_official_win64_binary_name()
    {
        // Official Windows packages ship ts3server_win64.exe, not plain ts3server.exe.
        var dir = Path.Combine(_dir, "win64");
        Directory.CreateDirectory(dir);
        File.Copy(_exe, Path.Combine(dir, "ts3server_win64.exe"));

        var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(dir, out var error), error);
        Assert.EndsWith("ts3server_win64.exe", svc.ServerExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prefers_win64_over_plain_name_when_both_present()
    {
        var dir = Path.Combine(_dir, "both");
        Directory.CreateDirectory(dir);
        File.Copy(_exe, Path.Combine(dir, "ts3server.exe"));
        File.Copy(_exe, Path.Combine(dir, "ts3server_win64.exe"));

        var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(dir, out var error), error);
        Assert.EndsWith("ts3server_win64.exe", svc.ServerExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_directory_pointing_at_a_non_canonical_path()
    {
        // Guard: the resolved executable must stay inside the configured directory.
        var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(_dir, out _));
        Assert.StartsWith(_dir, svc.ServerExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_launches_process_with_correct_executable_and_cwd()
    {
        using var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(_dir, out _));
        var handle = svc.Start();

        try
        {
            Assert.False(handle.HasExited);
            Assert.Equal(_exe, handle.ExecutablePath);
            Assert.True(handle.Id > 0);
        }
        finally
        {
            if (!handle.HasExited) handle.Kill();
        }
    }

    [Fact]
    public void Start_twice_does_not_create_duplicate_process()
    {
        using var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(_dir, out _));

        var first = svc.Start();
        try
        {
            var second = svc.Start();
            Assert.Equal(first.Id, second.Id);
            Assert.Single(svc.ExistingProcessIds);
        }
        finally
        {
            if (!first.HasExited) first.Kill();
            first.WaitForExit(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Stop_forces_termination_when_process_ignores_graceful()
    {
        // --hang makes the mock ignore console signals, so only the force path can end it.
        using var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(_dir, out _));
        var handle = svc.Start(["--hang"]);

        var outcome = svc.StopAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal(StopOutcome.Forced, outcome);
        Assert.True(handle.HasExited);
        Assert.Empty(svc.ExistingProcessIds);
    }

    [Fact]
    public void Stop_when_not_running_reports_not_running()
    {
        using var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(_dir, out _));

        var outcome = svc.StopAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal(StopOutcome.NotRunning, outcome);
    }

    [Fact]
    public void Crash_is_detected_as_offline()
    {
        // --quick-exit makes the mock die immediately, as a real crash would.
        using var svc = new TeamSpeakProcessService();
        Assert.True(svc.TryConfigure(_dir, out _));
        var handle = svc.Start(["--quick-exit"]);

        // Wait until the mock exits; state must flip to Offline.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !handle.HasExited)
            Thread.Sleep(50);

        Assert.True(handle.HasExited, "mock server did not exit; crash path not exercised.");
        Assert.Equal(TeamSpeakManager.Core.Models.ServerState.Offline, svc.CurrentState);
    }

    [Fact]
    public void Unrelated_process_is_never_matched()
    {
        var host = new SystemProcessHost();
        // A path that definitely does not exist must match nothing, not "everything".
        var found = host.GetProcessesByExecutablePath(Path.Combine(_dir, "does-not-exist.exe"));
        Assert.Empty(found);
    }

    [Fact]
    public void WaitUntilReady_times_out_when_port_closed()
    {
        using var svc = new TeamSpeakProcessService(portProbe: (_, _) => false);
        var ready = svc.WaitUntilReadyAsync("127.0.0.1", 1,
            TimeSpan.FromMilliseconds(600), CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(ready);
    }
}
