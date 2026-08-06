using SslVpnClient.Models;

namespace SslVpnClient.Services;

/// <summary>
/// 按网站订购规则过滤可选套餐（不改后端；对齐 OrderController / ApiController 限制）。
/// 产品 device 元数据来自 Produce::getProduceList（API 未返回 device）。
/// </summary>
public static class PortalProductFilter
{
    /// <summary>Pro 专业版 vip_type。</summary>
    public const int ProVipType = 2;

    /// <summary>体验包（限购）。</summary>
    public const int TrialVipType = 2511;

    /// <summary>4K/VR 套餐 id。</summary>
    private static readonly HashSet<int> FourKVipTypes = [6, 7, 8, 9];

    public static int DeviceSlots(int productId) => productId == ProVipType ? 5 : 1;

    public static bool IsProProduct(int productId) => productId == ProVipType;

    public static bool IsTrialProduct(int productId) => productId == TrialVipType;

    public static bool IsFourKProduct(int productId) => FourKVipTypes.Contains(productId);

    /// <summary>
    /// 是否 Pro 账户：API 的 vip_type==2；或未过期且当前套餐名/id 指向 Pro。
    /// </summary>
    public static bool IsProAccount(PortalUserInfo user) =>
        user.VipType == ProVipType;

    public static IReadOnlyList<PortalProduct> FilterForUser(
        IEnumerable<PortalProduct> all,
        PortalUserInfo user)
    {
        var list = all
            .Where(p => p.Id > 0 && !string.IsNullOrWhiteSpace(p.Name))
            .ToList();

        var expired = user.IsExpired;
        var isPro = IsProAccount(user);

        IEnumerable<PortalProduct> filtered;
        if (expired)
        {
            // 账期结束：可升级/降级（含 Pro ↔ 个人），体验包可买
            filtered = list;
        }
        else if (isPro)
        {
            // Pro 有效期内：仅续费 Pro（网站：到期前无法订个人版）
            filtered = list.Where(p => IsProProduct(p.Id));
        }
        else
        {
            // 个人/其它有效会员：同档 device=1 续费；不可直接升 Pro（需到期后）
            // 体验包对已有会员隐藏（网站对多数有效会员也不再主推）
            filtered = list.Where(p =>
                !IsProProduct(p.Id)
                && !IsTrialProduct(p.Id)
                && DeviceSlots(p.Id) == 1);
        }

        return filtered.ToList();
    }

    public static string DescribeFilter(PortalUserInfo user)
    {
        if (user.IsExpired)
        {
            return "账期已结束，可选择任意套餐（含升级/降级）";
        }

        if (IsProAccount(user))
        {
            return "Pro 有效期内仅可续费 Pro；升级/降级请等到期或新注册账号";
        }

        return "会员有效期内仅可续费同档个人套餐；升级 Pro 请等账期结束后再订购";
    }
}
