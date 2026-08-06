using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SslVpnClient.Mac.Services;
using SslVpnClient.Mac.Views;
using SslVpnClient.Services;
using SslVpnClient.ViewModels;

namespace SslVpnClient.Mac;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = ServiceCollectionExtensions.ConfigureServices();
        Services.GetRequiredService<SessionLogService>().Clear();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = Services.GetRequiredService<MainViewModel>();
            var controlVm = Services.GetRequiredService<VpnControlViewModel>();
            var tray = Services.GetRequiredService<TrayIconService>();
            var mainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            controlVm.OpenLogsRequested += () =>
            {
                var logs = new LogWindow(Services.GetRequiredService<SessionLogService>())
                {
                    WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner
                };
                logs.Show(mainWindow);
            };

            tray.Initialize(mainWindow, mainVm, controlVm);
            mainWindow.Closing += (_, e) => tray.HandleMainWindowClosing(e);

            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) =>
            {
                mainVm.DisconnectOnExit();
                try
                {
                    tray.Dispose();
                    Services.GetRequiredService<SessionLogService>().Clear();
                }
                catch
                {
                    // ignore
                }

                if (Services is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            };

            _ = mainVm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
