namespace TeamSpeakManager.Core.Models;

/// <summary>Lifecycle state of the locally managed TeamSpeak 3 server.</summary>
public enum ServerState
{
    Offline,
    Starting,
    Online,
    Stopping,
    Error
}

/// <summary>Snapshot of the local server, safe to bind to the UI.</summary>
public sealed record ServerStatus(
    ServerState State,
    int ConnectedClients = 0,
    int MaxClients = 0,
    TimeSpan Uptime = default,
    string? Error = null,
    int? ProcessId = null)
{
    public static ServerStatus Offline { get; } = new(ServerState.Offline);
}
