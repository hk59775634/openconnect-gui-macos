using System.Net;
using Microsoft.Extensions.Logging;
using SslVpnClient.Models;

namespace SslVpnClient.Services;

public class VpnProfileService
{
    private const int TimeoutSeconds = 30;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly XmlProfileParser _parser;
    private readonly ILogger<VpnProfileService> _logger;

    public VpnProfileService(
        IHttpClientFactory httpClientFactory,
        XmlProfileParser parser,
        ILogger<VpnProfileService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _parser = parser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GatewayNode>> LoadProfileFromServerAsync(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new ProfileLoadException("请先在连接配置页保存接入地址。");
        }

        var baseUrl = serverUrl.TrimEnd('/');
        var profileUrl = $"{baseUrl}/profile.xml";
        _logger.LogInformation("正在下载 profile.xml: {Url}", profileUrl);

        try
        {
            using var client = _httpClientFactory.CreateClient("VpnProfile");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
            using var response = await client.GetAsync(profileUrl, cts.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ProfileLoadException("服务器未提供 profile.xml 配置文件 (404)。");
            }

            response.EnsureSuccessStatusCode();
            var xmlContent = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var nodes = _parser.ParseGatewayNodes(xmlContent);
            _logger.LogInformation("成功解析 {Count} 个网关节点", nodes.Count);
            return nodes;
        }
        catch (ProfileLoadException)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new ProfileLoadException("获取节点列表超时，请检查网络连接。");
        }
        catch (HttpRequestException)
        {
            throw new ProfileLoadException("无法连接到服务器获取节点列表，请检查地址和网络。");
        }
        catch (Exception ex) when (ex is not ProfileLoadException)
        {
            throw new ProfileLoadException($"获取节点列表失败: {ex.Message}");
        }
    }
}

public class ProfileLoadException : Exception
{
    public ProfileLoadException(string message) : base(message)
    {
    }
}
