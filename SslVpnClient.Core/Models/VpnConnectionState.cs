namespace SslVpnClient.Models;

/// <summary>
/// VPN 连接状态枚举。
/// </summary>
public enum VpnConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
    Disconnecting,
    Reconnecting
}
