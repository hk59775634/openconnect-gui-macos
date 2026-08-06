using CommunityToolkit.Mvvm.ComponentModel;
using SslVpnClient.Services;

namespace SslVpnClient.ViewModels;

/// <summary>
/// 主窗口 ViewModel，负责页面导航与全局连接时长显示。
/// CurrentPage 直接绑定子 ViewModel，由 Avalonia DataTemplates 映射到视图。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private readonly ConnectionSetupViewModel _setupViewModel;
    private readonly VpnControlViewModel _controlViewModel;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _windowTitle = "OpenConnect Gui - SSL VPN";

    [ObservableProperty]
    private string _connectionDuration = string.Empty;

    [ObservableProperty]
    private bool _showConnectionDuration;

    public MainViewModel(
        ConfigurationService configurationService,
        ConnectionSetupViewModel setupViewModel,
        VpnControlViewModel controlViewModel)
    {
        _configurationService = configurationService;
        _setupViewModel = setupViewModel;
        _controlViewModel = controlViewModel;
        CurrentPage = _setupViewModel;

        _controlViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VpnControlViewModel.ConnectionDuration))
            {
                UpdateConnectionDuration(_controlViewModel.ConnectionDuration);
            }
        };

        _setupViewModel.LoginSucceeded += OnLoginSucceededAsync;
        _controlViewModel.LogoutRequested += OnLogoutRequested;
    }

    /// <summary>
    /// 应用启动：本地已有账号+密码则直接进入节点页并拉取 profile.xml。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (await _configurationService.HasSavedCredentialsAsync().ConfigureAwait(true))
        {
            await NavigateToControlAsync().ConfigureAwait(true);
        }
        else
        {
            await _setupViewModel.LoadSavedConfigAsync().ConfigureAwait(true);
            WindowTitle = "OpenConnect Gui - 登录";
        }
    }

    public async void NavigateToSetup()
    {
        await _setupViewModel.LoadSavedConfigAsync().ConfigureAwait(true);
        CurrentPage = _setupViewModel;
        WindowTitle = "OpenConnect Gui - 登录";
        ShowConnectionDuration = false;
    }

    public async void NavigateToControl()
    {
        await NavigateToControlAsync().ConfigureAwait(true);
    }

    private Task OnLoginSucceededAsync() => NavigateToControlAsync();

    private async Task NavigateToControlAsync()
    {
        CurrentPage = _controlViewModel;
        WindowTitle = "OpenConnect Gui - 节点连接";
        await _controlViewModel.InitializeAsync().ConfigureAwait(true);
    }

    private async void OnLogoutRequested()
    {
        try
        {
            if (_controlViewModel.IsConnected || _controlViewModel.IsConnecting)
            {
                await _controlViewModel.DisconnectCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            NavigateToSetup();
        }
        catch (Exception ex)
        {
            _controlViewModel.StatusMessage = $"返回登录失败: {ex.Message}";
        }
    }

    public void UpdateConnectionDuration(string duration)
    {
        ConnectionDuration = duration;
        ShowConnectionDuration = duration != "00:00:00";
    }

    public async void DisconnectOnExit()
    {
        if (_controlViewModel.IsConnected || _controlViewModel.IsConnecting)
        {
            await _controlViewModel.DisconnectCommand.ExecuteAsync(null).ConfigureAwait(false);
        }
    }
}
