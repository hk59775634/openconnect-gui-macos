namespace SslVpnClient.Models;

/// <summary>
/// 用户保存的 VPN 连接配置。
/// </summary>
public class ConnectionConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool SplitTunnelEnabled { get; set; }
    public string LastConnectedNodeName { get; set; } = string.Empty;
    public string LastConnectedNodeAddress { get; set; } = string.Empty;
    public bool LastWasConnected { get; set; }
}
