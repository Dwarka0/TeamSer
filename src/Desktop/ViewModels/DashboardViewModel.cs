using System.Diagnostics;
using System.IO;
using TeamSpeakManager.Core.Models;
using TeamSpeakManager.Core.Services;
using TeamSpeakManager.Core.Services.ServerQuery;
using TeamSpeakManager.Desktop.Services;

namespace TeamSpeakManager.Desktop.ViewModels;

public interface IDialogService
{
    bool Confirm(string message);
    void Info(string message);
    void Error(string message);
    string? PickFolder(string? initial);
}

/// <summary>Dashboard: real process + ServerQuery state, with all six controls wired.</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly TeamSpeakProcessService _process;
    private readonly ServerStatusService _status;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly AppConfig _config;

    private ServerState _state = ServerState.Offline;
    private int _connected;
    private int _max;
    private TimeSpan _uptime;
    private string _error = "";
    private string _busyText = "";

    public DashboardViewModel(AppViewModel owner)
        : this(owner.Config, new TeamSpeakProcessService(), ServiceLocator.Dialogs) { }

    public DashboardViewModel(AppConfig config, TeamSpeakProcessService process, IDialogService? dialogs)
    {
        _config = config;
        _process = process;
        _dialogs = dialogs ?? NullDialogs.Instance;
        _settings = ServiceLocator.Settings;

        var secrets = ServiceLocator.Secrets;
        _status = new ServerStatusService(_process, () => ServiceLocator.CreateQueryClient(), secrets);

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !IsBusy);
        OpenDirectoryCommand = new RelayCommand(OpenDirectory);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        PickDirectoryCommand = new RelayCommand(PickDirectory);

        DetectInstallation();
    }

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenDirectoryCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand PickDirectoryCommand { get; }

    public ServerState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(StatusBrush));
        }
    }

    public string StateText => _state switch
    {
        ServerState.Online => Strings.Online,
        ServerState.Starting => Strings.Starting,
        ServerState.Stopping => Strings.Stopping,
        ServerState.Error => Strings.ErrorState,
        _ => Strings.Offline,
    };

    /// <summary>Brush resolved from app resources so the VM stays free of hard-coded colours.</summary>
    public System.Windows.Media.Brush StatusBrush
    {
        get
        {
            var key = _state switch
            {
                ServerState.Online => "StateOnlineBrush",
                ServerState.Starting or ServerState.Stopping => "StateBusyBrush",
                ServerState.Error => "StateErrorBrush",
                _ => "StateOfflineBrush",
            };
            return System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
                   ?? System.Windows.Media.Brushes.Gray;
        }
    }

    public bool IsOnline => _state == ServerState.Online;
    public bool IsBusy => _busyText.Length > 0;

    public bool CanStart => !IsBusy && _state is ServerState.Offline or ServerState.Error;
    public bool CanStop => !IsBusy && _state is ServerState.Online or ServerState.Starting or ServerState.Error;

    public int ConnectedClients { get => _connected; set => SetProperty(ref _connected, value); }
    public int MaxClients { get => _max; set => SetProperty(ref _max, value); }
    public TimeSpan Uptime { get => _uptime; set { SetProperty(ref _uptime, value); OnPropertyChanged(nameof(UptimeText)); } }

    public string UptimeText => _uptime == TimeSpan.Zero
        ? "—"
        : $"{(int)_uptime.TotalHours:00}:{_uptime.Minutes:00}:{_uptime.Seconds:00}";

    public string Error { get => _error; set => SetProperty(ref _error, value); }

    public string BusyText
    {
        get => _busyText;
        set
        {
            if (!SetProperty(ref _busyText, value)) return;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            StartCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            RestartCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsInstalled => _process.ServerExecutablePath.Length > 0 && File.Exists(_process.ServerExecutablePath);

    public string InstallationText => IsInstalled ? Strings.TSInstalled : Strings.TSNotInstalled;

    public string ServerDirectory => _process.ServerDirectory;

    public string LocalIp => ServiceLocator.Network.GetLocalIPv4().Address ?? "—";

    public string ConnectionAddress => NetworkService.FormatConnectionAddress(LocalIp, _config.VoicePort);

    public string VoicePortText => _config.VoicePort.ToString();

    public async Task RefreshAsync()
    {
        if (IsBusy) return;

        var pid = _process.ManagedProcessId;
        if (pid is null)
        {
            State = ServerState.Offline;
            ConnectedClients = 0;
            MaxClients = 0;
            Uptime = TimeSpan.Zero;
            Error = "";
            return;
        }

        try
        {
            var status = await _status.GetStatusAsync(
                _config.QueryHost, _config.QueryPort, _config.QueryUser, _config.VoicePort).ConfigureAwait(true);

            State = status.State;
            ConnectedClients = status.ConnectedClients;
            MaxClients = status.MaxClients;
            Uptime = status.Uptime;
            Error = status.Error ?? "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            State = ServerState.Error;
            Error = "وضعیت سرور خوانده نشد.";
        }
    }

    private async Task StartAsync()
    {
        if (!EnsureConfigured()) return;

        BusyText = Strings.Starting;
        try
        {
            if (_process.ExistingProcessIds.Count > 0)
            {
                // Duplicate prevention: report, do not spawn a second instance.
                _dialogs.Info("یک نمونه از سرور از قبل در حال اجراست. سرور دوباره اجرا نشد.");
                await RefreshAsync().ConfigureAwait(true);
                return;
            }

            _process.Start();
            State = ServerState.Starting;

            var ready = await _process.WaitUntilReadyAsync(
                _config.QueryHost, _config.QueryPort,
                TimeSpan.FromSeconds(_config.StartupTimeoutSeconds), CancellationToken.None).ConfigureAwait(true);

            if (!ready)
            {
                State = ServerState.Error;
                Error = "سرور در زمان مقرر آماده نشد. پورت ServerQuery پاسخ نداد.";
                _dialogs.Error("سرور شروع شد اما آماده نشد. جزئیات را در لاگ‌ها ببینید.");
                return;
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            State = ServerState.Error;
            Error = ex.Message;
            _dialogs.Error($"اجرای سرور ممکن نشد: {ex.Message}");
        }
        finally
        {
            BusyText = "";
        }
    }

    private async Task StopAsync()
    {
        if (!_dialogs.Confirm(Strings.ConfirmStop)) return;

        BusyText = Strings.Stopping;
        try
        {
            _process.TryConfigure(_config.ServerDirectory, out _);
            var outcome = await _process.StopAsync(
                TimeSpan.FromSeconds(_config.GracefulStopTimeoutSeconds), CancellationToken.None).ConfigureAwait(true);

            State = ServerState.Offline;
            ConnectedClients = 0;
            MaxClients = 0;
            Uptime = TimeSpan.Zero;

            if (outcome == StopOutcome.Forced)
                _dialogs.Info("سرور به‌صورت اجباری پایان یافت؛ داده‌ها دست‌نخورده ماندند.");
            else if (outcome == StopOutcome.Timeout)
                _dialogs.Error("سرور خاموش نشد. لطفاً وضعیت را دستی بررسی کنید.");
        }
        finally
        {
            BusyText = "";
        }
    }

    private async Task RestartAsync()
    {
        if (!_dialogs.Confirm(Strings.ConfirmRestart)) return;
        if (!EnsureConfigured()) return;

        BusyText = Strings.Stopping;
        try
        {
            _process.TryConfigure(_config.ServerDirectory, out _);
            _ = await _process.StopAsync(
                TimeSpan.FromSeconds(_config.GracefulStopTimeoutSeconds), CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            BusyText = "";
        }

        BusyText = Strings.Starting;
        try
        {
            _process.Start();
            var ready = await _process.WaitUntilReadyAsync(
                _config.QueryHost, _config.QueryPort,
                TimeSpan.FromSeconds(_config.StartupTimeoutSeconds), CancellationToken.None).ConfigureAwait(true);

            State = ready ? ServerState.Online : ServerState.Error;
            if (!ready) Error = "سرور پس از راه‌اندازی دوباره آماده نشد.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            State = ServerState.Error;
            Error = ex.Message;
        }
        finally
        {
            BusyText = "";
        }
    }

    private bool EnsureConfigured()
    {
        if (_process.TryConfigure(_config.ServerDirectory, out var error) && File.Exists(_process.ServerExecutablePath))
            return true;

        _dialogs.Error(error + " ابتدا پوشه نصب را در تنظیمات انتخاب کنید.");
        return false;
    }

    public void DetectInstallation()
    {
        if (!_process.TryConfigure(_config.ServerDirectory, out _))
        {
            // Auto-detect a default install location if the user has not chosen one yet.
            var auto = DetectDefaultInstall();
            if (auto is not null)
            {
                _config.ServerDirectory = auto;
                _settings.Save(_config);
                _process.TryConfigure(auto, out _);
            }
        }
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(InstallationText));
        OnPropertyChanged(nameof(ServerDirectory));
    }

    private static string? DetectDefaultInstall()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TeamSpeak 3 Server"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TeamSpeak 3 Server"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "teamspeak3-server_win64"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "teamspeak3-server_win32"),
            @"C:\teamspeak3-server_win64",
            @"C:\teamspeak3-server_win32",
        };
        return candidates.FirstOrDefault(d =>
            TeamSpeakProcessService.CandidateNames.Any(n => File.Exists(Path.Combine(d, n))));
    }

    private void PickDirectory()
    {
        var picked = _dialogs.PickFolder(_config.ServerDirectory);
        if (string.IsNullOrWhiteSpace(picked)) return;

        if (!_process.TryConfigure(picked, out var error))
        {
            _dialogs.Error(error);
            return;
        }

        _config.ServerDirectory = picked;
        _settings.Save(_config);
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(InstallationText));
        OnPropertyChanged(nameof(ServerDirectory));
    }

    private void OpenDirectory()
    {
        var dir = _process.ServerDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _dialogs.Error("پوشه سرور تنظیم نشده یا وجود ندارد.");
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void OpenLogs()
    {
        var dir = _process.ServerDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _dialogs.Error("پوشه سرور تنظیم نشده یا وجود ندارد.");
            return;
        }
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs); // TS3 writes here; create so the button always works
        Process.Start(new ProcessStartInfo { FileName = logs, UseShellExecute = true });
    }

    private sealed class NullDialogs : IDialogService
    {
        public static readonly NullDialogs Instance = new();
        public bool Confirm(string message) => true;
        public void Info(string message) { }
        public void Error(string message) { }
        public string? PickFolder(string? initial) => null;
    }
}
