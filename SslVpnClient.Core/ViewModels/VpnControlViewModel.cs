using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SslVpnClient.Abstractions;
using SslVpnClient.Models;
using SslVpnClient.Services;
using SslVpnClient.Vpn;

namespace SslVpnClient.ViewModels;

/// <summary>
/// VPN 主控页 ViewModel（macOS / 跨平台精简版）：节点加载、连接控制与会话日志。
/// </summary>
public partial class VpnControlViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private readonly VpnProfileService _profileService;
    private readonly GatewayNodeCacheService _nodeCache;
    private readonly IVpnConnection _vpnConnection;
    private readonly VpnConnectionTimerService _timerService;
    private readonly SessionLogService _sessionLog;
    private readonly ILogger<VpnControlViewModel> _logger;
    private readonly IUiDispatcher _dispatcher;
    private ConnectionConfig? _savedConfig;
    private bool _networkMayNeedRestore;

    [ObservableProperty]
    private ObservableCollection<GatewayNode> _gatewayNodes = new();

    [ObservableProperty]
    private GatewayNode? _selectedGateway;

    [ObservableProperty]
    private bool _splitTunnelEnabled;

    [ObservableProperty]
    private VpnConnectionState _connectionState = VpnConnectionState.Disconnected;

    [ObservableProperty]
    private string _statusMessage = "请先登录";

    [ObservableProperty]
    private string _statusIndicatorColor = "#64748B";

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isLoadingNodes;

    [ObservableProperty]
    private string _connectionDuration = "00:00:00";

    [ObservableProperty]
    private string _profileStatusMessage = string.Empty;

    [ObservableProperty]
    private string _activeNodeName = string.Empty;

    [ObservableProperty]
    private string _activeSplitLabel = string.Empty;

    /// <summary>请求返回登录页（保留本地凭证）。</summary>
    public event Action? LogoutRequested;

    /// <summary>请求打开会话日志窗口（由宿主 UI 处理）。</summary>
    public event Action? OpenLogsRequested;

    /// <summary>未连接时可修改分流/节点。</summary>
    public bool ControlsEditable => !IsConnected && !IsConnecting;

    /// <summary>未连接时可修改分流。</summary>
    public bool SplitTunnelEditable => ControlsEditable;

    /// <summary>显示分流复选框。</summary>
    public bool ShowSplitTunnelOption => true;

    /// <summary>是否显示已连接信息面板。</summary>
    public bool ShowActiveConnection => IsConnected || _networkMayNeedRestore;

    /// <summary>未连接时显示节点选择区。</summary>
    public bool ShowNodePicker => !IsConnected;

    /// <summary>合并按钮文案：连接 / 连接中… / 断开。</summary>
    public string ConnectionActionText
    {
        get
        {
            if (IsConnecting ||
                ConnectionState is VpnConnectionState.Connecting
                    or VpnConnectionState.Reconnecting
                    or VpnConnectionState.Disconnecting)
            {
                return "连接中…";
            }

            if (IsConnected || _networkMayNeedRestore || _vpnConnection.NeedsNetworkRestore)
            {
                return "断开";
            }

            return "连接";
        }
    }

    public VpnControlViewModel(
        ConfigurationService configurationService,
        VpnProfileService profileService,
        GatewayNodeCacheService nodeCache,
        IVpnConnection vpnConnection,
        VpnConnectionTimerService timerService,
        SessionLogService sessionLog,
        ILogger<VpnControlViewModel> logger,
        IUiDispatcher dispatcher)
    {
        _configurationService = configurationService;
        _profileService = profileService;
        _nodeCache = nodeCache;
        _vpnConnection = vpnConnection;
        _timerService = timerService;
        _sessionLog = sessionLog;
        _logger = logger;
        _dispatcher = dispatcher;

        _vpnConnection.ConnectionStateChanged += OnConnectionStateChanged;
        _vpnConnection.LogMessage += OnLogMessage;
        _timerService.DurationChanged += (_, duration) =>
        {
            _dispatcher.Post(() => ConnectionDuration = duration);
        };
    }

    public async Task InitializeAsync()
    {
        _savedConfig = await _configurationService.LoadAsync().ConfigureAwait(true);
        SplitTunnelEnabled = _savedConfig.SplitTunnelEnabled;

        if (string.IsNullOrWhiteSpace(_savedConfig.ServerUrl)
            || string.IsNullOrWhiteSpace(_savedConfig.Username)
            || string.IsNullOrWhiteSpace(_savedConfig.Password))
        {
            StatusMessage = "请先登录并保存账号密码";
            GatewayNodes.Clear();
            ProfileStatusMessage = "本地无完整凭证，请返回登录页";
            return;
        }

        await LoadGatewayNodesAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshNodes))]
    public async Task LoadGatewayNodesAsync()
    {
        if (_savedConfig == null || string.IsNullOrWhiteSpace(_savedConfig.ServerUrl))
        {
            _savedConfig = await _configurationService.LoadAsync().ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(_savedConfig?.ServerUrl))
        {
            return;
        }

        var serverUrl = _savedConfig.ServerUrl;
        var hadCache = false;

        var cached = await _nodeCache.TryLoadAsync(serverUrl).ConfigureAwait(true);
        if (cached is { Count: > 0 })
        {
            ApplyGatewayNodes(cached, preserveSelection: true);
            ProfileStatusMessage = $"已加载缓存 {GatewayNodes.Count} 个节点，正在更新…";
            if (!IsConnected)
            {
                StatusMessage = "请选择节点后连接";
            }

            hadCache = true;
            AddLog($"[Info] 已载入缓存节点 {GatewayNodes.Count} 个");
        }
        else
        {
            IsLoadingNodes = true;
            ProfileStatusMessage = "正在获取节点列表…";
            GatewayNodes.Clear();
        }

        try
        {
            var nodes = await _profileService
                .LoadProfileFromServerAsync(serverUrl)
                .ConfigureAwait(true);

            if (nodes.Count > 0)
            {
                ApplyGatewayNodes(nodes, preserveSelection: true);
                await _nodeCache.SaveAsync(serverUrl, nodes).ConfigureAwait(true);
                ProfileStatusMessage = $"已加载 {GatewayNodes.Count} 个节点";
                if (!IsConnected)
                {
                    StatusMessage = "请选择节点后连接";
                }

                AddLog($"[Info] 节点列表已更新（{GatewayNodes.Count}）");
            }
            else if (!hadCache)
            {
                SelectedGateway = null;
                GatewayNodes.Clear();
                ProfileStatusMessage = "未解析到节点，请点击「刷新」重试";
                StatusMessage = "无可用节点";
            }
            else
            {
                ProfileStatusMessage = $"在线列表为空，仍使用缓存 {GatewayNodes.Count} 个节点";
            }
        }
        catch (ProfileLoadException ex)
        {
            _logger.LogWarning(ex, "获取 profile.xml 失败");
            AddLog($"[Warning] {ex.Message}");
            if (hadCache)
            {
                ProfileStatusMessage = $"网络更新失败，仍使用缓存 {GatewayNodes.Count} 个节点";
                if (!IsConnected)
                {
                    StatusMessage = "请选择节点后连接";
                }
            }
            else
            {
                SelectedGateway = null;
                ProfileStatusMessage = ex.Message + " 可点击「刷新」重试";
                StatusMessage = "获取节点失败";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载节点列表异常");
            AddLog($"[Error] {ex.Message}");
            if (hadCache)
            {
                ProfileStatusMessage = $"网络更新失败，仍使用缓存 {GatewayNodes.Count} 个节点";
            }
            else
            {
                SelectedGateway = null;
                ProfileStatusMessage = $"获取节点列表失败: {ex.Message}。可点击「刷新」重试";
                StatusMessage = "获取节点失败";
            }
        }
        finally
        {
            IsLoadingNodes = false;
        }
    }

    private void ApplyGatewayNodes(IReadOnlyList<GatewayNode> nodes, bool preserveSelection)
    {
        var preferredAddress = preserveSelection && SelectedGateway != null
            ? SelectedGateway.Address
            : _savedConfig?.LastConnectedNodeAddress;
        var preferredName = preserveSelection && SelectedGateway != null
            ? SelectedGateway.Name
            : _savedConfig?.LastConnectedNodeName;

        GatewayNodes.Clear();
        foreach (var node in nodes)
        {
            GatewayNodes.Add(node);
        }

        if (GatewayNodes.Count == 0)
        {
            SelectedGateway = null;
            return;
        }

        GatewayNode? match = null;
        if (!string.IsNullOrWhiteSpace(preferredAddress))
        {
            match = GatewayNodes.FirstOrDefault(n =>
                string.Equals(n.Address, preferredAddress, StringComparison.OrdinalIgnoreCase));
        }

        if (match == null && !string.IsNullOrWhiteSpace(preferredName))
        {
            match = GatewayNodes.FirstOrDefault(n =>
                string.Equals(n.Name, preferredName, StringComparison.OrdinalIgnoreCase));
        }

        SelectedGateway = match ?? ResolvePreferredNode(GatewayNodes, _savedConfig!);
    }

    private bool CanRefreshNodes() => ControlsEditable && !IsLoadingNodes;

    [RelayCommand]
    private void Logout() => LogoutRequested?.Invoke();

    [RelayCommand]
    private void ShowLogs() => OpenLogsRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (_savedConfig == null
            || string.IsNullOrWhiteSpace(_savedConfig.ServerUrl)
            || string.IsNullOrWhiteSpace(_savedConfig.Username)
            || string.IsNullOrWhiteSpace(_savedConfig.Password))
        {
            StatusMessage = "请先登录并保存账号密码";
            return;
        }

        if (SelectedGateway == null || string.IsNullOrWhiteSpace(SelectedGateway.Address))
        {
            StatusMessage = "请先成功加载并选择节点";
            return;
        }

        IsConnecting = true;
        AddLog("[Info] 开始连接…");
        if (SplitTunnelEnabled)
        {
            AddLog("[Info] 分流模式：国内直连 / 其它走 VPN");
        }

        var gatewayAddress = SelectedGateway.Address;
        ActiveNodeName = SelectedGateway.Name;
        ActiveSplitLabel = SplitTunnelEnabled ? "分流" : "全局";

        _savedConfig.SplitTunnelEnabled = SplitTunnelEnabled;
        _savedConfig.LastConnectedNodeName = SelectedGateway.Name;
        _savedConfig.LastConnectedNodeAddress = gatewayAddress;
        await _configurationService.SaveAsync(_savedConfig).ConfigureAwait(true);

        try
        {
            await _vpnConnection.ConnectAsync(
                _savedConfig.ServerUrl,
                _savedConfig.Username,
                _savedConfig.Password,
                new VpnConnectOptions
                {
                    SplitTunnelEnabled = SplitTunnelEnabled,
                    GatewayNodeAddress = gatewayAddress,
                    SavedServerUrl = _savedConfig.ServerUrl,
                }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接异常");
            AddLog($"[Error] {ex.Message}");
            UpdateStatus(VpnConnectionState.Error, ex.Message);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanConnect() => !IsConnecting && !IsConnected;

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private void ToggleConnection()
    {
        if (CanDisconnect())
        {
            if (DisconnectCommand.CanExecute(null))
            {
                _ = DisconnectCommand.ExecuteAsync(null);
            }

            return;
        }

        if (ConnectCommand.CanExecute(null))
        {
            _ = ConnectCommand.ExecuteAsync(null);
        }
    }

    private bool CanToggleConnection() => CanConnect() || CanDisconnect();

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        try
        {
            await _vpnConnection.DisconnectAsync(_savedConfig?.ServerUrl).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "断开失败");
            AddLog($"[Error] 断开失败: {ex.Message}");
            StatusMessage = $"断开失败: {ex.Message}";
        }
        finally
        {
            _timerService.Stop();
            ConnectionDuration = "00:00:00";
            _networkMayNeedRestore = false;
            IsConnected = false;
            IsConnecting = false;
            if (ConnectionState != VpnConnectionState.Disconnected)
            {
                StatusMessage = "已断开";
                ConnectionState = VpnConnectionState.Disconnected;
            }

            DisconnectCommand.NotifyCanExecuteChanged();
            ToggleConnectionCommand.NotifyCanExecuteChanged();
            NotifyControlLockChanged();
        }
    }

    private bool CanDisconnect() =>
        IsConnected ||
        IsConnecting ||
        ConnectionState == VpnConnectionState.Disconnecting ||
        ConnectionState == VpnConnectionState.Reconnecting ||
        _networkMayNeedRestore ||
        _vpnConnection.NeedsNetworkRestore;

    partial void OnIsConnectingChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        LoadGatewayNodesCommand.NotifyCanExecuteChanged();
        NotifyControlLockChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        LoadGatewayNodesCommand.NotifyCanExecuteChanged();
        NotifyControlLockChanged();
        _ = PersistConnectionStateAsync(value);
    }

    partial void OnIsLoadingNodesChanged(bool value) =>
        LoadGatewayNodesCommand.NotifyCanExecuteChanged();

    partial void OnSplitTunnelEnabledChanged(bool value)
    {
        if (!SplitTunnelEditable || _savedConfig == null)
        {
            return;
        }

        _savedConfig.SplitTunnelEnabled = value;
        _ = _configurationService.SaveAsync(_savedConfig);
    }

    private void NotifyControlLockChanged()
    {
        OnPropertyChanged(nameof(ControlsEditable));
        OnPropertyChanged(nameof(SplitTunnelEditable));
        OnPropertyChanged(nameof(ShowActiveConnection));
        OnPropertyChanged(nameof(ShowNodePicker));
        OnPropertyChanged(nameof(ConnectionActionText));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    private async Task PersistConnectionStateAsync(bool connected)
    {
        if (_savedConfig == null)
        {
            return;
        }

        _savedConfig.LastWasConnected = connected;
        if (connected && SelectedGateway != null)
        {
            _savedConfig.LastConnectedNodeName = SelectedGateway.Name;
            _savedConfig.LastConnectedNodeAddress = SelectedGateway.Address;
            _savedConfig.SplitTunnelEnabled = SplitTunnelEnabled;
            ActiveNodeName = SelectedGateway.Name;
            ActiveSplitLabel = SplitTunnelEnabled ? "分流" : "全局";
        }

        try
        {
            await _configurationService.SaveAsync(_savedConfig).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存连接状态失败");
        }
    }

    private static GatewayNode ResolvePreferredNode(
        ObservableCollection<GatewayNode> nodes,
        ConnectionConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.LastConnectedNodeAddress))
        {
            var match = nodes.FirstOrDefault(n =>
                string.Equals(n.Address, config.LastConnectedNodeAddress, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(config.LastConnectedNodeName))
        {
            var match = nodes.FirstOrDefault(n =>
                string.Equals(n.Name, config.LastConnectedNodeName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return nodes[0];
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            UpdateStatus(e.State, e.Message);
            if (e.State == VpnConnectionState.Connected)
            {
                IsConnected = true;
                IsConnecting = false;
                _networkMayNeedRestore = true;
                _timerService.Start();
                if (SelectedGateway != null)
                {
                    ActiveNodeName = SelectedGateway.Name;
                    ActiveSplitLabel = SplitTunnelEnabled ? "分流" : "全局";
                }

                DisconnectCommand.NotifyCanExecuteChanged();
                ToggleConnectionCommand.NotifyCanExecuteChanged();
                NotifyControlLockChanged();
            }
            else if (e.State == VpnConnectionState.Disconnecting)
            {
                IsConnecting = false;
                StatusMessage = e.Message;
                DisconnectCommand.NotifyCanExecuteChanged();
                ToggleConnectionCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ConnectionActionText));
            }
            else if (e.State is VpnConnectionState.Disconnected or VpnConnectionState.Error)
            {
                IsConnected = false;
                IsConnecting = false;
                _timerService.Stop();
                _networkMayNeedRestore = _vpnConnection.NeedsNetworkRestore;
                DisconnectCommand.NotifyCanExecuteChanged();
                ToggleConnectionCommand.NotifyCanExecuteChanged();
                NotifyControlLockChanged();
            }
            else if (e.State is VpnConnectionState.Connecting or VpnConnectionState.Reconnecting)
            {
                IsConnecting = true;
                IsConnected = false;
                DisconnectCommand.NotifyCanExecuteChanged();
                ToggleConnectionCommand.NotifyCanExecuteChanged();
                NotifyControlLockChanged();
            }
        });
    }

    private void OnLogMessage(object? sender, LogMessageEventArgs e)
    {
        AddLog($"[{e.Timestamp:HH:mm:ss}] [{e.Level}] {e.Message}");
    }

    private void UpdateStatus(VpnConnectionState state, string message)
    {
        ConnectionState = state;
        StatusMessage = message;
        StatusIndicatorColor = state switch
        {
            VpnConnectionState.Connected => "#0F766E",
            VpnConnectionState.Connecting or VpnConnectionState.Reconnecting => "#D97706",
            VpnConnectionState.Error => "#DC2626",
            _ => "#64748B"
        };
    }

    private void AddLog(string entry) => _sessionLog.Append(entry);
}
