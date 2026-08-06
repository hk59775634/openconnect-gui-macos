using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SslVpnClient.ViewModels;

namespace SslVpnClient.Mac.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    private void ServerUrl_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionSetupViewModel vm &&
            vm.NormalizeServerUrlCommand.CanExecute(null))
        {
            vm.NormalizeServerUrlCommand.Execute(null);
        }
    }

    private void LoginField_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (DataContext is ConnectionSetupViewModel vm &&
            vm.LoginCommand.CanExecute(null))
        {
            _ = vm.LoginCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}
