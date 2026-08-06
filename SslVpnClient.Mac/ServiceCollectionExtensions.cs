using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SslVpnClient.Abstractions;
using SslVpnClient.Mac.Services;
using SslVpnClient.Mac.Vpn;
using SslVpnClient.Services;
using SslVpnClient.ViewModels;

namespace SslVpnClient.Mac;

/// <summary>
/// 依赖注入容器配置。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddHttpClient("VpnProfile", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenConnectGui/2.0-macOS");
        });
        services.AddHttpClient("ChnRoutes", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenConnectGui/2.0-macOS");
        });

        services.AddSingleton<IPasswordProtector, AesPasswordProtector>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton<GatewayNodeCacheService>();
        services.AddSingleton<SessionLogService>();
        services.AddSingleton<XmlProfileParser>();
        services.AddSingleton<VpnProfileService>();
        services.AddSingleton<ChnRoutesService>();
        services.AddSingleton<VpnConnectionTimerService>();
        services.AddSingleton<IVpnConnection, OpenConnectCliVpnConnection>();

        services.AddSingleton<ConnectionSetupViewModel>();
        services.AddSingleton<VpnControlViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
