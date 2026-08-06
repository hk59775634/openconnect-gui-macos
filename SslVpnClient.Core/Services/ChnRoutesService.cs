using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace SslVpnClient.Services;

/// <summary>
/// 下载并缓存 chnroutes-v4，供 macOS / 跨平台智能分流使用。
/// </summary>
public sealed class ChnRoutesService
{
    public const string DefaultCdnUrl =
        "https://cdn.jsdelivr.net/gh/hk59775634/chnroutes@main/chnroutes-v4";

    private const int MinCidrCount = 100;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChnRoutesService> _logger;
    private readonly string _cachePath;

    public ChnRoutesService(IHttpClientFactory httpClientFactory, ILogger<ChnRoutesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        var dir = Path.Combine(AppPaths.GetConfigDirectory(), "routing");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "chnroutes-v4");
    }

    public string CachePath => _cachePath;

    /// <summary>
    /// 确保本地有可用的 chnroutes（优先缓存，过期或不存在则下载）。
    /// </summary>
    public async Task<string> EnsureIPv4RoutesAsync(CancellationToken cancellationToken = default)
    {
        if (await IsCacheValidAsync().ConfigureAwait(false))
        {
            _logger.LogInformation("使用本地 chnroutes 缓存: {Path}", _cachePath);
            return _cachePath;
        }

        _logger.LogInformation("正在下载 chnroutes-v4…");
        try
        {
            using var client = _httpClientFactory.CreateClient("ChnRoutes");
            using var response = await client.GetAsync(DefaultCdnUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var count = CountCidrs(text);
            if (count < MinCidrCount)
            {
                throw new InvalidOperationException($"chnroutes 条目过少 ({count})");
            }

            await File.WriteAllTextAsync(_cachePath, text, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("chnroutes 已保存: {Count} 条 → {Path}", count, _cachePath);
            return _cachePath;
        }
        catch (Exception ex) when (File.Exists(_cachePath))
        {
            _logger.LogWarning(ex, "下载失败，回退本地缓存");
            if (await IsCacheValidAsync().ConfigureAwait(false))
            {
                return _cachePath;
            }

            throw;
        }
    }

    private async Task<bool> IsCacheValidAsync()
    {
        if (!File.Exists(_cachePath))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(_cachePath);
            // 7 天内视为新鲜；过期仍可用但会尝试刷新
            var text = await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false);
            return CountCidrs(text) >= MinCidrCount;
        }
        catch
        {
            return false;
        }
    }

    private static int CountCidrs(string text)
    {
        var n = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#'))
            {
                continue;
            }

            if (t.Contains('/'))
            {
                n++;
            }
        }

        return n;
    }
}
