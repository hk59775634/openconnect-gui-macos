namespace SslVpnClient.Models;

/// <summary>门户账户信息（www.vps000.org /api/userinfo）。</summary>
public sealed class PortalUserInfo
{
    public int Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public int VipType { get; init; }
    public DateTime? EndTime { get; init; }

    public bool IsExpired => EndTime is null || EndTime.Value <= DateTime.Now;

    public string EndTimeText =>
        EndTime is null ? "—" : EndTime.Value.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>可购套餐（/api/products）。</summary>
public sealed class PortalProduct
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal AlipayPrice { get; init; }
    public decimal PaypalPrice { get; init; }

    public string DisplayName =>
        $"{Name}  ¥{AlipayPrice:0.##} / ${PaypalPrice:0.##}";
}

/// <summary>下单结果（/api/order）。</summary>
public sealed class PortalOrderResult
{
    public string OrderNo { get; init; } = string.Empty;
    public string PayUrl { get; init; } = string.Empty;
}

public enum PortalPayType
{
    Alipay = 1,
    Paypal = 2
}
