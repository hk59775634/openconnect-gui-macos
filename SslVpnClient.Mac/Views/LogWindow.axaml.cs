using Avalonia.Controls;
using Avalonia.Interactivity;
using SslVpnClient.Services;

namespace SslVpnClient.Mac.Views;

public partial class LogWindow : Window
{
    private SessionLogService? _sessionLog;

    public LogWindow()
    {
        InitializeComponent();
    }

    public LogWindow(SessionLogService sessionLog) : this()
    {
        _sessionLog = sessionLog;
        DataContext = sessionLog;
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var text = (_sessionLog ?? DataContext as SessionLogService)?.Text;
        var clipboard = Clipboard;
        if (clipboard != null && !string.IsNullOrEmpty(text))
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
