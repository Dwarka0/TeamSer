using TeamSpeakManager.Core.Models;
using TeamSpeakManager.Core.Services.ServerQuery;

namespace TeamSpeakManager.Core.Services;

/// <summary>
/// Builds a trustworthy ServerStatus by combining real process state with a ServerQuery probe.
/// Process presence alone is not "online"; the query endpoint must answer.
/// </summary>
public sealed class ServerStatusService
{
    private readonly TeamSpeakProcessService _process;
    private readonly Func<IServerQueryClient> _queryFactory;
    private readonly SecureCredentialStore _secrets;

    public ServerStatusService(
        TeamSpeakProcessService process,
        Func<IServerQueryClient> queryFactory,
        SecureCredentialStore secrets)
    {
        _process = process;
        _queryFactory = queryFactory;
        _secrets = secrets;
    }


    /// <summary>
    /// Never throws. Returns Error state with a message when the probe fails.
    /// </summary>
    public async Task<ServerStatus> GetStatusAsync(
        string queryHost, int queryPort, string queryUser, int voicePort = 9987, CancellationToken ct = default)
    {
        var pid = _process.ManagedProcessId;

        if (pid is null)
            return ServerStatus.Offline;

        try
        {
            using var q = _queryFactory();
            await q.ConnectAsync(queryHost, queryPort, ct).ConfigureAwait(false);

            var password = _secrets.GetQueryPassword();
            if (string.IsNullOrEmpty(password))
                return new ServerStatus(ServerState.Online, Error: "ServerQuery password not stored.", ProcessId: pid);

            var login = await q.LoginAsync(queryUser, password, ct).ConfigureAwait(false);
            if (!login.IsOk)
                return new ServerStatus(ServerState.Online,
                    Error: $"ServerQuery login failed: {login.ErrorMessage}", ProcessId: pid);

            // After login the query session has NO server selected, so serverinfo would act
            // on the null selection. Select first by virtual server id, then by UDP port,
            // both of which are documented `use` forms.
            var use = await q.ExecuteAsync("use sid=1", ct).ConfigureAwait(false);
            if (!use.IsOk)
            {
                use = await q.ExecuteAsync($"use port={voicePort}", ct).ConfigureAwait(false);
                if (!use.IsOk)
                    return new ServerStatus(ServerState.Online,
                        Error: "ServerQuery could not select a virtual server.", ProcessId: pid);
            }

            // serverinfo gives capacity/uptime; clientlist gives the real connected count.
            var info = await q.ExecuteAsync("serverinfo", ct).ConfigureAwait(false);
            var clients = await q.ExecuteAsync("clientlist", ct).ConfigureAwait(false);

            var infoRec = info.Records.FirstOrDefault() ?? new Dictionary<string, string>();
            var maxClients = ParseInt(infoRec.GetValueOrDefault("virtualserver_maxclients"));
            var uptime = TimeSpan.FromSeconds(ParseInt(infoRec.GetValueOrDefault("virtualserver_uptime")));

            // clientlist includes Query clients; exclude them so the number matches the UI expectation.
            var connected = clients.Records.Count(r =>
                !string.Equals(r.GetValueOrDefault("client_type"), "1", StringComparison.Ordinal));

            return new ServerStatus(ServerState.Online, connected, maxClients, uptime, null, pid);
        }
        catch (OperationCanceledException)
        {
            return new ServerStatus(ServerState.Online, Error: "Status refresh was cancelled.", ProcessId: pid);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            // Process is alive but ServerQuery is not answering: report it, do not claim Online.
            return new ServerStatus(ServerState.Error, Error: "Server process is running but ServerQuery is unreachable.", ProcessId: pid);
        }
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, out var v) && v >= 0 ? v : 0;
}
