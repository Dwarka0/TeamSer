using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TeamSpeakManager.Core.Services;
using TeamSpeakManager.Core.Services.ServerQuery;
using TeamSpeakManager.Desktop.ViewModels;

namespace TeamSpeakManager.Desktop.Services;

/// <summary>Composition root. One set of dependencies for the whole app.</summary>
public static class ServiceLocator
{
    private const string DefaultBackendUrl = "https://teamspeak-auth.example.workers.dev";

    private static readonly Lazy<SecureCredentialStore> _secrets = new(() => new SecureCredentialStore());
    private static readonly Lazy<SettingsService> _settings = new(() => new SettingsService());
    private static readonly Lazy<NetworkService> _network = new(() => new NetworkService());

    private static readonly object _authGate = new();
    private static IAuthService? _auth;

    public static SecureCredentialStore Secrets => _secrets.Value;
    public static SettingsService Settings => _settings.Value;
    public static NetworkService Network => _network.Value;

    public static IAuthService Auth
    {
        get
        {
            lock (_authGate)
            {
                if (_auth is not null) return _auth;
                var configured = _settings.Value.Load().BackendBaseUrl;
                _auth = new AuthService(string.IsNullOrWhiteSpace(configured) ? DefaultBackendUrl : configured);
                return _auth;
            }
        }
    }

    /// <summary>Factory so tests can swap the client without reaching global state.</summary>
    public static Func<IServerQueryClient> CreateQueryClient = () => new ServerQueryClient();

    public static IDialogService Dialogs = new DialogService();

    /// <summary>LocalUserId uses DPAPI-protected local path only — never exposes the secret.</summary>
    public static string DataDirectory() => SecureCredentialStore.DefaultDirectory;

    /// <summary>Rebuild the auth client after the user edits the backend URL.</summary>
    public static void ReloadBaseUrl()
    {
        lock (_authGate) _auth = null;
    }
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string message) =>
        MessageBox.Show(message, Strings.ConfirmOk, MessageBoxButton.OKCancel,
            MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void Info(string message) =>
        MessageBox.Show(message, Strings.InfoTitle, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message) =>
        MessageBox.Show(message, Strings.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? PickFolder(string? initial)
    {
        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dlg = new OpenFolderDialog
        {
            Title = Strings.SelectServerDirectory,
            InitialDirectory = !string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial) ? initial : fallback,
        };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
