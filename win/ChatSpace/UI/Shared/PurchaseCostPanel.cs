using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

// MARK: - Hints

/// Values worth offering from an invoice photo's recognised text.
public sealed record InvoiceHints
{
    public sealed record Amount(double Value, string Currency);

    public IReadOnlyList<Amount> Amounts { get; init; } = Array.Empty<Amount>();
    /// Amounts on lines naming the total ("Order total", "Paid"…), most telling keyword first.
    public IReadOnlyList<Amount> Totals { get; init; } = Array.Empty<Amount>();
    public IReadOnlyList<string> Ids { get; init; } = Array.Empty<string>();
    public string? OrderNumber { get; init; }
    public DateTimeOffset? PaymentDate { get; init; }

    /// Amounts in the order's currency, or every amount when the invoice shows none in it.
    public IReadOnlyList<Amount> AmountsFor(string? currency)
    {
        var mine = Amounts.Where(a => a.Currency == currency).ToList();
        return mine.Count == 0 ? Amounts : mine;
    }

    /// Only a total in the order's currency is worth filling in by itself.
    public double? TotalIn(string currency)
    {
        if (Totals.FirstOrDefault(a => a.Currency == currency) is { } t) return t.Value;
        var mine = Amounts.Where(a => a.Currency == currency).Select(a => a.Value).ToList();
        return mine.Count > 0 ? mine.Max() : null;
    }

    private static readonly string[] TotalKeys = ["order total", "grand total", "total paid", "amount paid", "paid", "total", "tổng"];
    private static readonly string[] IdKeys = ["order number", "order no", "order #", "order id", "transaction id", "mã đơn"];
    private static readonly string[] DateKeys = ["payment date", "paid on", "transaction date", "order date", "order placed", "ordered on", "purchase date", "date", "ngày"];
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly (string Mark, string Currency)[] Symbols =
    [
        ("US$", "USD"), ("AU$", "AUD"), ("CA$", "CAD"), ("A$", "AUD"), ("C$", "CAD"),
        ("$", "USD"), ("£", "GBP"), ("€", "EUR"), ("¥", "JPY"), ("￥", "JPY"),
    ];
    private static readonly string[] Codes = ["USD", "EUR", "GBP", "AUD", "CAD", "JPY", "CHF", "SGD", "CNY", "HKD", "KRW", "NZD"];

    // (pattern, index of the currency group, index of the number group)
    private static readonly (Regex Re, int Currency, int Number)[] AmountPatterns = BuildAmountPatterns();

    private static (Regex, int, int)[] BuildAmountPatterns()
    {
        var sym = string.Join("|", Symbols.Select(s => Regex.Escape(s.Mark).Replace("US", @"US\s?")));
        var code = string.Join("|", Codes);
        const string num = @"\d{1,3}(?:[.,]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?";
        const string minus = @"([-−]\s?)?";
        (string, int, int)[] patterns =
        [
            (minus + $"({sym})\\s?({num})", 2, 3),
            (minus + $"\\b({code})\\s?({num})", 2, 3),
            (minus + $"({num})\\s?({sym})(?![A-Za-z0-9])", 3, 2),
            (minus + $"({num})\\s?({code})\\b", 3, 2),
        ];
        return patterns.Select(p => (new Regex(p.Item1, RegexOptions.CultureInvariant), p.Item2, p.Item3)).ToArray();
    }

    private const string Mon = @"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?";

    // "10/30/2026" (US order; day first when the first number is over 12), "2026-10-30", "Oct 30, 2026", "30 Oct 2026".
    private static readonly (Regex Re, Func<string[], (int, int, int)?> Build)[] DatePatterns =
    [
        (new(@"\b(\d{1,2})/(\d{1,2})/(\d{4})\b", RegexOptions.CultureInvariant), g =>
        {
            if (!Int(g[1], out var a) || !Int(g[2], out var b) || !Int(g[3], out var y)) return null;
            return a > 12 ? (y, b, a) : (y, a, b);
        }),
        (new(@"\b(\d{4})-(\d{2})-(\d{2})\b", RegexOptions.CultureInvariant), g =>
        {
            if (!Int(g[1], out var y) || !Int(g[2], out var m) || !Int(g[3], out var d)) return null;
            return (y, m, d);
        }),
        (new(@"\b" + Mon + @"\s+(\d{1,2}),?\s+(\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), g =>
        {
            var m = Array.IndexOf(Months, g[1][..Math.Min(3, g[1].Length)].ToLowerInvariant());
            if (m < 0 || !Int(g[2], out var d) || !Int(g[3], out var y)) return null;
            return (y, m + 1, d);
        }),
        (new(@"\b(\d{1,2})\s+" + Mon + @",?\s+(\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), g =>
        {
            var m = Array.IndexOf(Months, g[2][..Math.Min(3, g[2].Length)].ToLowerInvariant());
            if (!Int(g[1], out var d) || m < 0 || !Int(g[3], out var y)) return null;
            return (y, m + 1, d);
        }),
    ];

    private static bool Int(string s, out int v) => int.TryParse(s, NumberStyles.None, Inv.C, out v);

    private static readonly Regex[] IdPatterns =
    [
        new(@"\b\d{2,3}-\d{5,7}-\d{5,7}\b", RegexOptions.CultureInvariant),
        new(@"(?<![\d*.,$])\b\d{8,20}\b", RegexOptions.CultureInvariant),
        new(@"\b(?=[A-Z0-9-]*\d)(?=[A-Z0-9-]*[A-Z])[A-Z0-9][A-Z0-9-]{7,24}\b", RegexOptions.CultureInvariant),
    ];
    private static readonly Regex AmazonId = new(@"^\d{2,3}-\d{5,7}-\d{5,7}$", RegexOptions.CultureInvariant);
    private static readonly Regex Newlines = new(@"\r\n|[\n\r\u000B\u000C\u0085\u2028\u2029]", RegexOptions.CultureInvariant);

    public static InvoiceHints Parse(string transcript)
    {
        var lines = Newlines.Split(transcript);
        var totals = new List<Amount>();
        foreach (var key in TotalKeys)
        {
            foreach (var line in lines)
            {
                var l = line.ToLowerInvariant();
                if (!l.Contains(key) || (key == "total" && l.Contains("subtotal"))) continue;
                var found = AmountsIn(line);
                if (found.Count == 0 || totals.Contains(found[0])) continue;
                totals.Add(found[0]);
            }
        }
        var amounts = new List<Amount>();
        var ids = new List<string>();
        foreach (var line in lines)
        {
            foreach (var a in AmountsIn(line)) if (!amounts.Contains(a)) amounts.Add(a);
            foreach (var id in IdsIn(line)) if (!ids.Contains(id)) ids.Add(id);
        }
        var keyed = lines.FirstOrDefault(line => IdKeys.Any(k => line.ToLowerInvariant().Contains(k)) && IdsIn(line).Count > 0);
        var orderNumber = keyed != null ? IdsIn(keyed).FirstOrDefault()
            : ids.FirstOrDefault(i => AmazonId.IsMatch(i)) ?? ids.FirstOrDefault();

        var dateLines = DateKeys.SelectMany(key => lines.Where(l => l.ToLowerInvariant().Contains(key))).Concat(lines);
        DateTimeOffset? paymentDate = null;
        foreach (var line in dateLines)
        {
            var found = DatesIn(line);
            if (found.Count > 0) { paymentDate = found[0]; break; }
        }
        return new InvoiceHints
        {
            Amounts = amounts,
            Totals = totals,
            Ids = ids,
            OrderNumber = orderNumber,
            PaymentDate = paymentDate,
        };
    }

    /// "10/30/2026" (US order; day first when the first number is over 12), "2026-10-30",
    /// "Oct 30, 2026", "30 Oct 2026". Only dates from the last two years up to today count.
    public static List<DateTimeOffset> DatesIn(string text)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(BankRates.Offset);
        var earliest = now.AddYears(-2);
        var latest = now.AddDays(1);
        var result = new List<DateTimeOffset>();
        foreach (var (re, build) in DatePatterns)
        {
            foreach (Match m in re.Matches(text))
            {
                var groups = Enumerable.Range(0, m.Groups.Count).Select(i => m.Groups[i].Success ? m.Groups[i].Value : "").ToArray();
                if (build(groups) is not { } ymd) continue;
                var (y, mo, d) = ymd;
                if (mo is < 1 or > 12 || d is < 1 or > 31) continue;
                if (BankRates.Date(y, mo, d) is not { } date || date < earliest || date > latest) continue;
                result.Add(date);
            }
        }
        return result;
    }

    /// Positive amounts with their currency: "$14.24", "US $1,299.00", "£12.50", "14,24 €", "1.299,00 EUR".
    /// Discounts ("-$5.46") are left out.
    public static List<Amount> AmountsIn(string text)
    {
        var result = new List<Amount>();
        foreach (var (re, ci, ni) in AmountPatterns)
        {
            foreach (Match m in re.Matches(text))
            {
                if (m.Groups[1].Success) continue;
                if (!m.Groups[ci].Success || !m.Groups[ni].Success) continue;
                if (PurchaseCostEntry.ParseAmount(m.Groups[ni].Value) is not { } v || v <= 0) continue;
                var mark = m.Groups[ci].Value.Replace(" ", "");
                var symbol = Array.FindIndex(Symbols, s => s.Mark == mark);
                var currency = symbol >= 0 ? Symbols[symbol].Currency : mark.ToUpperInvariant();
                var a = new Amount(v, currency);
                if (!result.Contains(a)) result.Add(a);
            }
        }
        return result;
    }

    /// Order / transaction numbers: Amazon "111-1234567-1234567", eBay "12-34567-89012", long digit runs,
    /// and letter+digit codes of 8 or more characters.
    public static List<string> IdsIn(string text)
    {
        var result = new List<string>();
        foreach (var re in IdPatterns)
        {
            foreach (Match m in re.Matches(text))
            {
                var s = m.Value;
                if (!s.StartsWith("PO", StringComparison.Ordinal) && !result.Any(x => x.Contains(s))) result.Add(s);
            }
        }
        return result;
    }
}

/// What the conversation around the photo says: the PO it belongs to and the rate people quoted.
public sealed record ChatCostContext
{
    public string? PoCode { get; init; }
    public double? Rate { get; init; }
    public string? Source { get; init; }

    private static readonly Regex PoRe = new(@"\bPO[A-Z0-9]{6,}\b", RegexOptions.CultureInvariant);
    private static readonly Regex RateRe = new(
        @"\b(usdt|usdc|usd|eur|euro|gbp|bảng|vcb|tcb|bank|gc|gift ?card|redotcard|ptm\s?\d*|tm)?\s*(?:rate|tỷ giá|tg)?\s*[:=]?\s*\b(1[5-9]|[2-4]\d)[.,]?(\d{3})\b(?![.,]?\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ChatCostContext Find(Message m, IReadOnlyList<Message> messages)
    {
        var i = -1;
        for (var k = 0; k < messages.Count; k++) if (messages[k].Id == m.Id) { i = k; break; }
        var before = new List<Message>();
        var after = new List<Message>();
        if (i >= 0)
        {
            var from = Math.Max(0, i - 30);
            for (var k = i - 1; k >= from; k--) before.Add(messages[k]);
            for (var k = i + 1; k < Math.Min(messages.Count, i + 9); k++) after.Add(messages[k]);
        }
        var poTexts = new List<string?> { m.Text, m.ReplyTo?.Text };
        poTexts.AddRange(before.Select(x => x.Text));
        poTexts.AddRange(after.Select(x => x.Text));
        string? po = null;
        foreach (var t in poTexts)
        {
            if (t != null && PoCodeIn(t) is { } found) { po = found; break; }
        }
        double? rate = null;
        string? source = null;
        var rateTexts = new List<string?> { m.Text };
        rateTexts.AddRange(after.Select(x => x.Text));
        foreach (var t in rateTexts)
        {
            if (t == null) continue;
            if (RateIn(t) is { } r) { rate = r.Item1; source = r.Item2; break; }
        }
        return new ChatCostContext { PoCode = po, Rate = rate, Source = source };
    }

    public static string? PoCodeIn(string text)
    {
        var m = PoRe.Match(text);
        return m.Success ? m.Value : null;
    }

    /// "Usdt 26350", "Rate 26.000", "GBP 34.250", "bank: 26,420" -> a VND rate between 15.000 and 49.999
    /// (USD, EUR, GBP, CHF…) and its source word.
    public static (double, string?)? RateIn(string text)
    {
        var m = RateRe.Match(text);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[2].Value + m.Groups[3].Value, NumberStyles.Float, Inv.C, out var rate)) return null;
        var source = m.Groups[1].Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        return (rate, source);
    }
}

// MARK: - Entry

/// The cost form for one invoice photo. Saving writes the cost to VietFlow first, then posts the
/// confirmation into the chat as a reply to the photo, so the chat never claims a cost VietFlow refused.
public sealed partial class PurchaseCostEntry : Observable
{
    public enum PhaseKind { Editing, Saving, Saved }

    public Message Message { get; }
    private readonly PlatformSession _session;

    private string _poCode = "";
    public string PoCode { get => Get(ref _poCode); set => Set(ref _poCode, value); }
    private VFPurchaseOrder? _order;
    public VFPurchaseOrder? Order { get => Get(ref _order); private set => Set(ref _order, value); }
    private bool _loadingOrder;
    public bool LoadingOrder { get => Get(ref _loadingOrder); private set => Set(ref _loadingOrder, value); }
    private string _totalPaid = "";
    public string TotalPaid { get => Get(ref _totalPaid); set => Set(ref _totalPaid, value); }
    private string _orderNumber = "";
    public string OrderNumber { get => Get(ref _orderNumber); set => Set(ref _orderNumber, value); }
    private VFFunding _funding = VFFunding.Pool;
    public VFFunding Funding { get => Get(ref _funding); set => Set(ref _funding, value); }
    private string _rate = "";
    public string Rate { get => Get(ref _rate); set => Set(ref _rate, value); }
    private string _sourceNote = "";
    public string SourceNote { get => Get(ref _sourceNote); set => Set(ref _sourceNote, value); }
    private string _note = "";
    public string Note { get => Get(ref _note); set => Set(ref _note, value); }
    private ImmutableDictionary<int, int> _quantities = ImmutableDictionary<int, int>.Empty;
    public ImmutableDictionary<int, int> Quantities { get => Get(ref _quantities); set => Set(ref _quantities, value); }
    private IReadOnlyList<VFGiftCard> _giftCards = Array.Empty<VFGiftCard>();
    public IReadOnlyList<VFGiftCard> GiftCards { get => Get(ref _giftCards); private set => Set(ref _giftCards, value); }
    private string? _giftCardsError;
    public string? GiftCardsError { get => Get(ref _giftCardsError); private set => Set(ref _giftCardsError, value); }
    private string? _poolError;
    public string? PoolError { get => Get(ref _poolError); private set => Set(ref _poolError, value); }
    private ImmutableList<int> _selectedGiftCards = ImmutableList<int>.Empty;
    public ImmutableList<int> SelectedGiftCards { get => Get(ref _selectedGiftCards); set => Set(ref _selectedGiftCards, value); }
    private bool _remainderFromPool;
    public bool RemainderFromPool { get => Get(ref _remainderFromPool); set => Set(ref _remainderFromPool, value); }
    private VFPoolPreview? _poolPreview;
    public VFPoolPreview? PoolPreview { get => Get(ref _poolPreview); private set => Set(ref _poolPreview, value); }
    private VFCostResult? _preview;
    public VFCostResult? Preview { get => Get(ref _preview); private set => Set(ref _preview, value); }
    private VFCostResult? _result;
    public VFCostResult? Result { get => Get(ref _result); private set => Set(ref _result, value); }
    private bool _sendToChat = true;
    public bool SendToChat { get => Get(ref _sendToChat); set => Set(ref _sendToChat, value); }
    private VNBank _bank;
    public VNBank Bank { get => Get(ref _bank); private set => Set(ref _bank, value); }
    private BankRateColumn _bankColumn;
    public BankRateColumn BankColumn { get => Get(ref _bankColumn); private set => Set(ref _bankColumn, value); }
    private string _bankFee;
    public string BankFee { get => Get(ref _bankFee); private set => Set(ref _bankFee, value); }
    private DateTimeOffset _rateDate;
    public DateTimeOffset RateDate { get => Get(ref _rateDate); private set => Set(ref _rateDate, value); }
    private string _rateDateSource = "ngày gửi ảnh";
    public string RateDateSource { get => Get(ref _rateDateSource); private set => Set(ref _rateDateSource, value); }
    /// The currency the card was charged in; Bank VN converts it through the bank's own boards.
    private string _payCurrency = "USD";
    public string PayCurrency { get => Get(ref _payCurrency); private set => Set(ref _payCurrency, value); }
    private string _payCurrencySource = "mặc định";
    public string PayCurrencySource { get => Get(ref _payCurrencySource); private set => Set(ref _payCurrencySource, value); }
    /// The bank's board for `PayCurrency`.
    private BankRateQuote? _bankQuote;
    public BankRateQuote? BankQuote { get => Get(ref _bankQuote); private set => Set(ref _bankQuote, value); }
    /// The bank's board for the PO's currency, when it differs from `PayCurrency`.
    private BankRateQuote? _poBankQuote;
    public BankRateQuote? PoBankQuote { get => Get(ref _poBankQuote); private set => Set(ref _poBankQuote, value); }
    private string? _bankError;
    public string? BankError { get => Get(ref _bankError); private set => Set(ref _bankError, value); }
    private bool _loadingBank;
    public bool LoadingBank { get => Get(ref _loadingBank); private set => Set(ref _loadingBank, value); }
    private bool _rateDatePicked;
    private bool _payCurrencyPicked;
    /// What the form last filled into `TotalPaid` by itself, so a currency switch may replace it.
    private string? _autoTotal;
    private int _bankLoadGeneration;
    private PhaseKind _phase = PhaseKind.Editing;
    public PhaseKind Phase { get => Get(ref _phase); private set => Set(ref _phase, value); }
    private string? _error;
    public string? Error { get => Get(ref _error); private set => Set(ref _error, value); }
    private string? _sentText;
    public string? SentText { get => Get(ref _sentText); private set => Set(ref _sentText, value); }
    private bool _sending;
    public bool Sending { get => Get(ref _sending); private set => Set(ref _sending, value); }
    private string? _sendError;
    public string? SendError { get => Get(ref _sendError); private set => Set(ref _sendError, value); }
    /// Spreadsheet names for sources and currencies, edited once and remembered.
    private ImmutableDictionary<string, string> _reportNames;
    public ImmutableDictionary<string, string> ReportNames { get => Get(ref _reportNames); private set => Set(ref _reportNames, value); }
    private bool _reportCopied;
    public bool ReportCopied { get => Get(ref _reportCopied); private set => Set(ref _reportCopied, value); }
    private InvoiceHints _hints = new();
    public InvoiceHints Hints { get => Get(ref _hints); private set => Set(ref _hints, value); }
    private ChatCostContext _context = new();
    public ChatCostContext Context { get => Get(ref _context); private set => Set(ref _context, value); }
    private bool _appliedTranscript;

    public PurchaseCostEntry(Message message, PlatformSession session)
    {
        Message = message;
        _session = session;
        _context = ChatCostContext.Find(message, session.MessagesIn(message.ChatId));
        var savedBank = VNBankExt.FromRaw(Prefs.GetString("vietflow.bank")) ?? VNBank.VCB;
        _bank = savedBank;
        _bankColumn = BankRateColumnExt.FromRaw((int)(Prefs.GetDouble("vietflow.bankColumn") ?? 0)) ?? BankRateColumn.Sell;
        _bankFee = Prefs.GetString($"vietflow.bankFee.{savedBank.Raw()}") ?? "";
        _rateDate = DateTimeOffset.FromUnixTimeMilliseconds((long)message.Timestamp);
        _reportNames = LoadReportNames();
        _poCode = _context.PoCode ?? "";
        if (_context.Rate is { } r)
        {
            _funding = VFFunding.Manual;
            _rate = FormatRate(r);
            _sourceNote = _context.Source ?? "";
        }
        if (_poCode.Length > 0) _ = LoadOrder();
    }

    public void ApplyTranscript(string transcript)
    {
        if (_appliedTranscript || transcript.Length == 0) return;
        _appliedTranscript = true;
        Hints = InvoiceHints.Parse(transcript);
        if (!_payCurrencyPicked && (Hints.Totals.FirstOrDefault()?.Currency ?? Hints.Amounts.FirstOrDefault()?.Currency) is { } c)
        {
            PayCurrency = c;
            PayCurrencySource = "invoice";
        }
        FillTotalFromHints();
        if (OrderNumber.Length == 0 && Hints.OrderNumber is { } o) OrderNumber = o;
        if (!_rateDatePicked && Hints.PaymentDate is { } d)
        {
            RateDate = d;
            RateDateSource = "ngày thanh toán trên invoice";
        }
    }

    /// The currency "Tổng đã trả" is typed in: the card's currency for Bank VN, else the PO's.
    public string AmountCurrency => Funding == VFFunding.Bank ? PayCurrency : Order?.CurrencyCode ?? "USD";

    /// An invoice total only counts when it is in `AmountCurrency`; outside Bank VN that waits for the order.
    /// A total the form filled itself follows currency changes, one the user typed stays.
    public void FillTotalFromHints()
    {
        if (!(TotalPaid.Length == 0 || TotalPaid == _autoTotal) || !(Funding == VFFunding.Bank || Order != null)) return;
        var t = Hints.TotalIn(AmountCurrency) is { } v ? FormatAmount(v) : "";
        TotalPaid = t;
        _autoTotal = t;
    }

    public void PickHintAmount(InvoiceHints.Amount a)
    {
        if (Funding == VFFunding.Bank && a.Currency != PayCurrency) SelectPayCurrency(a.Currency);
        TotalPaid = FormatAmount(a.Value);
    }

    // MARK: Bank VN

    private static string SavedFee(VNBank b) => Prefs.GetString($"vietflow.bankFee.{b.Raw()}") ?? "";

    public void SelectBank(VNBank b)
    {
        Bank = b;
        Prefs.Set("vietflow.bank", b.Raw());
        BankFee = SavedFee(b);
    }

    public void SelectColumn(BankRateColumn c)
    {
        BankColumn = c;
        Prefs.Set("vietflow.bankColumn", (int)c);
    }

    public void SetBankFee(string s)
    {
        BankFee = s;
        Prefs.Set($"vietflow.bankFee.{Bank.Raw()}", s);
    }

    public void SetRateDate(DateTimeOffset d)
    {
        RateDate = d;
        _rateDatePicked = true;
        RateDateSource = "ngày chọn tay";
    }

    public void SelectPayCurrency(string c)
    {
        PayCurrency = c;
        _payCurrencyPicked = true;
        PayCurrencySource = "chọn tay";
        FillTotalFromHints();
    }

    /// Paid in one currency, recorded on a PO in another.
    public bool CrossCurrency => Funding == VFFunding.Bank && Order is { } o && o.CurrencyCode != PayCurrency;

    /// Changes whenever a different bank board is needed.
    public string BankKey => Funding == VFFunding.Bank
        ? $"{Bank.Raw()}|{PayCurrency}|{Order?.CurrencyCode ?? ""}|{BankRates.DayString(RateDate)}" : "";

    public async Task LoadBankQuote()
    {
        if (Funding != VFFunding.Bank) return;
        // A newer load (another key) supersedes this one, like `.task(id:)` cancelling the old task.
        var generation = ++_bankLoadGeneration;
        LoadingBank = true;
        BankQuote = null;
        PoBankQuote = null;
        BankError = null;
        try
        {
            var bank = Bank;
            var date = RateDate;
            var quote = await BankRates.Quote(bank, PayCurrency, date);
            if (generation != _bankLoadGeneration) return;
            BankQuote = quote;
            if (CrossCurrency && Order is { } order)
            {
                var po = await BankRates.Quote(bank, order.CurrencyCode, date);
                if (generation != _bankLoadGeneration) return;
                PoBankQuote = po;
            }
        }
        catch (Exception e)
        {
            if (generation == _bankLoadGeneration) BankError = e.Message;
        }
        finally
        {
            if (generation == _bankLoadGeneration) LoadingBank = false;
        }
    }

    public double BankFeeValue
    {
        get
        {
            var t = new string(BankFee.Replace(",", ".").Where(c => "0123456789.".Contains(c)).ToArray());
            return double.TryParse(t, NumberStyles.AllowDecimalPoint, Inv.C, out var v) ? v : 0;
        }
    }

    private double WithFee(double v) => Math.Round(v * (1 + BankFeeValue / 100) * 100, MidpointRounding.AwayFromZero) / 100;

    /// The chosen column for the card's currency, before and after the fee.
    public double? BankBaseRate => BankQuote?.Value(BankColumn);
    public double? BankRate => BankBaseRate is { } v ? WithFee(v) : null;

    /// The same column for the PO's currency: what the paid amount is converted with and what VietFlow records.
    public double? PoBaseRate => CrossCurrency ? PoBankQuote?.Value(BankColumn) : BankBaseRate;
    public double? PoRate => PoBaseRate is { } v ? WithFee(v) : null;

    /// What the card was charged in VND: paid amount × the card currency's rate with the fee.
    public double? BankChargeVnd
    {
        get
        {
            if (TypedAmount is not { } typed || BankRate is not { } r) return null;
            return Math.Round(typed * r, MidpointRounding.AwayFromZero);
        }
    }

    /// The rate VietFlow records. Paid in another currency, it is the VND charge over the converted amount,
    /// so the PO's VND cost equals the card charge even though the converted amount is rounded to cents.
    public double? BankRecordedRate
    {
        get
        {
            if (!CrossCurrency) return PoRate;
            if (BankChargeVnd is not { } vnd || PaidAmount is not { } paid || paid <= 0) return null;
            return Math.Round(vnd / paid * 100, MidpointRounding.AwayFromZero) / 100;
        }
    }

    /// "VCB Bán GBP 35.185,92 + phí 1,1%"
    public string BankDescription
    {
        get
        {
            var baseText = BankBaseRate is { } b ? " " + FormatRate(b) : "";
            return $"{Bank.Raw()} {BankColumn.Label()} {PayCurrency}{baseText}" + (BankFeeValue > 0 ? $" + phí {FormatPercent(BankFeeValue)}%" : "");
        }
    }

    public double? EffectiveRate => Funding == VFFunding.Bank ? BankRecordedRate : RateValue;

    // MARK: Loading

    public async Task LoadOrder()
    {
        var code = PoCode.Trim().ToUpperInvariant();
        if (code.Length == 0 || !VietFlow.Shared.IsLoggedIn) return;
        PoCode = code;
        LoadingOrder = true;
        Error = null;
        try
        {
            var o = await VietFlow.Shared.PurchaseOrder(code);
            Order = o;
            Quantities = o.Items.Where(i => !i.IsFreeItem)
                .GroupBy(i => i.Id).ToImmutableDictionary(g => g.Key, g => g.First().QuantityPurchased > 0 ? g.First().QuantityPurchased : g.First().QuantityRequested);
            if (OrderNumber.Length == 0 && o.Items.Select(i => i.OrderNumber).FirstOrDefault(n => !string.IsNullOrEmpty(n)) is { } existing)
                OrderNumber = existing;
            if (Rate.Length == 0 && Funding == VFFunding.Manual && o.BaseRate is { } baseRate) Rate = FormatRate(baseRate);
            FillTotalFromHints();
            try
            {
                GiftCards = await VietFlow.Shared.GiftCards(o.CurrencyCode);
                GiftCardsError = null;
            }
            catch (Exception e)
            {
                GiftCards = Array.Empty<VFGiftCard>();
                GiftCardsError = e.Message;
            }
        }
        catch (Exception e)
        {
            Order = null;
            Error = e.Message;
        }
        finally { LoadingOrder = false; }
    }

    /// Changes whenever the server-side split or the pool preview could change.
    public string PreviewKey =>
        $"{Order?.Id ?? 0}|{(PaidAmount ?? 0).ToString(Inv.C)}|{Funding.Raw()}|{(EffectiveRate ?? 0).ToString(Inv.C)}|"
        + string.Join(",", Quantities.OrderBy(q => q.Key).Select(q => $"{q.Key}:{q.Value}"));

    public async Task RefreshPreview()
    {
        if (Phase != PhaseKind.Editing || Order is not { } order || PaidAmount is not { } total) { Preview = null; PoolPreview = null; return; }
        try { Preview = await VietFlow.Shared.RecordCost(Request(total), order.Id, true); }
        catch { Preview = null; }
        if (Funding != VFFunding.Pool) { PoolPreview = null; PoolError = null; return; }
        try
        {
            PoolPreview = await VietFlow.Shared.PoolPreview(order.CurrencyCode, total);
            PoolError = null;
        }
        catch (Exception e)
        {
            PoolPreview = null;
            PoolError = e.Message;
        }
    }

    // MARK: Saving

    /// What was typed into "Tổng đã trả", in `AmountCurrency`.
    public double? TypedAmount => ParseAmount(TotalPaid) is { } v && v > 0 ? v : null;

    /// The paid total in the PO's currency, which is what VietFlow splits and records. Paid in another
    /// currency, it goes through VND: amount × that currency's column ÷ the PO currency's column, same bank and day.
    public double? PaidAmount
    {
        get
        {
            if (TypedAmount is not { } typed) return null;
            if (!CrossCurrency) return typed;
            if (BankBaseRate is not { } pay || PoBaseRate is not { } po) return null;
            return Math.Round(typed * pay / po * 100, MidpointRounding.AwayFromZero) / 100;
        }
    }

    public double? RateValue => ParseRate(Rate) is { } v && v > 0 ? v : null;

    public string? Problem
    {
        get
        {
            if (Order is not { } order) return PoCode.Length == 0 ? "Nhập mã PO." : $"Chưa tải được PO {PoCode}.";
            if (order.IsCancelled) return $"PO {order.OrderCode} đã hủy.";
            if (TypedAmount == null) return "Nhập tổng tiền đã trả.";
            switch (Funding)
            {
                case VFFunding.Manual:
                    if (RateValue == null) return "Nhập tỷ giá mua.";
                    break;
                case VFFunding.Bank:
                    if (BankQuote == null || (CrossCurrency && PoBankQuote == null))
                        return LoadingBank ? $"Đang lấy tỷ giá {Bank.Raw()}…" : $"Chưa lấy được tỷ giá {Bank.Raw()}.";
                    if (BankBaseRate == null) return $"{Bank.Raw()} không có cột {BankColumn.Label()} cho {PayCurrency}.";
                    if (PoBaseRate == null) return $"{Bank.Raw()} không có cột {BankColumn.Label()} cho {order.CurrencyCode}.";
                    break;
                case VFFunding.GiftCard:
                    if (SelectedGiftCards.Count == 0) return "Chọn ít nhất một cục Gift Card.";
                    if (!RemainderFromPool && RateValue == null && GiftShortfall > 0) return "Nhập tỷ giá cho phần Gift Card không đủ.";
                    break;
                case VFFunding.Pool:
                    if (PoolPreview is { IsSufficient: false } && RateValue == null) return "Quỹ không đủ: nhập tỷ giá cho phần thiếu.";
                    break;
            }
            return null;
        }
    }

    public double GiftShortfall
    {
        get
        {
            var selected = SelectedGiftCards;
            var covered = GiftCards.Where(g => selected.Contains(g.Id)).Sum(g => g.RemainingAmount);
            return Math.Max(0, (PaidAmount ?? 0) - covered);
        }
    }

    private VFCostRequest Request(double total) => new()
    {
        TotalPaid = total,
        Funding = Funding,
        ExchangeRate = EffectiveRate,
        OrderNumber = OrderNumber.Trim(),
        GiftCardIds = SelectedGiftCards,
        RemainderFromPool = RemainderFromPool,
        Quantities = Quantities,
    };

    public async Task Save()
    {
        if (Problem != null || Order is not { } order || PaidAmount is not { } total) return;
        Phase = PhaseKind.Saving;
        Error = null;
        try
        {
            Result = await VietFlow.Shared.RecordCost(Request(total), order.Id, false);
            Phase = PhaseKind.Saved;
        }
        catch (Exception e)
        {
            Phase = PhaseKind.Editing;
            Error = e.Message;
            return;
        }
        if (SendToChat) await Send();
    }

    public async Task Send()
    {
        if (Result is not { } result) return;
        var text = Confirmation(result);
        Sending = true;
        SendError = null;
        try
        {
            await _session.Post(text, Message.ChatId, reply: Message);
            SentText = text;
        }
        catch (Exception e)
        {
            SendError = e.Message;
        }
        finally { Sending = false; }
    }

    public void EditAgain()
    {
        Phase = PhaseKind.Editing;
        Result = null;
        ReportCopied = false;
        SentText = null;
        SendError = null;
    }

    // MARK: Confirmation message

    /// Internal accounting group: every figure goes in.
    public string Confirmation(VFCostResult r)
    {
        var cur = r.PurchaseOrder.CurrencyCode;
        var lines = new List<string>
        {
            "✅ Đã nhập chi phí " + r.PurchaseOrder.OrderCode + (r.PurchaseOrder.CustomerName is { } name ? " · " + name : ""),
        };
        var order = OrderNumber.Trim();
        if (order.Length > 0) lines.Add("Order: " + order);
        var outcome = Math.Abs(r.ProfitForeign) < 0.005 ? "hòa"
            : r.ProfitForeign > 0 ? "lãi " + Money(r.ProfitForeign, cur) : "lỗ " + Money(-r.ProfitForeign, cur);
        var paidIn = "";
        if (CrossCurrency && TypedAmount is { } typed) paidIn = $"{Money(typed, PayCurrency)} = ";
        lines.Add($"Đã trả: {paidIn}{Money(r.TotalPaid, cur)} · giá bán {Money(r.SellingTotal, cur)} → {outcome}");
        lines.Add("Nguồn: " + FundingLine(r));
        if (r.CostVnd is { } cost)
            lines.Add("Giá vốn: " + Vnd(cost) + (r.AverageRate is { } avg ? $" (tỷ giá TB {FormatRate(avg)})" : ""));
        foreach (var i in r.Items.Where(i => !i.IsFreeItem))
            lines.Add($"• {Short(i.ProductName)}: {i.Quantity} × {Money(i.UnitCost, cur)} = {Money(i.LineTotal, cur)} (bán {Money(i.SellingTotal, cur)})");
        if (Math.Abs(r.RoundingDrift) >= 0.005) lines.Add("Làm tròn đơn giá lệch " + Money(r.RoundingDrift, cur));
        var n = Note.Trim();
        if (n.Length > 0) lines.Add("Ghi chú: " + n);
        return string.Join("\n", lines);
    }

    private string FundingLine(VFCostResult r)
    {
        var cur = r.PurchaseOrder.CurrencyCode;
        var fromBatches = r.Sources.Select(s => $"{(s.Source.Length == 0 ? "#" + s.BatchId : s.Source)} {Money(s.Amount, cur)} @ {FormatRate(s.Rate)}").ToList();
        var batchTotal = r.Sources.Sum(s => s.Amount);
        var rest = r.RecordedTotal - batchTotal;
        var parts = new List<string>(fromBatches);
        if (rest >= 0.005 && Funding == VFFunding.Bank && BankQuote is { } q)
        {
            var rateText = EffectiveRate is { } er ? FormatRate(er) : "?";
            if (CrossCurrency && BankRate is { } payRate)
            {
                var poBase = PoBaseRate is { } pb ? " " + FormatRate(pb) : "";
                var charge = BankChargeVnd is { } vnd ? $", bank trừ {Vnd(vnd)}" : "";
                parts.Add($"{BankDescription} = {FormatRate(payRate)}{charge}; quy ra {cur} theo {BankColumn.Label()} {cur}{poBase}"
                          + $" → @ {rateText} (tỷ giá ngày {Day(q.Day)})");
            }
            else
            {
                parts.Add($"{BankDescription} @ {rateText} (tỷ giá ngày {Day(q.Day)})");
            }
        }
        else if (rest >= 0.005)
        {
            var label = SourceNote.Trim();
            var rateText = RateValue is { } rv ? " @ " + FormatRate(rv) : "";
            parts.Add(fromBatches.Count == 0
                ? (label.Length == 0 ? "tỷ giá tay" : label) + rateText
                : "phần thiếu " + Money(rest, cur) + (label.Length == 0 ? "" : " " + label) + rateText);
        }
        return string.Join("; ", parts);
    }

    // MARK: Formatting

    /// Reads both "1,299.00" and "1.299,00": with both separators the last one is the decimal point; a lone
    /// separator followed by exactly three digits ("26.350", "1,299") groups thousands, otherwise it is decimal.
    public static double? ParseNumber(string s)
    {
        var t = new string(s.Where(c => "0123456789.,".Contains(c)).ToArray());
        var last = t.LastIndexOfAny(['.', ',']);
        if (last < 0) return double.TryParse(t, NumberStyles.None, Inv.C, out var plain) ? plain : null;
        var sep = t[last];
        bool decimalPoint;
        if (t.Contains(sep == '.' ? ',' : '.')) decimalPoint = true;
        else decimalPoint = t.Count(c => c == sep) == 1 && t.Length - last - 1 != 3;
        string digits;
        if (decimalPoint)
            digits = new string(t[..last].Where(char.IsDigit).ToArray()) + "." + t[(last + 1)..];
        else
            digits = new string(t.Where(char.IsDigit).ToArray());
        return double.TryParse(digits, NumberStyles.AllowDecimalPoint, Inv.C, out var v) ? v : null;
    }

    public static double? ParseAmount(string s) => ParseNumber(s);

    public static double? ParseRate(string s) => ParseNumber(s);

    public static string FormatAmount(double v) => v.ToString("F2", Inv.C);

    /// vi_VN: "." groups thousands, "," starts the decimals (0...2 digits).
    public static string FormatRate(double v) => v.ToString("#,##0.##", Inv.C).Replace(",", "\u0001").Replace(".", ",").Replace("\u0001", ".");

    /// vi_VN, 0...3 decimals: "1,1".
    public static string FormatPercent(double v) => v.ToString("#,##0.###", Inv.C).Replace(",", "\u0001").Replace(".", ",").Replace("\u0001", ".");

    public static string Day(DateTimeOffset d)
    {
        var (y, m, day) = BankRates.Parts(d);
        return $"{day:D2}/{m:D2}/{y:D4}";
    }

    public static string Money(double v, string currency)
    {
        var n = v.ToString("F2", Inv.C);
        var prefix = currency switch { "USD" => "$", "GBP" => "£", "EUR" => "€", "JPY" => "¥", "AUD" => "A$", "CAD" => "C$", _ => null };
        return prefix != null ? prefix + n : $"{n} {currency}";
    }

    public static string Vnd(double v) => v.ToString("#,##0", Inv.C).Replace(",", ".") + " ₫";

    private static string Short(string s) => s.GraphemeCount() > 60 ? new string(s.Take(57).ToArray()) + "…" : s;
}

// MARK: - Look

/// Colors and small helpers of the dark cost panel (the SwiftUI view runs in dark appearance).
internal static class PC
{
    public static readonly SColor Bg = SColor.Hex(0x1A1A1A);
    public static readonly SColor Secondary = SColor.White.Opacity(0.5);
    public static readonly SColor Placeholder = SColor.Hex(0x8A8A8A);
    public static readonly SColor Red = SColor.Hex(0xFF453A);
    public static readonly SColor Orange = SColor.Hex(0xFF9F0A);
    public static readonly SColor Green = SColor.Hex(0x30D158);
    public static readonly SColor Card = SColor.White.Opacity(0.06);
    public static readonly SColor Rule = SColor.Hex(0x565656);
    public static readonly SColor ChipOn = SColor.Hex(0x0462DD);
    public static readonly SColor ChipOff = SColor.Hex(0x313131);
    public static readonly SColor Accent = SColor.Hex(0x165CE0);
    /// `Color.accentColor` as the dark window shows it (measured from the chips: accent at 85% over the panel).
    public static readonly SColor AccentBlue = SColor.Hex(0x006FFF);

    public static SText Lbl(string s) => PC.Fit(Text(s).Font(11, Weight.Medium), 11).Foreground(Secondary);
    public static SText Cap(string s) => PC.Fit(Text(s).Font(10.5), 10.5).Foreground(Secondary);

    /// A caption under `.foregroundStyle(.orange)` / `.red`: its `.secondary` level is that colour at half strength.
    public static SText CapTint(string s, SColor tint) => Cap(s).Foreground(tint.Opacity(0.5));

    /// AppKit lays a line of the system font out at ceil(size x 1.193) points; Avalonia uses the unrounded font height.
    public static SText Fit(SText t, double size)
    {
        t.LineHeight = Math.Ceiling(size * 1.193);
        // The 10.5 and 11.5 pt lines sit one device pixel higher in AppKit than in Skia's baseline placement.
        if (size is 10.5 or 11.5) t.RenderTransform = new TranslateTransform(0, -0.5);
        return t;
    }

    /// `.monospacedDigit()`: tabular figures of the system font.
    public static SText Digits(SText t)
    {
        t.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        return t;
    }

    public static Control Row(string k, string v) =>
        HStack(PC.Fit(Text(k).Font(11.5), 11.5).Foreground(Secondary), Spacer(), Digits(PC.Fit(Text(v).Font(11.5), 11.5)));

    public static Binding<string> Bind(Func<string> get, Action<string> set) => new(get, set);
}

/// A rounded box with optional edge highlights, the background of the dark buttons and fields.
internal sealed class PCBox : Control, IFlexible
{
    public Color Fill { get; init; }
    public double Radius { get; init; } = 5;
    public Color? Border { get; init; }
    public double BorderWidth { get; init; } = 0.5;
    public Color? TopLine { get; init; }
    public Color? BottomLine { get; init; }
    /// Opaque grey levels of the first / last pixel rows (0.5 pt each), measured from the AppKit rendering.
    public byte[]? TopRows { get; init; }
    public byte[]? BottomRows { get; init; }
    /// Fill gradient end (top = Fill).
    public Color? FillBottom { get; init; }
    /// 0.5 pt ring just outside the box, and the rows of shadow below it.
    public byte? Outline { get; init; }
    public Thickness Inset { get; init; }
    public byte[]? ShadowRows { get; init; }

    public bool IsFlexible(Orientation axis) => true;
    protected override Size MeasureOverride(Size available) => default;

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size).Deflate(Inset);
        static IBrush Grey(byte v) => new ImmutableSolidColorBrush(Color.FromRgb(v, v, v));
        if (Outline is { } o)
            ctx.DrawRectangle(null, new Pen(Grey(o), 0.5), r.Inflate(0.25), Radius + 0.25, Radius + 0.25);
        for (var i = 0; ShadowRows != null && i < ShadowRows.Length; i++)
            ctx.DrawRectangle(Grey(ShadowRows[i]), null, new Rect(r.X + Radius * 0.6, r.Bottom + i * 0.5, Math.Max(0, r.Width - Radius * 1.2), 0.5));
        IBrush fill = new ImmutableSolidColorBrush(Fill);
        if (FillBottom is { } fb)
            fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Fill, 0), new GradientStop(fb, 1) },
            };
        ctx.DrawRectangle(fill, null, r, Radius, Radius);
        using (ctx.PushClip(new RoundedRect(r, Radius)))
        {
            if (TopLine is { } t) ctx.DrawRectangle(new ImmutableSolidColorBrush(t), null, new Rect(r.X, r.Y, r.Width, 0.5));
            if (BottomLine is { } b) ctx.DrawRectangle(new ImmutableSolidColorBrush(b), null, new Rect(r.X, r.Bottom - 0.5, r.Width, 0.5));
            for (var i = 0; TopRows != null && i < TopRows.Length; i++)
                ctx.DrawRectangle(Grey(TopRows[i]), null, new Rect(r.X, r.Y + i * 0.5, r.Width, 0.5));
            for (var i = 0; BottomRows != null && i < BottomRows.Length; i++)
                ctx.DrawRectangle(Grey(BottomRows[i]), null, new Rect(r.X, r.Bottom - (i + 1) * 0.5, r.Width, 0.5));
        }
        if (Border is { } c)
            ctx.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(c), BorderWidth), r.Deflate(BorderWidth / 2), Radius, Radius);
    }
}

/// `TextField(...).textFieldStyle(.roundedBorder)` in dark appearance: 20 pt tall, white text, grey placeholder.
internal sealed class PCField : Panel, IFlexible
{
    public readonly TextBox Box;
    private readonly SText _placeholder;
    private readonly Binding<string> _text;
    private readonly Chrome _chrome;
    private readonly Watch _watch;
    private bool _pushing;
    public Action? OnSubmit { get; set; }

    public PCField(string placeholder, Binding<string> text, bool secure = false, double fontSize = 13)
    {
        _text = text;
        var f = SFont.System(fontSize);
        Box = new TextBox
        {
            Text = Tracker.Untracked(() => text.Value),
            FontFamily = f.AvaloniaFamily,
            FontSize = f.Size,
            LetterSpacing = f.LetterSpacing,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0x0A, 0x60, 0xFF)),
            SelectionForegroundBrush = Brushes.White,
            Padding = new Thickness(11, 0),
        };
        if (secure) Box.PasswordChar = '•';
        _placeholder = new SText(placeholder) { OwnFont = f, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 1, 11, 0) };
        _placeholder.SetValue(Env.LineLimitProperty, 1);
        _placeholder.SetValue(Env.ForegroundProperty, PC.Placeholder);
        _chrome = new Chrome(this) { Margin = new Thickness(1, 1, 0.5, 1) };
        Height = 22;
        Children.Add(_chrome);
        Children.Add(Box);
        Children.Add(_placeholder);
        _placeholder.IsVisible = string.IsNullOrEmpty(Box.Text);
        Box.TextChanged += (_, _) =>
        {
            _placeholder.IsVisible = string.IsNullOrEmpty(Box.Text);
            if (_pushing) return;
            _pushing = true;
            try { _text.Value = Box.Text ?? ""; } finally { _pushing = false; }
        };
        Box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && OnSubmit != null) { OnSubmit(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Box.GotFocus += (_, _) => _chrome.InvalidateVisual();
        Box.LostFocus += (_, _) => _chrome.InvalidateVisual();
        _watch = Watch.Effect(() =>
        {
            var v = _text.Value;
            if (_pushing || v == (Box.Text ?? "")) return;
            Box.Text = v;
            Box.CaretIndex = v.Length;
        });
    }

    public bool IsFlexible(Orientation axis) => axis == Orientation.Horizontal;

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = base.MeasureOverride(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? Math.Max(s.Width, 120) : availableSize.Width, 22);
    }

    // Opaque grey levels of the first / last pixel rows of the AppKit bezel (0.5 pt each), then flat 48.
    private static readonly byte[] ChromeTop = [78, 41, 42, 45];
    private static readonly byte[] ChromeBottom = [129, 79];

    /// The rounded-border bezel of a dark text field, drawn into `size` (shared with `PCDate`).
    internal static void DrawChrome(DrawingContext ctx, Size size, bool focused)
    {
        var r = new Rect(0, 0, Math.Max(0, size.Width - 0.5), size.Height);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(48, 48, 48)), null, r, 5, 5);
        ctx.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)), 0.5), r.Deflate(0.25), 5, 5);
        using (ctx.PushClip(new RoundedRect(r, 5)))
        {
            for (var i = 0; i < ChromeTop.Length; i++)
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(ChromeTop[i], ChromeTop[i], ChromeTop[i])), null, new Rect(0, i * 0.5, r.Width, 0.5));
            for (var i = 0; i < ChromeBottom.Length; i++)
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(ChromeBottom[i], ChromeBottom[i], ChromeBottom[i])), null, new Rect(0, r.Height - (i + 1) * 0.5, r.Width, 0.5));
        }
        if (focused)
            ctx.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x99, 0x0A, 0x60, 0xFF)), 3), r.Inflate(1), 6, 6);
    }

    private sealed class Chrome(PCField f) : Control
    {
        public override void Render(DrawingContext ctx) => DrawChrome(ctx, Bounds.Size, f.Box.IsFocused);
    }
}

/// Dark checkbox (`.toggleStyle(.checkbox)`): 14 pt rounded square, blue with a check when on.
internal sealed class PCCheck : Control
{
    private readonly bool _on;
    public PCCheck(bool on) { _on = on; }
    protected override Size MeasureOverride(Size available) => new(14, 14);

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(0, 0, 14, 14);
        if (_on)
        {
            ctx.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 0.75), r.Inflate(0.375), 3.9, 3.9);
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(22, 112, 225), 0), new GradientStop(Color.FromRgb(21, 98, 196), 1) },
            }, null, r, 3.5, 3.5);
            using (ctx.PushClip(new RoundedRect(r, 3.5)))
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x48, 255, 255, 255)), null, new Rect(0, 0, 14, 0.5));
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(3.3, 7.2), false);
                s.LineTo(new Point(5.9, 9.9));
                s.LineTo(new Point(10.8, 4.3));
                s.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(Color.FromRgb(220, 233, 250)), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
        }
        else
        {
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)),
                new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x59, 255, 255, 255)), 0.5), r.Deflate(0.25), 3.5, 3.5);
        }
    }
}

/// `DatePicker(...).datePickerStyle(.field)` in dark appearance (vi_VN): a text-field bezel showing "dd/MM/yyyy",
/// no later than today. Click the day, month or year, then type digits or use the arrow keys.
internal sealed class PCDate : Panel
{
    private readonly Binding<DateTimeOffset> _value;
    private readonly Watch _watch;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0.5, 10, 0) };
    private readonly Control _chrome;
    private int _part = -1; // 0 day, 1 month, 2 year
    private string _typed = "";

    public PCDate(Binding<DateTimeOffset> value)
    {
        _value = value;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Height = 22;
        _chrome = new ChromeControl(this);
        Children.Add(_chrome);
        Children.Add(_row);
        _watch = Watch.Effect(() => { _ = _value.Value; Refresh(); });
    }

    private (int y, int m, int d) Now() => BankRates.Parts(Tracker.Untracked(() => _value.Value));

    /// One text per segment ("dd", "/", "MM", "/", "yyyy"); the selected one is lit while the field has focus.
    private void Refresh()
    {
        var (y, m, d) = Now();
        string[] parts = [d.ToString("D2", Inv.C), "/", m.ToString("D2", Inv.C), "/", y.ToString("D4", Inv.C)];
        _row.Children.Clear();
        for (var i = 0; i < parts.Length; i++)
        {
            var text = PC.Fit(Text(parts[i]).Font(13), 13).Foreground(SColor.White);
            var lit = IsFocused && i % 2 == 0 && i / 2 == _part;
            var cell = new Border
            {
                Child = text,
                Background = lit ? new ImmutableSolidColorBrush(Color.FromArgb(0xE0, 0x00, 0x6F, 0xFF)) : null,
                CornerRadius = new CornerRadius(3),
                Tag = i / 2,
            };
            if (i % 2 == 0) cell.PointerPressed += OnSegmentPressed;
            _row.Children.Add(cell);
        }
        _chrome.InvalidateVisual();
    }

    private void OnSegmentPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: int part }) return;
        Focus();
        _part = part;
        _typed = "";
        Refresh();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled) return;
        Focus();
        if (_part < 0) _part = 0;
        Refresh();
        e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        if (_part < 0) _part = 0;
        Refresh();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _part = -1;
        _typed = "";
        Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Up: Step(1); e.Handled = true; break;
            case Key.Down: Step(-1); e.Handled = true; break;
            case Key.Left when _part > 0: _part--; _typed = ""; Refresh(); e.Handled = true; break;
            case Key.Right when _part < 2: _part++; _typed = ""; Refresh(); e.Handled = true; break;
            default:
                if (e.KeySymbol is { Length: 1 } s && s[0] is >= '0' and <= '9') { TypeDigit(s[0]); e.Handled = true; }
                break;
        }
    }

    private void Step(int delta)
    {
        var (y, m, d) = Now();
        _typed = "";
        switch (_part < 0 ? 0 : _part)
        {
            case 0:
                var days = DateTime.DaysInMonth(y, m);
                d = (d - 1 + delta + days) % days + 1;
                break;
            case 1: m = (m - 1 + delta + 12) % 12 + 1; break;
            default: y = Math.Clamp(y + delta, 1900, 9999); break;
        }
        Commit(y, m, d);
    }

    private void TypeDigit(char digit)
    {
        var (y, m, d) = Now();
        var part = _part < 0 ? 0 : _part;
        var max = part == 0 ? DateTime.DaysInMonth(y, m) : part == 1 ? 12 : 9999;
        var length = part == 2 ? 4 : 2;
        _typed += digit;
        if (!int.TryParse(_typed, NumberStyles.None, Inv.C, out var v) || v > max) { _typed = digit.ToString(); v = digit - '0'; }
        if (part == 2 ? _typed.Length == 4 : v >= 1)
        {
            if (part == 0) d = v; else if (part == 1) m = v; else y = Math.Max(1900, v);
            Commit(y, m, d);
        }
        if (_typed.Length >= length || v * 10 > max)
        {
            _typed = "";
            _part = Math.Min(2, part + 1);
            Refresh();
        }
    }

    private void Commit(int y, int m, int d)
    {
        d = Math.Min(d, DateTime.DaysInMonth(y, m));
        if (BankRates.Date(y, m, d) is not { } date) return;
        var today = BankRates.StartOfDay(DateTimeOffset.UtcNow);
        if (date > today) date = today;
        _value.Value = date;
    }

    protected override Size MeasureOverride(Size available)
    {
        _row.Measure(Size.Infinity);
        _chrome.Measure(new Size(_row.DesiredSize.Width + 20, 22));
        return new Size(_row.DesiredSize.Width + 20, 22);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _chrome.Arrange(new Rect(final));
        _row.Arrange(new Rect(0, 0, final.Width, final.Height));
        return final;
    }

    private sealed class ChromeControl(PCDate owner) : Control
    {
        public override void Render(DrawingContext ctx) => PCField.DrawChrome(ctx, Bounds.Size, owner.IsFocused);
    }
}

/// The accent-coloured up/down indicator at the end of a pop-up button.
internal sealed class PCPopArrow : Control
{
    protected override Size MeasureOverride(Size available) => new(16, 16);

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(0x00, 0x6F, 0xFF)), null, new Rect(0, 0, 16, 16), 4, 4);
        var pen = new Pen(Brushes.White, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var up = new StreamGeometry();
        using (var s = up.Open())
        {
            s.BeginFigure(new Point(5.2, 6.4), false);
            s.LineTo(new Point(8, 3.8));
            s.LineTo(new Point(10.8, 6.4));
            s.EndFigure(false);
            s.BeginFigure(new Point(5.2, 9.6), false);
            s.LineTo(new Point(8, 12.2));
            s.LineTo(new Point(10.8, 9.6));
            s.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, up);
    }
}

/// Wrapping row (`FlowLayout`): children left to right, new line when the next one does not fit.
internal sealed class PCFlow : Panel, IFlexible
{
    public double Spacing { get; init; } = 5;
    public bool IsFlexible(Orientation axis) => axis == Orientation.Horizontal;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 300 : available.Width;
        double x = 0, y = 0, line = 0;
        foreach (var c in Children)
        {
            c.Measure(Size.Infinity);
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > width) { x = 0; y += line + Spacing; line = 0; }
            x += s.Width + Spacing;
            line = Math.Max(line, s.Height);
        }
        return new Size(width, y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var c in Children)
        {
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { x = 0; y += line + Spacing; line = 0; }
            c.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + Spacing;
            line = Math.Max(line, s.Height);
        }
        return final;
    }
}

/// Vertical scroll view with AppKit's legacy scroller (a 15 pt gutter that only appears when the content overflows).
internal sealed class PCScroll : Panel, IFlexible
{
    private const double Gutter = 15;
    private readonly Control _content;
    private readonly Scroller _bar;
    private double _offset, _extent, _viewport;
    private bool _show;

    public PCScroll(Control content)
    {
        _content = content;
        _bar = new Scroller(this);
        ClipToBounds = true;
        Children.Add(content);
        Children.Add(_bar);
    }

    public bool IsFlexible(Orientation axis) => true;

    protected override Size MeasureOverride(Size available)
    {
        var w = available.Width;
        var h = available.Height;
        _content.Measure(new Size(w, double.PositiveInfinity));
        _show = !double.IsInfinity(h) && _content.DesiredSize.Height > h + 0.5;
        if (_show) _content.Measure(new Size(Math.Max(0, w - Gutter), double.PositiveInfinity));
        _extent = _content.DesiredSize.Height;
        _bar.IsVisible = _show;
        _bar.Measure(new Size(Gutter, double.IsInfinity(h) ? _extent : h));
        return new Size(double.IsInfinity(w) ? _content.DesiredSize.Width : w, double.IsInfinity(h) ? _extent : h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _viewport = final.Height;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent - _viewport));
        _content.Arrange(new Rect(0, -_offset, Math.Max(0, final.Width - (_show ? Gutter : 0)), _extent));
        _bar.Arrange(new Rect(final.Width - Gutter, 0, Gutter, final.Height));
        return final;
    }

    internal double Offset
    {
        get => _offset;
        set
        {
            var v = Math.Clamp(value, 0, Math.Max(0, _extent - _viewport));
            if (Math.Abs(v - _offset) < 0.01) return;
            _offset = v;
            InvalidateArrange();
            _bar.InvalidateVisual();
        }
    }

    internal double Extent => _extent;
    internal double Viewport => _viewport;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!_show) return;
        Offset -= e.Delta.Y * 48;
        e.Handled = true;
    }

    private sealed class Scroller(PCScroll owner) : Control
    {
        private bool _drag;
        private double _grab;
        private const double Inset = 3;

        private (double top, double len) Thumb()
        {
            var track = Math.Max(1, Bounds.Height - Inset * 2);
            var len = Math.Max(24, track * owner.Viewport / Math.Max(1, owner.Extent));
            var range = Math.Max(0, owner.Extent - owner.Viewport);
            var top = Inset + (range <= 0 ? 0 : owner.Offset / range * (track - len));
            return (top, len);
        }

        public override void Render(DrawingContext ctx)
        {
            var r = new Rect(Bounds.Size);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(38, 38, 38)), null, r);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(58, 58, 58)), null, new Rect(0, 0, 0.5, r.Height));
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(42, 42, 42)), null, new Rect(0.5, 0, 0.5, r.Height));
            var (top, len) = Thumb();
            ctx.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(Color.FromRgb(30, 30, 30)), 0.5), new Rect(3.75, top - 0.25, 8.5, len + 0.5), 4.25, 4.25);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(104, 104, 104)), null, new Rect(4, top, 8, len), 4, 4);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var y = e.GetPosition(this).Y;
            var (top, len) = Thumb();
            if (y >= top && y <= top + len) { _drag = true; _grab = y - top; e.Pointer.Capture(this); }
            else owner.Offset += (y < top ? -1 : 1) * owner.Viewport * 0.9;
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_drag) return;
            var track = Math.Max(1, Bounds.Height - Inset * 2);
            var (_, len) = Thumb();
            var range = Math.Max(0, owner.Extent - owner.Viewport);
            var t = (e.GetPosition(this).Y - _grab - Inset) / Math.Max(1, track - len);
            owner.Offset = Math.Clamp(t, 0, 1) * range;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _drag = false;
            e.Pointer.Capture(null);
        }
    }
}

// MARK: - Buttons and pickers (dark)

internal static class PCButtons
{
    /// Bordered push button ("Tải"): 20 pt tall, grey; dimmer when disabled.
    public static Control Push(Control label, Action action, bool enabled)
    {
        var level = (byte)(enabled ? 83 : 55);
        var box = new PCBox
        {
            Inset = new Thickness(0.5, 1),
            Fill = Color.FromRgb(level, level, level),
            Radius = 5,
            TopRows = enabled ? [118, 90] : [72, 58],
            Outline = (byte)(enabled ? 23 : 25),
            ShadowRows = enabled ? [21, 23, 25] : [23, 25],
        };
        // The 22 pt frame holds the 20 pt bezel with its 0.5 pt outline and the shadow rows.
        var b = Button(action, label.PaddingH(8).Frame(height: 22).Background(box));
        b.IsEnabled = enabled;
        return b;
    }

    /// AppKit centres the title in the 22 pt frame around the bezel, so it sits a little above the bezel's centre.
    public static Control PushTitle(string t, bool enabled) =>
        PC.Fit(Text(t).Font(13), 13).Foreground(enabled ? SColor.Hex(0xE6E6E6) : SColor.Hex(0x696969)).LineLimit(1).Offset(0, -1.25);

    /// `.buttonStyle(.borderedProminent).controlSize(.large)`: full width, 28 pt, accent.
    public static Control Prominent(Control label, Action action, bool enabled)
    {
        var box = enabled
            ? new PCBox
            {
                Fill = Color.FromRgb(22, 92, 224),
                Radius = 6,
                TopRows = null,
                TopLine = Color.FromArgb(0xA0, 140, 190, 255),
                Outline = 24,
                ShadowRows = [22, 24],
            }
            : new PCBox
            {
                Fill = Color.FromRgb(41, 41, 41),
                FillBottom = Color.FromRgb(36, 36, 36),
                Radius = 6,
                TopRows = [67, 44],
                Outline = 24,
                ShadowRows = [23, 24, 25],
            };
        var b = Button(action, label.FillWidth(Alignment.Center).Frame(height: 28).Background(box));
        b.IsEnabled = enabled;
        return b.FillWidth();
    }

    public static Control ProminentLabel(string title, bool enabled, bool busy) =>
        HStack(6,
            busy ? new ProgressView { Size = ControlSize.Small } : null,
            PC.Fit(Text(title).Font(13), 13).Foreground(enabled ? SColor.White : SColor.Hex(0x5D5D5D)).LineLimit(1).Offset(0, -1.25));

    /// `Label(title, systemImage:)` inside a prominent button.
    public static Control ProminentIconLabel(string title, string symbol) =>
        HStack(6,
            Image(symbol).Font(13),
            PC.Fit(Text(title).Font(13), 13).Foreground(SColor.White).LineLimit(1).Offset(0, -1.25));

    /// `Picker(...).labelsHidden().fixedSize()`: the pop-up button, sized to its widest title.
    public static Control PopUp<T>(Binding<T> selection, IReadOnlyList<(T value, string title)> options)
    {
        var face = SFont.System(13);
        var type = new Typeface(face.AvaloniaFamily, FontStyle.Normal, face.AvaloniaWeight);
        var widest = options.Select(o => new FormattedText(o.title, Inv.C, FlowDirection.LeftToRight, type, face.Size, Brushes.White).Width)
            .DefaultIfEmpty(0).Max();
        return View(() =>
        {
            var cur = selection.Value;
            var title = options.FirstOrDefault(o => Equals(o.value, cur)).title ?? "";
            var box = new PCBox
            {
                Inset = new Thickness(0.5, 1),
                Fill = Color.FromRgb(83, 83, 83),
                Radius = 5,
                TopRows = [118, 90],
                Outline = 23,
                ShadowRows = [21, 23, 25],
            };
            var label = HStack(6,
                    PC.Fit(Text(title).Font(13), 13).Foreground(SColor.Hex(0xE6E6E6)).LineLimit(1).Offset(0, -1.25)
                        .Frame(width: Math.Ceiling(widest), alignment: Alignment.Leading),
                    new PCPopArrow())
                .PaddingLeading(9).PaddingTrailing(3.5).Frame(height: 22).Background(box);
            Control? anchor = null;
            anchor = Button(() => Menus.Popup(anchor!, options.Select(o =>
            {
                var v = o.value;
                return MenuEntry.Item(o.title, () => selection.Value = v, Equals(v, Tracker.Untracked(() => selection.Value)));
            })), label);
            return anchor;
        });
    }

    /// Plain-looking link button (`.buttonStyle(.link)` under a white foreground style).
    public static Control Link(string title, double size, Action action) =>
        Button(action, PC.Fit(Text(title).Font(size), size).Foreground(SColor.White)).Cursor(StandardCursorType.Hand);

    /// A row `Toggle` with `.checkbox` style.
    public static Control CheckRow(bool on, Action toggle, Control label, VAlign align = VAlign.Center) =>
        Button(toggle, HStack(align, 5, new PCCheck(on), label));

    /// `Picker(...).pickerStyle(.segmented)` in dark appearance: 22 pt track, selected segment lifted.
    public static Control Segmented<T>(Binding<T> selection, IReadOnlyList<(T value, string title)> options)
    {
        return View(() =>
        {
            var cur = selection.Value;
            var items = new List<Control?>();
            for (var i = 0; i < options.Count; i++)
            {
                var (v, t) = options[i];
                var on = Equals(v, cur);
                if (i > 0) items.Add(ColorView(SColor.Black.Opacity(on || Equals(options[i - 1].value, cur) ? 0 : 0.18)).Frame(width: 1, height: 12));
                // Segments split the width equally; the padding only has to leave room for the widest title (4 segments of 83 pt).
                var label = Text(t).Font(13, Weight.Regular).LineLimit(1).Offset(0, -0.5).PaddingH(4).Frame(height: 20).FillWidth();
                Control body = on
                    ? label.Background(new PCBox { Fill = Color.FromRgb(92, 92, 92), Radius = 5, TopRows = [123, 97], BottomLine = Color.FromArgb(0x50, 0, 0, 0) })
                    : label;
                items.Add(Button(() => selection.Value = v, body));
            }
            return HStack(0, items.ToArray()).Padding(1)
                .Background(new PCBox { Fill = Color.FromRgb(38, 38, 38), Radius = 6, Border = Color.FromArgb(0x14, 255, 255, 255), TopRows = [55, 34, 35, 36], BottomRows = [85, 55] });
        });
    }

    /// Mini `Stepper`: label then a 9 x 14 pt up/down control.
    public static Control Stepper(Control label, Binding<int> value, int min, int max)
    {
        Control Half(string symbol, bool flip, Action act)
        {
            Control icon = Image(symbol).Font(6, Weight.Bold).Foreground(SColor.White.Opacity(0.9));
            if (flip) icon = icon.RotationEffect(180);
            return Button(act, icon.Frame(width: 9.5, height: 7));
        }
        var box = new PCBox { Fill = Color.FromRgb(93, 93, 93), Radius = 3.5 };
        var control = VStack(0,
                Half("chevron.down", true, () => value.Value = Math.Min(max, Tracker.Untracked(() => value.Value) + 1)),
                ColorView(SColor.Hex(0x4A4A4A)).Frame(width: 9.5, height: 0.5),
                Half("chevron.down", false, () => value.Value = Math.Max(min, Tracker.Untracked(() => value.Value) - 1)))
            .Background(box);
        return HStack(7, label, control).Help("Số lượng đã mua");
    }
}

// MARK: - View

public sealed class PurchaseCostPanel : Decorator, IFlexible
{
    private readonly PurchaseCostEntry _entry;
    private readonly Action _onClose;
    private readonly VietFlow _account = VietFlow.Shared;
    private Watch? _previewWatch, _bankWatch, _fundingWatch;
    private string? _lastPreviewKey, _lastBankKey;
    private VFFunding _lastFunding;
    private CancellationTokenSource? _debounce;

    public PurchaseCostPanel(PurchaseCostEntry entry, Action onClose)
    {
        _entry = entry;
        _onClose = onClose;
        Child = Body();
    }

    public bool IsFlexible(Orientation axis) => true;

    // `.task(id: entry.previewKey)`: a 350 ms debounce, then the server-side preview.
    // `.task(id: entry.bankKey)`: the bank's rate board. `.onChange(of: entry.funding)`: refill the invoice total.
    // Each runs when its id changes (and once on appearing), not on every change of what it read.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _previewWatch ??= Watch.Effect(() =>
        {
            var key = _entry.PreviewKey;
            if (key == _lastPreviewKey) return;
            _lastPreviewKey = key;
            Debounce();
        });
        _bankWatch ??= Watch.Effect(() =>
        {
            var key = _entry.BankKey;
            if (key == _lastBankKey) return;
            _lastBankKey = key;
            Tracker.Untracked(() => { _ = _entry.LoadBankQuote(); return 0; });
        });
        if (_fundingWatch == null)
        {
            _lastFunding = Tracker.Untracked(() => _entry.Funding);
            _fundingWatch = Watch.Effect(() =>
            {
                var funding = _entry.Funding;
                if (funding == _lastFunding) return;
                _lastFunding = funding;
                Tracker.Untracked(() => { _entry.FillTotalFromHints(); return 0; });
            });
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _previewWatch?.Dispose();
        _previewWatch = null;
        _bankWatch?.Dispose();
        _bankWatch = null;
        _fundingWatch?.Dispose();
        _fundingWatch = null;
        _lastPreviewKey = null;
        _lastBankKey = null;
        _debounce?.Cancel();
    }

    private async void Debounce()
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        try { await Task.Delay(350, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        await _entry.RefreshPreview();
    }

    private Control Body() =>
        VStack(HAlign.Leading, 0,
                Header(),
                ColorView(PC.Rule).Frame(height: 1),
                View(() =>
                {
                    if (!_account.IsLoggedIn) return new VietFlowLoginView(() => _ = _entry.LoadOrder());
                    if (_entry.Phase == PurchaseCostEntry.PhaseKind.Saved && _entry.Result is { } r) return new PCScroll(SavedView(r));
                    return new PCScroll(View(Form));
                }))
            .Foreground(SColor.White)
            .Background(PC.Bg);

    private Control Header() => View(() =>
    {
        Control? menu = null;
        if (_account.IsLoggedIn)
        {
            var user = _account.UserName ?? "VietFlow";
            Control? anchor = null;
            anchor = Button(() => Menus.Popup(anchor!, new MenuEntry[]
                {
                    MenuEntry.Header(user),
                    MenuEntry.Item("Đăng xuất VietFlow", _account.Logout),
                }, Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight),
                Image("person.crop.circle").Font(12.6).Foreground(SColor.White.Opacity(0.86)).Frame(width: 21).Offset(0, 0.7)).Help(user);
            menu = anchor;
        }
        return HStack(8,
                PC.Fit(Text("Nhập chi phí PO").Font(13, Weight.Semibold), 13),
                Spacer(),
                menu,
                Button(_onClose, Image("xmark").Font(11, Weight.Semibold).Offset(0, 0.7)).Help("Đóng"))
            .PaddingH(14).PaddingTop(34).PaddingBottom(10);
    });

    // MARK: Form

    private Control Form()
    {
        var e = _entry;
        return VStack(HAlign.Leading, 14,
            OrderSection(),
            e.Hints.Amounts.Count > 0 || e.Hints.Ids.Count > 0 ? HintsSection() : null,
            FieldsSection(),
            FundingSection(),
            e.Preview is { } p ? SplitSection(p) : null,
            Footer()).Padding(14);
    }

    private Control OrderSection()
    {
        var e = _entry;
        var po = Keep("po", () => new PCField("POVF…", PC.Bind(() => e.PoCode, v => e.PoCode = v)) { OnSubmit = () => _ = e.LoadOrder() });
        var load = PCButtons.Push(
            e.LoadingOrder ? new ProgressView { Size = ControlSize.Small } : PCButtons.PushTitle("Tải", e.PoCode.Length > 0),
            () => _ = e.LoadOrder(), e.PoCode.Length > 0 && !e.LoadingOrder);
        return VStack(HAlign.Leading, 8,
            PC.Lbl("Mã PO"),
            HStack(6, po, load),
            e.Order == null && e.PoCode.Length == 0 && e.Context.PoCode == null
                ? PC.Cap("Không thấy mã PO trong tin nhắn quanh ảnh, nhập tay để tải.") : null,
            e.Order is { } o ? OrderCard(o) : null);
    }

    private Control OrderCard(VFPurchaseOrder o)
    {
        var e = _entry;
        var rows = new List<Control?>
        {
            HStack(PC.Fit(Text(o.CustomerName ?? o.OrderCode).Font(12.5, Weight.Semibold), 12.5), Spacer(), PC.Fit(Text(o.CurrencyCode).Font(11), 11).Foreground(PC.Secondary)),
            o.IsCancelled ? PC.Fit(Text("PO đã hủy").Font(11.5), 11.5).Foreground(PC.Red) : null,
        };
        foreach (var item in o.Items)
        {
            var sub = item.IsFreeItem ? "Hàng tặng"
                : $"Giá bán {PurchaseCostEntry.Money(item.SellingPriceForeign, o.CurrencyCode)} / sp"
                  + (item.CostPriceForeign is { } cost ? $" · đã nhập vốn {PurchaseCostEntry.Money(cost, o.CurrencyCode)}" : "");
            var id = item.Id;
            var qty = e.Quantities.TryGetValue(id, out var q) ? q : item.QuantityRequested;
            rows.Add(HStack(VAlign.Top, 6,
                VStack(HAlign.Leading, 2,
                    PC.Fit(Text(item.ProductName).Font(11.5), 11.5).LineLimit(2),
                    PC.Fit(Text(sub).Font(10.5), 10.5).Foreground(PC.Secondary)),
                Spacer(4),
                item.IsFreeItem ? null : PCButtons.Stepper(
                    PC.Digits(PC.Fit(Text($"×{qty}").Font(11.5), 11.5)),
                    new Binding<int>(() => e.Quantities.TryGetValue(id, out var cur) ? cur : item.QuantityRequested,
                        v => e.Quantities = e.Quantities.SetItem(id, Math.Max(1, v))), 1, 999)));
        }
        if (o.Items.Any(i => i.CostPriceForeign != null))
            rows.Add(PC.Cap("PO đã có giá vốn, lưu sẽ ghi đè và phân bổ lại tiền.").Foreground(PC.Orange.Opacity(0.5)));
        return VStack(HAlign.Leading, 6, rows.ToArray()).Padding(10).Background(RoundedRectangle(8).Fill(PC.Card));
    }

    private Control HintsSection()
    {
        var e = _entry;
        var rows = new List<Control?> { PC.Lbl("Từ ảnh invoice") };
        var cur = e.AmountCurrency;
        var amounts = e.Hints.AmountsFor(cur);
        if (amounts.Count > 0)
        {
            rows.Add(Chips(amounts.Select(a => (PurchaseCostEntry.Money(a.Value, a.Currency),
                e.TypedAmount == a.Value && e.AmountCurrency == a.Currency, (Action)(() => e.PickHintAmount(a))))));
            if (!amounts.Any(a => a.Currency == cur))
                rows.Add(PC.CapTint(e.Funding == VFFunding.Bank
                    ? $"Invoice không ghi tiền {cur}; bấm một số để chuyển loại tiền thanh toán theo nó."
                    : $"Invoice không ghi tiền {cur}; kiểm tra lại số đã trả theo {cur}, hoặc dùng Bank VN để quy đổi.", PC.Orange));
        }
        if (e.Hints.Ids.Count > 0)
            rows.Add(Chips(e.Hints.Ids.Select(id => (id, e.OrderNumber == id, (Action)(() => e.OrderNumber = id)))));
        rows.Add(PC.Cap("Bấm để điền: số tiền vào Tổng đã trả, mã vào Order. Hoặc bôi đen chữ trên ảnh rồi Ctrl+C, Ctrl+V."));
        return VStack(HAlign.Leading, 6, rows.ToArray());
    }

    /// Wrapping row of tappable values read from the photo.
    private static Control Chips(IEnumerable<(string title, bool on, Action pick)> items)
    {
        var flow = new PCFlow { Spacing = 5 };
        foreach (var (title, on, pick) in items)
        {
            flow.Children.Add(Button(pick, PC.Digits(PC.Fit(Text(title).Font(11.5), 11.5)).Foreground(SColor.White)
                .PaddingH(8).PaddingV(3).Background(Capsule().Fill(on ? PC.ChipOn : PC.ChipOff))));
        }
        return flow;
    }

    private Control FieldsSection()
    {
        var e = _entry;
        Control? converted = null;
        if (e.CrossCurrency && e.TypedAmount is { } typed && e.PaidAmount is { } paid && e.Order?.CurrencyCode is { } cur)
            converted = PC.Cap($"= {PurchaseCostEntry.Money(paid, cur)} ghi vào PO (quy đổi qua {e.Bank.Raw()} {e.BankColumn.Label()}, "
                               + $"{PurchaseCostEntry.Money(typed, e.PayCurrency)} × {PurchaseCostEntry.FormatRate(e.BankBaseRate ?? 0)}"
                               + $" ÷ {PurchaseCostEntry.FormatRate(e.PoBaseRate ?? 0)})");
        return VStack(HAlign.Leading, 8,
            PC.Lbl($"Tổng đã trả ({e.AmountCurrency})"),
            Keep("total", () => new PCField("14.24", PC.Bind(() => e.TotalPaid, v => e.TotalPaid = v))),
            converted,
            PC.Lbl("Order number (mã đơn shop)"),
            Keep("orderNumber", () => new PCField("VD: 111-1234567-1234567", PC.Bind(() => e.OrderNumber, v => e.OrderNumber = v))));
    }

    private Control FundingSection()
    {
        var e = _entry;
        return VStack(HAlign.Leading, 8,
            PC.Lbl("Nguồn tiền"),
            PCButtons.Segmented(new Binding<VFFunding>(() => e.Funding, v => e.Funding = v),
                VFFundingExt.All.Select(f => (f, f.Label())).ToList()),
            e.Funding switch
            {
                VFFunding.Pool => PoolFunding(),
                VFFunding.GiftCard => GiftFunding(),
                VFFunding.Manual => ManualFunding(),
                _ => BankFunding(),
            });
    }

    private Control BankFunding()
    {
        var e = _entry;
        var rows = new List<Control?>
        {
            PCButtons.Segmented(new Binding<VNBank>(() => e.Bank, e.SelectBank), VNBankExt.All.Select(b => (b, b.Raw())).ToList()),
            HStack(
                PC.Lbl("Loại tiền thanh toán"),
                Spacer(),
                PCButtons.PopUp(new Binding<string>(() => e.PayCurrency, e.SelectPayCurrency),
                    PayCurrencies(e.PayCurrency).Select(c => (c, c)).ToList())),
            PC.Cap(e.PayCurrencySource == "invoice" ? "Tự chọn theo tiền trên ảnh invoice, đổi được."
                : e.PayCurrencySource == "chọn tay" ? "Chọn tay." : "Mặc định USD (không đọc được loại tiền trên ảnh)."),
            PC.Lbl($"Tỷ giá {e.PayCurrency} của {e.Bank.Raw()}"),
            HStack(6, BankRateColumnExt.All.Select(ColumnCard).ToArray()),
            HStack(
                PC.Lbl("Ngày tỷ giá"),
                Spacer(),
                Keep("rateDate", () => new PCDate(new Binding<DateTimeOffset>(() => e.RateDate, e.SetRateDate)))),
        };
        if (e.BankQuote is { } q && BankRates.DayString(q.Day) != BankRates.DayString(e.RateDate))
            rows.Add(PC.CapTint($"{q.Bank.Raw()} không có bảng ngày {PurchaseCostEntry.Day(e.RateDate)}, dùng bảng ngày {PurchaseCostEntry.Day(q.Day)}.", PC.Orange));
        else
            rows.Add(PC.Cap($"Theo {e.RateDateSource}."));
        rows.Add(PC.Lbl("Phí giao dịch (%)"));
        rows.Add(Keep("bankFee", () => new PCField("VD: 1,1", PC.Bind(() => e.BankFee, e.SetBankFee))));
        if (e.BankBaseRate is { } baseRate && e.BankRate is { } rate)
        {
            var card = new List<Control?>
            {
                HStack(
                    PC.Fit(Text("Tỷ giá mua").Font(12, Weight.Semibold), 12),
                    Spacer(),
                    PC.Digits(PC.Fit(Text(PurchaseCostEntry.FormatRate(rate)).Font(13, Weight.Semibold), 13))),
                PC.Cap($"{e.PayCurrency} {PurchaseCostEntry.FormatRate(baseRate)} × (1 + {PurchaseCostEntry.FormatPercent(e.BankFeeValue)}%) = {PurchaseCostEntry.FormatRate(rate)}"),
            };
            if (e.CrossCurrency && e.Order?.CurrencyCode is { } cur && e.TypedAmount is { } typed && e.BankChargeVnd is { } vnd)
            {
                card.Add(PC.Cap($"Bank trừ: {PurchaseCostEntry.Money(typed, e.PayCurrency)} × {PurchaseCostEntry.FormatRate(rate)} = {PurchaseCostEntry.Vnd(vnd)}"));
                if (e.PaidAmount is { } paid && e.BankRecordedRate is { } recorded)
                {
                    card.Add(new DividerView { Color = SColor.White.Opacity(0.1) });
                    card.Add(HStack(
                        PC.Fit(Text($"Tỷ giá ghi vào PO ({cur})").Font(12, Weight.Semibold), 12),
                        Spacer(),
                        PC.Digits(PC.Fit(Text(PurchaseCostEntry.FormatRate(recorded)).Font(13, Weight.Semibold), 13))));
                    card.Add(PC.Cap($"{PurchaseCostEntry.Vnd(vnd)} ÷ {PurchaseCostEntry.Money(paid, cur)}, để giá vốn VND đúng bằng số bank trừ."));
                }
            }
            rows.Add(VStack(HAlign.Leading, 3, card.ToArray()).Padding(10).Background(RoundedRectangle(8).Fill(PC.Card)));
        }
        else if (e.LoadingBank)
            rows.Add(HStack(6, new ProgressView { Size = ControlSize.Small }, PC.Cap($"Đang lấy tỷ giá {e.Bank.Raw()}…")));
        else if (e.BankError is { } err)
            rows.Add(PC.CapTint(err, PC.Red));
        return VStack(HAlign.Leading, 8, rows.ToArray());
    }

    /// Currencies both VCB and TCB publish boards for.
    private static List<string> PayCurrencies(string including)
    {
        var list = new List<string> { "USD", "EUR", "GBP", "JPY", "AUD", "CAD", "CHF", "SGD", "CNY", "HKD", "KRW", "THB" };
        if (!list.Contains(including)) list.Add(including);
        return list;
    }

    private Control ColumnCard(BankRateColumn c)
    {
        var e = _entry;
        var value = e.BankQuote?.Value(c);
        var selected = e.BankColumn == c;
        var body = VStack(2,
                PC.Fit(Text($"{(int)c} · {c.Label()}").Font(10.5), 10.5).Foreground(PC.Secondary),
                PC.Digits(PC.Fit(Text(value is { } v ? PurchaseCostEntry.FormatRate(v) : "—").Font(12.5, Weight.Semibold), 12.5)))
            .FillWidth().PaddingV(7)
            .Background(RoundedRectangle(7).Fill(selected ? PC.AccentBlue.Opacity(0.3) : PC.Card))
            .Overlay(RoundedRectangle(7).StrokeBorder(selected ? PC.AccentBlue : SColor.Clear, 1));
        return Button(() => e.SelectColumn(c), body).FillWidth().Help($"Cột {(int)c}: {c.Label()}");
    }

    private Control PoolFunding()
    {
        var e = _entry;
        var cur = e.Order?.CurrencyCode ?? "USD";
        var rows = new List<Control?>();
        if (e.PoolPreview is { } p)
        {
            foreach (var a in p.Allocations)
                rows.Add(PC.Row(a.Source.Length == 0 ? "#" + a.BatchId : a.Source, $"{PurchaseCostEntry.Money(a.Amount, cur)} @ {PurchaseCostEntry.FormatRate(a.Rate)}"));
            rows.Add(PC.Row("Tỷ giá FIFO", PurchaseCostEntry.FormatRate(p.WeightedRate)));
            if (!p.IsSufficient)
            {
                rows.Add(PC.Cap($"Quỹ thiếu {PurchaseCostEntry.Money(p.Shortfall, cur)}.").Foreground(PC.Orange.Opacity(0.5)));
                rows.Add(RateField("Tỷ giá cho phần thiếu"));
            }
        }
        else if (e.Order == null) rows.Add(PC.Cap("Tải PO trước để xem quỹ sẽ trừ cục tiền nào."));
        else if (e.PoolError is { } err) rows.Add(PC.CapTint($"Không xem trước được quỹ: {err}", PC.Red));
        else rows.Add(PC.Cap("Trừ quỹ ngoại tệ thường theo thứ tự cũ trước (FIFO), như dialog trên web."));
        return VStack(HAlign.Leading, 6, rows.ToArray());
    }

    private Control GiftFunding()
    {
        var e = _entry;
        var cur = e.Order?.CurrencyCode ?? "USD";
        var rows = new List<Control?>();
        if (e.Order == null) rows.Add(PC.Cap("Tải PO trước để xem các cục Gift Card."));
        else if (e.GiftCardsError is { } err) rows.Add(PC.CapTint($"Không tải được Gift Card: {err}", PC.Red));
        else if (e.GiftCards.Count == 0) rows.Add(PC.Cap($"Không có cục Gift Card {e.Order?.CurrencyCode ?? ""} nào còn số dư."));
        foreach (var gc in e.GiftCards)
        {
            var id = gc.Id;
            var on = e.SelectedGiftCards.Contains(id);
            rows.Add(PCButtons.CheckRow(on,
                () => e.SelectedGiftCards = on ? e.SelectedGiftCards.RemoveAll(x => x == id) : e.SelectedGiftCards.RemoveAll(x => x == id).Add(id),
                VStack(HAlign.Leading, 1,
                    PC.Fit(Text($"#{gc.Id} · {gc.Source ?? "Gift Card"}").Font(12), 12),
                    PC.Fit(Text($"{PurchaseCostEntry.FormatRate(gc.BuyRate)} ₫ · còn {PurchaseCostEntry.Money(gc.RemainingAmount, gc.CurrencyCode)}").Font(10.5), 10.5).Foreground(PC.Secondary)),
                VAlign.Top));
        }
        if (e.SelectedGiftCards.Count > 0 && e.GiftShortfall > 0)
        {
            rows.Add(PC.Cap($"Gift Card thiếu {PurchaseCostEntry.Money(e.GiftShortfall, cur)}.").Foreground(PC.Orange.Opacity(0.5)));
            rows.Add(PCButtons.Segmented(new Binding<bool>(() => e.RemainderFromPool, v => e.RemainderFromPool = v),
                new List<(bool, string)> { (false, "Tỷ giá tay"), (true, "Quỹ FIFO") }));
            if (!e.RemainderFromPool) rows.Add(RateField("Tỷ giá cho phần thiếu"));
        }
        return VStack(HAlign.Leading, 6, rows.ToArray());
    }

    private Control ManualFunding()
    {
        var e = _entry;
        var rows = new List<Control?> { RateField("Tỷ giá mua") };
        if (e.Order?.BaseRate is { } baseRate)
            rows.Add(PCButtons.Link($"Dùng tỷ giá cơ sở {PurchaseCostEntry.FormatRate(baseRate)}", 11, () => e.Rate = PurchaseCostEntry.FormatRate(baseRate)));
        rows.Add(PC.Lbl("Nguồn ghi vào tin nhắn"));
        rows.Add(Keep("source", () => new PCField("VD: USDT, Redotcard, PTM 4004", PC.Bind(() => e.SourceNote, v => e.SourceNote = v))));
        if (e.Context.Rate is { } r)
            rows.Add(PC.Cap($"Lấy từ tin nhắn: {(e.Context.Source is { } s ? s + " " : "")}{PurchaseCostEntry.FormatRate(r)}"));
        return VStack(HAlign.Leading, 6, rows.ToArray());
    }

    private Control RateField(string title)
    {
        var e = _entry;
        return VStack(HAlign.Leading, 4,
            PC.Lbl(title),
            Keep("rate", () => new PCField("26.350", PC.Bind(() => e.Rate, v => e.Rate = v))));
    }

    private Control SplitSection(VFCostResult p)
    {
        var cur = p.PurchaseOrder.CurrencyCode;
        var rows = new List<Control?> { PC.Lbl("Chia vào sản phẩm") };
        foreach (var i in p.Items.Where(i => !i.IsFreeItem))
            rows.Add(HStack(VAlign.Center, 6,
                PC.Fit(Text(i.ProductName).Font(11.5), 11.5).LineLimit(1),
                Spacer(6),
                PC.Digits(PC.Fit(Text($"{PurchaseCostEntry.Money(i.SellingTotal, cur)} → {PurchaseCostEntry.Money(i.LineTotal, cur)}").Font(11.5), 11.5))));
        var tone = p.ProfitForeign > 0.004 ? PC.Green : p.ProfitForeign < -0.004 ? PC.Red : PC.Secondary;
        rows.Add(HStack(
            PC.Fit(Text(Math.Abs(p.ProfitForeign) < 0.005 ? "Hòa vốn" : p.ProfitForeign > 0 ? "Lãi" : "Lỗ").Font(12, Weight.Semibold), 12),
            Spacer(),
            PC.Digits(PC.Fit(Text(PurchaseCostEntry.Money(Math.Abs(p.ProfitForeign), cur)).Font(12, Weight.Semibold), 12))).Foreground(tone));
        if (Math.Abs(p.RoundingDrift) >= 0.005)
            rows.Add(PC.Cap($"Đơn giá làm tròn 2 số lẻ nên lệch {PurchaseCostEntry.Money(p.RoundingDrift, cur)}."));
        return VStack(HAlign.Leading, 6, rows.ToArray()).Padding(10).Background(RoundedRectangle(8).Fill(PC.Card));
    }

    private Control Footer()
    {
        var e = _entry;
        var saving = e.Phase == PurchaseCostEntry.PhaseKind.Saving;
        var enabled = e.Problem == null && !saving;
        var rows = new List<Control?>
        {
            PC.Lbl("Ghi chú (tuỳ chọn)"),
            Keep("note", () => new PCField("", PC.Bind(() => e.Note, v => e.Note = v))),
            PCButtons.CheckRow(e.SendToChat, () => e.SendToChat = !e.SendToChat,
                PC.Fit(Text("Gửi tin xác nhận vào cuộc trò chuyện (trả lời ảnh này)").Font(11.5), 11.5)),
            e.Error is { } err ? PC.Fit(Text(err).Font(11.5), 11.5).Foreground(PC.Red) : null,
            PCButtons.Prominent(
                PCButtons.ProminentLabel(e.SendToChat ? "Lưu vào VietFlow & gửi" : "Lưu vào VietFlow", enabled, saving),
                () => _ = e.Save(), enabled),
            e.Problem is { } problem && (e.Order != null || e.PoCode.Length > 0) ? PC.Cap(problem) : null,
        };
        return VStack(HAlign.Leading, 8, rows.ToArray());
    }

    // MARK: Saved

    private Control SavedView(VFCostResult r)
    {
        var e = _entry;
        var cur = r.PurchaseOrder.CurrencyCode;
        var paidIn = e.CrossCurrency && e.TypedAmount is { } typed ? PurchaseCostEntry.Money(typed, e.PayCurrency) + " = " : "";
        var summary = new List<Control?>
        {
            PC.Row("PO", r.PurchaseOrder.OrderCode),
            PC.Row("Đã trả", paidIn + PurchaseCostEntry.Money(r.TotalPaid, cur)),
            r.AverageRate is { } rate ? PC.Row("Tỷ giá TB", PurchaseCostEntry.FormatRate(rate)) : null,
            r.CostVnd is { } cost ? PC.Row("Giá vốn", PurchaseCostEntry.Vnd(cost)) : null,
        };
        var rows = new List<Control?>
        {
            HStack(6, Image("checkmark.circle.fill"), Text("Đã lưu vào VietFlow")).Font(13, Weight.Semibold).Foreground(PC.Green),
            VStack(HAlign.Leading, 4, summary.ToArray()).Padding(10).Background(RoundedRectangle(8).Fill(PC.Card)),
            CostReportSection.Build(e, r),
        };
        if (e.SentText is { } sent)
        {
            rows.Add(PC.Lbl("Đã gửi vào cuộc trò chuyện"));
            rows.Add(SelectableText(sent).Padding(10).FillWidth(Alignment.Leading).Background(RoundedRectangle(8).Fill(PC.Card)));
        }
        else if (e.Sending)
            rows.Add(HStack(new ProgressView { Size = ControlSize.Small }, PC.Cap("Đang gửi tin xác nhận…")));
        else if (e.SendError is { } sendError)
        {
            rows.Add(PC.Fit(Text($"Chưa gửi được tin xác nhận: {sendError}").Font(11.5), 11.5).Foreground(PC.Orange));
            rows.Add(PCButtons.Push(PCButtons.PushTitle("Gửi lại tin xác nhận", true), () => _ = e.Send(), true));
        }
        else rows.Add(PCButtons.Push(PCButtons.PushTitle("Gửi tin xác nhận vào cuộc trò chuyện", true), () => _ = e.Send(), true));
        rows.Add(PCButtons.Link("Sửa lại", 11.5, e.EditAgain));
        return VStack(HAlign.Leading, 12, rows.ToArray()).Padding(14);
    }

    /// `.textSelection(.enabled)`.
    private static Control SelectableText(string text)
    {
        var f = SFont.System(11.5);
        return new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = f.AvaloniaFamily,
            FontSize = f.Size,
            LetterSpacing = f.LetterSpacing,
            Foreground = Brushes.White,
            SelectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0x0A, 0x60, 0xFF)),
        };
    }
}

// MARK: - Login

/// VietFlow sign-in, shown in place of the form until an account is connected.
public sealed class VietFlowLoginView : Decorator, IFlexible
{
    private readonly Action _onLogin;
    private readonly State<string> _email = new("");
    private readonly State<string> _password = new("");
    private readonly State<string> _server = new(VietFlow.Shared.BaseUrl);
    private readonly State<bool> _busy = new(false);
    private readonly State<string?> _error = new(null);
    private readonly State<bool> _showServer = new(false);

    public VietFlowLoginView(Action onLogin)
    {
        _onLogin = onLogin;
        Child = View(Body);
    }

    public bool IsFlexible(Orientation axis) => true;

    private Control Body()
    {
        var email = Keep("email", () => new PCField("Email", PC.Bind(() => _email.Value, v => _email.Value = v)));
        var password = Keep("password", () => new PCField("Mật khẩu", PC.Bind(() => _password.Value, v => _password.Value = v), secure: true) { OnSubmit = Login });
        var server = Keep("server", () => new PCField(VietFlow.DefaultBaseUrl, PC.Bind(() => _server.Value, v => _server.Value = v)));
        var busy = _busy.Value;
        var enabled = !busy && _email.Value.Length > 0 && _password.Value.Length > 0;
        return VStack(HAlign.Leading, 10,
            PC.Fit(Text("Đăng nhập tài khoản VietFlow có quyền sửa PO. Mật khẩu không được lưu, chỉ lưu khoá phiên đã mã hoá trên máy này.")
                .Font(11.5), 11.5).Foreground(PC.Secondary),
            email,
            password,
            VStack(HAlign.Leading, 6,
                Button(() => _showServer.Value = !_showServer.Value,
                    HStack(4,
                        Image(_showServer.Value ? "chevron.down" : "chevron.right").Font(9, Weight.Bold).Foreground(PC.Secondary).Frame(width: 12),
                        PC.Fit(Text("Máy chủ").Font(11.5), 11.5))),
                _showServer.Value ? server : null),
            _error.Value is { } err ? PC.Fit(Text(err).Font(11.5), 11.5).Foreground(PC.Red) : null,
            PCButtons.Prominent(PCButtons.ProminentLabel("Đăng nhập", enabled, busy), Login, enabled),
            Spacer()).Padding(14);
    }

    private async void Login()
    {
        if (_busy.Value || _email.Value.Length == 0 || _password.Value.Length == 0) return;
        _busy.Value = true;
        _error.Value = null;
        var server = _server.Value.Trim();
        VietFlow.Shared.BaseUrl = server.Length == 0 ? VietFlow.DefaultBaseUrl : server;
        try
        {
            await VietFlow.Shared.Login(_email.Value, _password.Value);
            _password.Value = "";
            _onLogin();
        }
        catch (Exception e)
        {
            _error.Value = e.Message;
        }
        _busy.Value = false;
    }
}
