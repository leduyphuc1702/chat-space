import Foundation

enum VNBank: String, CaseIterable, Sendable {
    case vcb = "VCB", tcb = "TCB"
}

/// The three columns of a Vietnamese bank's rate board, numbered the way the team calls them.
enum BankRateColumn: Int, CaseIterable, Sendable {
    case cashBuy = 1, transferBuy = 2, sell = 3

    var label: String {
        switch self {
        case .cashBuy: return "Mua TM"
        case .transferBuy: return "Mua CK"
        case .sell: return "Bán"
        }
    }
}

struct BankRateQuote: Equatable, Sendable {
    let bank: VNBank
    let currency: String
    /// The day the board belongs to; earlier than the requested day when the bank published nothing that day.
    let day: Date
    let cashBuy: Double?
    let transferBuy: Double?
    let sell: Double?

    func value(_ c: BankRateColumn) -> Double? {
        switch c {
        case .cashBuy: return cashBuy
        case .transferBuy: return transferBuy
        case .sell: return sell
        }
    }
}

/// Published VND rate boards of Vietcombank and Techcombank, by day.
enum BankRates {
    @MainActor private static var cache: [String: BankRateQuote] = [:]

    static let calendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "Asia/Ho_Chi_Minh") ?? .current
        return c
    }()

    static func dayString(_ d: Date) -> String {
        let c = calendar.dateComponents([.year, .month, .day], from: d)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    /// Walks back up to a week when the bank has no board for the day (holidays, a day not published yet).
    @MainActor
    static func quote(_ bank: VNBank, currency: String, on date: Date) async throws -> BankRateQuote {
        let key = "\(bank.rawValue)|\(currency)|\(dayString(date))"
        if let q = cache[key] { return q }
        if DemoData.enabled {
            let boards: [String: [Double]] = bank == .vcb
                ? ["USD": [25_580, 25_610, 25_990], "EUR": [28_141.01, 28_425.26, 29_625.48], "GBP": [33_752.11, 34_093.04, 35_185.92]]
                : ["USD": [25_606, 25_620, 25_990], "EUR": [28_272, 28_488, 29_571], "GBP": [33_854, 34_244, 35_179]]
            guard let r = boards[currency] else {
                throw VietFlowError.message("\(bank.rawValue) không có tỷ giá \(currency) quanh ngày \(dayString(date)).")
            }
            let q = BankRateQuote(bank: bank, currency: currency, day: date, cashBuy: r[0], transferBuy: r[1], sell: r[2])
            cache[key] = q
            return q
        }
        var day = calendar.startOfDay(for: min(date, Date()))
        for _ in 0..<7 {
            if let q = try await fetch(bank, currency: currency, day: day) {
                cache[key] = q
                return q
            }
            day = calendar.date(byAdding: .day, value: -1, to: day) ?? day
        }
        throw VietFlowError.message("\(bank.rawValue) không có tỷ giá \(currency) quanh ngày \(dayString(date)).")
    }

    private static func fetch(_ bank: VNBank, currency: String, day: Date) async throws -> BankRateQuote? {
        switch bank {
        case .vcb:
            let url = URL(string: "https://www.vietcombank.com.vn/api/exchangerates?date=\(dayString(day))")!
            let json = try await get(url)
            guard let rows = json["Data"] as? [[String: Any]],
                  let row = rows.first(where: { ($0["currencyCode"] as? String) == currency }) else { return nil }
            return BankRateQuote(bank: bank, currency: currency, day: day, cashBuy: number(row["cash"]),
                                 transferBuy: number(row["transfer"]), sell: number(row["sell"]))
        case .tcb:
            let url = URL(string: "https://techcombank.com/content/techcombank/web/vn/vi/cong-cu-tien-ich/ty-gia/_jcr_content.exchange-rates.\(dayString(day)).integration.json")!
            let json = try await get(url)
            guard let rows = (json["exchangeRate"] as? [String: Any])?["data"] as? [[String: Any]] else { return nil }
            // USD is split by note size; only the 50/100 row carries the transfer and sell columns.
            let label = currency == "USD" ? "USD (50,100)" : currency
            guard let row = rows.first(where: { ($0["label"] as? String) == label }) else { return nil }
            return BankRateQuote(bank: bank, currency: currency, day: day, cashBuy: number(row["bidRateTM"]),
                                 transferBuy: number(row["bidRateCK"]), sell: number(row["askRate"]))
        }
    }

    private static func get(_ url: URL) async throws -> [String: Any] {
        var req = URLRequest(url: url, timeoutInterval: 20)
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        req.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko)", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else { throw VietFlowError.message("Trang tỷ giá ngân hàng trả lỗi \(status).") }
        return (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }

    private static func number(_ v: Any?) -> Double? {
        let d: Double?
        switch v {
        case let s as String: d = Double(s.replacingOccurrences(of: ",", with: ""))
        case let n as NSNumber: d = n.doubleValue
        default: d = nil
        }
        return d.flatMap { $0 > 0 ? $0 : nil }
    }
}
