using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SslVpnClient.Models;

namespace SslVpnClient.Services;

/// <summary>
/// 门户账户 API（www.vps000.org），与 VPN 节点地址无关。
/// 不修改后端；仅调用既有 /api/* 接口。
/// </summary>
public sealed class PortalAccountService
{
    public const string DefaultBaseUrl = "https://www.vps000.org";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PortalAccountService> _logger;

    public PortalAccountService(
        IHttpClientFactory httpClientFactory,
        ILogger<PortalAccountService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PortalUserInfo> GetUserInfoAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var root = await PostFormAsync(
            "api/userinfo",
            new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password
            },
            cancellationToken).ConfigureAwait(false);

        EnsureSuccess(root, "获取账户信息失败");
        var data = root.GetProperty("data");
        return ParseUser(data);
    }

    public async Task<IReadOnlyList<PortalProduct>> GetProductsAsync(
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client
            .GetAsync("api/products", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = doc.RootElement;
        EnsureSuccess(root, "获取套餐列表失败");

        var list = new List<PortalProduct>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                list.Add(new PortalProduct
                {
                    Id = item.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    AlipayPrice = ReadDecimal(item, "alipay_price"),
                    PaypalPrice = ReadDecimal(item, "paypal_price")
                });
            }
        }

        return list;
    }

    public async Task<PortalOrderResult> CreateOrderAsync(
        int userId,
        int productId,
        PortalPayType payType,
        int month = 1,
        CancellationToken cancellationToken = default)
    {
        var root = await PostFormAsync(
            "api/order",
            new Dictionary<string, string>
            {
                ["user_id"] = userId.ToString(),
                ["product_id"] = productId.ToString(),
                ["month"] = Math.Max(1, month).ToString(),
                ["pay_type"] = ((int)payType).ToString()
            },
            cancellationToken).ConfigureAwait(false);

        EnsureSuccess(root, "下单失败");
        var data = root.GetProperty("data");
        return new PortalOrderResult
        {
            OrderNo = data.TryGetProperty("order_no", out var no) ? no.GetString() ?? "" : "",
            PayUrl = data.TryGetProperty("pay_url", out var url) ? url.GetString() ?? "" : ""
        };
    }

    public async Task<bool> IsOrderPaidAsync(
        int userId,
        string orderNo,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var url =
            $"api/order-status?user_id={Uri.EscapeDataString(userId.ToString())}&order_no={Uri.EscapeDataString(orderNo)}";
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 200)
        {
            return false;
        }

        if (!root.TryGetProperty("data", out var data))
        {
            return false;
        }

        return data.TryGetProperty("pay_status", out var status) && status.GetInt32() == 1;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("PortalApi");
        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri(DefaultBaseUrl.TrimEnd('/') + "/");
        }

        return client;
    }

    private async Task<JsonElement> PostFormAsync(
        string relativeUrl,
        IDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var content = new FormUrlEncodedContent(form);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var response = await client
            .PostAsync(relativeUrl, content, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Portal API {Url} HTTP {Status}: {Body}", relativeUrl, (int)response.StatusCode, Truncate(body));
            throw new PortalApiException($"门户接口 HTTP {(int)response.StatusCode}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Portal API 非 JSON: {Body}", Truncate(body));
            throw new PortalApiException("门户接口返回格式异常");
        }
    }

    private static void EnsureSuccess(JsonElement root, string fallback)
    {
        if (root.TryGetProperty("code", out var codeEl))
        {
            var code = codeEl.GetInt32();
            if (code == 200)
            {
                return;
            }

            var msg = root.TryGetProperty("msg", out var msgEl)
                ? msgEl.GetString()
                : null;
            throw new PortalApiException(string.IsNullOrWhiteSpace(msg) ? fallback : msg!);
        }

        throw new PortalApiException(fallback);
    }

    private static PortalUserInfo ParseUser(JsonElement data)
    {
        DateTime? end = null;
        if (data.TryGetProperty("end_time", out var endEl))
        {
            var raw = endEl.GetString();
            if (!string.IsNullOrWhiteSpace(raw)
                && DateTime.TryParse(raw, out var parsed))
            {
                end = parsed;
            }
        }

        return new PortalUserInfo
        {
            Id = data.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            Username = data.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
            Email = data.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
            VipType = data.TryGetProperty("vip_type", out var v) ? v.GetInt32() : 0,
            EndTime = end
        };
    }

    private static decimal ReadDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var el))
        {
            return 0;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : el.GetDouble() is var x ? (decimal)x : 0,
            JsonValueKind.String => decimal.TryParse(el.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }

    private static string Truncate(string s) =>
        s.Length <= 200 ? s : s[..200] + "…";
}

public sealed class PortalApiException : Exception
{
    public PortalApiException(string message) : base(message)
    {
    }
}
