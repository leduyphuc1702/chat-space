import Foundation
import NaturalLanguage
import Observation

/// Settings → Dịch tin nhắn.
struct TranslationSettings: Codable, Equatable {
    /// Google Translate code that messages are translated into, and the language the user writes in.
    var language = "vi"
    /// A reply goes out in the language of the message it answers.
    var matchReplyLanguage = true
    /// Outgoing language chosen per chat (`ChatRef.id`); absent means `language`, i.e. sent as typed.
    var chatLanguages: [String: String] = [:]
}

enum TranslationLanguages {
    static let all: [(code: String, name: String)] = [
        ("vi", "Tiếng Việt"), ("en", "Tiếng Anh"), ("zh-CN", "Tiếng Trung (giản thể)"), ("zh-TW", "Tiếng Trung (phồn thể)"),
        ("ja", "Tiếng Nhật"), ("ko", "Tiếng Hàn"), ("th", "Tiếng Thái"), ("lo", "Tiếng Lào"), ("km", "Tiếng Khmer"),
        ("id", "Tiếng Indonesia"), ("ms", "Tiếng Mã Lai"), ("tl", "Tiếng Philippines"), ("hi", "Tiếng Hindi"),
        ("ru", "Tiếng Nga"), ("fr", "Tiếng Pháp"), ("de", "Tiếng Đức"), ("es", "Tiếng Tây Ban Nha"),
        ("pt", "Tiếng Bồ Đào Nha"), ("it", "Tiếng Ý"), ("ar", "Tiếng Ả Rập"),
    ]

    static func name(_ code: String) -> String {
        if let n = all.first(where: { $0.code.caseInsensitiveCompare(code) == .orderedSame })?.name { return n }
        return Locale(identifier: "vi").localizedString(forIdentifier: code).map { "Tiếng " + $0 } ?? code.uppercased()
    }

    /// Badge text on the composer's language button.
    static func short(_ code: String) -> String {
        code.caseInsensitiveCompare("zh-TW") == .orderedSame ? "TW" : String(code.prefix { $0 != "-" }).uppercased()
    }

    static func same(_ a: String, _ b: String) -> Bool { a.caseInsensitiveCompare(b) == .orderedSame }
}

enum TranslationError: LocalizedError {
    case http(Int)
    case badResponse

    var errorDescription: String? {
        switch self {
        case .http(429): "Google Dịch đang giới hạn tần suất, thử lại sau ít phút"
        case .http(let c): "Google Dịch trả lỗi \(c)"
        case .badResponse: "Google Dịch trả dữ liệu không đọc được"
        }
    }
}

/// Message translation through Google Translate's public web endpoint (no API key).
@MainActor
@Observable
final class Translator {
    static let shared = Translator()

    struct Entry: Codable, Equatable {
        /// The message text this translation belongs to; an edited message is translated again.
        var original: String
        var text: String
        var source: String
        var target: String
        var shown: Bool
        var at: Date
    }

    private(set) var entries: [String: Entry] = [:]
    private(set) var loading: Set<String> = []
    private(set) var failed: [String: String] = [:]
    /// Detected language of messages being replied to, by message key.
    private(set) var replyLanguages: [String: String] = [:]
    /// Chats (`ChatRef.id`) where the user overrode the automatic reply language for the current reply.
    var replyAutoOff: Set<String> = []
    /// Why the last send in a chat was held back.
    var sendError: [String: String] = [:]

    @ObservationIgnored private var detecting: [String: Task<String?, Never>] = [:]
    @ObservationIgnored private var saveTask: Task<Void, Never>?

    private static let maxEntries = 3000
    private static var fileURL: URL {
        Platform.zalo.dataDirectory.deletingLastPathComponent().appendingPathComponent("translations.json")
    }

    private init() {
        guard !DemoData.enabled, let d = try? Data(contentsOf: Self.fileURL),
              let e = try? JSONDecoder().decode([String: Entry].self, from: d) else { return }
        entries = e
    }

    var settings: TranslationSettings { Workspace.shared.translation }

    func key(_ m: Message) -> String { m.chatId + "|" + m.id }

    /// Text worth translating: has letters, not just emoji, links or numbers.
    nonisolated static func hasWords(_ text: String?) -> Bool {
        guard let text else { return false }
        let stripped = text.replacingOccurrences(of: #"https?://\S+"#, with: "", options: .regularExpression)
        return stripped.unicodeScalars.contains { CharacterSet.letters.contains($0) }
    }

    static func canTranslate(_ m: Message) -> Bool {
        !m.isDeleted && m.kind != .system && hasWords(m.text)
    }

    // MARK: Incoming

    /// The translation to show under a message, if the user asked for it and it is still current.
    func shownEntry(_ m: Message) -> Entry? {
        guard let e = entries[key(m)], e.shown, e.original == m.text else { return nil }
        return e
    }

    func isShown(_ m: Message) -> Bool { shownEntry(m) != nil || loading.contains(key(m)) || failed[key(m)] != nil }
    func isLoading(_ m: Message) -> Bool { loading.contains(key(m)) }
    func failure(_ m: Message) -> String? { failed[key(m)] }

    func toggle(_ m: Message) {
        let k = key(m)
        if isShown(m) {
            entries[k]?.shown = false
            loading.remove(k)
            failed[k] = nil
            scheduleSave()
            return
        }
        translate(m)
    }

    func translate(_ m: Message) {
        let k = key(m)
        guard let text = m.text, Self.hasWords(text), !loading.contains(k) else { return }
        let target = settings.language
        failed[k] = nil
        if var e = entries[k], e.original == text, TranslationLanguages.same(e.target, target) {
            e.shown = true
            entries[k] = e
            scheduleSave()
            return
        }
        loading.insert(k)
        Task {
            do {
                let r = try await Self.translate(text, to: target)
                guard loading.remove(k) != nil else { return }
                entries[k] = Entry(original: text, text: r.text, source: r.source, target: target, shown: true, at: Date())
                scheduleSave()
            } catch {
                guard loading.remove(k) != nil else { return }
                failed[k] = error.localizedDescription
            }
        }
    }

    // MARK: Outgoing

    func chatLanguage(_ ref: ChatRef) -> String { settings.chatLanguages[ref.id] ?? settings.language }

    func setChatLanguage(_ code: String, for ref: ChatRef) {
        Workspace.shared.translation.chatLanguages[ref.id] = TranslationLanguages.same(code, settings.language) ? nil : code
    }

    /// The language a message typed now would go out in; nil while the replied-to message's language is
    /// still being detected.
    func outgoingLanguage(_ ref: ChatRef, reply: Message?) -> String? {
        guard let reply, settings.matchReplyLanguage, !replyAutoOff.contains(ref.id), Self.hasWords(reply.text) else {
            return chatLanguage(ref)
        }
        return knownLanguage(of: reply)
    }

    /// A message's language, if a translation or a detection already found it.
    func knownLanguage(of m: Message) -> String? {
        if let e = entries[key(m)], e.original == m.text { return e.source }
        return replyLanguages[key(m)]
    }

    func clearCache() {
        entries = [:]
        failed = [:]
        replyLanguages = [:]
        scheduleSave()
    }

    /// Same, waiting for detection; falls back to the chat's language when it can't tell.
    func resolveOutgoing(_ ref: ChatRef, reply: Message?) async -> String {
        if let l = outgoingLanguage(ref, reply: reply) { return l }
        if let reply, let l = await detectLanguage(of: reply) { return l }
        return chatLanguage(ref)
    }

    @discardableResult
    func detectLanguage(of m: Message) async -> String? {
        let k = key(m)
        if let l = knownLanguage(of: m) { return l }
        guard let text = m.text, Self.hasWords(text) else { return nil }
        if let t = detecting[k] { return await t.value }
        let target = settings.language
        let task = Task<String?, Never> {
            if let r = try? await Self.translate(String(text.prefix(600)), to: target), r.source != "auto" { return r.source }
            return Self.localGuess(text)
        }
        detecting[k] = task
        let l = await task.value
        detecting[k] = nil
        if let l { replyLanguages[k] = l }
        return l
    }

    /// Shows what the user typed under a message that went out translated.
    func rememberSent(_ m: Message, typed: String, language: String) {
        guard let sent = m.text else { return }
        entries[key(m)] = Entry(original: sent, text: typed, source: language, target: settings.language, shown: true, at: Date())
        scheduleSave()
    }

    // MARK: Google Translate

    /// One request per ≤ 4,500 characters; the endpoint rejects much longer texts.
    nonisolated static func translate(_ text: String, from source: String = "auto", to target: String) async throws -> (text: String, source: String) {
        var out = ""
        var detected = source
        for chunk in chunks(text, limit: 4500) {
            let r = try await request(chunk, from: source, to: target)
            out += r.text
            if detected == "auto" { detected = r.source }
        }
        return (out, detected)
    }

    /// Translates around `@name` tokens so mentions keep pointing at the right people.
    nonisolated static func translate(_ text: String, keeping tokens: [String], to target: String) async throws -> String {
        var pieces: [(String, Bool)] = []
        var rest = Substring(text)
        while true {
            let hits = tokens.filter { !$0.isEmpty }.compactMap { t in rest.range(of: t).map { ($0, t) } }
            guard let r = hits.min(by: { $0.0.lowerBound < $1.0.lowerBound })?.0 else { break }
            pieces.append((String(rest[..<r.lowerBound]), true))
            pieces.append((String(rest[r]), false))
            rest = rest[r.upperBound...]
        }
        pieces.append((String(rest), true))
        var out = ""
        for (piece, isText) in pieces {
            guard isText, hasWords(piece) else { out += piece; continue }
            let lead = String(piece.prefix { $0.isWhitespace })
            let trail = String(piece.reversed().prefix { $0.isWhitespace }.reversed())
            let core = piece.trimmingCharacters(in: .whitespacesAndNewlines)
            let translated = try await translate(core, to: target).text
            out += lead + translated + trail
        }
        return out
    }

    nonisolated private static func request(_ text: String, from source: String, to target: String) async throws -> (text: String, source: String) {
        var c = URLComponents(string: "https://translate.googleapis.com/translate_a/single")!
        c.queryItems = [.init(name: "client", value: "gtx"), .init(name: "sl", value: source),
                        .init(name: "tl", value: target), .init(name: "dt", value: "t")]
        var req = URLRequest(url: c.url!, timeoutInterval: 15)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded;charset=UTF-8", forHTTPHeaderField: "Content-Type")
        let allowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        req.httpBody = Data(("q=" + (text.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")).utf8)
        let (data, resp) = try await URLSession.shared.data(for: req)
        if let code = (resp as? HTTPURLResponse)?.statusCode, code != 200 { throw TranslationError.http(code) }
        // [[["translated","original",…],…],null,"detected source",…]
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [Any],
              let sentences = root.first as? [Any] else { throw TranslationError.badResponse }
        let translated = sentences.compactMap { ($0 as? [Any])?.first as? String }.joined()
        guard !translated.isEmpty else { throw TranslationError.badResponse }
        let detected = root.count > 2 ? (root[2] as? String) ?? source : source
        return (translated, detected)
    }

    nonisolated private static func chunks(_ text: String, limit: Int) -> [String] {
        guard text.count > limit else { return [text] }
        var out: [String] = []
        var current = ""
        for line in text.split(separator: "\n", omittingEmptySubsequences: false) {
            var line = String(line)
            while line.count > limit {
                if !current.isEmpty { out.append(current); current = "" }
                out.append(String(line.prefix(limit)) + "\n")
                line = String(line.dropFirst(limit))
            }
            if current.count + line.count + 1 > limit { out.append(current); current = "" }
            current += line + "\n"
        }
        if !current.isEmpty { out.append(current) }
        if out.last?.hasSuffix("\n") == true, !text.hasSuffix("\n") { out[out.count - 1].removeLast() }
        return out
    }

    nonisolated private static func localGuess(_ text: String) -> String? {
        guard let l = NLLanguageRecognizer.dominantLanguage(for: text) else { return nil }
        switch l {
        case .simplifiedChinese: return "zh-CN"
        case .traditionalChinese: return "zh-TW"
        default: return l.rawValue
        }
    }

    // MARK: Persistence

    private func scheduleSave() {
        guard !DemoData.enabled else { return }
        saveTask?.cancel()
        saveTask = Task {
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard !Task.isCancelled else { return }
            if entries.count > Self.maxEntries {
                let keep = entries.sorted { $0.value.at > $1.value.at }.prefix(Self.maxEntries)
                entries = Dictionary(uniqueKeysWithValues: keep.map { ($0.key, $0.value) })
            }
            let snapshot = entries
            let url = Self.fileURL
            await Task.detached(priority: .utility) {
                try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                if let d = try? JSONEncoder().encode(snapshot) { try? d.write(to: url, options: .atomic) }
            }.value
        }
    }
}
