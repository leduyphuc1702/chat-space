import AppKit
import SwiftUI

/// One line of the team's purchase-cost spreadsheet: one per money source behind a saved cost.
struct CostReportRow: Identifiable {
    let id: Int
    /// Remembers the spreadsheet name for this kind of source ("pool", "gift", "bank.TCB", "manual.USDT").
    let sourceKey: String
    let date: Date
    let amount: Double
    let currency: String
    let rate: Double?
}

extension PurchaseCostEntry {
    private static let defaultSources = [
        "pool": "Thẻ USDT", "gift": "GIFT", "bank.TCB": "Thẻ Tech c Hồng", "bank.VCB": "Thẻ VCB",
        "manual.USDT": "Thẻ USDT", "manual.USDC": "Thẻ USDT", "manual.GC": "GIFT", "manual.GIFTCARD": "GIFT", "manual.GIFT CARD": "GIFT",
    ]
    private static let defaultCurrencies = ["USD": "Dollar", "GBP": "Pound", "EUR": "Euro", "JPY": "Yen"]

    static func loadReportNames() -> [String: String] {
        UserDefaults.standard.dictionary(forKey: "vietflow.reportNames") as? [String: String] ?? [:]
    }

    func reportSource(_ key: String) -> String {
        reportNames[key] ?? Self.defaultSources[key] ?? (key.hasPrefix("manual.") ? String(key.dropFirst(7)) : "")
    }

    func reportCurrency(_ code: String) -> String {
        reportNames["cur.\(code)"] ?? Self.defaultCurrencies[code] ?? code
    }

    func setReportSource(_ key: String, _ name: String) { setReportName(key, name) }
    func setReportCurrency(_ code: String, _ name: String) { setReportName("cur.\(code)", name) }

    private func setReportName(_ key: String, _ name: String) {
        reportNames[key] = name
        UserDefaults.standard.set(reportNames, forKey: "vietflow.reportNames")
        reportCopied = false
    }

    /// Batches the server drew from, then whatever the form's own rate covered. A card paid in another
    /// currency is reported in that currency at the card's rate, as the bank charged it.
    func reportRows(_ r: VFCostResult) -> [CostReportRow] {
        let cur = r.purchaseOrder.currencyCode
        var rows: [CostReportRow] = []
        for s in r.sources {
            rows.append(.init(id: rows.count, sourceKey: s.isGiftCard ? "gift" : "pool", date: rateDate,
                              amount: s.amount, currency: cur, rate: s.rate))
        }
        let rest = r.recordedTotal - r.sources.reduce(0) { $0 + $1.amount }
        guard rest >= 0.005 else { return rows }
        if funding == .bank {
            let key = "bank.\(bank.rawValue)"
            if crossCurrency, let typed = typedAmount {
                rows.append(.init(id: rows.count, sourceKey: key, date: rateDate, amount: typed, currency: payCurrency, rate: bankRate))
            } else {
                rows.append(.init(id: rows.count, sourceKey: key, date: rateDate, amount: rest, currency: cur, rate: effectiveRate))
            }
        } else {
            let note = sourceNote.trimmingCharacters(in: .whitespaces).uppercased()
            rows.append(.init(id: rows.count, sourceKey: note.isEmpty ? "manual" : "manual.\(note)", date: rateDate,
                              amount: rest, currency: cur, rate: rateValue ?? r.averageRate))
        }
        return rows
    }

    /// Tab-separated, one line per row, in the sheet's column order:
    /// date · source · amount · USDT amount (left empty) · currency · rate · PO · note.
    func reportText(_ r: VFCostResult) -> String {
        let note = note.components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
        return reportRows(r).map { row in
            [Self.reportDate(row.date), reportSource(row.sourceKey), Self.reportNumber(row.amount), "",
             reportCurrency(row.currency), row.rate.map(Self.reportNumber) ?? "", r.purchaseOrder.orderCode, note]
                .map { $0.replacingOccurrences(of: "\t", with: " ") }
                .joined(separator: "\t")
        }.joined(separator: "\n")
    }

    func copyReport(_ r: VFCostResult) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(reportText(r), forType: .string)
        reportCopied = true
    }

    /// "10/26/2026", as the sheet shows it.
    static func reportDate(_ d: Date) -> String {
        let c = BankRates.calendar.dateComponents([.year, .month, .day], from: d)
        return "\(c.month ?? 0)/\(c.day ?? 0)/\(c.year ?? 0)"
    }

    /// "33,14", "23500", "34620,68": comma decimal, no grouping, as the sheet shows it.
    static func reportNumber(_ v: Double) -> String {
        v.formatted(.number.precision(.fractionLength(0...2)).grouping(.never).locale(Locale(identifier: "vi_VN")))
    }
}

/// The saved view's block that copies the spreadsheet lines.
struct CostReportSection: View {
    @Bindable var entry: PurchaseCostEntry
    let result: VFCostResult

    var body: some View {
        let rows = entry.reportRows(result)
        VStack(alignment: .leading, spacing: 8) {
            Text("Báo cáo Excel").font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
            ForEach(rows) { row in
                VStack(alignment: .leading, spacing: 5) {
                    Text("\(PurchaseCostEntry.reportDate(row.date)) · \(PurchaseCostEntry.money(row.amount, row.currency))"
                         + (row.rate.map { " @ \(PurchaseCostEntry.formatRate($0))" } ?? "") + " · \(result.purchaseOrder.orderCode)")
                        .font(.system(size: 11.5).monospacedDigit())
                    HStack(spacing: 6) {
                        field("Nguồn tiền", Binding(get: { entry.reportSource(row.sourceKey) }, set: { entry.setReportSource(row.sourceKey, $0) }))
                        field("Ngoại tệ", Binding(get: { entry.reportCurrency(row.currency) }, set: { entry.setReportCurrency(row.currency, $0) }))
                            .frame(width: 90)
                    }
                }
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06)))
            }
            Button {
                entry.copyReport(result)
            } label: {
                Label(entry.reportCopied ? "Đã copy, dán vào cột A" : rows.count > 1 ? "Copy \(rows.count) dòng báo cáo" : "Copy dòng báo cáo",
                      systemImage: entry.reportCopied ? "checkmark" : "doc.on.doc")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent).controlSize(.large)
            Text("Dán vào ô cột A của dòng trống: điền Ngày → Ghi chú (A–H). Nguồn tiền và Ngoại tệ phải trùng tên trong danh sách chọn của file; sửa một lần, app nhớ.")
                .font(.system(size: 10.5)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func field(_ title: String, _ text: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.system(size: 10)).foregroundStyle(.secondary)
            TextField(title, text: text).textFieldStyle(.roundedBorder).font(.system(size: 11.5))
        }
    }
}
