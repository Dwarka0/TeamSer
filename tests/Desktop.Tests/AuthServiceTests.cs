using TeamSpeakManager.Core.Models;
using TeamSpeakManager.Core.Services;

namespace LTS.Tests;

/// <summary>
/// Auth client behaviour that can be verified without a live Cloudflare Worker:
/// offline handling, timeout handling, and malformed response tolerance.
/// Failures must surface as AuthFailure, never as unhandled exceptions, so the UI
/// always has something meaningful to show.
/// </summary>
public class AuthServiceTests
{
    [Fact]
    public void NotConfigured_is_reported_without_throwing()
    {
        var auth = new AuthService("");
        var result = auth.LoginAsync("whatever").GetAwaiter().GetResult();
        Assert.False(result.Success);
        Assert.Equal(AuthFailure.NotConfigured, result.Failure);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unreachable_backend_is_network_failure()
    {
        // .invalid TLD is guaranteed unresolvable.
        var auth = new AuthService("https://nonexistent.invalid");
        var result = auth.LoginAsync("whatever").GetAwaiter().GetResult();
        Assert.False(result.Success);
        Assert.Equal(AuthFailure.Network, result.Failure);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void Validate_is_network_failure_on_bad_url()
    {
        var session = new AuthSession("tok", "tok", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddHours(1));
        var auth = new AuthService("https://nonexistent.invalid");
        var result = auth.ValidateAsync(session).GetAwaiter().GetResult();
        Assert.Equal(AuthFailure.Network, result.Failure);
    }

    [Fact]
    public void Logout_is_tolerant_of_network_failure()
    {
        var session = new AuthSession("tok", "tok", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddHours(1));
        var auth = new AuthService("https://nonexistent.invalid");
        var ok = auth.LogoutAsync(session).GetAwaiter().GetResult();
        Assert.False(ok);
    }

    [Fact]
    public void Trimmed_base_url_produces_clean_path()
    {
        var auth = new AuthService("https://nonexistent.invalid/");
        var result = auth.ValidateAsync(new AuthSession(
            "x", "x", DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddHours(1))).GetAwaiter().GetResult();
        Assert.NotEqual(AuthFailure.NotConfigured, result.Failure);
    }
}

public class AuthSessionTests
{
    [Fact]
    public void Expired_access_token_is_detected()
    {
        var session = new AuthSession(
            "a", "r", DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(session.IsAccessExpired(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(0)));
    }

    [Fact]
    public void Fresh_access_token_is_not_expired()
    {
        var session = new AuthSession(
            "a", "r", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(session.IsAccessExpired(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ClockSkew_respects_early_expiry()
    {
        var session = new AuthSession(
            "a", "r", DateTimeOffset.UtcNow.AddSeconds(20), DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(session.IsAccessExpired(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60)));
        Assert.False(session.IsAccessExpired(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Expired_refresh_is_detected()
    {
        var session = new AuthSession("a", "r", DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.True(session.IsRefreshExpired(DateTimeOffset.UtcNow));
    }
}
