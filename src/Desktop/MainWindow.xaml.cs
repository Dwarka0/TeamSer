using System.Windows;
using System.Windows.Controls;
using TeamSpeakManager.Desktop.ViewModels;

namespace TeamSpeakManager.Desktop;

public partial class MainWindow : Window
{
    private readonly AppViewModel _vm;

    public MainWindow(AppViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Loaded += async (_, _) =>
        {
            // Silent resume: a valid remembered session skips the password prompt.
            if (!await _vm.TryResumeSessionAsync().ConfigureAwait(true))
            {
                PasswordBox.Focus();
            }
        };
    }

    private void PasswordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && _vm.LoginCommand.CanExecute(null))
        {
            _vm.LoginCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void QueryPasswordBox_Changed(object sender, RoutedEventArgs e)
    {
        // Handed straight to the DPAPI-backed store; never persisted as a plain string field.
        if (sender is PasswordBox pb) _vm.PendingQueryPassword = pb.Password;
    }
}
