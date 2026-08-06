using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

        var menu = new NativeMenu();
        menu.Items.Add(CreateMenuItem("显示主窗口", ShowMainWindow));
        menu.Items.Add(CreateMenuItem("VPN 控制", () =>
        {
            ShowMainWindow();
            _mainViewModel?.NavigateToControl();
        }));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(CreateMenuItem("连接", async () =>
        {
            ShowMainWindow();
            _mainViewModel?.NavigateToControl();
            if (_controlViewModel != null)
            {
                await _controlViewModel.ConnectCommand.ExecuteAsync(null);
            }
        }));
        menu.Items.Add(CreateMenuItem("断开", async () =>
        {
            if (_controlViewModel != null)
            {
                await _controlViewModel.DisconnectCommand.ExecuteAsync(null);
            }
        }));
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
    /// 处理主窗口关闭：已连接 → 隐藏到托盘；未连接 → 允许关闭并退出。
    /// </summary>
    public bool HandleMainWindowClosing(CancelEventArgs e)
    {
        if (_exiting || !MinimizeToTrayOnClose || _mainWindow == null)
        {
            return false;
        }

        if (_vpnConnection.CurrentState != VpnConnectionState.Connected)
        {
            return false;
        }

        e.Cancel = true;
        _mainWindow.Hide();
        return true;
    }

    private static NativeMenuItem CreateMenuItem(string header, Action action) =>
        new(header) { Command = new RelayActionCommand(action) };

    private static NativeMenuItem CreateMenuItem(string header, Func<Task> action) =>
        new(header) { Command = new RelayActionCommand(action) };

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
        if (_mainWindow == null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
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

    private sealed class RelayActionCommand : ICommand
    {
        private readonly Action? _sync;
        private readonly Func<Task>? _async;

        public RelayActionCommand(Action sync) => _sync = sync;

        public RelayActionCommand(Func<Task> async) => _async = async;

        public bool CanExecute(object? parameter) => true;

        public async void Execute(object? parameter)
        {
            if (_async != null)
            {
                await _async();
            }
            else
            {
                _sync?.Invoke();
            }
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
