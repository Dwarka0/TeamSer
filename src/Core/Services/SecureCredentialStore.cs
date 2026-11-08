using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamSpeakManager.Core.Models;

namespace TeamSpeakManager.Core.Services;

/// <summary>
/// Local secret storage. Secrets are encrypted with Windows DPAPI (CurrentUser scope) and
/// persisted under %LOCALAPPDATA%\TeamSer\secrets.bin. Nothing is written verbatim.
/// </summary>
public sealed class SecureCredentialStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly byte[] _entropy;

    // App-scope entropy binds the blob to this application, not to any hard-coded string alone.
    private static readonly byte[] AppEntropy =
        Encoding.UTF8.GetBytes("TeamSer|dpapi|v1");

    public SecureCredentialStore() : this(DefaultDirectory) { }

    public SecureCredentialStore(string directory)
    {
        _directory = directory;
        // User-scope entropy is itself DPAPI-protected the first time, so no plaintext secret
        // and no static plaintext entropy file ever sits on disk.
        _entropyPath = Path.Combine(directory, "entropy.bin");
        _blobPath = Path.Combine(directory, "secrets.bin");
        Directory.CreateDirectory(directory);
        _entropy = LoadOrCreateEntropy();
    }

    private readonly string _directory;
    private readonly string _entropyPath;
    private readonly string _blobPath;

    public static string DefaultDirectory
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "TeamSer");
        }
    }

    private byte[] LoadOrCreateEntropy()
    {
        if (File.Exists(_entropyPath))
        {
            try
            {
                return ProtectedData.Unprotect(File.ReadAllBytes(_entropyPath), AppEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Blob written by a different Windows user or damaged profile: start fresh.
                File.Delete(_entropyPath);
            }
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_entropyPath, ProtectedData.Protect(fresh, AppEntropy, DataProtectionScope.CurrentUser));
        return fresh;
    }

    private byte[] Load()
    {
        if (!File.Exists(_blobPath)) return [];
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(_blobPath), _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Corrupt or foreign blob: treat as empty rather than crash the app on start.
            return [];
        }
    }

    private void Save(byte[] plaintext)
    {
        Directory.CreateDirectory(_directory);
        var tmp = _blobPath + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser));
        // Swap, not write-overwrite, so a crash mid-write cannot truncate good data.
        File.Move(tmp, _blobPath, overwrite: true);
    }

    private static byte[] ToBytes(string json) => Encoding.UTF8.GetBytes(json);

    private static string FromBytes(byte[] data) => Encoding.UTF8.GetString(data);

    private static readonly Dictionary<string, string> Empty = new();

    public T? Get<T>(string key) where T : class
    {
        var map = ReadMap();
        if (!map.TryGetValue(key, out var json) || json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException)
        {
            // Stored value no longer matches the model (e.g. older schema). Treat as absent.
            return null;
        }
    }

    private Dictionary<string, string> ReadMap()
    {
        var bytes = Load();
        if (bytes.Length == 0) return new Dictionary<string, string>(Empty);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(FromBytes(bytes), JsonOpts)
                   ?? new Dictionary<string, string>(Empty);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(Empty);
        }
    }

    public void Set<T>(string key, T value) where T : class
    {
        var map = ReadMap();
        map[key] = JsonSerializer.Serialize(value, JsonOpts);
        Save(ToBytes(JsonSerializer.Serialize(map, JsonOpts)));
    }

    public void Remove(string key)
    {
        var map = ReadMap();
        if (map.Remove(key)) Save(ToBytes(JsonSerializer.Serialize(map, JsonOpts)));
    }

    public const string SessionKey = "auth.session";
    public const string QueryPasswordKey = "ts3.queryPassword";

    public AuthSession? GetSession() => Get<AuthSession>(SessionKey);

    public void StoreSession(AuthSession session) => Set(SessionKey, session);

    public void ClearSession() => Remove(SessionKey);

    public string? GetQueryPassword() => Get<SecretValue>(QueryPasswordKey)?.Value;

    public void StoreQueryPassword(string plaintext) => Set(QueryPasswordKey, new SecretValue(plaintext));

    public void ClearQueryPassword() => Remove(QueryPasswordKey);
}

public sealed record SecretValue(string Value);
