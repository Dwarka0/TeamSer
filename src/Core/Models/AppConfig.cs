namespace TeamSpeakManager.Core.Models;

/// <summary>
/// Non-secret application configuration. Persisted as JSON under %LOCALAPPDATA%.
/// ServerQuery password and auth tokens live in DPAPI storage, never here.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Base URL of the Cloudflare auth Worker, e.g. https://auth.example.workers.dev</summary>
    public string BackendBaseUrl { get; set; } = "";

    /// <summary>TeamSpeak server installation directory (contains ts3server.exe).</summary>
    public string? ServerDirectory { get; set; }

    /// <summary>ServerQuery host. Default loopback - administrative interface must stay local.</summary>
    public string QueryHost { get; set; } = "127.0.0.1";

    /// <summary>ServerQuery TCP port. Default 10011.</summary>
    public int QueryPort { get; set; } = 10011;

    /// <summary>ServerQuery login name (usually "serveradmin"). Not a secret by itself.</summary>
    public string QueryUser { get; set; } = "serveradmin";

    /// <summary>Voice UDP port. Default 9987.</summary>
    public int VoicePort { get; set; } = 9987;

    /// <summary>File transfer TCP port. Default 30033.</summary>
    public int FileTransferPort { get; set; } = 30033;

    /// <summary>HTTPS endpoint used to discover the public IPv4 address.</summary>
    public string PublicIpEndpoint { get; set; } = "https://api.ipify.org";

    /// <summary>Seconds to wait for the ServerQuery port before declaring startup failed.</summary>
    public int StartupTimeoutSeconds { get; set; } = 60;

    /// <summary>Seconds to wait for graceful shutdown before forcing termination.</summary>
    public int GracefulStopTimeoutSeconds { get; set; } = 15;

    /// <summary>Whether the user already completed the login flow on this machine.</summary>
    public bool RememberLogin { get; set; } = true;
}
