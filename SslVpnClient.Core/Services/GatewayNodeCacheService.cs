using System.Text.Json;
using Microsoft.Extensions.Logging;
using SslVpnClient.Models;

namespace SslVpnClient.Services;

public sealed class GatewayNodeCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<GatewayNodeCacheService> _logger;
    private readonly string _cachePath;

    public GatewayNodeCacheService(ILogger<GatewayNodeCacheService> logger)
    {
        _logger = logger;
        _cachePath = Path.Combine(AppPaths.GetConfigDirectory(), "gateway-nodes-cache.json");
    }

    public async Task<IReadOnlyList<GatewayNode>?> TryLoadAsync(string serverUrl)
    {
        var key = NormalizeKey(serverUrl);
        if (string.IsNullOrEmpty(key) || !File.Exists(_cachePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false);
            var store = JsonSerializer.Deserialize<CacheStore>(json, JsonOptions);
            if (store?.Entries == null ||
                !store.Entries.TryGetValue(key, out var entry) ||
                entry.Nodes == null ||
                entry.Nodes.Count == 0)
            {
                return null;
            }

            return entry.Nodes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取节点缓存失败");
            return null;
        }
    }

    public async Task SaveAsync(string serverUrl, IReadOnlyList<GatewayNode> nodes)
    {
        var key = NormalizeKey(serverUrl);
        if (string.IsNullOrEmpty(key) || nodes.Count == 0)
        {
            return;
        }

        try
        {
            var store = await LoadStoreAsync().ConfigureAwait(false);
            store.Entries[key] = new CacheEntry
            {
                UpdatedUtc = DateTime.UtcNow,
                Nodes = nodes.Select(n => new GatewayNode
                {
                    Name = n.Name,
                    Address = n.Address,
                    UserGroup = n.UserGroup
                }).ToList()
            };

            var json = JsonSerializer.Serialize(store, JsonOptions);
            await File.WriteAllTextAsync(_cachePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存节点缓存失败");
        }
    }

    private async Task<CacheStore> LoadStoreAsync()
    {
        if (!File.Exists(_cachePath))
        {
            return new CacheStore();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CacheStore>(json, JsonOptions) ?? new CacheStore();
        }
        catch
        {
            return new CacheStore();
        }
    }

    private static string NormalizeKey(string serverUrl) =>
        ConfigurationService.NormalizeServerUrl(serverUrl).TrimEnd('/').ToLowerInvariant();

    private sealed class CacheStore
    {
        public Dictionary<string, CacheEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CacheEntry
    {
        public DateTime UpdatedUtc { get; set; }
        public List<GatewayNode> Nodes { get; set; } = new();
    }
}
