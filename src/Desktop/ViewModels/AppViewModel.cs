using System.IO;
using System.Windows.Threading;
using TeamSpeakManager.Core.Models;
using TeamSpeakManager.Core.Services;
using TeamSpeakManager.Desktop.Services;

namespace TeamSpeakManager.Desktop.ViewModels;

/// <summary>Root view model: login gate, navigation, and the app-level error surface.</summary>
public sealed class AppViewModel : ObservableObject
{
    private const int LoginFailLimit = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StatusPoll = TimeSpan.FromSeconds(5);

    private readonly IAuthService _auth;
    private readonly SecureCredentialStore _secrets;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _lockoutTimer;

    private int _failedAttempts;
    private DateTimeOffset _lockoutUntil = DateTimeOffset.MinValue;

    private string _password = "";
    private bool _busy;
    private bool _isLoggedIn;
    private string _loginError = "";

    public AppViewModel()
        : this(ServiceLocator.Auth, ServiceLocator.Secrets) { }

    public AppViewModel(IAuthService auth, SecureCredentialStore secrets)
    {
        _auth = auth;
        _secrets = secrets;
        Config = ServiceLocator.Settings.Load();
        Server = new DashboardViewModel(this);
        Network = new NetworkViewModel(Config.PublicIpEndpoint);

        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsBusy && !IsLockedOut);
        LogoutCommand = new RelayCommand(Logout);

        _timer = new DispatcherTimer { Interval = StatusPoll };
        _timer.Tick += async (_, _) => await Server.RefreshAsync().ConfigureAwait(true);

        _lockoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _lockoutTimer.Tick += (_, _) =>
        {
            _lockoutTimer.Stop();
            OnPropertyChanged(nameof(IsLockedOut));
            OnPropertyChanged(nameof(LockoutRemaining));
        };
    }

    public AppConfig Config { get; }
    public DashboardViewModel Server { get; }
    public NetworkViewModel Network { get; }

    public string Password
    {
        get => _password;
        set
        {
            SetProperty(ref _password, value);
            LoginCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        set { SetProperty(ref _busy, value); LoginCommand.RaiseCanExecuteChanged(); }
    }

    public bool IsLoggedIn { get => _isLoggedIn; set => SetProperty(ref _isLoggedIn, value); }

    public string LoginError
    {
        get => _loginError;
        set => SetProperty(ref _loginError, value);
    }

    public bool IsLockedOut => DateTimeOffset.UtcNow < _lockoutUntil;

    public string LockoutRemaining => IsLockedOut
        ? (_lockoutUntil - DateTimeOffset.UtcNow).ToString(@"mm\:ss")
        : "";

    public AsyncRelayCommand LoginCommand { get; }
    public RelayCommand LogoutCommand { get; }

    public RelayCommand CopyAddressCommand => new(() =>
    {
        try
        {
            System.Windows.Clipboard.SetText(Server.ConnectionAddress);
            StatusMessage = "آدرس اتصال کپی شد.";
        }
        catch (Exception)
        {
            StatusMessage = "کپی آدرس ممکن نشد.";
        }
    });

    public RelayCommand RefreshNetworkCommand => new(() =>
    {
        Network.Refresh();
        OnPropertyChanged(nameof(LogText));
        StatusMessage = "تشخیص شبکه بروزرسانی شد.";
    });

    public RelayCommand SaveSettingsCommand => new(() =>
    {
        ServiceLocator.Settings.Save(Config);
        ServiceLocator.ReloadBaseUrl();
        StatusMessage = "تنظیمات ذخیره شد. اتصال بعدی از نشانی جدید استفاده می‌کند.";
        Server.DetectInstallation();
    });

    public RelayCommand SaveQueryPasswordCommand => new(() =>
    {
        // The password is captured by the PasswordBox in Settings; see MainWindow.SettingsPasswordChanged.
        if (PendingQueryPassword is { Length: > 0 } pw)
        {
            _secrets.StoreQueryPassword(pw);
            PendingQueryPassword = "";
            StatusMessage = "رمز ServerQuery به‌صورت رمزنگاری‌شده ذخیره شد.";
        }
        else
        {
            StatusMessage = "ابتدا رمز ServerQuery را وارد کنید.";
        }
        OnPropertyChanged(nameof(QueryPasswordStatus));
    });

    public string PendingQueryPassword { get; set; } = "";

    public string QueryPasswordStatus => _secrets.GetQueryPassword() is null
        ? "رمز ServerQuery ذخیره نشده است."
        : "رمز ServerQuery ذخیره شده است.";

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public string LogText
    {
        get
        {
            var dir = ServiceLocator.Settings.Load().ServerDirectory;
            if (string.IsNullOrWhiteSpace(dir)) return "پوشه سرور تنظیم نشده است.";

            var logs = Path.Combine(dir, "logs");
            if (!Directory.Exists(logs)) return "پوشه لاگ وجود ندارد.";

            var newest = Directory.EnumerateFiles(logs, "*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return "فایل لاگی یافت نشد.";

            try
            {
                // Read the tail only: server logs can grow large.
                var lines = File.ReadLines(newest).TakeLast(300);
                return string.Join(Environment.NewLine, lines);
            }
            catch (IOException)
            {
                return "خواندن فایل لاگ ممکن نشد.";
            }
        }
    }

    /// <summary>Try the remembered session before showing the password box.</summary>
    public async Task<bool> TryResumeSessionAsync()
    {
        var stored = _secrets.GetSession();
        if (stored is null) return false;

        IsBusy = true;
        try
        {
            var result = await _auth.ValidateAsync(stored).ConfigureAwait(true);
            if (result.Success && result.Session is not null)
            {
                _secrets.StoreSession(result.Session);
                IsLoggedIn = true;
                _timer.Start();
                await Server.RefreshAsync().ConfigureAwait(true);
                return true;
            }

            // Offline backend keeps the stored session: do not destroy credentials because
            // the internet is down. Only definitive rejections clear it.
            if (result.Failure is AuthFailure.Network or AuthFailure.Server or AuthFailure.NotConfigured)
            {
                LoginError = result.Message;
                return false;
            }

            _secrets.ClearSession();
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoginAsync()
    {
        if (IsLockedOut)
        {
            LoginError = $"تعداد تلاش‌ها زیاد است. {LockoutRemaining} دیگر تلاش کنید.";
            return;
        }

        if (string.IsNullOrEmpty(Password))
        {
            LoginError = Strings.LoginInvalidPassword;
            return;
        }

        IsBusy = true;
        LoginError = "";
        try
        {
            var attempt = await _auth.LoginAsync(Password).ConfigureAwait(true);
            if (attempt.Success && attempt.Session is not null)
            {
                _failedAttempts = 0;
                _lockoutUntil = DateTimeOffset.MinValue;
                if (Config.RememberLogin) _secrets.StoreSession(attempt.Session);
                Password = "";
                IsLoggedIn = true;
                _timer.Start();
                await Server.RefreshAsync().ConfigureAwait(true);
                return;
            }

            _failedAttempts++;
            if (_failedAttempts >= LoginFailLimit)
            {
                _lockoutUntil = DateTimeOffset.UtcNow + LockoutWindow;
                _lockoutTimer.Start();
            }
            LoginError = DescribeFailure(attempt.Failure, attempt.Message);
            Password = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Logout()
    {
        _timer.Stop();
        var stored = _secrets.GetSession();
        if (stored is not null)
        {
            _ = _auth.LogoutAsync(stored);
            _secrets.ClearSession();
        }
        IsLoggedIn = false;
        Server.State = ServerState.Offline;
    }

    private static string DescribeFailure(AuthFailure f, string detail) => f switch
    {
        AuthFailure.InvalidCredentials => Strings.LoginInvalidPassword,
        AuthFailure.RateLimited => Strings.LoginRateLimited,
        AuthFailure.Network => Strings.LoginNetworkFail,
        AuthFailure.Revoked => "دسترسی شما باطل شده است.",
        AuthFailure.Expired => "اعتبار شما منقضی شده است، دوباره وارد شوید.",
        AuthFailure.Server => $"خطای سرور تأیید: {detail}",
        AuthFailure.NotConfigured => "نشانی سرور تأیید پیکربندی نشده است.",
        AuthFailure.None => "خطای ناشناخته",
        _ => Strings.LoginInvalidPassword,
    };
}
