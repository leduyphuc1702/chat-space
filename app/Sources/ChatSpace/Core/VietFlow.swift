import Foundation
import Security

/// VietFlow TMS account used to record a purchase order's cost from an invoice photo
/// (`/api/v1/purchase-cost/*`, Sanctum token from `/api/v1/auth/login`).
@MainActor
@Observable
final class VietFlow {
    static let shared = VietFlow()

    static let defaultBaseURL = "https://app.vflow.vn/api/v1"

    var baseURL: String {
        didSet { UserDefaults.standard.set(baseURL, forKey: "vietflow.baseURL") }
    }
    private(set) var userName: String?
    private(set) var isLoggedIn: Bool

    @ObservationIgnored private var token: String?

    private init() {
        baseURL = UserDefaults.standard.string(forKey: "vietflow.baseURL") ?? Self.defaultBaseURL
        userName = UserDefaults.standard.string(forKey: "vietflow.userName")
        token = DemoData.enabled ? "demo" : Keychain.read("vietflow.token")
        isLoggedIn = token != nil
    }

    // MARK: Account

    func login(email: String, password: String) async throws {
        if DemoData.enabled {
            setSession(token: "demo", name: "Minh (demo)")
            return
        }
        let body: [String: Any] = ["email": email, "password": password, "device_name": "chatspace-\(Host.current().localizedName ?? "mac")"]
        let data = try await request("POST", "auth/login", body: body, authorized: false)
        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let token = json["token"] as? String else { throw VietFlowError.message("Phản hồi đăng nhập không hợp lệ.") }
        let user = json["user"] as? [String: Any]
        setSession(token: token, name: (user?["name"] as? String) ?? (user?["email"] as? String) ?? email)
    }

    func logout() {
        if !DemoData.enabled { Keychain.delete("vietflow.token") }
        UserDefaults.standard.removeObject(forKey: "vietflow.userName")
        token = nil
        userName = nil
        isLoggedIn = false
    }

    private func setSession(token: String, name: String) {
        if !DemoData.enabled {
            Keychain.write("vietflow.token", token)
            UserDefaults.standard.set(name, forKey: "vietflow.userName")
        }
        self.token = token
        userName = name
        isLoggedIn = true
    }

    // MARK: Purchase cost

    func purchaseOrder(code: String) async throws -> VFPurchaseOrder {
        if DemoData.enabled { return DemoVietFlow.purchaseOrder(code) }
        let path = "purchase-cost/purchase-orders/" + (code.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? code)
        return try decode(try await request("GET", path))
    }

    func giftCards(currency: String) async throws -> [VFGiftCard] {
        if DemoData.enabled { return DemoVietFlow.giftCards }
        struct Response: Decodable { let batches: [VFGiftCard] }
        let r: Response = try decode(try await request("GET", "purchase-cost/gift-cards", query: ["currency": currency]))
        return r.batches
    }

    func poolPreview(currency: String, amount: Double) async throws -> VFPoolPreview {
        if DemoData.enabled { return DemoVietFlow.poolPreview(amount) }
        return try decode(try await request("GET", "purchase-cost/pool-preview",
                                            query: ["currency": currency, "amount": String(format: "%.2f", amount)]))
    }

    /// `dryRun` only computes the split; otherwise the cost is written to every item of the order.
    func recordCost(_ entry: VFCostRequest, purchaseOrderId: Int, dryRun: Bool) async throws -> VFCostResult {
        if DemoData.enabled { return DemoVietFlow.record(entry, dryRun: dryRun) }
        var body = entry.body
        body["dry_run"] = dryRun
        return try decode(try await request("POST", "purchase-cost/purchase-orders/\(purchaseOrderId)/record", body: body))
    }

    // MARK: HTTP

    private func request(_ method: String, _ path: String, query: [String: String] = [:], body: [String: Any]? = nil,
                         authorized: Bool = true) async throws -> Data {
        let base = baseURL.trimmingCharacters(in: CharacterSet(charactersIn: "/ "))
        guard var comps = URLComponents(string: base + "/" + path) else { throw VietFlowError.message("Địa chỉ máy chủ VietFlow không hợp lệ.") }
        if !query.isEmpty { comps.queryItems = query.map { URLQueryItem(name: $0.key, value: $0.value) } }
        guard let url = comps.url else { throw VietFlowError.message("Địa chỉ máy chủ VietFlow không hợp lệ.") }
        var req = URLRequest(url: url, timeoutInterval: 30)
        req.httpMethod = method
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        if authorized {
            guard let token else { throw VietFlowError.loggedOut }
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        if let body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try JSONSerialization.data(withJSONObject: body)
        }
        let (data, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard !(200..<300).contains(status) else { return data }
        if status == 401 && authorized {
            logout()
            throw VietFlowError.loggedOut
        }
        throw VietFlowError.message(Self.errorMessage(data, status: status))
    }

    private static func errorMessage(_ data: Data, status: Int) -> String {
        let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        if let errors = json?["errors"] as? [String: Any],
           let first = errors.values.compactMap({ ($0 as? [String])?.first }).first {
            return first
        }
        if status == 403 { return "Tài khoản VietFlow không có quyền sửa PO." }
        let m = json?["message"] as? String ?? ""
        if status == 404 && (m.isEmpty || m.hasPrefix("The route")) {
            return "Máy chủ VietFlow chưa có chức năng nhập chi phí PO (bản vietflow-web mới chưa được deploy)."
        }
        if !m.isEmpty { return m }
        return "VietFlow trả lỗi \(status)."
    }

    private func decode<T: Decodable>(_ data: Data) throws -> T {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(T.self, from: data)
    }
}

enum VietFlowError: LocalizedError {
    case loggedOut
    case message(String)

    var errorDescription: String? {
        switch self {
        case .loggedOut: return "Phiên đăng nhập VietFlow đã hết, đăng nhập lại để tiếp tục."
        case .message(let m): return m
        }
    }
}

// MARK: - API types

struct VFPurchaseOrder: Decodable, Sendable {
    struct Item: Decodable, Sendable, Identifiable {
        let id: Int
        let productName: String
        let productVariant: String?
        let isFreeItem: Bool
        let quantityRequested: Int
        let quantityPurchased: Int
        let sellingPriceForeign: Double
        let costPriceForeign: Double?
        let costExchangeRate: Double?
        let orderNumber: String?
    }

    let id: Int
    let orderCode: String
    let customerName: String?
    let currencyCode: String
    let sellingExchangeRate: Double
    let baseRate: Double?
    let isCancelled: Bool
    let items: [Item]
}

struct VFGiftCard: Decodable, Sendable, Identifiable {
    let id: Int
    let currencyCode: String
    let remainingAmount: Double
    let buyRate: Double
    let source: String?
}

struct VFPoolPreview: Decodable, Sendable {
    struct Allocation: Decodable, Sendable {
        let batchId: Int
        let amount: Double
        let rate: Double
        let source: String
    }

    let allocations: [Allocation]
    let weightedRate: Double
    let totalAvailable: Double
    let shortfall: Double
    let isSufficient: Bool
}

enum VFFunding: String, CaseIterable, Sendable {
    case pool, giftCard = "gift_card", manual, bank

    var label: String {
        switch self {
        case .pool: return "Quỹ FIFO"
        case .giftCard: return "Gift Card"
        case .manual: return "Tỷ giá tay"
        case .bank: return "Bank VN"
        }
    }

    /// A bank rate is computed in ChatSpace, so VietFlow records it like a hand-typed rate.
    var serverValue: String { self == .bank ? VFFunding.manual.rawValue : rawValue }
}

struct VFCostRequest: Sendable {
    var totalPaid: Double
    var funding: VFFunding
    var exchangeRate: Double?
    var orderNumber: String?
    var giftCardIds: [Int] = []
    var remainderFromPool = false
    var quantities: [Int: Int] = [:]

    var body: [String: Any] {
        var b: [String: Any] = ["total_paid": totalPaid, "funding": funding.serverValue]
        if let exchangeRate { b["exchange_rate"] = exchangeRate }
        if let orderNumber, !orderNumber.isEmpty { b["order_number"] = orderNumber }
        if funding == .giftCard {
            b["gift_card_batch_ids"] = giftCardIds
            b["remainder_mode"] = remainderFromPool ? "pool" : "manual"
        }
        if !quantities.isEmpty { b["quantities"] = Dictionary(uniqueKeysWithValues: quantities.map { (String($0.key), $0.value) }) }
        return b
    }
}

struct VFCostResult: Decodable, Sendable {
    struct Order: Decodable, Sendable {
        let id: Int
        let orderCode: String
        let customerName: String?
        let currencyCode: String
    }

    struct Source: Decodable, Sendable {
        let batchId: Int
        let source: String
        let rate: Double
        let isGiftCard: Bool
        let amount: Double
    }

    struct Item: Decodable, Sendable, Identifiable {
        let id: Int
        let productName: String
        let productVariant: String?
        let isFreeItem: Bool
        let quantity: Int
        let sellingTotal: Double
        let unitCost: Double
        let lineTotal: Double
        let profitForeign: Double
        let exchangeRate: Double?
        let costVnd: Double?
    }

    let committed: Bool
    let purchaseOrder: Order
    let funding: String
    let totalPaid: Double
    let recordedTotal: Double
    let roundingDrift: Double
    let sellingTotal: Double
    let profitForeign: Double
    let costVnd: Double?
    let averageRate: Double?
    let sources: [Source]
    let items: [Item]
}

// MARK: - Keychain

enum Keychain {
    private static let service = "vn.chatspace.vietflow"

    static func read(_ account: String) -> String? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: account, kSecReturnData as String: true]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func write(_ account: String, _ value: String) {
        delete(account)
        let item: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                   kSecAttrAccount as String: account, kSecValueData as String: Data(value.utf8),
                                   kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock]
        SecItemAdd(item as CFDictionary, nil)
    }

    static func delete(_ account: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: account]
        SecItemDelete(query as CFDictionary)
    }
}

// MARK: - Demo

/// Offline stand-in for the API under CHATSPACE_DEMO, mirroring the server's split rule.
enum DemoVietFlow {
    static func purchaseOrder(_ code: String) -> VFPurchaseOrder {
        VFPurchaseOrder(id: 82, orderCode: code.uppercased(), customerName: "VF MAN NHI", currencyCode: DemoData.currency,
                        sellingExchangeRate: 27_000, baseRate: 26_420, isCancelled: false, items: [
                            .init(id: 1, productName: "Victoria's Secret Ivory Satin Lace Trim Slip Nightgown L", productVariant: "Ivory / L",
                                  isFreeItem: false, quantityRequested: 1, quantityPurchased: 0, sellingPriceForeign: 15,
                                  costPriceForeign: nil, costExchangeRate: nil, orderNumber: nil),
                        ])
    }

    static let giftCards = [
        VFGiftCard(id: 41, currencyCode: DemoData.currency, remainingAmount: 10, buyRate: 25_500, source: "GC Amazon"),
        VFGiftCard(id: 57, currencyCode: DemoData.currency, remainingAmount: 120.5, buyRate: 25_900, source: "GC eBay"),
    ]

    static func poolPreview(_ amount: Double) -> VFPoolPreview {
        let available = 500.0
        let take = min(amount, available)
        return VFPoolPreview(allocations: [.init(batchId: 12, amount: take, rate: 26_000, source: "PTM 4004")],
                             weightedRate: 26_000, totalAvailable: available, shortfall: max(0, amount - available),
                             isSufficient: amount <= available)
    }

    static func record(_ r: VFCostRequest, dryRun: Bool) -> VFCostResult {
        let po = purchaseOrder("POVFMANNHI0082")
        let item = po.items[0]
        let qty = r.quantities[item.id] ?? 1
        let selling = item.sellingPriceForeign * Double(qty)
        let rate: Double? = dryRun ? nil : {
            switch r.funding {
            case .manual, .bank: return r.exchangeRate ?? 26_350
            case .pool: return 26_000
            case .giftCard: return giftCards.first.map { gc in
                let covered = min(gc.remainingAmount, r.totalPaid)
                return (covered * gc.buyRate + (r.totalPaid - covered) * (r.exchangeRate ?? 26_350)) / r.totalPaid
            } ?? 26_000
            }
        }()
        let cost = rate.map { (r.totalPaid * $0).rounded() }
        let sources: [VFCostResult.Source] = dryRun || r.funding == .manual || r.funding == .bank ? [] : r.funding == .pool
            ? [.init(batchId: 12, source: "PTM 4004", rate: 26_000, isGiftCard: false, amount: r.totalPaid)]
            : [.init(batchId: 41, source: "GC Amazon", rate: 25_500, isGiftCard: true, amount: min(10, r.totalPaid))]
        return VFCostResult(
            committed: !dryRun,
            purchaseOrder: .init(id: po.id, orderCode: po.orderCode, customerName: po.customerName, currencyCode: DemoData.currency),
            funding: r.funding.serverValue, totalPaid: r.totalPaid, recordedTotal: r.totalPaid, roundingDrift: 0,
            sellingTotal: selling, profitForeign: selling - r.totalPaid, costVnd: cost, averageRate: rate, sources: sources,
            items: [.init(id: item.id, productName: item.productName, productVariant: item.productVariant, isFreeItem: false,
                          quantity: qty, sellingTotal: selling, unitCost: r.totalPaid / Double(qty), lineTotal: r.totalPaid,
                          profitForeign: selling - r.totalPaid, exchangeRate: rate, costVnd: cost)])
    }
}
