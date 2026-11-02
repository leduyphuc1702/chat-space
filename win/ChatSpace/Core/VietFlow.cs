using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChatSpace.Kit;

namespace ChatSpace.Core;

/// VietFlow TMS account used to record a purchase order's cost from an invoice photo
/// (`/api/v1/purchase-cost/*`, Sanctum token from `/api/v1/auth/login`).
public sealed class VietFlow : Observable
{
    public static readonly VietFlow Shared = new();
    public const string DefaultBaseUrl = "https://app.vflow.vn/api/v1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private string _baseUrl;
    public string BaseUrl
    {
        get => Get(ref _baseUrl);
        set { Set(ref _baseUrl, value); Prefs.Set("vietflow.baseURL", value); }
    }
    private string? _userName;
    public string? UserName { get => Get(ref _userName); private set => Set(ref _userName, value); }
    private bool _isLoggedIn;
    public bool IsLoggedIn { get => Get(ref _isLoggedIn); private set => Set(ref _isLoggedIn, value); }

    private string? _token;

    private VietFlow()
    {
        _baseUrl = Prefs.GetString("vietflow.baseURL") ?? DefaultBaseUrl;
        _userName = Prefs.GetString("vietflow.userName");
        _token = DemoData.Enabled ? "demo" : SecretStore.Read("vietflow.token");
        _isLoggedIn = _token != null;
    }

    // MARK: Account

    public async Task Login(string email, string password)
    {
        if (DemoData.Enabled) { SetSession("demo", "Minh (demo)"); return; }
        var body = new JsonObject { ["email"] = email, ["password"] = password, ["device_name"] = "chatspace-" + Environment.MachineName };
        var data = await Request("POST", "auth/login", body: body, authorized: false);
        var json = JsonNode.Parse(data) as JsonObject;
        if (json?["token"]?.GetValue<string>() is not { } token) throw new VietFlowException("Phản hồi đăng nhập không hợp lệ.");
        var user = json["user"] as JsonObject;
        SetSession(token, user?.Str("name") ?? user?.Str("email") ?? email);
    }

    public void Logout()
    {
        if (!DemoData.Enabled) SecretStore.Delete("vietflow.token");
        Prefs.Remove("vietflow.userName");
        _token = null;
        UserName = null;
        IsLoggedIn = false;
    }

    private void SetSession(string token, string name)
    {
        if (!DemoData.Enabled)
        {
            SecretStore.Write("vietflow.token", token);
            Prefs.Set("vietflow.userName", name);
        }
        _token = token;
        UserName = name;
        IsLoggedIn = true;
    }

    // MARK: Purchase cost

    public async Task<VFPurchaseOrder> PurchaseOrder(string code)
    {
        if (DemoData.Enabled) return DemoVietFlow.PurchaseOrder(code);
        return Decode<VFPurchaseOrder>(await Request("GET", "purchase-cost/purchase-orders/" + Uri.EscapeDataString(code)));
    }

    private sealed record GiftCardsResponse(List<VFGiftCard> Batches);

    public async Task<IReadOnlyList<VFGiftCard>> GiftCards(string currency)
    {
        if (DemoData.Enabled) return DemoVietFlow.GiftCards;
        return Decode<GiftCardsResponse>(await Request("GET", "purchase-cost/gift-cards", new() { ["currency"] = currency })).Batches;
    }

    public async Task<VFPoolPreview> PoolPreview(string currency, double amount)
    {
        if (DemoData.Enabled) return DemoVietFlow.PoolPreview(amount);
        return Decode<VFPoolPreview>(await Request("GET", "purchase-cost/pool-preview",
            new() { ["currency"] = currency, ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture) }));
    }

    /// `dryRun` only computes the split; otherwise the cost is written to every item of the order.
    public async Task<VFCostResult> RecordCost(VFCostRequest entry, int purchaseOrderId, bool dryRun)
    {
        if (DemoData.Enabled) return DemoVietFlow.Record(entry, dryRun);
        var body = entry.Body();
        body["dry_run"] = dryRun;
        return Decode<VFCostResult>(await Request("POST", $"purchase-cost/purchase-orders/{purchaseOrderId}/record", body: body));
    }

    // MARK: HTTP

    private async Task<byte[]> Request(string method, string path, Dictionary<string, string>? query = null, JsonObject? body = null, bool authorized = true)
    {
        var b = BaseUrl.Trim('/', ' ');
        var url = b + "/" + path;
        if (query is { Count: > 0 }) url += "?" + string.Join("&", query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new VietFlowException("Địa chỉ máy chủ VietFlow không hợp lệ.");
        using var req = new HttpRequestMessage(new HttpMethod(method), uri);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authorized)
        {
            if (_token == null) throw VietFlowException.LoggedOut;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        var data = await resp.Content.ReadAsByteArrayAsync();
        var status = (int)resp.StatusCode;
        if (status is >= 200 and < 300) return data;
        if (resp.StatusCode == HttpStatusCode.Unauthorized && authorized)
        {
            Logout();
            throw VietFlowException.LoggedOut;
        }
        throw new VietFlowException(ErrorMessage(data, status));
    }

    private static string ErrorMessage(byte[] data, int status)
    {
        JsonObject? json = null;
        try { json = JsonNode.Parse(data) as JsonObject; } catch { }
        if (json?["errors"] is JsonObject errors)
        {
            foreach (var (_, v) in errors)
                if (v is JsonArray { Count: > 0 } a && a[0]?.GetValue<string>() is { } first) return first;
        }
        if (status == 403) return "Tài khoản VietFlow không có quyền sửa PO.";
        var m = json?.Str("message") ?? "";
        if (status == 404 && (m.Length == 0 || m.StartsWith("The route", StringComparison.Ordinal)))
            return "Máy chủ VietFlow chưa có chức năng nhập chi phí PO (bản vietflow-web mới chưa được deploy).";
        if (m.Length > 0) return m;
        return $"VietFlow trả lỗi {status}.";
    }

    private static T Decode<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Snake) ?? throw new VietFlowException("Phản hồi VietFlow không hợp lệ.");
}

public sealed class VietFlowException(string message) : Exception(message)
{
    public static VietFlowException LoggedOut => new("Phiên đăng nhập VietFlow đã hết, đăng nhập lại để tiếp tục.");
}

// MARK: - API types

public sealed record VFPurchaseOrder(int Id, string OrderCode, string? CustomerName, string CurrencyCode, double SellingExchangeRate,
    double? BaseRate, bool IsCancelled, List<VFPurchaseOrder.Item> Items)
{
    public sealed record Item(int Id, string ProductName, string? ProductVariant, bool IsFreeItem, int QuantityRequested, int QuantityPurchased,
        double SellingPriceForeign, double? CostPriceForeign, double? CostExchangeRate, string? OrderNumber);
}

public sealed record VFGiftCard(int Id, string CurrencyCode, double RemainingAmount, double BuyRate, string? Source);

public sealed record VFPoolPreview(List<VFPoolPreview.Allocation> Allocations, double WeightedRate, double TotalAvailable, double Shortfall, bool IsSufficient)
{
    public sealed record Allocation(int BatchId, double Amount, double Rate, string Source);
}

public enum VFFunding { Pool, GiftCard, Manual, Bank }

public static class VFFundingExt
{
    public static readonly VFFunding[] All = [VFFunding.Pool, VFFunding.GiftCard, VFFunding.Manual, VFFunding.Bank];
    public static string Raw(this VFFunding f) => f switch { VFFunding.Pool => "pool", VFFunding.GiftCard => "gift_card", VFFunding.Bank => "bank", _ => "manual" };
    public static string Label(this VFFunding f) => f switch { VFFunding.Pool => "Quỹ FIFO", VFFunding.GiftCard => "Gift Card", VFFunding.Bank => "Bank VN", _ => "Tỷ giá tay" };

    /// A bank rate is computed in ChatSpace, so VietFlow records it like a hand-typed rate.
    public static string ServerValue(this VFFunding f) => f == VFFunding.Bank ? VFFunding.Manual.Raw() : f.Raw();
}

public sealed record VFCostRequest
{
    public double TotalPaid { get; init; }
    public VFFunding Funding { get; init; }
    public double? ExchangeRate { get; init; }
    public string? OrderNumber { get; init; }
    public IReadOnlyList<int> GiftCardIds { get; init; } = [];
    public bool RemainderFromPool { get; init; }
    public IReadOnlyDictionary<int, int> Quantities { get; init; } = new Dictionary<int, int>();

    public JsonObject Body()
    {
        var b = new JsonObject { ["total_paid"] = TotalPaid, ["funding"] = Funding.ServerValue() };
        if (ExchangeRate is { } r) b["exchange_rate"] = r;
        if (!string.IsNullOrEmpty(OrderNumber)) b["order_number"] = OrderNumber;
        if (Funding == VFFunding.GiftCard)
        {
            b["gift_card_batch_ids"] = new JsonArray(GiftCardIds.Select(i => (JsonNode)i).ToArray());
            b["remainder_mode"] = RemainderFromPool ? "pool" : "manual";
        }
        if (Quantities.Count > 0)
        {
            var q = new JsonObject();
            foreach (var (k, v) in Quantities) q[k.ToString(CultureInfo.InvariantCulture)] = v;
            b["quantities"] = q;
        }
        return b;
    }
}

public sealed record VFCostResult(bool Committed, VFCostResult.Order PurchaseOrder, string Funding, double TotalPaid, double RecordedTotal,
    double RoundingDrift, double SellingTotal, double ProfitForeign, double? CostVnd, double? AverageRate, List<VFCostResult.Src> Sources,
    List<VFCostResult.Item> Items)
{
    public sealed record Order(int Id, string OrderCode, string? CustomerName, string CurrencyCode);
    public sealed record Src(int BatchId, string Source, double Rate, bool IsGiftCard, double Amount);
    public sealed record Item(int Id, string ProductName, string? ProductVariant, bool IsFreeItem, int Quantity, double SellingTotal,
        double UnitCost, double LineTotal, double ProfitForeign, double? ExchangeRate, double? CostVnd);
}

/// Per-user secret storage (macOS Keychain): DPAPI-encrypted files under the app data folder.
public static class SecretStore
{
    private static string PathFor(string account) => Path.Combine(Platforms.BaseDirectory, "secrets", account + ".bin");

    public static string? Read(string account)
    {
        try
        {
            var p = PathFor(account);
            if (!File.Exists(p)) return null;
            var bytes = File.ReadAllBytes(p);
#if WINDOWS
            bytes = System.Security.Cryptography.ProtectedData.Unprotect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
#endif
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    public static void Write(string account, string value)
    {
        try
        {
            var p = PathFor(account);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var bytes = Encoding.UTF8.GetBytes(value);
#if WINDOWS
            bytes = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
#endif
            File.WriteAllBytes(p, bytes);
        }
        catch { }
    }

    public static void Delete(string account)
    {
        try { File.Delete(PathFor(account)); } catch { }
    }
}

// MARK: - Demo

/// Offline stand-in for the API under CHATSPACE_DEMO, mirroring the server's split rule.
public static class DemoVietFlow
{
    public static VFPurchaseOrder PurchaseOrder(string code) =>
        new(82, code.ToUpperInvariant(), "VF MAN NHI", DemoData.Currency, 27_000, 26_420, false,
        [
            new(1, "Victoria's Secret Ivory Satin Lace Trim Slip Nightgown L", "Ivory / L", false, 1, 0, 15, null, null, null),
        ]);

    public static readonly IReadOnlyList<VFGiftCard> GiftCards =
    [
        new(41, DemoData.Currency, 10, 25_500, "GC Amazon"),
        new(57, DemoData.Currency, 120.5, 25_900, "GC eBay"),
    ];

    public static VFPoolPreview PoolPreview(double amount)
    {
        const double available = 500.0;
        var take = Math.Min(amount, available);
        return new([new(12, take, 26_000, "PTM 4004")], 26_000, available, Math.Max(0, amount - available), amount <= available);
    }

    public static VFCostResult Record(VFCostRequest r, bool dryRun)
    {
        var po = PurchaseOrder("POVFMANNHI0082");
        var item = po.Items[0];
        var qty = r.Quantities.TryGetValue(item.Id, out var q) ? q : 1;
        var selling = item.SellingPriceForeign * qty;
        double? rate = dryRun ? null : r.Funding switch
        {
            VFFunding.Manual or VFFunding.Bank => r.ExchangeRate ?? 26_350,
            VFFunding.Pool => 26_000,
            _ => GiftCards.Count > 0
                ? (Math.Min(GiftCards[0].RemainingAmount, r.TotalPaid) * GiftCards[0].BuyRate
                   + (r.TotalPaid - Math.Min(GiftCards[0].RemainingAmount, r.TotalPaid)) * (r.ExchangeRate ?? 26_350)) / r.TotalPaid
                : 26_000,
        };
        double? cost = rate is { } rr ? Math.Round(r.TotalPaid * rr, MidpointRounding.AwayFromZero) : null;
        List<VFCostResult.Src> sources = dryRun || r.Funding == VFFunding.Manual || r.Funding == VFFunding.Bank ? []
            : r.Funding == VFFunding.Pool ? [new(12, "PTM 4004", 26_000, false, r.TotalPaid)]
            : [new(41, "GC Amazon", 25_500, true, Math.Min(10, r.TotalPaid))];
        return new(!dryRun, new(po.Id, po.OrderCode, po.CustomerName, DemoData.Currency), r.Funding.ServerValue(), r.TotalPaid, r.TotalPaid, 0,
            selling, selling - r.TotalPaid, cost, rate, sources,
            [new(item.Id, item.ProductName, item.ProductVariant, false, qty, selling, r.TotalPaid / qty, r.TotalPaid, selling - r.TotalPaid, rate, cost)]);
    }
}
