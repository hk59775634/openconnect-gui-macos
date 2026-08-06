using System.Text.Json;
using Microsoft.Extensions.Logging;
using SslVpnClient.Abstractions;
using SslVpnClient.Models;

namespace SslVpnClient.Services;

/// <summary>
/// 本地连接配置的读写服务。账号明文保存，密码经 <see cref="IPasswordProtector"/> 保护后持久化。
/// </summary>
public class ConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private readonly IPasswordProtector _passwordProtector;
    private readonly string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConfigurationService(ILogger<ConfigurationService> logger, IPasswordProtector passwordProtector)
    {
        _logger = logger;
        _passwordProtector = passwordProtector;
        _configPath = AppPaths.ConnectionConfigPath;
    }

    /// <summary>
    /// 将接入地址规范化为含协议前缀的 URL。
    /// </summary>
    public static string NormalizeServerUrl(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        var trimmed = address.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return "https://" + trimmed;
    }

    /// <summary>
    /// 异步保存连接配置到本地 JSON 文件。
    /// </summary>
    public async Task SaveAsync(ConnectionConfig config)
    {
        config.ServerUrl = NormalizeServerUrl(config.ServerUrl);
        var stored = new StoredConnectionConfig
        {
            ServerUrl = config.ServerUrl,
            Username = config.Username,
            PasswordProtected = ProtectPassword(config.Password),
            SplitTunnelEnabled = config.SplitTunnelEnabled,
            LastConnectedNodeName = config.LastConnectedNodeName,
            LastConnectedNodeAddress = config.LastConnectedNodeAddress,
            LastWasConnected = config.LastWasConnected
        };
        var json = JsonSerializer.Serialize(stored, JsonOptions);
        await File.WriteAllTextAsync(_configPath, json).ConfigureAwait(false);
        _logger.LogInformation("配置已保存至 {Path}", _configPath);
    }

    /// <summary>
    /// 异步从本地 JSON 文件加载连接配置。
    /// </summary>
    public async Task<ConnectionConfig> LoadAsync()
    {
        if (!File.Exists(_configPath))
        {
            _logger.LogDebug("配置文件不存在: {Path}", _configPath);
            return new ConnectionConfig();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath).ConfigureAwait(false);
            var stored = JsonSerializer.Deserialize<StoredConnectionConfig>(json, JsonOptions);
            if (stored is null)
            {
                return new ConnectionConfig();
            }

            return new ConnectionConfig
            {
                ServerUrl = stored.ServerUrl ?? string.Empty,
                Username = stored.Username ?? string.Empty,
                Password = UnprotectPassword(stored.PasswordProtected, stored.Password),
                SplitTunnelEnabled = stored.SplitTunnelEnabled,
                LastConnectedNodeName = stored.LastConnectedNodeName ?? string.Empty,
                LastConnectedNodeAddress = stored.LastConnectedNodeAddress ?? string.Empty,
                LastWasConnected = stored.LastWasConnected
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取配置文件失败");
            return new ConnectionConfig();
        }
    }

    /// <summary>
    /// 是否已本地保存完整登录凭证（服务器地址 + 账号 + 密码）。
    /// </summary>
    public async Task<bool> HasSavedCredentialsAsync()
    {
        var config = await LoadAsync().ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(config.ServerUrl)
               && !string.IsNullOrWhiteSpace(config.Username)
               && !string.IsNullOrWhiteSpace(config.Password);
    }

    /// <summary>
    /// 清除本地保存的账号密码与配置（退出登录）。
    /// </summary>
    public Task ClearAsync()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                File.Delete(_configPath);
                _logger.LogInformation("已清除本地配置: {Path}", _configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清除本地配置失败");
            throw;
        }

        return Task.CompletedTask;
    }

    private string ProtectPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return string.Empty;
        }

        return _passwordProtector.Protect(password);
    }

    private string UnprotectPassword(string? passwordProtected, string? legacyPlainPassword)
    {
        if (!string.IsNullOrEmpty(passwordProtected))
        {
            try
            {
                return _passwordProtector.Unprotect(passwordProtected);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解密密码失败，将要求用户重新登录");
                return string.Empty;
            }
        }

        return legacyPlainPassword ?? string.Empty;
    }

    private sealed class StoredConnectionConfig
    {
        public string ServerUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string PasswordProtected { get; set; } = string.Empty;
        public string? Password { get; set; }
        public bool SplitTunnelEnabled { get; set; }
        public string LastConnectedNodeName { get; set; } = string.Empty;
        public string LastConnectedNodeAddress { get; set; } = string.Empty;
        public bool LastWasConnected { get; set; }
    }
}
