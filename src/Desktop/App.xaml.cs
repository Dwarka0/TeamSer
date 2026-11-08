using System.Threading;
using System.Windows;
using TeamSpeakManager.Desktop.ViewModels;

namespace TeamSpeakManager.Desktop;

public partial class App : Application
{
    private Mutex? _mutex;
    public const string MutexName = "Local\\TeamSer.SingleInstance";

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Single instance: a second launch must not try to control the same server.
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("برنامه از قبل در حال اجراست.", "TeamSer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"خطای غیرمنتظره:\n{args.Exception.Message}", "TeamSer",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var vm = new AppViewModel();
        var main = new MainWindow(vm);
        MainWindow = main;
        main.Show();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        // Deliberately NOT killing the server on exit: the app closing must not
        // take down a server the user chose to keep running.
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
