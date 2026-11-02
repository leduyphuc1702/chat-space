import Foundation
import ImageIO
import Observation
import SQLite3
import Vision

/// A photo whose recognised text matched a search.
struct ImageTextHit: Sendable {
    var platform: Platform
    var message: Message
    var text: String
}

/// Text recognised in chat photos, kept in Application Support/ChatSpace/image-text.sqlite. Every photo looked at
/// gets a row (empty text when it has none) so it is never recognised twice.
final class ImageTextStore: @unchecked Sendable {
    private var db: OpaquePointer?
    private let queue = DispatchQueue(label: "vn.chatspace.image-text")
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    init?(url: URL) {
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard sqlite3_open(url.path, &db) == SQLITE_OK else { return nil }
        exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS image_text(
                platform TEXT NOT NULL, chat_id TEXT NOT NULL, message_id TEXT NOT NULL,
                ts REAL NOT NULL, text TEXT NOT NULL, folded TEXT NOT NULL, message BLOB,
                PRIMARY KEY(platform, chat_id, message_id));
            CREATE INDEX IF NOT EXISTS image_text_ts ON image_text(ts DESC);
            """)
    }

    deinit { sqlite3_close(db) }

    private func exec(_ sql: String) {
        queue.sync { _ = sqlite3_exec(db, sql, nil, nil, nil) }
    }

    static func key(_ p: Platform, _ chatId: String, _ messageId: String) -> String {
        p.rawValue + "|" + chatId + "|" + messageId
    }

    /// Keys of every photo already looked at.
    func knownKeys() -> Set<String> {
        queue.sync {
            var out = Set<String>()
            var st: OpaquePointer?
            guard sqlite3_prepare_v2(db, "SELECT platform, chat_id, message_id FROM image_text", -1, &st, nil) == SQLITE_OK else { return out }
            defer { sqlite3_finalize(st) }
            while sqlite3_step(st) == SQLITE_ROW {
                out.insert(Self.column(st, 0) + "|" + Self.column(st, 1) + "|" + Self.column(st, 2))
            }
            return out
        }
    }

    /// Photos that contain text.
    func count() -> Int {
        queue.sync {
            var st: OpaquePointer?
            guard sqlite3_prepare_v2(db, "SELECT COUNT(*) FROM image_text WHERE text != ''", -1, &st, nil) == SQLITE_OK else { return 0 }
            defer { sqlite3_finalize(st) }
            return sqlite3_step(st) == SQLITE_ROW ? Int(sqlite3_column_int64(st, 0)) : 0
        }
    }

    func put(_ p: Platform, _ m: Message, text: String) {
        let json = (try? JSONEncoder().encode(m)) ?? Data()
        let folded = SearchText.fold(text)
        queue.sync {
            var st: OpaquePointer?
            let sql = "INSERT OR REPLACE INTO image_text(platform, chat_id, message_id, ts, text, folded, message) VALUES(?,?,?,?,?,?,?)"
            guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return }
            defer { sqlite3_finalize(st) }
            sqlite3_bind_text(st, 1, p.rawValue, -1, Self.transient)
            sqlite3_bind_text(st, 2, m.chatId, -1, Self.transient)
            sqlite3_bind_text(st, 3, m.id, -1, Self.transient)
            sqlite3_bind_double(st, 4, m.timestamp)
            sqlite3_bind_text(st, 5, text, -1, Self.transient)
            sqlite3_bind_text(st, 6, folded, -1, Self.transient)
            _ = json.withUnsafeBytes { sqlite3_bind_blob(st, 7, $0.baseAddress, Int32($0.count), Self.transient) }
            sqlite3_step(st)
        }
    }

    /// Newest photos whose text contains every term (terms already folded).
    func search(_ terms: [String], platforms: Set<Platform>, chatId: String? = nil, before: Double? = nil, limit: Int = 50) -> [ImageTextHit] {
        guard !terms.isEmpty, !platforms.isEmpty else { return [] }
        var sql = "SELECT platform, text, message FROM image_text WHERE text != ''"
        var binds: [String] = []
        sql += " AND platform IN (" + platforms.map { _ in "?" }.joined(separator: ",") + ")"
        binds += platforms.map(\.rawValue)
        if let chatId { sql += " AND chat_id = ?"; binds.append(chatId) }
        for t in terms {
            sql += " AND folded LIKE ? ESCAPE '\\'"
            let escaped = t.replacingOccurrences(of: "\\", with: "\\\\")
                .replacingOccurrences(of: "%", with: "\\%").replacingOccurrences(of: "_", with: "\\_")
            binds.append("%" + escaped + "%")
        }
        if before != nil { sql += " AND ts < ?" }
        sql += " ORDER BY ts DESC LIMIT \(max(1, limit))"
        return queue.sync {
            var st: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return [] }
            defer { sqlite3_finalize(st) }
            for (i, b) in binds.enumerated() { sqlite3_bind_text(st, Int32(i + 1), b, -1, Self.transient) }
            if let before { sqlite3_bind_double(st, Int32(binds.count + 1), before) }
            var out: [ImageTextHit] = []
            while sqlite3_step(st) == SQLITE_ROW {
                guard let p = Platform(rawValue: Self.column(st, 0)),
                      let blob = sqlite3_column_blob(st, 2) else { continue }
                let data = Data(bytes: blob, count: Int(sqlite3_column_bytes(st, 2)))
                guard let m = try? JSONDecoder().decode(Message.self, from: data) else { continue }
                out.append(ImageTextHit(platform: p, message: m, text: Self.column(st, 1)))
            }
            return out
        }
    }

    /// Recognised text of one photo, if it was looked at.
    func text(_ p: Platform, chatId: String, messageId: String) -> String? {
        queue.sync {
            var st: OpaquePointer?
            let sql = "SELECT text FROM image_text WHERE platform = ? AND chat_id = ? AND message_id = ?"
            guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return nil }
            defer { sqlite3_finalize(st) }
            sqlite3_bind_text(st, 1, p.rawValue, -1, Self.transient)
            sqlite3_bind_text(st, 2, chatId, -1, Self.transient)
            sqlite3_bind_text(st, 3, messageId, -1, Self.transient)
            return sqlite3_step(st) == SQLITE_ROW ? Self.column(st, 0) : nil
        }
    }

    private static func column(_ st: OpaquePointer?, _ i: Int32) -> String {
        sqlite3_column_text(st, i).map { String(cString: $0) } ?? ""
    }
}

/// Recognises text in chat photos in the background (Apple Vision, Vietnamese + English) so search finds it.
///
/// Photos come from chats as they load or arrive, and from a crawl of each platform's stored photos
/// (`messages.search` with `kinds: ["image"]`). Images are read from the local file or the platform CDN;
/// encrypted media is fetched with `media.download`, except on Messenger, which is only ever asked for
/// media by an explicit user action.
@MainActor
@Observable
final class ImageTextIndexer {
    static let shared = ImageTextIndexer()

    var enabled: Bool = UserDefaults.standard.object(forKey: "imageTextEnabled") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(enabled, forKey: "imageTextEnabled")
            if enabled { kick() }
        }
    }
    /// Photos with recognised text.
    private(set) var indexed = 0
    /// Photos waiting to be looked at.
    private(set) var pending = 0

    @ObservationIgnored let store: ImageTextStore?
    @ObservationIgnored private var known: Set<String> = []
    @ObservationIgnored private var queue: [(Platform, Message)] = []
    @ObservationIgnored private var queued: Set<String> = []
    @ObservationIgnored private var failed: Set<String> = []
    @ObservationIgnored private var worker: Task<Void, Never>?
    @ObservationIgnored private var crawled: Set<Platform> = []

    /// How many of the newest stored photos the crawl looks at per platform; downloads cost the most on Telegram.
    private static let crawlLimit: [Platform: Int] = [.zalo: 3000, .messenger: 1500, .whatsapp: 1500, .telegram: 800]

    private init() {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ChatSpace", isDirectory: true)
        store = ImageTextStore(url: base.appendingPathComponent(DemoData.enabled ? "image-text-demo.sqlite" : "image-text.sqlite"))
        known = store?.knownKeys() ?? []
        indexed = store?.count() ?? 0
    }

    static func isPhoto(_ m: Message) -> Bool {
        guard !m.isDeleted, m.media != nil else { return false }
        if m.kind == .image { return true }
        return m.kind == .file && (m.media?.mime?.hasPrefix("image/") ?? false)
    }

    /// Queues the photos among `list` that were never looked at; newest are looked at first.
    func consider(_ list: [Message], on p: Platform) {
        guard store != nil else { return }
        var added = false
        for m in list where Self.isPhoto(m) {
            let k = ImageTextStore.key(p, m.chatId, m.id)
            guard !known.contains(k), !queued.contains(k), !failed.contains(k), !m.id.hasPrefix("pending-") else { continue }
            queued.insert(k)
            queue.append((p, m))
            added = true
        }
        guard added else { return }
        queue.sort { $0.1.timestamp < $1.1.timestamp }
        pending = queue.count
        kick()
    }

    /// Lists the platform's stored photos once per launch and queues them.
    func crawl(_ session: PlatformSession) {
        guard store != nil, session.can("search"), session.authState == .loggedIn, !crawled.contains(session.platform) else { return }
        crawled.insert(session.platform)
        let cap = Self.crawlLimit[session.platform] ?? 1000
        Task {
            try? await Task.sleep(nanoseconds: 15_000_000_000)
            var before: Double?
            var seen = 0
            while seen < cap {
                guard let page = try? await session.search("", kinds: [.image], before: before, limit: 200), !page.isEmpty else { break }
                seen += page.count
                consider(page, on: session.platform)
                guard let oldest = page.last?.timestamp, oldest != before else { break }
                before = oldest
                try? await Task.sleep(nanoseconds: 500_000_000)
            }
        }
    }

    func search(_ query: String, platforms: Set<Platform>, chatId: String? = nil, before: Double? = nil, limit: Int = 50) async -> [ImageTextHit] {
        guard let store else { return [] }
        let terms = SearchText.terms(query)
        return await Task.detached { store.search(terms, platforms: platforms, chatId: chatId, before: before, limit: limit) }.value
    }

    func text(of m: Message, on p: Platform) -> String? {
        store?.text(p, chatId: m.chatId, messageId: m.id)
    }

    private func kick() {
        guard enabled, worker == nil, !queue.isEmpty else { return }
        worker = Task { [weak self] in
            await self?.drain()
            self?.worker = nil
        }
    }

    private func drain() async {
        while enabled, let (p, m) = queue.popLast() {
            pending = queue.count
            let k = ImageTextStore.key(p, m.chatId, m.id)
            queued.remove(k)
            guard !known.contains(k) else { continue }
            guard let (url, temporary) = await imageFile(m, on: p) else {
                failed.insert(k)
                continue
            }
            let text = await Task.detached(priority: .utility) { Self.recognize(url) }.value
            if temporary { try? FileManager.default.removeItem(at: url) }
            guard let text else {
                failed.insert(k)
                continue
            }
            store?.put(p, m, text: text)
            known.insert(k)
            if !text.isEmpty { indexed += 1 }
            try? await Task.sleep(nanoseconds: 120_000_000)
        }
        pending = queue.count
    }

    /// A local copy of the photo; `temporary` files are deleted after recognition.
    private func imageFile(_ m: Message, on p: Platform) async -> (URL, Bool)? {
        if let path = m.media?.path, FileManager.default.fileExists(atPath: path) {
            return (URL(fileURLWithPath: path), false)
        }
        if let u = m.media?.url, u.hasPrefix("http"), let url = URL(string: u) {
            guard let (tmp, resp) = try? await URLSession.shared.download(from: url),
                  (resp as? HTTPURLResponse)?.statusCode ?? 200 < 400 else { return nil }
            return (tmp, true)
        }
        guard p != .messenger, !DemoData.enabled else { return nil }
        let session = AppModel.shared.session(p)
        guard session.authState == .loggedIn,
              let r = try? await session.call("media.download", ["chatId": .string(m.chatId), "messageId": .string(m.id)]),
              let path = r["path"]?.stringValue else { return nil }
        try? await Task.sleep(nanoseconds: 300_000_000)
        return (URL(fileURLWithPath: path), false)
    }

    nonisolated private static let languages: [String] = {
        let supported = (try? VNRecognizeTextRequest().supportedRecognitionLanguages()) ?? ["en-US"]
        return supported.filter { $0.hasPrefix("vi") } + ["en-US"]
    }()

    /// Text in the image, lines joined by newlines; nil when the file is not a readable image.
    nonisolated static func recognize(_ url: URL) -> String? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(src, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceThumbnailMaxPixelSize: 2600,
                  kCGImageSourceCreateThumbnailWithTransform: true,
              ] as CFDictionary) else { return nil }
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.recognitionLanguages = languages
        do {
            try VNImageRequestHandler(cgImage: image).perform([request])
        } catch {
            return nil
        }
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
    }
}
