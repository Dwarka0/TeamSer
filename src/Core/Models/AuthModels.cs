namespace TeamSpeakManager.Core.Models;

/// <summary>Session material returned by the Cloudflare Worker. Tokens never go to disk in plaintext.</summary>
public sealed record AuthSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt)
{
    public bool IsAccessExpired(DateTimeOffset now, TimeSpan skew) =>
        now + skew >= AccessExpiresAt;

    public bool IsRefreshExpired(DateTimeOffset now) =>
        now >= RefreshExpiresAt;
}

public enum AuthFailure
{
    None,
    InvalidCredentials,
    RateLimited,
    Network,
    Server,
    Revoked,
    Expired,
    NotConfigured
}

public sealed record AuthResult(bool Success, AuthSession? Session, AuthFailure Failure, string Message)
{
    public static AuthResult Ok(AuthSession session) => new(true, session, AuthFailure.None, "");
    public static AuthResult Fail(AuthFailure failure, string message) => new(false, null, failure, message);
}
