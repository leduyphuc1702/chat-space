import AppKit
import Foundation
import Observation

extension Platform: Codable {}

/// What the left rail shows: the ChatSpace inbox, one platform's own UI, or a user group.
enum RailItem: Hashable, Codable {
    case inbox
    case platform(Platform)
    case group(UUID)

    var key: String {
        switch self {
        case .inbox: "inbox"
        case .platform(let p): "platform:" + p.rawValue
        case .group(let id): "group:" + id.uuidString
        }
    }

    init?(key: String) {
        if key == "inbox" { self = .inbox; return }
        let parts = key.split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2 else { return nil }
        if parts[0] == "platform", let p = Platform(rawValue: parts[1]) { self = .platform(p); return }
        if parts[0] == "group", let id = UUID(uuidString: parts[1]) { self = .group(id); return }
        return nil
    }
}

/// A chat on a specific platform.
struct ChatRef: Hashable, Codable, Identifiable {
    var platform: Platform
    var chatId: String
    var id: String { platform.rawValue + "|" + chatId }
}

/// User-defined "platform": every chat (any platform) whose title matches one of the keywords.
struct CustomGroup: Codable, Identifiable, Hashable {
    enum Match: String, Codable, CaseIterable { case contains, prefix, word }

    var id = UUID()
    var name: String
    var keywords: [String]
    var match: Match = .contains
    /// Empty = all platforms.
    var platforms: [Platform] = []
    var symbol: String? = "folder.fill"
    /// Custom image copied into Application Support/ChatSpace/groups.
    var imagePath: String?
    var colorHex: UInt32 = 0x5E5CE6

    func matches(_ chat: Chat, on platform: Platform) -> Bool {
        if !platforms.isEmpty && !platforms.contains(platform) { return false }
        let title = Self.normalize(chat.title)
        return keywords.contains { raw in
            let k = Self.normalize(raw)
            guard !k.isEmpty else { return false }
            switch match {
            case .contains: return title.contains(k)
            case .prefix: return title.hasPrefix(k)
            case .word:
                // "VF" matches "VF 1", "VF-2", "VF3" but not "AVF".
                var search = title.startIndex..<title.endIndex
                while let r = title.range(of: k, range: search) {
                    if r.lowerBound == title.startIndex || !title[title.index(before: r.lowerBound)].isLetter { return true }
                    search = r.upperBound..<title.endIndex
                }
                return false
            }
        }
    }

    static func normalize(_ s: String) -> String {
        s.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: Locale(identifier: "vi_VN"))
            .replacingOccurrences(of: "đ", with: "d")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// A chat the user chose to skip in the ChatSpace inbox ("Bỏ qua cho lần sau").
struct IgnoredChat: Codable, Identifiable, Hashable {
    var ref: ChatRef
    var title: String
    var ignoredAt: Date
    var id: String { ref.id }
}

/// Settings → Dữ liệu và bộ nhớ (modelled on Telegram's Data and Storage).
struct StorageSettings: Codable, Equatable {
    /// 0 = forever.
    var keepMediaDays = 7
    /// 0 = unlimited.
    var maxCacheMB = 2048
    var compressImages = true
    /// 0 = unlimited.
    var maxStoredMessagesPerChat = 1000
    var autoDownloadPhotos = true
    var autoDownloadVideos = false
    var autoDownloadFiles = false
    var autoDownloadMaxMB = 10.0

    func autoDownloads(_ m: Message) -> Bool {
        if let size = m.media?.size, size > autoDownloadMaxMB * 1_048_576 { return false }
        switch m.kind {
        case .image, .sticker: return autoDownloadPhotos
        case .video, .gif: return autoDownloadVideos
        case .voice, .audio: return true
        default: return autoDownloadFiles
        }
    }

    var bridgeParams: [String: JSONValue] {
        ["keepMediaDays": .number(Double(keepMediaDays)), "maxCacheMB": .number(Double(maxCacheMB)),
         "compressImages": .bool(compressImages), "maxStoredMessagesPerChat": .number(Double(maxStoredMessagesPerChat))]
    }
}

/// Floating chat heads (Telegram Android "bubbles").
struct BubbleSettings: Codable, Equatable {
    enum Trigger: String, Codable, CaseIterable { case off, newMessages, manual }
    var trigger: Trigger = .newMessages
    /// Only pop a bubble when ChatSpace is not the frontmost app.
    var onlyInBackground = false
    /// Show the incoming message beside its bubble for a few seconds.
    var showPreview = true
    var maxBubbles = 5
    var includeGroups = true
}

/// Everything ChatSpace itself owns (as opposed to platform data), persisted in UserDefaults.
@MainActor
@Observable
final class Workspace {
    static let shared = Workspace()

    var groups: [CustomGroup] { didSet { save(groups, "workspace.groups") } }
    var ignored: [IgnoredChat] { didSet { save(ignored, "workspace.ignored") } }
    var storage: StorageSettings { didSet { save(storage, "workspace.storage"); if storage != oldValue { pushStorage() } } }
    var bubbles: BubbleSettings { didSet { save(bubbles, "workspace.bubbles") } }
    var showInbox: Bool { didSet { if !DemoData.enabled { UserDefaults.standard.set(showInbox, forKey: "workspace.showInbox") } } }
    var translation: TranslationSettings { didSet { save(translation, "workspace.translation") } }
    /// Offer sending typed arithmetic with its result before a message goes out.
    var mathPreview: Bool { didSet { if !DemoData.enabled { UserDefaults.standard.set(mathPreview, forKey: "workspace.mathPreview") } } }

    private init() {
        groups = Self.load("workspace.groups") ?? []
        ignored = Self.load("workspace.ignored") ?? []
        storage = Self.load("workspace.storage") ?? StorageSettings()
        bubbles = Self.load("workspace.bubbles") ?? BubbleSettings()
        translation = Self.load("workspace.translation") ?? TranslationSettings()
        showInbox = UserDefaults.standard.object(forKey: "workspace.showInbox") as? Bool ?? true
        mathPreview = UserDefaults.standard.object(forKey: "workspace.mathPreview") as? Bool ?? true
        if DemoData.enabled && groups.isEmpty {
            groups = [CustomGroup(id: Self.demoGroupId, name: "VF", keywords: ["VF"], match: .word, symbol: "car.fill", colorHex: 0x0A84FF)]
            // CHATSPACE_DEMO_GROUPS=n adds n more, to make the rail scroll.
            let extra = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_GROUPS"].flatMap(Int.init) ?? 0
            groups += (0..<max(0, extra)).map { CustomGroup(name: "Nhóm \($0 + 1)", keywords: ["Nhóm"], colorHex: 0x34C759) }
        }
    }

    static let demoGroupId = UUID(uuidString: "00000000-0000-0000-0000-0000000000F1")!

    func isIgnored(_ ref: ChatRef) -> Bool { ignored.contains { $0.ref == ref } }

    func ignore(_ ref: ChatRef, title: String) {
        guard !isIgnored(ref) else { return }
        ignored.append(IgnoredChat(ref: ref, title: title, ignoredAt: Date()))
    }

    func restore(_ ref: ChatRef) { ignored.removeAll { $0.ref == ref } }

    func group(_ id: UUID) -> CustomGroup? { groups.first { $0.id == id } }

    func upsert(_ g: CustomGroup) {
        if let i = groups.firstIndex(where: { $0.id == g.id }) { groups[i] = g } else { groups.append(g) }
    }

    func delete(_ id: UUID) {
        if let path = group(id)?.imagePath { try? FileManager.default.removeItem(atPath: path) }
        groups.removeAll { $0.id == id }
        if AppModel.shared.rail == .group(id) { AppModel.shared.rail = .inbox }
    }

    /// Copies a user-picked image next to the settings so the group icon survives the original moving.
    static func importGroupImage(_ url: URL, for id: UUID) -> String? {
        let dir = Platform.zalo.dataDirectory.deletingLastPathComponent().appendingPathComponent("groups", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        guard let img = NSImage(contentsOf: url), let tiff = img.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff) else { return nil }
        // Icons are shown at ≤ 44 pt; 176 px PNG keeps them sharp on Retina without bloating disk.
        let side = 176
        guard let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
        let w = CGFloat(rep.pixelsWide), h = CGFloat(rep.pixelsHigh), s = min(w, h)
        img.draw(in: NSRect(x: 0, y: 0, width: side, height: side),
                 from: NSRect(x: (w - s) / 2 * img.size.width / w, y: (h - s) / 2 * img.size.height / h,
                              width: s * img.size.width / w, height: s * img.size.height / h),
                 operation: .copy, fraction: 1)
        NSGraphicsContext.restoreGraphicsState()
        let dest = dir.appendingPathComponent(id.uuidString + ".png")
        guard let png = out.representation(using: .png, properties: [:]), (try? png.write(to: dest)) != nil else { return nil }
        return dest.path
    }

    func pushStorage() {
        for s in AppModel.shared.sessions.values { s.configureStorage() }
    }

    /// Demo mode never touches the real user's saved groups, ignored chats or settings.
    private func save<T: Encodable>(_ v: T, _ key: String) {
        guard !DemoData.enabled else { return }
        if let d = try? JSONEncoder().encode(v) { UserDefaults.standard.set(d, forKey: key) }
    }

    private static func load<T: Decodable>(_ key: String) -> T? {
        guard !DemoData.enabled, let d = UserDefaults.standard.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(T.self, from: d)
    }
}
