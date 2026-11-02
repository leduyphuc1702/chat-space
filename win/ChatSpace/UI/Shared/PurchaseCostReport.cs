using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Layout;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// One line of the team's purchase-cost spreadsheet: one per money source behind a saved cost.
/// `SourceKey` remembers the spreadsheet name for this kind of source ("pool", "gift", "bank.TCB", "manual.USDT").
public sealed record CostReportRow(
    int Id,
    string SourceKey,
    DateTimeOffset Date,
    double Amount,
    string Currency,
    double? Rate);

public sealed partial class PurchaseCostEntry
{
    private static readonly Dictionary<string, string> DefaultSources = new()
    {
        ["pool"] = "Thẻ USDT", ["gift"] = "GIFT", ["bank.TCB"] = "Thẻ Tech c Hồng", ["bank.VCB"] = "Thẻ VCB",
        ["manual.USDT"] = "Thẻ USDT", ["manual.USDC"] = "Thẻ USDT", ["manual.GC"] = "GIFT", ["manual.GIFTCARD"] = "GIFT", ["manual.GIFT CARD"] = "GIFT",
    };
    private static readonly Dictionary<string, string> DefaultCurrencies = new()
    {
        ["USD"] = "Dollar", ["GBP"] = "Pound", ["EUR"] = "Euro", ["JPY"] = "Yen",
    };

    public static ImmutableDictionary<string, string> LoadReportNames() =>
        (Prefs.Get<Dictionary<string, string>>("vietflow.reportNames") ?? new()).ToImmutableDictionary();

    public string ReportSource(string key) =>
        ReportNames.TryGetValue(key, out var v) ? v
        : DefaultSources.TryGetValue(key, out var d) ? d
        : key.StartsWith("manual.", StringComparison.Ordinal) ? key[7..] : "";

    public string ReportCurrency(string code) =>
        ReportNames.TryGetValue("cur." + code, out var v) ? v
        : DefaultCurrencies.TryGetValue(code, out var d) ? d : code;

    public void SetReportSource(string key, string name) => SetReportName(key, name);
    public void SetReportCurrency(string code, string name) => SetReportName("cur." + code, name);

    private void SetReportName(string key, string name)
    {
        ReportNames = ReportNames.SetItem(key, name);
        Prefs.Set("vietflow.reportNames", ReportNames.ToDictionary(kv => kv.Key, kv => kv.Value));
        ReportCopied = false;
    }

    /// Batches the server drew from, then whatever the form's own rate covered. A card paid in another
    /// currency is reported in that currency at the card's rate, as the bank charged it.
    public List<CostReportRow> ReportRows(VFCostResult r)
    {
        var cur = r.PurchaseOrder.CurrencyCode;
        var rows = new List<CostReportRow>();
        foreach (var s in r.Sources)
            rows.Add(new(rows.Count, s.IsGiftCard ? "gift" : "pool", RateDate, s.Amount, cur, s.Rate));
        var rest = r.RecordedTotal - r.Sources.Sum(s => s.Amount);
        if (rest < 0.005) return rows;
        if (Funding == VFFunding.Bank)
        {
            var key = "bank." + Bank.Raw();
            if (CrossCurrency && TypedAmount is { } typed)
                rows.Add(new(rows.Count, key, RateDate, typed, PayCurrency, BankRate));
            else
                rows.Add(new(rows.Count, key, RateDate, rest, cur, EffectiveRate));
        }
        else
        {
            var note = SourceNote.Trim().ToUpperInvariant();
            rows.Add(new(rows.Count, note.Length == 0 ? "manual" : "manual." + note, RateDate, rest, cur, RateValue ?? r.AverageRate));
        }
        return rows;
    }

    /// Tab-separated, one line per row, in the sheet's column order:
    /// date · source · amount · USDT amount (left empty) · currency · rate · PO · note.
    public string ReportText(VFCostResult r)
    {
        var oneLineNote = string.Join(" ", Note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.Join("\n", ReportRows(r).Select(row =>
            string.Join("\t", new[]
            {
                ReportDate(row.Date), ReportSource(row.SourceKey), ReportNumber(row.Amount), "",
                ReportCurrency(row.Currency), row.Rate is { } rate ? ReportNumber(rate) : "", r.PurchaseOrder.OrderCode, oneLineNote,
            }.Select(f => f.Replace("\t", " ")))));
    }

    public void CopyReport(VFCostResult r)
    {
        Clip.SetText(ReportText(r));
        ReportCopied = true;
    }

    /// "10/26/2026", as the sheet shows it.
    public static string ReportDate(DateTimeOffset d)
    {
        var (y, m, day) = BankRates.Parts(d);
        return $"{m}/{day}/{y}";
    }

    /// "33,14", "23500", "34620,68": comma decimal, no grouping, as the sheet shows it.
    public static string ReportNumber(double v) =>
        Math.Round(v, 2, MidpointRounding.ToEven).ToString("0.##", Inv.C).Replace('.', ',');
}

/// The saved view's block that copies the spreadsheet lines.
internal static class CostReportSection
{
    public static Control Build(PurchaseCostEntry entry, VFCostResult result) => View(() =>
    {
        var rows = entry.ReportRows(result);
        var copied = entry.ReportCopied;
        var children = new List<Control?> { PC.Lbl("Báo cáo Excel") };
        foreach (var row in rows) children.Add(Card(entry, result, row));
        children.Add(PCButtons.Prominent(
            PCButtons.ProminentIconLabel(
                copied ? "Đã copy, dán vào cột A" : rows.Count > 1 ? $"Copy {rows.Count} dòng báo cáo" : "Copy dòng báo cáo",
                copied ? "checkmark" : "doc.on.doc"),
            () => entry.CopyReport(result), true));
        children.Add(PC.Cap("Dán vào ô cột A của dòng trống: điền Ngày → Ghi chú (A–H). Nguồn tiền và Ngoại tệ phải trùng tên trong danh sách chọn của file; sửa một lần, app nhớ."));
        return VStack(HAlign.Leading, 8, children.ToArray());
    });

    private static Control Card(PurchaseCostEntry entry, VFCostResult result, CostReportRow row)
    {
        var line = $"{PurchaseCostEntry.ReportDate(row.Date)} · {PurchaseCostEntry.Money(row.Amount, row.Currency)}"
                   + (row.Rate is { } rate ? $" @ {PurchaseCostEntry.FormatRate(rate)}" : "")
                   + $" · {result.PurchaseOrder.OrderCode}";
        return VStack(HAlign.Leading, 5,
                PC.Digits(PC.Fit(Text(line).Font(11.5), 11.5)),
                HStack(6,
                    Field("Nguồn tiền", ("rptSource", row.Id, row.SourceKey),
                        PC.Bind(() => entry.ReportSource(row.SourceKey), v => entry.SetReportSource(row.SourceKey, v))),
                    Field("Ngoại tệ", ("rptCurrency", row.Id, row.Currency),
                        PC.Bind(() => entry.ReportCurrency(row.Currency), v => entry.SetReportCurrency(row.Currency, v))).Frame(width: 90)))
            .Padding(8)
            .Background(RoundedRectangle(8).Fill(PC.Card));
    }

    private static Control Field(string title, object key, Binding<string> text) =>
        VStack(HAlign.Leading, 2,
            PC.Fit(Text(title).Font(10), 10).Foreground(PC.Secondary),
            Keep(key, () => new PCField(title, text, fontSize: 11.5)));
}
