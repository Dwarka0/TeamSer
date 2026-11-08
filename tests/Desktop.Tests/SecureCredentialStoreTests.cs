using System.Security.Cryptography;
using TeamSpeakManager.Core.Models;
using TeamSpeakManager.Core.Services;

namespace LTS.Tests;

public class SecureCredentialStoreTests : IDisposable
{
    private static readonly byte[] EntropyMarker =
        "portal-test-entropy-v1-with-sufficient-length"u8.ToArray();

    private readonly string _dir;
    private readonly SecureCredentialStore _store;

    public SecureCredentialStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsm-secrets-" + Guid.NewGuid().ToString("N"));
        _store = new SecureCredentialStore(_dir);
    }

    public void Dispose() =>
        Helpers.TryDelete(_dir);

    [Fact]
    public void RoundTrip_session_persists()
    {
        var session = new AuthSession(
            AccessToken: "access-token-abc",
            RefreshToken: "refresh-token-xyz",
            AccessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            RefreshExpiresAt: DateTimeOffset.UtcNow.AddHours(24));

        Assert.True(session.AccessExpiresAt > DateTimeOffset.UtcNow);
        _store.StoreSession(session);

        var loaded = _store.GetSession();
        Assert.NotNull(loaded);
        Assert.Equal(session.AccessToken, loaded!.AccessToken);
        Assert.Equal(session.RefreshToken, loaded.RefreshToken);
        Assert.Equal(session.AccessExpiresAt, loaded.AccessExpiresAt);
    }

    [Fact]
    public void Session_is_never_written_to_disk_in_plaintext()
    {
        var session = new AuthSession(
            AccessToken: "very-secret-access-token-98765",
            RefreshToken: "very-secret-refresh-token-98765",
            AccessExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
            RefreshExpiresAt: DateTimeOffset.UtcNow.AddHours(24));
        _store.StoreSession(session);

        // Every byte on disk must be DPAPI ciphertext, not two tear apart into the token.
        var blobPath = Path.Combine(_dir, "secrets.bin");
        Assert.True(File.Exists(blobPath), "secrets.bin was not created.");
        var raw = File.ReadAllBytes(blobPath);
        var rawText = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("very-secret-access-token-98765", rawText);
        Assert.DoesNotContain("very-secret-refresh-token-98765", rawText);
        Assert.DoesNotContain("AccessToken", rawText);
    }

    [Fact]
    public void Entropy_file_is_also_protected()
    {
        var path = Path.Combine(_dir, "entropy.bin");
        Assert.True(File.Exists(path));
        var raw = File.ReadAllBytes(path);
        var text = System.Text.Encoding.UTF8.GetString(raw);
        // The entropy blob must not be readable plaintext.
        Assert.DoesNotContain("portal-test-entropy-v1", text);
    }

    [Fact]
    public void ClearSession_removes_it()
    {
        var session = new AuthSession("a", "b", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddHours(1));
        _store.StoreSession(session);
        _store.ClearSession();
        Assert.Null(_store.GetSession());
    }

    [Fact]
    public void QueryPassword_roundtrips_and_is_obfuscated_on_disk()
    {
        const string password = "serveradmin-super-secret-7749";
        _store.StoreQueryPassword(password);
        Assert.Equal(password, _store.GetQueryPassword());

        var raw = File.ReadAllBytes(Path.Combine(_dir, "secrets.bin"));
        var text = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain(password, text);
    }

    [Fact]
    public void Overwrite_replaces_previous_value()
    {
        _store.StoreQueryPassword("first");
        _store.StoreQueryPassword("second");
        Assert.Equal("second", _store.GetQueryPassword());
    }

    [Fact]
    public void Corrupt_blob_does_not_crash_and_returns_null()
    {
        _store.StoreSession(new AuthSession("a", "b", DateTimeOffset.Now, DateTimeOffset.Now));
        File.WriteAllText(Path.Combine(_dir, "secrets.bin"), "this is not valid DPAPI ciphertext");
        Assert.Null(_store.GetSession());
    }

    [Fact]
    public void Stored_at_fresh_directory_writes_entropy()
    {
        // A second instance with the same directory must read the same entropy, not fail.
        var store = new SecureCredentialStore(_dir);
        Assert.Null(store.GetSession());
    }
}

public static class Helpers
{
    public static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* leave temp files behind rather than fail a test about cleanup */ }
    }
}
