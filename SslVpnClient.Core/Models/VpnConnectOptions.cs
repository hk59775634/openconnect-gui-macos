namespace SslVpnClient.Models;

/// <summary>
/// VPN 连接选项。
/// </summary>
public class VpnConnectOptions
{
    public bool SplitTunnelEnabled { get; set; }
    public string? GatewayNodeAddress { get; set; }
    public string? SavedServerUrl { get; set; }
}
