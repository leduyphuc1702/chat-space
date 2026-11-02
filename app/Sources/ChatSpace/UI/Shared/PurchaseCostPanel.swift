import AppKit
import SwiftUI

// MARK: - Hints

/// Values worth offering from an invoice photo's recognised text.
struct InvoiceHints: Equatable {
    struct Amount: Equatable {
        var value: Double
        var currency: String
    }

    var amounts: [Amount] = []
    /// Amounts on lines naming the total ("Order total", "Paid"…), most telling keyword first.
    var totals: [Amount] = []
    var ids: [String] = []
    var orderNumber: String?
    var paymentDate: Date?

    /// Amounts in the order's currency, or every amount when the invoice shows none in it.
    func amounts(in currency: String?) -> [Amount] {
        let mine = amounts.filter { $0.currency == currency }
        return mine.isEmpty ? amounts : mine
    }

    /// Only a total in the order's currency is worth filling in by itself.
    func total(in currency: String) -> Double? {
        totals.first { $0.currency == currency }?.value
            ?? amounts.filter { $0.currency == currency }.map(\.value).max()
    }

    static func parse(_ transcript: String) -> InvoiceHints {
        var h = InvoiceHints()
        let lines = transcript.components(separatedBy: .newlines)
        let totalKeys = ["order total", "grand total", "total paid", "amount paid", "paid", "total", "tổng"]
        for key in totalKeys {
            for line in lines {
                let l = line.lowercased()
                guard l.contains(key), key != "total" || !l.contains("subtotal"),
                      let a = amounts(in: line).first, !h.totals.contains(a) else { continue }
                h.totals.append(a)
            }
        }
        for line in lines {
            for a in amounts(in: line) where !h.amounts.contains(a) { h.amounts.append(a) }
            for id in ids(in: line) where !h.ids.contains(id) { h.ids.append(id) }
        }

        let idKeys = ["order number", "order no", "order #", "order id", "transaction id", "mã đơn"]
        let keyed = lines.first { line in idKeys.contains { line.lowercased().contains($0) } && !ids(in: line).isEmpty }
        h.orderNumber = keyed.flatMap { ids(in: $0).first }
            ?? h.ids.first { $0.range(of: #"^\d{2,3}-\d{5,7}-\d{5,7}$"#, options: .regularExpression) != nil }
            ?? h.ids.first

        let dateKeys = ["payment date", "paid on", "transaction date", "order date", "order placed", "ordered on", "purchase date", "date", "ngày"]
        let dateLines = dateKeys.flatMap { key in lines.filter { $0.lowercased().contains(key) } } + lines
        h.paymentDate = dateLines.lazy.compactMap { dates(in: $0).first }.first
        return h
    }

    /// "10/30/2026" (US order; day first when the first number is over 12), "2026-10-30",
    /// "Oct 30, 2026", "30 Oct 2026". Only dates from the last two years up to today count.
    static func dates(in text: String) -> [Date] {
        let months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]
        let mon = #"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?"#
        let patterns: [(String, (([String]) -> (Int, Int, Int)?))] = [
            (#"\b(\d{1,2})/(\d{1,2})/(\d{4})\b"#, { g in
                guard let a = Int(g[1]), let b = Int(g[2]), let y = Int(g[3]) else { return nil }
                return a > 12 ? (y, b, a) : (y, a, b)
            }),
            (#"\b(\d{4})-(\d{2})-(\d{2})\b"#, { g in
                guard let y = Int(g[1]), let m = Int(g[2]), let d = Int(g[3]) else { return nil }
                return (y, m, d)
            }),
            (#"(?i)\b"# + mon + #"\s+(\d{1,2}),?\s+(\d{4})\b"#, { g in
                guard let m = months.firstIndex(of: String(g[1].prefix(3)).lowercased()), let d = Int(g[2]), let y = Int(g[3]) else { return nil }
                return (y, m + 1, d)
            }),
            (#"(?i)\b(\d{1,2})\s+"# + mon + #",?\s+(\d{4})\b"#, { g in
                guard let d = Int(g[1]), let m = months.firstIndex(of: String(g[2].prefix(3)).lowercased()), let y = Int(g[3]) else { return nil }
                return (y, m + 1, d)
            }),
        ]
        let cal = BankRates.calendar
        let earliest = cal.date(byAdding: .year, value: -2, to: Date()) ?? .distantPast
        let latest = cal.date(byAdding: .day, value: 1, to: Date()) ?? Date()
        var out: [Date] = []
        for (p, build) in patterns {
            guard let re = try? NSRegularExpression(pattern: p) else { continue }
            for m in re.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                let groups = (0..<m.numberOfRanges).map { i in Range(m.range(at: i), in: text).map { String(text[$0]) } ?? "" }
                guard let (y, mo, d) = build(groups), (1...12).contains(mo), (1...31).contains(d),
                      let date = cal.date(from: DateComponents(year: y, month: mo, day: d)),
                      cal.component(.day, from: date) == d, date >= earliest, date <= latest else { continue }
                out.append(date)
            }
        }
        return out
    }

    private static let symbols: [(String, String)] = [
        ("US$", "USD"), ("AU$", "AUD"), ("CA$", "CAD"), ("A$", "AUD"), ("C$", "CAD"),
        ("$", "USD"), ("£", "GBP"), ("€", "EUR"), ("¥", "JPY"), ("￥", "JPY"),
    ]
    private static let codes = ["USD", "EUR", "GBP", "AUD", "CAD", "JPY", "CHF", "SGD", "CNY", "HKD", "KRW", "NZD"]

    /// Positive amounts with their currency: "$14.24", "US $1,299.00", "£12.50", "14,24 €", "1.299,00 EUR".
    /// Discounts ("-$5.46") are left out.
    static func amounts(in text: String) -> [Amount] {
        let sym = symbols.map { NSRegularExpression.escapedPattern(for: $0.0).replacingOccurrences(of: "US", with: #"US\s?"#) }
            .joined(separator: "|")
        let code = codes.joined(separator: "|")
        let num = #"\d{1,3}(?:[.,]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?"#
        let minus = #"([-−]\s?)?"#
        // (pattern, index of the currency group, index of the number group)
        let patterns: [(String, Int, Int)] = [
            (minus + "(\(sym))\\s?(\(num))", 2, 3),
            (minus + "\\b(\(code))\\s?(\(num))", 2, 3),
            (minus + "(\(num))\\s?(\(sym))(?![A-Za-z0-9])", 3, 2),
            (minus + "(\(num))\\s?(\(code))\\b", 3, 2),
        ]
        var out: [Amount] = []
        for (p, ci, ni) in patterns {
            guard let re = try? NSRegularExpression(pattern: p) else { continue }
            for m in re.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                guard m.range(at: 1).location == NSNotFound,
                      let cr = Range(m.range(at: ci), in: text), let nr = Range(m.range(at: ni), in: text),
                      let v = PurchaseCostEntry.parseAmount(String(text[nr])), v > 0 else { continue }
                let mark = text[cr].replacingOccurrences(of: " ", with: "")
                let currency = symbols.first { $0.0 == mark }?.1 ?? mark.uppercased()
                let a = Amount(value: v, currency: currency)
                if !out.contains(a) { out.append(a) }
            }
        }
        return out
    }

    /// Order / transaction numbers: Amazon "111-1234567-1234567", eBay "12-34567-89012", long digit runs,
    /// and letter+digit codes of 8 or more characters.
    static func ids(in text: String) -> [String] {
        let patterns = [#"\b\d{2,3}-\d{5,7}-\d{5,7}\b"#, #"(?<![\d*.,$])\b\d{8,20}\b"#,
                        #"\b(?=[A-Z0-9-]*\d)(?=[A-Z0-9-]*[A-Z])[A-Z0-9][A-Z0-9-]{7,24}\b"#]
        var out: [String] = []
        for p in patterns {
            guard let re = try? NSRegularExpression(pattern: p) else { continue }
            for m in re.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                guard let r = Range(m.range, in: text) else { continue }
                let s = String(text[r])
                if !s.hasPrefix("PO") && !out.contains(where: { $0.contains(s) }) { out.append(s) }
            }
        }
        return out
    }
}

/// What the conversation around the photo says: the PO it belongs to and the rate people quoted.
struct ChatCostContext: Equatable {
    var poCode: String?
    var rate: Double?
    var source: String?

    static func find(for m: Message, in messages: [Message]) -> ChatCostContext {
        var c = ChatCostContext()
        let i = messages.firstIndex { $0.id == m.id }
        let before = i.map { Array(messages[max(0, $0 - 30)..<$0].reversed()) } ?? []
        let after = i.map { Array(messages[($0 + 1)..<min(messages.count, $0 + 9)]) } ?? []
        let poTexts = [m.text, m.replyTo?.text] + before.map(\.text) + after.map(\.text)
        c.poCode = poTexts.lazy.compactMap { $0.flatMap(poCode) }.first
        for text in ([m.text] + after.map(\.text)).compactMap({ $0 }) {
            if let (rate, source) = rate(in: text) { c.rate = rate; c.source = source; break }
        }
        return c
    }

    static func poCode(in text: String) -> String? {
        text.range(of: #"\bPO[A-Z0-9]{6,}\b"#, options: .regularExpression).map { String(text[$0]) }
    }

    /// "Usdt 26350", "Rate 26.000", "GBP 34.250", "bank: 26,420" → a VND rate between 15.000 and 49.999
    /// (USD, EUR, GBP, CHF…) and its source word.
    static func rate(in text: String) -> (Double, String?)? {
        let p = #"(?i)\b(usdt|usdc|usd|eur|euro|gbp|bảng|vcb|tcb|bank|gc|gift ?card|redotcard|ptm\s?\d*|tm)?\s*(?:rate|tỷ giá|tg)?\s*[:=]?\s*\b(1[5-9]|[2-4]\d)[.,]?(\d{3})\b(?![.,]?\d)"#
        guard let re = try? NSRegularExpression(pattern: p),
              let m = re.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let a = Range(m.range(at: 2), in: text), let b = Range(m.range(at: 3), in: text),
              let rate = Double(text[a] + text[b]) else { return nil }
        let source = Range(m.range(at: 1), in: text).map { String(text[$0]).uppercased() }
        return (rate, source)
    }
}

// MARK: - Entry

/// The cost form for one invoice photo. Saving writes the cost to VietFlow first, then posts the
/// confirmation into the chat as a reply to the photo, so the chat never claims a cost VietFlow refused.
@MainActor
@Observable
final class PurchaseCostEntry {
    enum Phase { case editing, saving, saved }

    let message: Message
    @ObservationIgnored weak var session: PlatformSession?

    var poCode = ""
    private(set) var order: VFPurchaseOrder?
    private(set) var loadingOrder = false
    var totalPaid = ""
    var orderNumber = ""
    var funding: VFFunding = .pool
    var rate = ""
    var sourceNote = ""
    var note = ""
    var quantities: [Int: Int] = [:]
    private(set) var giftCards: [VFGiftCard] = []
    private(set) var giftCardsError: String?
    private(set) var poolError: String?
    var selectedGiftCards: [Int] = []
    var remainderFromPool = false
    private(set) var poolPreview: VFPoolPreview?
    private(set) var preview: VFCostResult?
    private(set) var result: VFCostResult?
    var sendToChat = true
    private(set) var bank: VNBank
    private(set) var bankColumn: BankRateColumn
    private(set) var bankFee: String
    private(set) var rateDate: Date
    private(set) var rateDateSource = "ngày gửi ảnh"
    /// The currency the card was charged in; Bank VN converts it through the bank's own boards.
    private(set) var payCurrency = "USD"
    private(set) var payCurrencySource = "mặc định"
    /// The bank's board for `payCurrency`.
    private(set) var bankQuote: BankRateQuote?
    /// The bank's board for the PO's currency, when it differs from `payCurrency`.
    private(set) var poBankQuote: BankRateQuote?
    private(set) var bankError: String?
    private(set) var loadingBank = false
    @ObservationIgnored private var rateDatePicked = false
    @ObservationIgnored private var payCurrencyPicked = false
    /// What the form last filled into `totalPaid` by itself, so a currency switch may replace it.
    @ObservationIgnored private var autoTotal: String?
    private(set) var phase = Phase.editing
    private(set) var error: String?
    private(set) var sentText: String?
    private(set) var sending = false
    private(set) var sendError: String?
    /// Spreadsheet names for sources and currencies, edited once and remembered.
    var reportNames = PurchaseCostEntry.loadReportNames()
    var reportCopied = false
    private(set) var hints = InvoiceHints()
    private(set) var context = ChatCostContext()
    @ObservationIgnored private var appliedTranscript = false

    init(message: Message, session: PlatformSession) {
        self.message = message
        self.session = session
        context = ChatCostContext.find(for: message, in: session.messages[message.chatId] ?? [])
        let defaults = UserDefaults.standard
        let savedBank = defaults.string(forKey: "vietflow.bank").flatMap(VNBank.init(rawValue:)) ?? .vcb
        bank = savedBank
        bankColumn = BankRateColumn(rawValue: defaults.integer(forKey: "vietflow.bankColumn")) ?? .sell
        bankFee = defaults.string(forKey: "vietflow.bankFee.\(savedBank.rawValue)") ?? ""
        rateDate = Date(timeIntervalSince1970: message.timestamp / 1000)
        poCode = context.poCode ?? ""
        if let r = context.rate {
            funding = .manual
            rate = Self.formatRate(r)
            sourceNote = context.source ?? ""
        }
        if !poCode.isEmpty { Task { await loadOrder() } }
    }

    func applyTranscript(_ transcript: String) {
        guard !appliedTranscript, !transcript.isEmpty else { return }
        appliedTranscript = true
        hints = InvoiceHints.parse(transcript)
        if !payCurrencyPicked, let c = hints.totals.first?.currency ?? hints.amounts.first?.currency {
            payCurrency = c
            payCurrencySource = "invoice"
        }
        fillTotalFromHints()
        if orderNumber.isEmpty, let o = hints.orderNumber { orderNumber = o }
        if !rateDatePicked, let d = hints.paymentDate {
            rateDate = d
            rateDateSource = "ngày thanh toán trên invoice"
        }
    }

    /// The currency "Tổng đã trả" is typed in: the card's currency for Bank VN, else the PO's.
    var amountCurrency: String {
        funding == .bank ? payCurrency : order?.currencyCode ?? "USD"
    }

    /// An invoice total only counts when it is in `amountCurrency`; outside Bank VN that waits for the order.
    /// A total the form filled itself follows currency changes, one the user typed stays.
    func fillTotalFromHints() {
        guard totalPaid.isEmpty || totalPaid == autoTotal, funding == .bank || order != nil else { return }
        let t = hints.total(in: amountCurrency).map(Self.formatAmount) ?? ""
        totalPaid = t
        autoTotal = t
    }

    func pickHintAmount(_ a: InvoiceHints.Amount) {
        if funding == .bank, a.currency != payCurrency { selectPayCurrency(a.currency) }
        totalPaid = Self.formatAmount(a.value)
    }

    // MARK: Bank VN

    private static func savedFee(_ b: VNBank) -> String {
        UserDefaults.standard.string(forKey: "vietflow.bankFee.\(b.rawValue)") ?? ""
    }

    func selectBank(_ b: VNBank) {
        bank = b
        UserDefaults.standard.set(b.rawValue, forKey: "vietflow.bank")
        bankFee = Self.savedFee(b)
    }

    func selectColumn(_ c: BankRateColumn) {
        bankColumn = c
        UserDefaults.standard.set(c.rawValue, forKey: "vietflow.bankColumn")
    }

    func setBankFee(_ s: String) {
        bankFee = s
        UserDefaults.standard.set(s, forKey: "vietflow.bankFee.\(bank.rawValue)")
    }

    func setRateDate(_ d: Date) {
        rateDate = d
        rateDatePicked = true
        rateDateSource = "ngày chọn tay"
    }

    func selectPayCurrency(_ c: String) {
        payCurrency = c
        payCurrencyPicked = true
        payCurrencySource = "chọn tay"
        fillTotalFromHints()
    }

    /// Paid in one currency, recorded on a PO in another.
    var crossCurrency: Bool {
        funding == .bank && order.map { $0.currencyCode != payCurrency } == true
    }

    /// Changes whenever a different bank board is needed.
    var bankKey: String {
        funding == .bank ? "\(bank.rawValue)|\(payCurrency)|\(order?.currencyCode ?? "")|\(BankRates.dayString(rateDate))" : ""
    }

    func loadBankQuote() async {
        guard funding == .bank else { return }
        loadingBank = true
        bankQuote = nil
        poBankQuote = nil
        bankError = nil
        defer { loadingBank = false }
        do {
            bankQuote = try await BankRates.quote(bank, currency: payCurrency, on: rateDate)
            if crossCurrency, let order {
                poBankQuote = try await BankRates.quote(bank, currency: order.currencyCode, on: rateDate)
            }
        } catch {
            bankError = error.localizedDescription
        }
    }

    var bankFeeValue: Double {
        Double(bankFee.replacingOccurrences(of: ",", with: ".").filter { "0123456789.".contains($0) }) ?? 0
    }

    private func withFee(_ v: Double) -> Double { (v * (1 + bankFeeValue / 100) * 100).rounded() / 100 }

    /// The chosen column for the card's currency, before and after the fee.
    var bankBaseRate: Double? { bankQuote?.value(bankColumn) }
    var bankRate: Double? { bankBaseRate.map(withFee) }

    /// The same column for the PO's currency: what the paid amount is converted with and what VietFlow records.
    var poBaseRate: Double? { crossCurrency ? poBankQuote?.value(bankColumn) : bankBaseRate }
    var poRate: Double? { poBaseRate.map(withFee) }

    /// What the card was charged in VND: paid amount × the card currency's rate with the fee.
    var bankChargeVnd: Double? {
        guard let typed = typedAmount, let r = bankRate else { return nil }
        return (typed * r).rounded()
    }

    /// The rate VietFlow records. Paid in another currency, it is the VND charge over the converted amount,
    /// so the PO's VND cost equals the card charge even though the converted amount is rounded to cents.
    var bankRecordedRate: Double? {
        guard crossCurrency else { return poRate }
        guard let vnd = bankChargeVnd, let paid = paidAmount, paid > 0 else { return nil }
        return (vnd / paid * 100).rounded() / 100
    }

    /// "VCB Bán GBP 35.185,92 + phí 1,1%"
    var bankDescription: String {
        let base = bankBaseRate.map { " " + Self.formatRate($0) } ?? ""
        return "\(bank.rawValue) \(bankColumn.label) \(payCurrency)\(base)" + (bankFeeValue > 0 ? " + phí \(Self.formatPercent(bankFeeValue))%" : "")
    }

    var effectiveRate: Double? { funding == .bank ? bankRecordedRate : rateValue }

    // MARK: Loading

    func loadOrder() async {
        let code = poCode.trimmingCharacters(in: .whitespaces).uppercased()
        guard !code.isEmpty, VietFlow.shared.isLoggedIn else { return }
        poCode = code
        loadingOrder = true
        error = nil
        defer { loadingOrder = false }
        do {
            let o = try await VietFlow.shared.purchaseOrder(code: code)
            order = o
            quantities = Dictionary(uniqueKeysWithValues: o.items.filter { !$0.isFreeItem }.map {
                ($0.id, $0.quantityPurchased > 0 ? $0.quantityPurchased : $0.quantityRequested)
            })
            if orderNumber.isEmpty, let existing = o.items.compactMap(\.orderNumber).first(where: { !$0.isEmpty }) {
                orderNumber = existing
            }
            if rate.isEmpty, funding == .manual, let base = o.baseRate { rate = Self.formatRate(base) }
            fillTotalFromHints()
            do {
                giftCards = try await VietFlow.shared.giftCards(currency: o.currencyCode)
                giftCardsError = nil
            } catch {
                giftCards = []
                giftCardsError = error.localizedDescription
            }
        } catch {
            order = nil
            self.error = error.localizedDescription
        }
    }

    /// Changes whenever the server-side split or the pool preview could change.
    var previewKey: String {
        "\(order?.id ?? 0)|\(paidAmount ?? 0)|\(funding.rawValue)|\(effectiveRate ?? 0)|\(quantities.sorted { $0.key < $1.key })"
    }

    func refreshPreview() async {
        guard phase == .editing, let order, let total = paidAmount else { preview = nil; poolPreview = nil; return }
        preview = try? await VietFlow.shared.recordCost(request(total), purchaseOrderId: order.id, dryRun: true)
        guard funding == .pool else { poolPreview = nil; poolError = nil; return }
        do {
            poolPreview = try await VietFlow.shared.poolPreview(currency: order.currencyCode, amount: total)
            poolError = nil
        } catch {
            poolPreview = nil
            poolError = error.localizedDescription
        }
    }

    // MARK: Saving

    /// What was typed into "Tổng đã trả", in `amountCurrency`.
    var typedAmount: Double? { Self.parseAmount(totalPaid).flatMap { $0 > 0 ? $0 : nil } }

    /// The paid total in the PO's currency, which is what VietFlow splits and records. Paid in another
    /// currency, it goes through VND: amount × that currency's column ÷ the PO currency's column, same bank and day.
    var paidAmount: Double? {
        guard let typed = typedAmount else { return nil }
        guard crossCurrency else { return typed }
        guard let pay = bankBaseRate, let po = poBaseRate else { return nil }
        return (typed * pay / po * 100).rounded() / 100
    }

    var rateValue: Double? { Self.parseRate(rate).flatMap { $0 > 0 ? $0 : nil } }

    var problem: String? {
        guard let order else { return poCode.isEmpty ? "Nhập mã PO." : "Chưa tải được PO \(poCode)." }
        if order.isCancelled { return "PO \(order.orderCode) đã hủy." }
        if typedAmount == nil { return "Nhập tổng tiền đã trả." }
        switch funding {
        case .manual: if rateValue == nil { return "Nhập tỷ giá mua." }
        case .bank:
            if bankQuote == nil || (crossCurrency && poBankQuote == nil) {
                return loadingBank ? "Đang lấy tỷ giá \(bank.rawValue)…" : "Chưa lấy được tỷ giá \(bank.rawValue)."
            }
            if bankBaseRate == nil { return "\(bank.rawValue) không có cột \(bankColumn.label) cho \(payCurrency)." }
            if poBaseRate == nil { return "\(bank.rawValue) không có cột \(bankColumn.label) cho \(order.currencyCode)." }
        case .giftCard: if selectedGiftCards.isEmpty { return "Chọn ít nhất một cục Gift Card." }
            if !remainderFromPool && rateValue == nil && giftShortfall > 0 { return "Nhập tỷ giá cho phần Gift Card không đủ." }
        case .pool: if let p = poolPreview, !p.isSufficient, rateValue == nil { return "Quỹ không đủ: nhập tỷ giá cho phần thiếu." }
        }
        return nil
    }

    var giftShortfall: Double {
        let covered = giftCards.filter { selectedGiftCards.contains($0.id) }.reduce(0) { $0 + $1.remainingAmount }
        return max(0, (paidAmount ?? 0) - covered)
    }

    private func request(_ total: Double) -> VFCostRequest {
        VFCostRequest(totalPaid: total, funding: funding, exchangeRate: effectiveRate,
                      orderNumber: orderNumber.trimmingCharacters(in: .whitespacesAndNewlines),
                      giftCardIds: selectedGiftCards, remainderFromPool: remainderFromPool, quantities: quantities)
    }

    func save() async {
        guard problem == nil, let order, let total = paidAmount else { return }
        phase = .saving
        error = nil
        do {
            result = try await VietFlow.shared.recordCost(request(total), purchaseOrderId: order.id, dryRun: false)
            phase = .saved
        } catch {
            phase = .editing
            self.error = error.localizedDescription
            return
        }
        if sendToChat { await send() }
    }

    func send() async {
        guard let result, let session else { return }
        let text = confirmation(result)
        sending = true
        sendError = nil
        defer { sending = false }
        do {
            try await session.post(text, in: message.chatId, reply: message)
            sentText = text
        } catch {
            sendError = error.localizedDescription
        }
    }

    func editAgain() {
        phase = .editing
        result = nil
        reportCopied = false
        sentText = nil
        sendError = nil
    }

    // MARK: Confirmation message

    /// Internal accounting group: every figure goes in.
    func confirmation(_ r: VFCostResult) -> String {
        let cur = r.purchaseOrder.currencyCode
        var lines: [String] = []
        lines.append("✅ Đã nhập chi phí \(r.purchaseOrder.orderCode)" + (r.purchaseOrder.customerName.map { " · \($0)" } ?? ""))
        let order = orderNumber.trimmingCharacters(in: .whitespacesAndNewlines)
        if !order.isEmpty { lines.append("Order: \(order)") }
        let outcome = abs(r.profitForeign) < 0.005 ? "hòa"
            : r.profitForeign > 0 ? "lãi \(Self.money(r.profitForeign, cur))" : "lỗ \(Self.money(-r.profitForeign, cur))"
        let paidIn = crossCurrency ? typedAmount.map { "\(Self.money($0, payCurrency)) = " } ?? "" : ""
        lines.append("Đã trả: \(paidIn)\(Self.money(r.totalPaid, cur)) · giá bán \(Self.money(r.sellingTotal, cur)) → \(outcome)")
        lines.append("Nguồn: " + fundingLine(r))
        if let cost = r.costVnd {
            lines.append("Giá vốn: \(Self.vnd(cost))" + (r.averageRate.map { " (tỷ giá TB \(Self.formatRate($0)))" } ?? ""))
        }
        for i in r.items where !i.isFreeItem {
            lines.append("• \(Self.short(i.productName)): \(i.quantity) × \(Self.money(i.unitCost, cur)) = \(Self.money(i.lineTotal, cur)) (bán \(Self.money(i.sellingTotal, cur)))")
        }
        if abs(r.roundingDrift) >= 0.005 { lines.append("Làm tròn đơn giá lệch \(Self.money(r.roundingDrift, cur))") }
        let n = note.trimmingCharacters(in: .whitespacesAndNewlines)
        if !n.isEmpty { lines.append("Ghi chú: \(n)") }
        return lines.joined(separator: "\n")
    }

    private func fundingLine(_ r: VFCostResult) -> String {
        let cur = r.purchaseOrder.currencyCode
        let fromBatches = r.sources.map { "\($0.source.isEmpty ? "#\($0.batchId)" : $0.source) \(Self.money($0.amount, cur)) @ \(Self.formatRate($0.rate))" }
        let batchTotal = r.sources.reduce(0) { $0 + $1.amount }
        let rest = r.recordedTotal - batchTotal
        var parts = fromBatches
        if rest >= 0.005, funding == .bank, let q = bankQuote {
            let rateText = effectiveRate.map(Self.formatRate) ?? "?"
            if crossCurrency, let payRate = bankRate {
                let poBase = poBaseRate.map { " " + Self.formatRate($0) } ?? ""
                let charge = bankChargeVnd.map { ", bank trừ \(Self.vnd($0))" } ?? ""
                parts.append("\(bankDescription) = \(Self.formatRate(payRate))\(charge); quy ra \(cur) theo \(bankColumn.label) \(cur)\(poBase)"
                             + " → @ \(rateText) (tỷ giá ngày \(Self.day(q.day)))")
            } else {
                parts.append("\(bankDescription) @ \(rateText) (tỷ giá ngày \(Self.day(q.day)))")
            }
        } else if rest >= 0.005 {
            let label = sourceNote.trimmingCharacters(in: .whitespaces)
            let rateText = rateValue.map { " @ \(Self.formatRate($0))" } ?? ""
            parts.append(fromBatches.isEmpty
                ? (label.isEmpty ? "tỷ giá tay" : label) + rateText
                : "phần thiếu \(Self.money(rest, cur))" + (label.isEmpty ? "" : " \(label)") + rateText)
        }
        return parts.joined(separator: "; ")
    }

    // MARK: Formatting

    /// Reads both "1,299.00" and "1.299,00": with both separators the last one is the decimal point; a lone
    /// separator followed by exactly three digits ("26.350", "1,299") groups thousands, otherwise it is decimal.
    nonisolated static func parseNumber(_ s: String) -> Double? {
        let t = s.filter { "0123456789.,".contains($0) }
        guard let last = t.lastIndex(where: { $0 == "." || $0 == "," }) else { return Double(t) }
        let sep = t[last]
        let decimal: Bool
        if t.contains(sep == "." ? "," : ".") {
            decimal = true
        } else {
            decimal = t.filter { $0 == sep }.count == 1 && t.distance(from: last, to: t.endIndex) - 1 != 3
        }
        var digits = t
        if decimal {
            digits = String(t[..<last]).filter(\.isNumber) + "." + String(t[t.index(after: last)...])
        } else {
            digits = t.filter(\.isNumber)
        }
        return Double(digits)
    }

    nonisolated static func parseAmount(_ s: String) -> Double? { parseNumber(s) }

    nonisolated static func parseRate(_ s: String) -> Double? { parseNumber(s) }

    static func formatAmount(_ v: Double) -> String { String(format: "%.2f", v) }

    static func formatRate(_ v: Double) -> String {
        v.formatted(.number.precision(.fractionLength(0...2)).locale(Locale(identifier: "vi_VN")))
    }

    static func formatPercent(_ v: Double) -> String {
        v.formatted(.number.precision(.fractionLength(0...3)).locale(Locale(identifier: "vi_VN")))
    }

    static func day(_ d: Date) -> String {
        let c = BankRates.calendar.dateComponents([.year, .month, .day], from: d)
        return String(format: "%02d/%02d/%04d", c.day ?? 0, c.month ?? 0, c.year ?? 0)
    }

    static func money(_ v: Double, _ currency: String) -> String {
        let n = String(format: "%.2f", v)
        let prefix = ["USD": "$", "GBP": "£", "EUR": "€", "JPY": "¥", "AUD": "A$", "CAD": "C$"][currency]
        return prefix.map { $0 + n } ?? "\(n) \(currency)"
    }

    static func vnd(_ v: Double) -> String {
        v.formatted(.number.precision(.fractionLength(0)).locale(Locale(identifier: "vi_VN"))) + " ₫"
    }

    private static func short(_ s: String) -> String { s.count > 60 ? String(s.prefix(57)) + "…" : s }
}

// MARK: - View

struct PurchaseCostPanel: View {
    @Bindable var entry: PurchaseCostEntry
    let onClose: () -> Void
    @State private var account = VietFlow.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().overlay(Color.white.opacity(0.1))
            if !account.isLoggedIn {
                VietFlowLoginView { Task { await entry.loadOrder() } }
            } else if entry.phase == .saved, let r = entry.result {
                savedView(r)
            } else {
                form
            }
        }
        .foregroundStyle(.white)
        .background(Color(white: 0.11))
        .task(id: entry.previewKey) {
            try? await Task.sleep(nanoseconds: 350_000_000)
            guard !Task.isCancelled else { return }
            await entry.refreshPreview()
        }
        .task(id: entry.bankKey) { await entry.loadBankQuote() }
        .onChange(of: entry.funding) { entry.fillTotalFromHints() }
    }

    private var header: some View {
        HStack(spacing: 8) {
            Text("Nhập chi phí PO").font(.system(size: 13, weight: .semibold))
            Spacer()
            if account.isLoggedIn {
                Menu {
                    Text(account.userName ?? "VietFlow")
                    Button("Đăng xuất VietFlow") { account.logout() }
                } label: {
                    Image(systemName: "person.crop.circle").font(.system(size: 14))
                }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                .help(account.userName ?? "VietFlow")
            }
            Button(action: onClose) { Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)) }
                .buttonStyle(.borderless).help("Đóng")
        }
        .padding(.horizontal, 14).padding(.top, 34).padding(.bottom, 10)
    }

    // MARK: Form

    private var form: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                orderSection
                if !entry.hints.amounts.isEmpty || !entry.hints.ids.isEmpty { hintsSection }
                fieldsSection
                fundingSection
                if let p = entry.preview { splitSection(p) }
                footer
            }
            .padding(14)
        }
    }

    private var orderSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            label("Mã PO")
            HStack(spacing: 6) {
                TextField("POVF…", text: $entry.poCode)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { Task { await entry.loadOrder() } }
                Button { Task { await entry.loadOrder() } } label: {
                    if entry.loadingOrder { ProgressView().controlSize(.small) } else { Text("Tải") }
                }
                .disabled(entry.poCode.isEmpty || entry.loadingOrder)
            }
            if entry.order == nil && entry.poCode.isEmpty && entry.context.poCode == nil {
                caption("Không thấy mã PO trong tin nhắn quanh ảnh, nhập tay để tải.")
            }
            if let o = entry.order { orderCard(o) }
        }
    }

    private func orderCard(_ o: VFPurchaseOrder) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(o.customerName ?? o.orderCode).font(.system(size: 12.5, weight: .semibold))
                Spacer()
                Text(o.currencyCode).font(.system(size: 11)).foregroundStyle(.secondary)
            }
            if o.isCancelled { Text("PO đã hủy").font(.system(size: 11.5)).foregroundStyle(.red) }
            ForEach(o.items) { item in
                HStack(alignment: .top, spacing: 6) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.productName).font(.system(size: 11.5)).lineLimit(2)
                        Text(item.isFreeItem ? "Hàng tặng" : "Giá bán \(PurchaseCostEntry.money(item.sellingPriceForeign, o.currencyCode)) / sp"
                             + (item.costPriceForeign.map { " · đã nhập vốn \(PurchaseCostEntry.money($0, o.currencyCode))" } ?? ""))
                            .font(.system(size: 10.5)).foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 4)
                    if !item.isFreeItem {
                        Stepper(value: Binding(get: { entry.quantities[item.id] ?? item.quantityRequested },
                                               set: { entry.quantities[item.id] = max(1, $0) }), in: 1...999) {
                            Text("×\(entry.quantities[item.id] ?? item.quantityRequested)").font(.system(size: 11.5).monospacedDigit())
                        }
                        .controlSize(.mini).fixedSize()
                        .help("Số lượng đã mua")
                    }
                }
            }
            if o.items.contains(where: { $0.costPriceForeign != nil }) {
                caption("PO đã có giá vốn, lưu sẽ ghi đè và phân bổ lại tiền.").foregroundStyle(.orange)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06)))
    }

    private var hintsSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            label("Từ ảnh invoice")
            let cur = entry.amountCurrency
            let amounts = entry.hints.amounts(in: cur)
            if !amounts.isEmpty {
                ChipRow(items: amounts.map { (PurchaseCostEntry.money($0.value, $0.currency), $0) },
                        selected: { entry.typedAmount == $0.value && entry.amountCurrency == $0.currency }) { entry.pickHintAmount($0) }
                if !amounts.contains(where: { $0.currency == cur }) {
                    caption(entry.funding == .bank
                            ? "Invoice không ghi tiền \(cur); bấm một số để chuyển loại tiền thanh toán theo nó."
                            : "Invoice không ghi tiền \(cur); kiểm tra lại số đã trả theo \(cur), hoặc dùng Bank VN để quy đổi.")
                        .foregroundStyle(.orange)
                }
            }
            if !entry.hints.ids.isEmpty {
                ChipRow(items: entry.hints.ids.map { ($0, $0) }, selected: { entry.orderNumber == $0 }) { entry.orderNumber = $0 }
            }
            caption("Bấm để điền: số tiền vào Tổng đã trả, mã vào Order. Hoặc bôi đen chữ trên ảnh rồi ⌘C, ⌘V.")
        }
    }

    private var fieldsSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            label("Tổng đã trả (\(entry.amountCurrency))")
            TextField("14.24", text: $entry.totalPaid).textFieldStyle(.roundedBorder)
            if entry.crossCurrency, let typed = entry.typedAmount, let paid = entry.paidAmount, let cur = entry.order?.currencyCode {
                caption("= \(PurchaseCostEntry.money(paid, cur)) ghi vào PO (quy đổi qua \(entry.bank.rawValue) \(entry.bankColumn.label), "
                        + "\(PurchaseCostEntry.money(typed, entry.payCurrency)) × \(PurchaseCostEntry.formatRate(entry.bankBaseRate ?? 0))"
                        + " ÷ \(PurchaseCostEntry.formatRate(entry.poBaseRate ?? 0)))")
            }
            label("Order number (mã đơn shop)")
            TextField("VD: 111-1234567-1234567", text: $entry.orderNumber).textFieldStyle(.roundedBorder)
        }
    }

    private var fundingSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            label("Nguồn tiền")
            Picker("", selection: $entry.funding) {
                ForEach(VFFunding.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented).labelsHidden()
            switch entry.funding {
            case .pool: poolFunding
            case .giftCard: giftFunding
            case .manual: manualFunding
            case .bank: bankFunding
            }
        }
    }

    private var bankFunding: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker("", selection: Binding(get: { entry.bank }, set: { entry.selectBank($0) })) {
                ForEach(VNBank.allCases, id: \.self) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented).labelsHidden()
            HStack {
                label("Loại tiền thanh toán")
                Spacer()
                Picker("", selection: Binding(get: { entry.payCurrency }, set: { entry.selectPayCurrency($0) })) {
                    ForEach(Self.payCurrencies(including: entry.payCurrency), id: \.self) { Text($0).tag($0) }
                }
                .labelsHidden().fixedSize()
            }
            caption(entry.payCurrencySource == "invoice" ? "Tự chọn theo tiền trên ảnh invoice, đổi được."
                    : entry.payCurrencySource == "chọn tay" ? "Chọn tay." : "Mặc định USD (không đọc được loại tiền trên ảnh).")
            label("Tỷ giá \(entry.payCurrency) của \(entry.bank.rawValue)")
            HStack(spacing: 6) {
                ForEach(BankRateColumn.allCases, id: \.self) { columnCard($0) }
            }
            HStack {
                label("Ngày tỷ giá")
                Spacer()
                DatePicker("", selection: Binding(get: { entry.rateDate }, set: { entry.setRateDate($0) }),
                           in: ...Date(), displayedComponents: .date)
                    .labelsHidden().datePickerStyle(.field).fixedSize()
                    .environment(\.locale, Locale(identifier: "vi_VN"))
            }
            if let q = entry.bankQuote, BankRates.dayString(q.day) != BankRates.dayString(entry.rateDate) {
                caption("\(q.bank.rawValue) không có bảng ngày \(PurchaseCostEntry.day(entry.rateDate)), dùng bảng ngày \(PurchaseCostEntry.day(q.day)).")
                    .foregroundStyle(.orange)
            } else {
                caption("Theo \(entry.rateDateSource).")
            }
            label("Phí giao dịch (%)")
            TextField("VD: 1,1", text: Binding(get: { entry.bankFee }, set: { entry.setBankFee($0) }))
                .textFieldStyle(.roundedBorder)
            if let base = entry.bankBaseRate, let rate = entry.bankRate {
                VStack(alignment: .leading, spacing: 3) {
                    HStack {
                        Text("Tỷ giá mua").font(.system(size: 12, weight: .semibold))
                        Spacer()
                        Text(PurchaseCostEntry.formatRate(rate)).font(.system(size: 13, weight: .semibold).monospacedDigit())
                    }
                    caption("\(entry.payCurrency) \(PurchaseCostEntry.formatRate(base)) × (1 + \(PurchaseCostEntry.formatPercent(entry.bankFeeValue))%) = \(PurchaseCostEntry.formatRate(rate))")
                    if entry.crossCurrency, let cur = entry.order?.currencyCode,
                       let typed = entry.typedAmount, let vnd = entry.bankChargeVnd {
                        caption("Bank trừ: \(PurchaseCostEntry.money(typed, entry.payCurrency)) × \(PurchaseCostEntry.formatRate(rate)) = \(PurchaseCostEntry.vnd(vnd))")
                        if let paid = entry.paidAmount, let recorded = entry.bankRecordedRate {
                            Divider().overlay(Color.white.opacity(0.1))
                            HStack {
                                Text("Tỷ giá ghi vào PO (\(cur))").font(.system(size: 12, weight: .semibold))
                                Spacer()
                                Text(PurchaseCostEntry.formatRate(recorded)).font(.system(size: 13, weight: .semibold).monospacedDigit())
                            }
                            caption("\(PurchaseCostEntry.vnd(vnd)) ÷ \(PurchaseCostEntry.money(paid, cur)), để giá vốn VND đúng bằng số bank trừ.")
                        }
                    }
                }
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06)))
            } else if entry.loadingBank {
                HStack(spacing: 6) { ProgressView().controlSize(.small); caption("Đang lấy tỷ giá \(entry.bank.rawValue)…") }
            } else if let e = entry.bankError {
                caption(e).foregroundStyle(.red)
            }
        }
    }

    /// Currencies both VCB and TCB publish boards for.
    private static func payCurrencies(including c: String) -> [String] {
        let list = ["USD", "EUR", "GBP", "JPY", "AUD", "CAD", "CHF", "SGD", "CNY", "HKD", "KRW", "THB"]
        return list.contains(c) ? list : list + [c]
    }

    private func columnCard(_ c: BankRateColumn) -> some View {
        let value = entry.bankQuote?.value(c)
        let selected = entry.bankColumn == c
        return Button { entry.selectColumn(c) } label: {
            VStack(spacing: 2) {
                Text("\(c.rawValue) · \(c.label)").font(.system(size: 10.5)).foregroundStyle(.secondary)
                Text(value.map(PurchaseCostEntry.formatRate) ?? "—").font(.system(size: 12.5, weight: .semibold).monospacedDigit())
            }
            .frame(maxWidth: .infinity).padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: 7).fill(selected ? Color.accentColor.opacity(0.3) : Color.white.opacity(0.06)))
            .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(selected ? Color.accentColor : .clear, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Cột \(c.rawValue): \(c.label)")
    }

    private var poolFunding: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let p = entry.poolPreview {
                ForEach(Array(p.allocations.enumerated()), id: \.offset) { _, a in
                    row(a.source.isEmpty ? "#\(a.batchId)" : a.source,
                        "\(PurchaseCostEntry.money(a.amount, entry.order?.currencyCode ?? "USD")) @ \(PurchaseCostEntry.formatRate(a.rate))")
                }
                row("Tỷ giá FIFO", PurchaseCostEntry.formatRate(p.weightedRate))
                if !p.isSufficient {
                    caption("Quỹ thiếu \(PurchaseCostEntry.money(p.shortfall, entry.order?.currencyCode ?? "USD")).").foregroundStyle(.orange)
                    rateField("Tỷ giá cho phần thiếu")
                }
            } else if entry.order == nil {
                caption("Tải PO trước để xem quỹ sẽ trừ cục tiền nào.")
            } else if let e = entry.poolError {
                caption("Không xem trước được quỹ: \(e)").foregroundStyle(.red)
            } else {
                caption("Trừ quỹ ngoại tệ thường theo thứ tự cũ trước (FIFO), như dialog trên web.")
            }
        }
    }

    private var giftFunding: some View {
        VStack(alignment: .leading, spacing: 6) {
            if entry.order == nil {
                caption("Tải PO trước để xem các cục Gift Card.")
            } else if let e = entry.giftCardsError {
                caption("Không tải được Gift Card: \(e)").foregroundStyle(.red)
            } else if entry.giftCards.isEmpty {
                caption("Không có cục Gift Card \(entry.order?.currencyCode ?? "") nào còn số dư.")
            }
            ForEach(entry.giftCards) { gc in
                Toggle(isOn: Binding(get: { entry.selectedGiftCards.contains(gc.id) },
                                     set: { on in
                                         entry.selectedGiftCards.removeAll { $0 == gc.id }
                                         if on { entry.selectedGiftCards.append(gc.id) }
                                     })) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("#\(gc.id) · \(gc.source ?? "Gift Card")").font(.system(size: 12))
                        Text("\(PurchaseCostEntry.formatRate(gc.buyRate)) ₫ · còn \(PurchaseCostEntry.money(gc.remainingAmount, gc.currencyCode))")
                            .font(.system(size: 10.5)).foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.checkbox)
            }
            if !entry.selectedGiftCards.isEmpty && entry.giftShortfall > 0 {
                caption("Gift Card thiếu \(PurchaseCostEntry.money(entry.giftShortfall, entry.order?.currencyCode ?? "USD")).").foregroundStyle(.orange)
                Picker("Phần thiếu", selection: $entry.remainderFromPool) {
                    Text("Tỷ giá tay").tag(false)
                    Text("Quỹ FIFO").tag(true)
                }
                .pickerStyle(.segmented)
                if !entry.remainderFromPool { rateField("Tỷ giá cho phần thiếu") }
            }
        }
    }

    private var manualFunding: some View {
        VStack(alignment: .leading, spacing: 6) {
            rateField("Tỷ giá mua")
            if let base = entry.order?.baseRate {
                Button("Dùng tỷ giá cơ sở \(PurchaseCostEntry.formatRate(base))") { entry.rate = PurchaseCostEntry.formatRate(base) }
                    .buttonStyle(.link).font(.system(size: 11))
            }
            label("Nguồn ghi vào tin nhắn")
            TextField("VD: USDT, Redotcard, PTM 4004", text: $entry.sourceNote).textFieldStyle(.roundedBorder)
            if let r = entry.context.rate {
                caption("Lấy từ tin nhắn: \(entry.context.source.map { $0 + " " } ?? "")\(PurchaseCostEntry.formatRate(r))")
            }
        }
    }

    private func rateField(_ title: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            label(title)
            TextField("26.350", text: $entry.rate).textFieldStyle(.roundedBorder)
        }
    }

    private func splitSection(_ p: VFCostResult) -> some View {
        let cur = p.purchaseOrder.currencyCode
        return VStack(alignment: .leading, spacing: 6) {
            label("Chia vào sản phẩm")
            ForEach(p.items.filter { !$0.isFreeItem }) { i in
                HStack(alignment: .firstTextBaseline) {
                    Text(i.productName).font(.system(size: 11.5)).lineLimit(1)
                    Spacer(minLength: 6)
                    Text("\(PurchaseCostEntry.money(i.sellingTotal, cur)) → \(PurchaseCostEntry.money(i.lineTotal, cur))")
                        .font(.system(size: 11.5).monospacedDigit())
                }
            }
            HStack {
                Text(abs(p.profitForeign) < 0.005 ? "Hòa vốn" : p.profitForeign > 0 ? "Lãi" : "Lỗ").font(.system(size: 12, weight: .semibold))
                Spacer()
                Text(PurchaseCostEntry.money(abs(p.profitForeign), cur)).font(.system(size: 12, weight: .semibold).monospacedDigit())
            }
            .foregroundStyle(p.profitForeign > 0.004 ? .green : p.profitForeign < -0.004 ? .red : .secondary)
            if abs(p.roundingDrift) >= 0.005 {
                caption("Đơn giá làm tròn 2 số lẻ nên lệch \(PurchaseCostEntry.money(p.roundingDrift, cur)).")
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06)))
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            label("Ghi chú (tuỳ chọn)")
            TextField("", text: $entry.note).textFieldStyle(.roundedBorder)
            Toggle("Gửi tin xác nhận vào cuộc trò chuyện (trả lời ảnh này)", isOn: $entry.sendToChat)
                .toggleStyle(.checkbox).font(.system(size: 11.5))
            if let e = entry.error { Text(e).font(.system(size: 11.5)).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            Button {
                Task { await entry.save() }
            } label: {
                HStack {
                    if entry.phase == .saving { ProgressView().controlSize(.small) }
                    Text(entry.sendToChat ? "Lưu vào VietFlow & gửi" : "Lưu vào VietFlow").frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(.borderedProminent).controlSize(.large)
            .disabled(entry.problem != nil || entry.phase == .saving)
            if let p = entry.problem, entry.order != nil || !entry.poCode.isEmpty { caption(p) }
        }
    }

    // MARK: Saved

    private func savedView(_ r: VFCostResult) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Label("Đã lưu vào VietFlow", systemImage: "checkmark.circle.fill")
                    .font(.system(size: 13, weight: .semibold)).foregroundStyle(.green)
                VStack(alignment: .leading, spacing: 4) {
                    row("PO", r.purchaseOrder.orderCode)
                    row("Đã trả", (entry.crossCurrency ? entry.typedAmount.map { PurchaseCostEntry.money($0, entry.payCurrency) + " = " } ?? "" : "")
                        + PurchaseCostEntry.money(r.totalPaid, r.purchaseOrder.currencyCode))
                    if let rate = r.averageRate { row("Tỷ giá TB", PurchaseCostEntry.formatRate(rate)) }
                    if let cost = r.costVnd { row("Giá vốn", PurchaseCostEntry.vnd(cost)) }
                }
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06)))
                CostReportSection(entry: entry, result: r)
                if let text = entry.sentText {
                    label("Đã gửi vào cuộc trò chuyện")
                    Text(text).font(.system(size: 11.5)).textSelection(.enabled)
                        .padding(10).frame(maxWidth: .infinity, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06)))
                } else if entry.sending {
                    HStack { ProgressView().controlSize(.small); caption("Đang gửi tin xác nhận…") }
                } else if let e = entry.sendError {
                    Text("Chưa gửi được tin xác nhận: \(e)").font(.system(size: 11.5)).foregroundStyle(.orange)
                    Button("Gửi lại tin xác nhận") { Task { await entry.send() } }
                } else {
                    Button("Gửi tin xác nhận vào cuộc trò chuyện") { Task { await entry.send() } }
                }
                Button("Sửa lại") { entry.editAgain() }.buttonStyle(.link).font(.system(size: 11.5))
            }
            .padding(14)
        }
    }

    // MARK: Bits

    private func label(_ s: String) -> some View {
        Text(s).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
    }

    private func caption(_ s: String) -> some View {
        Text(s).font(.system(size: 10.5)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }

    private func row(_ k: String, _ v: String) -> some View {
        HStack {
            Text(k).font(.system(size: 11.5)).foregroundStyle(.secondary)
            Spacer()
            Text(v).font(.system(size: 11.5).monospacedDigit()).textSelection(.enabled)
        }
    }
}

/// Wrapping row of tappable values read from the photo.
private struct ChipRow<Value>: View {
    let items: [(String, Value)]
    let selected: (Value) -> Bool
    let pick: (Value) -> Void

    var body: some View {
        FlowLayout(spacing: 5) {
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                let on = selected(item.1)
                Button { pick(item.1) } label: {
                    Text(item.0).font(.system(size: 11.5).monospacedDigit())
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Capsule().fill(on ? Color.accentColor.opacity(0.85) : Color.white.opacity(0.1)))
                }
                .buttonStyle(.plain)
            }
        }
    }
}

private struct FlowLayout: Layout {
    var spacing: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 300
        var x: CGFloat = 0, y: CGFloat = 0, line: CGFloat = 0
        for s in subviews {
            let size = s.sizeThatFits(.unspecified)
            if x > 0 && x + size.width > width { x = 0; y += line + spacing; line = 0 }
            x += size.width + spacing
            line = max(line, size.height)
        }
        return CGSize(width: width, height: y + line)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, line: CGFloat = 0
        for s in subviews {
            let size = s.sizeThatFits(.unspecified)
            if x > bounds.minX && x + size.width > bounds.maxX { x = bounds.minX; y += line + spacing; line = 0 }
            s.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            line = max(line, size.height)
        }
    }
}

struct VietFlowLoginView: View {
    let onLogin: () -> Void
    @State private var email = ""
    @State private var password = ""
    @State private var server = VietFlow.shared.baseURL
    @State private var busy = false
    @State private var error: String?
    @State private var showServer = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Đăng nhập tài khoản VietFlow có quyền sửa PO. Mật khẩu không được lưu, chỉ lưu khoá phiên trong Keychain.")
                .font(.system(size: 11.5)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            TextField("Email", text: $email).textFieldStyle(.roundedBorder).textContentType(.username)
            SecureField("Mật khẩu", text: $password).textFieldStyle(.roundedBorder).onSubmit(login)
            DisclosureGroup("Máy chủ", isExpanded: $showServer) {
                TextField(VietFlow.defaultBaseURL, text: $server).textFieldStyle(.roundedBorder)
            }
            .font(.system(size: 11.5))
            if let error { Text(error).font(.system(size: 11.5)).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            Button(action: login) {
                HStack {
                    if busy { ProgressView().controlSize(.small) }
                    Text("Đăng nhập").frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(.borderedProminent).controlSize(.large)
            .disabled(busy || email.isEmpty || password.isEmpty)
            Spacer()
        }
        .padding(14)
    }

    private func login() {
        guard !busy, !email.isEmpty, !password.isEmpty else { return }
        busy = true
        error = nil
        let server = server.trimmingCharacters(in: .whitespaces)
        VietFlow.shared.baseURL = server.isEmpty ? VietFlow.defaultBaseURL : server
        Task {
            do {
                try await VietFlow.shared.login(email: email, password: password)
                password = ""
                onLogin()
            } catch {
                self.error = error.localizedDescription
            }
            busy = false
        }
    }
}
