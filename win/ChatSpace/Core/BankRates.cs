using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatSpace.Core;

public enum VNBank { VCB, TCB }

public static class VNBankExt
{
    public static readonly VNBank[] All = [VNBank.VCB, VNBank.TCB];
    public static string Raw(this VNBank b) => b == VNBank.VCB ? "VCB" : "TCB";
    public static VNBank? FromRaw(string? raw) => All.Cast<VNBank?>().FirstOrDefault(b => b!.Value.Raw() == raw);
}

/// The three columns of a Vietnamese bank's rate board, numbered the way the team calls them.
public enum BankRateColumn { CashBuy = 1, TransferBuy = 2, Sell = 3 }

public static class BankRateColumnExt
{
    public static readonly BankRateColumn[] All = [BankRateColumn.CashBuy, BankRateColumn.TransferBuy, BankRateColumn.Sell];
    public static string Label(this BankRateColumn c) => c switch
    {
        BankRateColumn.CashBuy => "Mua TM",
        BankRateColumn.TransferBuy => "Mua CK",
        _ => "Bán",
    };
    public static BankRateColumn? FromRaw(int raw) => All.Cast<BankRateColumn?>().FirstOrDefault(c => (int)c!.Value == raw);
}

public sealed record BankRateQuote(VNBank Bank, string Currency, DateTimeOffset Day, double? CashBuy, double? TransferBuy, double? Sell)
{
    public double? Value(BankRateColumn c) => c switch
    {
        BankRateColumn.CashBuy => CashBuy,
        BankRateColumn.TransferBuy => TransferBuy,
        _ => Sell,
    };
}

/// Published VND rate boards of Vietcombank and Techcombank, by day.
public static class BankRates
{
    private static readonly ConcurrentDictionary<string, BankRateQuote> Cache = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Calendar: gregorian, Asia/Ho_Chi_Minh. Vietnam has no daylight saving, so a fixed +07:00 offset is that zone.
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    /// The day an instant falls on in Vietnam, as (year, month, day).
    public static (int Year, int Month, int Day) Parts(DateTimeOffset d)
    {
        var l = d.ToOffset(Offset);
        return (l.Year, l.Month, l.Day);
    }

    /// `calendar.date(from: DateComponents(year:month:day:))`: midnight of that day in Vietnam, or null when it does not exist.
    public static DateTimeOffset? Date(int year, int month, int day)
    {
        if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateTimeOffset(year, month, day, 0, 0, 0, Offset);
    }

    public static DateTimeOffset StartOfDay(DateTimeOffset d)
    {
        var (y, m, day) = Parts(d);
        return new DateTimeOffset(y, m, day, 0, 0, 0, Offset);
    }

    public static string DayString(DateTimeOffset d)
    {
        var (y, m, day) = Parts(d);
        return $"{y:D4}-{m:D2}-{day:D2}";
    }

    /// Walks back up to a week when the bank has no board for the day (holidays, a day not published yet).
    public static async Task<BankRateQuote> Quote(VNBank bank, string currency, DateTimeOffset date)
    {
        var key = $"{bank.Raw()}|{currency}|{DayString(date)}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (DemoData.Enabled)
        {
            // Demo mode never leaves the machine.
            Dictionary<string, double[]> boards = bank == VNBank.VCB
                ? new() { ["USD"] = [25_580, 25_610, 25_990], ["EUR"] = [28_141.01, 28_425.26, 29_625.48], ["GBP"] = [33_752.11, 34_093.04, 35_185.92] }
                : new() { ["USD"] = [25_606, 25_620, 25_990], ["EUR"] = [28_272, 28_488, 29_571], ["GBP"] = [33_854, 34_244, 35_179] };
            if (!boards.TryGetValue(currency, out var r))
                throw new VietFlowException($"{bank.Raw()} không có tỷ giá {currency} quanh ngày {DayString(date)}.");
            var demo = new BankRateQuote(bank, currency, date, r[0], r[1], r[2]);
            Cache[key] = demo;
            return demo;
        }
        var now = DateTimeOffset.UtcNow;
        var day = StartOfDay(date < now ? date : now);
        for (var i = 0; i < 7; i++)
        {
            if (await Fetch(bank, currency, day) is { } q)
            {
                Cache[key] = q;
                return q;
            }
            day = StartOfDay(day.AddDays(-1));
        }
        throw new VietFlowException($"{bank.Raw()} không có tỷ giá {currency} quanh ngày {DayString(date)}.");
    }

    private static async Task<BankRateQuote?> Fetch(VNBank bank, string currency, DateTimeOffset day)
    {
        switch (bank)
        {
            case VNBank.VCB:
            {
                var json = await Get($"https://www.vietcombank.com.vn/api/exchangerates?date={DayString(day)}");
                if (json["Data"] is not JsonArray rows) return null;
                var row = rows.OfType<JsonObject>().FirstOrDefault(o => StringOf(o["currencyCode"]) == currency);
                if (row == null) return null;
                return new BankRateQuote(bank, currency, day, Number(row["cash"]), Number(row["transfer"]), Number(row["sell"]));
            }
            default:
            {
                var json = await Get($"https://techcombank.com/content/techcombank/web/vn/vi/cong-cu-tien-ich/ty-gia/_jcr_content.exchange-rates.{DayString(day)}.integration.json");
                if ((json["exchangeRate"] as JsonObject)?["data"] is not JsonArray rows) return null;
                // USD is split by note size; only the 50/100 row carries the transfer and sell columns.
                var label = currency == "USD" ? "USD (50,100)" : currency;
                var row = rows.OfType<JsonObject>().FirstOrDefault(o => StringOf(o["label"]) == label);
                if (row == null) return null;
                return new BankRateQuote(bank, currency, day, Number(row["bidRateTM"]), Number(row["bidRateCK"]), Number(row["askRate"]));
            }
        }
    }

    private static async Task<JsonObject> Get(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko)");
        using var resp = await Http.SendAsync(req);
        var status = (int)resp.StatusCode;
        if (status is < 200 or >= 300) throw new VietFlowException($"Trang tỷ giá ngân hàng trả lỗi {status}.");
        var data = await resp.Content.ReadAsByteArrayAsync();
        try { return JsonNode.Parse(data) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string? StringOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// A number or a string such as "25,580.00"; only positive values count.
    private static double? Number(JsonNode? n)
    {
        double? d = null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
                d = double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            else if (v.TryGetValue<double>(out var x)) d = x;
        }
        return d is > 0 ? d : null;
    }
}
