using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SslVpnClient.Services;

namespace SslVpnClient.ViewModels;

public partial class ConnectionSetupViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private readonly VpnProfileService _profileService;
    private readonly GatewayNodeCacheService _nodeCache;

    public event Func<Task>? LoginSucceeded;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isPasswordVisible;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ConnectionSetupViewModel(
        ConfigurationService configurationService,
        VpnProfileService profileService,
        GatewayNodeCacheService nodeCache)
    {
        _configurationService = configurationService;
        _profileService = profileService;
        _nodeCache = nodeCache;
    }

    public async Task LoadSavedConfigAsync()
    {
        var config = await _configurationService.LoadAsync().ConfigureAwait(true);
        ServerUrl = config.ServerUrl;
        Username = config.Username;
        Password = config.Password;
    }

    [RelayCommand]
    private void NormalizeServerUrl()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            return;
        }

        ServerUrl = ConfigurationService.NormalizeServerUrl(ServerUrl);
    }

    [RelayCommand]
    private void TogglePasswordVisibility() => IsPasswordVisible = !IsPasswordVisible;

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl))
        {
            StatusMessage = "请输入服务器地址";
            return;
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            StatusMessage = "请输入账号";
            return;
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "请输入密码";
            return;
        }

        IsBusy = true;
        StatusMessage = "正在保存并获取节点列表…";

        try
        {
            var config = new Models.ConnectionConfig
            {
                ServerUrl = ConfigurationService.NormalizeServerUrl(ServerUrl),
                Username = Username.Trim(),
                Password = Password
            };

            await _configurationService.SaveAsync(config).ConfigureAwait(true);
            ServerUrl = config.ServerUrl;

            try
            {
                var nodes = await _profileService
                    .LoadProfileFromServerAsync(config.ServerUrl)
                    .ConfigureAwait(true);

                if (nodes.Count == 0)
                {
                    var cachedEmptyFallback = await _nodeCache.TryLoadAsync(config.ServerUrl).ConfigureAwait(true);
                    if (cachedEmptyFallback is { Count: > 0 })
                    {
                        StatusMessage = $"在线列表为空，使用缓存 {cachedEmptyFallback.Count} 个节点进入…";
                        await RaiseLoginSucceededAsync().ConfigureAwait(true);
                        return;
                    }

                    StatusMessage = "profile.xml 中未解析到任何节点，请检查服务器配置后重试。";
                    return;
                }

                await _nodeCache.SaveAsync(config.ServerUrl, nodes).ConfigureAwait(true);
                StatusMessage = $"已加载 {nodes.Count} 个节点，正在进入…";
                await RaiseLoginSucceededAsync().ConfigureAwait(true);
            }
            catch (ProfileLoadException ex)
            {
                var cached = await _nodeCache.TryLoadAsync(config.ServerUrl).ConfigureAwait(true);
                if (cached is { Count: > 0 })
                {
                    StatusMessage = $"网络异常，使用缓存 {cached.Count} 个节点进入…";
                    await RaiseLoginSucceededAsync().ConfigureAwait(true);
                    return;
                }

                StatusMessage = ex.Message + " 请检查地址后重试。";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"登录失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RaiseLoginSucceededAsync()
    {
        if (LoginSucceeded is not null)
        {
            await LoginSucceeded.Invoke().ConfigureAwait(true);
        }
    }
}
