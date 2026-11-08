using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamSpeakManager.Core.Models;

namespace TeamSpeakManager.Core.Services;

/// <summary>
/// Talks to the Cloudflare Worker that validates the shared access password.
/// Only authentication; no server control, no data, no command execution downstream.
/// </summary>
public interface IAuthService
{
    /// <summary>Exchanges the shared password for an access/refresh token pair.</summary>
    Task<AuthResult> LoginAsync(string password, CancellationToken ct = default);

    /// <summary>Validates a stored session, refreshing if the access token has expired.</summary>
    Task<AuthResult> ValidateAsync(AuthSession session, CancellationToken ct = default);

    Task<AuthResult> RefreshAsync(AuthSession session, CancellationToken ct = default);

    Task<bool> LogoutAsync(AuthSession session, CancellationToken ct = default);
}

public sealed class AuthService : IAuthService
{
    public const string SkewSeconds = "30";
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http;
    private readonly string _baseUrl;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    static AuthService()
    {
        var h = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false, // an auth Worker must never be redirected
        };
        Http = new HttpClient(h, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public AuthService(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    public Task<AuthResult> LoginAsync(string password, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "/api/auth/login", body: new { password }, session: null, ct);

    public async Task<AuthResult> ValidateAsync(AuthSession session, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Get, "/api/auth/validate", body: null, session, ct).ConfigureAwait(false);

        // /validate rotates the access token and deliberately returns no refresh material
        // (the Worker only stores its hash). Keep the refresh token the client already holds.
        if (result.Success && result.Session is not null && string.IsNullOrEmpty(result.Session.RefreshToken))
        {
            return AuthResult.Ok(result.Session with { RefreshToken = session.RefreshToken });
        }

        return result;
    }

    public Task<AuthResult> RefreshAsync(AuthSession session, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "/api/auth/refresh", body: null, session, ct);

    public Task<bool> LogoutAsync(AuthSession session, CancellationToken ct = default)
        => SendLogoutAsync(session, ct);

    private static readonly string[] AccessScopes = { "access" };
    private const string AccessHeader = "Bearer";

    private async Task<AuthResult> SendAsync(
        HttpMethod method, string path, object? body, AuthSession? session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return AuthResult.Fail(AuthFailure.NotConfigured, "Backend URL is not configured.");

        using var req = new HttpRequestMessage(method, _baseUrl + path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = session is not null
            ? new AuthenticationHeaderValue(AccessHeader, session.AccessToken)
            : null;

        if (body is not null)
            req.Content = JsonContent.Create(body, options: Json);
        else
            req.Content = new ByteArrayContent(Array.Empty<byte>());

        HttpResponseMessage res;
        try
        {
            res = await Http.SendAsync(req, ct);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail(AuthFailure.Network, "Cannot reach the authentication backend.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return AuthResult.Fail(AuthFailure.Network, "Authentication timed out. Check your connection.");
        }

        return await ParseAsync(res, ct);
    }

    private static async Task<AuthResult> ParseAsync(HttpResponseMessage res, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await res.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            return AuthResult.Fail(AuthFailure.Server, "Malformed response from authentication backend.");
        }

        if (string.IsNullOrWhiteSpace(raw))
            return AuthResult.Fail(AuthFailure.Server, $"Unexpected empty response ({(int)res.StatusCode}).");

        FromPipe? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FromPipe>(raw, Json);
        }
        catch (JsonException)
        {
            return AuthResult.Fail(AuthFailure.Server, $"Unexpected response ({(int)res.StatusCode}).");
        }

        var failure = payload?.Error is { Length: > 0 } e ? MapFailure(e, (int)res.StatusCode) : (AuthFailure?)null;
        if (failure is not null && !res.IsSuccessStatusCode)
            return AuthResult.Fail(failure.Value, payload?.Message ?? $"Got {(int)res.StatusCode}.");

        if (!res.IsSuccessStatusCode || payload?.Session is null)
            return AuthResult.Fail(MapFailure(null, (int)res.StatusCode), payload?.Message ?? $"Got {(int)res.StatusCode}.");

        var s = payload.Session;
        var session = new AuthSession(
            s.AccessToken,
            s.RefreshToken,
            DateTimeOffset.TryParse(s.AccessExpiresAt, out var a) ? a : DateTimeOffset.MinValue,
            DateTimeOffset.TryParse(s.RefreshExpiresAt, out var r) ? r : DateTimeOffset.MinValue);

        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken))
            return AuthResult.Fail(AuthFailure.Server, "Backend did not return a complete session.");

        return AuthResult.Ok(session);
    }

    private static AuthFailure MapFailure(string? code, int status) => code switch
    {
        "invalid_credentials" => AuthFailure.InvalidCredentials,
        "rate_limited" => AuthFailure.RateLimited,
        "revoked" => AuthFailure.Revoked,
        "expired" => AuthFailure.Expired,
        "unauthorized" => status == 401 ? AuthFailure.Expired : AuthFailure.InvalidCredentials,
        _ => status >= 500 ? AuthFailure.Server : AuthFailure.InvalidCredentials,
    };

    private sealed record FromPipe(SessionDto? Session, string? Error, string? Message);
    private sealed record SessionDto(string AccessToken, string RefreshToken, string AccessExpiresAt, string RefreshExpiresAt);

    private async Task<bool> SendLogoutAsync(AuthSession session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/auth/logout");
        req.Headers.Authorization = new AuthenticationHeaderValue(AccessHeader, session.AccessToken);
        req.Content = new ByteArrayContent(Array.Empty<byte>());
        try
        {
            using var res = await Http.SendAsync(req, ct);
            // Best effort: stale session is cleared locally regardless.
            return res.IsSuccessStatusCode || (int)res.StatusCode == 401;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
}
