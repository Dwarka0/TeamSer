using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamSpeakManager.Core.Models;

namespace TeamSpeakManager.Core.Services;

/// <summary>
/// Non-secret settings under %LOCALAPPDATA%\TeamSer\config.json.
/// No secret is ever written through this class.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public SettingsService(string? directory = null)
    {
        var dir = directory ?? SecureCredentialStore.DefaultDirectory;
        _path = Path.Combine(dir, "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_path)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), JsonOpts) ?? new AppConfig();
        }
        catch (JsonException)
        {
            // Corrupt config: keep defaults rather than blocking startup. ponytail: real fix is
            // schema versioning; needed when settings shape grows beyond the current ~10 fields.
            return new AppConfig();
        }
        catch (IOException)
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(config, JsonOpts));
    }
}
