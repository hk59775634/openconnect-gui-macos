using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SslVpnClient.Abstractions;
using SslVpnClient.Models;
using SslVpnClient.Services;
using SslVpnClient.ViewModels;
using SslVpnClient.Vpn;

namespace SslVpnClient.Mac.Services;

/// <summary>
/// macOS 菜单栏托盘：显示连接状态，支持关窗隐藏与快捷操作。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly IVpnConnection _vpnConnection;
    private readonly VpnConnectionTimerService _timerService;
    private readonly ILogger<TrayIconService> _logger;
    private TrayIcon? _trayIcon;
    private Window? _mainWindow;
    private MainViewModel? _mainViewModel;
    private VpnControlViewModel? _controlViewModel;
    private readonly Dictionary<VpnConnectionState, WindowIcon> _iconCache = new();
    private string _connectionDuration = "00:00:00";
    private string _statusMessage = "已断开";
    private bool _exiting;
    private bool _hiddenToTray;
    private IActivatableLifetime? _activatable;

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public TrayIconService(
        IVpnConnection vpnConnection,
        VpnConnectionTimerService timerService,
        ILogger<TrayIconService> logger)
    {
        _vpnConnection = vpnConnection;
        _timerService = timerService;
        _logger = logger;
    }

    public void Initialize(
        Window mainWindow,
        MainViewModel mainViewModel,
        VpnControlViewModel controlViewModel)
    {
        _mainWindow = mainWindow;
        _mainViewModel = mainViewModel;
        _controlViewModel = controlViewModel;

        // 仅在「关窗进托盘」时需要显式退出；启动时仍用默认关闭行为的语义由 HandleMainWindowClosing 接管。
        // 这里设 OnExplicitShutdown，避免 Hide 后进程被当成无窗口退出；启动 UI 由 App 显式 Show。
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        if (Application.Current?.TryGetFeature<IActivatableLifetime>(out var activatable) == true)
        {
            _activatable = activatable;
            _activatable.Activated += OnApplicationActivated;
        }

        // macOS NativeMenu：用 Click，不要依赖 ICommand（托盘回调线程/绑定更可靠）
        var menu = new NativeMenu();
        menu.Items.Add(CreateMenuItem("显示主窗口", ShowMainWindow));
        menu.Items.Add(CreateMenuItem("VPN 控制", () =>
        {
            ShowMainWindow();
            _mainViewModel?.NavigateToControl();
        }));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateMenuItem("连接", () =>
        {
            ShowMainWindow();
            _mainViewModel?.NavigateToControl();
            _ = ConnectFromTrayAsync();
        }));
        menu.Items.Add(CreateMenuItem("断开", () => _ = DisconnectFromTrayAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateMenuItem("退出", ExitApplication));

        _trayIcon = new TrayIcon
        {
            ToolTipText = "OpenConnect Gui - 已断开",
            Icon = GetCachedIcon(VpnConnectionState.Disconnected),
            IsVisible = true,
            Menu = menu
        };

        TrayIcon.SetIcons(Application.Current!, [_trayIcon]);

        _vpnConnection.ConnectionStateChanged += OnConnectionStateChanged;
        _timerService.DurationChanged += OnDurationChanged;
        UpdateTrayText(VpnConnectionState.Disconnected, "已断开", "00:00:00");
        _logger.LogInformation("系统托盘已初始化");
    }

    /// <summary>
    /// 处理主窗口关闭：已连接 → 隐藏到托盘；未连接 → 退出应用。
    /// </summary>
    public bool HandleMainWindowClosing(CancelEventArgs e)
    {
        if (_exiting || _mainWindow == null)
        {
            return false;
        }

        if (MinimizeToTrayOnClose
            && _vpnConnection.CurrentState == VpnConnectionState.Connected)
        {
            e.Cancel = true;
            _hiddenToTray = true;
            _mainWindow.Hide();
            _activatable?.TryEnterBackground();
            _logger.LogInformation("主窗口已隐藏到菜单栏托盘");
            return true;
        }

        // OnExplicitShutdown：未连接关窗也要主动退出
        e.Cancel = true;
        ExitApplication();
        return true;
    }

    private static NativeMenuItem CreateMenuItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) =>
        {
            // 原生菜单回调可能不在 UI 线程
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        };
        return item;
    }

    private async Task ConnectFromTrayAsync()
    {
        if (_controlViewModel == null)
        {
            return;
        }

        await _controlViewModel.ConnectCommand.ExecuteAsync(null);
    }

    private async Task DisconnectFromTrayAsync()
    {
        if (_controlViewModel == null)
        {
            return;
        }

        await _controlViewModel.DisconnectCommand.ExecuteAsync(null);
    }

    private void OnApplicationActivated(object? sender, ActivatedEventArgs e)
    {
        // 启动阶段不要抢焦点；仅托盘隐藏后的 Dock 重开 / 回前台才唤回窗口
        if (!_hiddenToTray)
        {
            return;
        }

        if (e.Kind is ActivationKind.Reopen or ActivationKind.Background)
        {
            ShowMainWindow();
        }
    }

    private void OnDurationChanged(object? sender, string duration)
    {
        _connectionDuration = duration;
        if (_trayIcon == null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
            UpdateTrayText(_vpnConnection.CurrentState, _statusMessage, _connectionDuration));
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (_trayIcon == null)
        {
            return;
        }

        _statusMessage = e.Message;
        Dispatcher.UIThread.Post(() =>
        {
            _trayIcon.Icon = GetCachedIcon(e.State);
            UpdateTrayText(e.State, e.Message, _connectionDuration);
        });
    }

    private void UpdateTrayText(VpnConnectionState state, string message, string duration)
    {
        if (_trayIcon == null)
        {
            return;
        }

        var durationPart = state == VpnConnectionState.Connected && duration != "00:00:00"
            ? $" [{duration}]"
            : string.Empty;
        _trayIcon.ToolTipText = $"OpenConnect Gui - {message}{durationPart}";
    }

    private void ShowMainWindow()
    {
        void ShowCore()
        {
            if (_mainWindow == null)
            {
                return;
            }

            _activatable?.TryLeaveBackground();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow ??= _mainWindow;
            }

            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();

            // macOS：强制提到前面（Activate 有时不够）
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;

            _hiddenToTray = false;
            _logger.LogInformation("主窗口已从托盘恢复");
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowCore();
        }
        else
        {
            Dispatcher.UIThread.Post(ShowCore);
        }
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _hiddenToTray = false;
        _mainViewModel?.DisconnectOnExit();
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private WindowIcon GetCachedIcon(VpnConnectionState state)
    {
        if (!_iconCache.TryGetValue(state, out var icon))
        {
            icon = CreateStatusIcon(state);
            _iconCache[state] = icon;
        }

        return icon;
    }

    private static WindowIcon CreateStatusIcon(VpnConnectionState state)
    {
        var color = state switch
        {
            VpnConnectionState.Connected => Color.FromRgb(76, 175, 80),
            VpnConnectionState.Connecting or VpnConnectionState.Reconnecting => Color.FromRgb(255, 152, 0),
            VpnConnectionState.Error => Color.FromRgb(244, 67, 54),
            _ => Color.FromRgb(158, 158, 158)
        };

        const int size = 32;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawEllipse(
                new SolidColorBrush(color),
                null,
                new Rect(4, 4, size - 8, size - 8));
        }

        return new WindowIcon(bitmap);
    }

    public void Dispose()
    {
        _vpnConnection.ConnectionStateChanged -= OnConnectionStateChanged;
        _timerService.DurationChanged -= OnDurationChanged;
        if (_activatable != null)
        {
            _activatable.Activated -= OnApplicationActivated;
            _activatable = null;
        }

        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (Application.Current != null)
        {
            TrayIcon.SetIcons(Application.Current, null);
        }

        _iconCache.Clear();
    }
}
