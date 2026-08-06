namespace SslVpnClient.Models;

/// <summary>
/// 从 profile.xml 解析出的 VPN 网关节点。
/// </summary>
public class GatewayNode
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? UserGroup { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}
