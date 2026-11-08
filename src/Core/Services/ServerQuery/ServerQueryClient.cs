using System.Net.Sockets;
using System.Text;

namespace TeamSpeakManager.Core.Services.ServerQuery;

/// <summary>
/// Live TeamSpeak 3 ServerQuery TCP client.
/// Connects only to a configured management endpoint, defaults to loopback so the
/// administrative interface is never exposed off-box.
/// </summary>
public interface IServerQueryClient : IDisposable
{
    Task ConnectAsync(string host, int port, CancellationToken ct = default);
    Task<QueryReply> ExecuteAsync(string command, CancellationToken ct = default);
    Task<QueryReply> LoginAsync(string user, string password, CancellationToken ct = default);
}

public sealed class ServerQueryClient : IServerQueryClient
{
    private const int MaxResponseChars = 1_000_000;

    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Query host is required.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _tcp = tcp;

        // Banner: "Welcome to the TeamSpeak 3 ServerQuery"
        await ReadUntilErrorAsync(ct).ConfigureAwait(false);
    }

    private async Task<QueryReply> ReadUntilErrorAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];

        while (sb.Length < MaxResponseChars)
        {
            var n = await _reader!.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            sb.Append(buffer, 0, n);

            // Error line terminates a reply; it is always the final line.
            if (sb.ToString().EndsWith("error id=", StringComparison.Ordinal) ||
                LooksTerminated(sb)) break;
        }

        var text = sb.ToString();
        if (!text.Contains("error id=", StringComparison.Ordinal))
            return QueryReply.Failed(-1, "connection closed before a complete response");

        return ServerQueryParser.ParseSingle(text);
    }

    private static bool LooksTerminated(StringBuilder sb)
    {
        var tail = sb.ToString();
        var idx = tail.LastIndexOf("error id=", StringComparison.Ordinal);
        if (idx < 0) return false;
        var line = tail[idx..];
        return line.EndsWith('\n') || line.Contains(" msg=", StringComparison.Ordinal);
    }

    public async Task<QueryReply> ExecuteAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command is required.", nameof(command));
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writer is null) throw new InvalidOperationException("Not connected.");
            await _writer.WriteLineAsync(command).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
            return await ReadUntilErrorAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<QueryReply> LoginAsync(string user, string password, CancellationToken ct = default)
    {
        // Values are escaped so password content cannot inject additional commands.
        var cmd = "login client_login_name=" + ServerQueryParser.Escape(user) +
                  " client_login_password=" + ServerQueryParser.Escape(password);
        return ExecuteAsync(cmd, ct);
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _tcp?.Dispose();
        _lock.Dispose();
        _writer = null;
        _reader = null;
        _tcp = null;
    }
}
