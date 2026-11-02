import AppKit
import UserNotifications

/// Posts per-chat notifications that behave like the official desktop apps:
/// grouped by conversation, the platform's own tone, inline reply / mark-as-read actions,
/// silent in-app sound while ChatSpace is frontmost, and withdrawn once the chat is read
/// (here or on another device).
@MainActor
final class Notifier {
    static let shared = Notifier()
    private(set) var authorized = false

    /// UNUserNotificationCenter needs a real bundle; `swift run` builds have none.
    private var available: Bool { Bundle.main.bundleIdentifier != nil }

    func requestAuthorization() {
        guard available else { return }
        let center = UNUserNotificationCenter.current()
        center.setNotificationCategories(Set(Platform.allCases.map(category)))
        center.requestAuthorization(options: [.alert, .sound, .badge]) { ok, _ in
            Task { @MainActor in Notifier.shared.authorized = ok }
        }
    }

    func statusDescription() async -> String {
        guard available else { return "Không khả dụng khi chạy bằng swift run" }
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        switch settings.authorizationStatus {
        case .authorized, .provisional, .ephemeral:
            return settings.alertStyle == .none ? "Đã cho phép (không hiện banner)" : "Đã cho phép"
        case .denied: return "Đã bị chặn — bật lại trong Cài đặt hệ thống"
        default: return "Chưa hỏi quyền"
        }
    }

    static func categoryID(_ p: Platform) -> String { "chat.\(p.rawValue)" }
    static func threadID(_ p: Platform, _ chatId: String) -> String { "\(p.rawValue)|\(chatId)" }

    private struct Labels { let reply, send, placeholder, markRead: String }

    private func labels(_ p: Platform) -> Labels {
        switch p {
        case .telegram: Labels(reply: "Reply", send: "Send", placeholder: "Message", markRead: "Mark as Read")
        case .whatsapp: Labels(reply: "Trả lời", send: "Gửi", placeholder: "Nhập tin nhắn", markRead: "Đánh dấu là đã đọc")
        case .messenger: Labels(reply: "Trả lời", send: "Gửi", placeholder: "Aa", markRead: "Đánh dấu là đã đọc")
        case .zalo: Labels(reply: "Trả lời", send: "Gửi", placeholder: "Nhập tin nhắn", markRead: "Đánh dấu đã đọc")
        }
    }

    private func category(_ p: Platform) -> UNNotificationCategory {
        let l = labels(p)
        let reply = UNTextInputNotificationAction(identifier: "reply", title: l.reply, options: [],
                                                  textInputButtonTitle: l.send, textInputPlaceholder: l.placeholder)
        let read = UNNotificationAction(identifier: "read", title: l.markRead, options: [])
        return UNNotificationCategory(identifier: Self.categoryID(p), actions: [reply, read], intentIdentifiers: [], options: [])
    }

    func notify(platform p: Platform, message m: Message, chat: Chat?) {
        // While ChatSpace is frontmost the official apps only play their in-app tone.
        if NSApp.isActive {
            NativeAssets.shared.play(p, .incoming)
            return
        }
        guard available, authorized else { return }

        let content = UNMutableNotificationContent()
        let sender = m.senderName ?? ""
        content.title = chat?.title ?? (sender.isEmpty ? p.displayName : sender)
        content.subtitle = p.displayName
        let body = Self.body(p, m)
        // Messenger's media wording already names the sender ("An đã gửi một ảnh.").
        let namesSender = p == .messenger && ![.text, .link].contains(m.kind)
        content.body = chat?.isGroup == true && !sender.isEmpty && !namesSender
            ? "\(Self.shortName(sender, p)): \(body)" : body
        content.threadIdentifier = Self.threadID(p, m.chatId)
        content.categoryIdentifier = Self.categoryID(p)
        content.userInfo = ["platform": p.rawValue, "chatId": m.chatId, "messageId": m.id]
        if let name = NativeAssets.shared.notificationSoundName(p) {
            content.sound = UNNotificationSound(named: UNNotificationSoundName(name))
        } else {
            content.sound = .default
        }
        let id = "\(Self.threadID(p, m.chatId))|\(m.id)"
        let avatar = chat?.avatar ?? m.senderAvatar
        Task {
            if let att = await Self.avatarAttachment(avatar, id: id) { content.attachments = [att] }
            try? await UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: id, content: content, trigger: nil))
        }
    }

    /// Withdraws every banner of one conversation (it was read here or elsewhere).
    func clear(platform p: Platform, chatId: String) {
        guard available else { return }
        let thread = Self.threadID(p, chatId)
        let center = UNUserNotificationCenter.current()
        center.getDeliveredNotifications { list in
            let ids = list.filter { $0.request.content.threadIdentifier == thread }.map(\.request.identifier)
            if !ids.isEmpty { center.removeDeliveredNotifications(withIdentifiers: ids) }
        }
    }

    /// Withdraws banners of a platform whose chats are no longer unread.
    func reconcile(platform p: Platform, unread: Set<String>) {
        guard available else { return }
        let prefix = p.rawValue + "|"
        let center = UNUserNotificationCenter.current()
        center.getDeliveredNotifications { list in
            let ids = list.compactMap { n -> String? in
                let t = n.request.content.threadIdentifier
                guard t.hasPrefix(prefix), !unread.contains(String(t.dropFirst(prefix.count))) else { return nil }
                return n.request.identifier
            }
            if !ids.isEmpty { center.removeDeliveredNotifications(withIdentifiers: ids) }
        }
    }

    // MARK: Text

    private static func shortName(_ name: String, _ p: Platform) -> String {
        p == .zalo || p == .messenger ? (name.components(separatedBy: " ").last ?? name) : name
    }

    /// Message body worded the way each app words its notifications.
    static func body(_ p: Platform, _ m: Message) -> String {
        let t = m.text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if m.isDeleted {
            return p == .telegram ? "Message deleted" : "Tin nhắn đã được thu hồi"
        }
        switch p {
        case .zalo:
            switch m.kind {
            case .image: return "[Hình ảnh]"
            case .video, .gif: return "[Video]"
            case .sticker: return "[Sticker]"
            case .voice, .audio: return "[Tin nhắn thoại]"
            case .file: return "[File] " + (m.media?.fileName ?? "")
            case .link: return "[Link] " + t
            case .location: return "[Vị trí]"
            case .contact: return "[Danh thiếp]"
            default: return t
            }
        case .messenger:
            let who = m.senderName ?? "Ai đó"
            switch m.kind {
            case .image: return "\(who) đã gửi một ảnh."
            case .video, .gif: return "\(who) đã gửi một video."
            case .sticker: return "\(who) đã gửi một nhãn dán."
            case .voice, .audio: return "\(who) đã gửi một tin nhắn thoại."
            case .file: return "\(who) đã gửi một file đính kèm."
            default: return t
            }
        case .whatsapp:
            return m.preview(photo: "📷 Ảnh", video: "🎥 Video", voice: "🎤 Tin nhắn thoại", sticker: "Nhãn dán",
                             file: "📄 Tài liệu", deleted: "Tin nhắn này đã bị xóa")
        case .telegram:
            switch m.kind {
            case .image: return t.isEmpty ? "🖼 Photo" : "🖼 " + t
            case .video: return t.isEmpty ? "📹 Video" : "📹 " + t
            case .gif: return "GIF"
            case .sticker: return (t.isEmpty ? "" : t + " ") + "Sticker"
            case .voice: return "🎤 Voice message"
            case .audio: return "🎵 " + (m.media?.fileName ?? "Audio")
            case .file: return "📎 " + (m.media?.fileName ?? "File")
            case .location: return "📍 Location"
            case .contact: return "👤 Contact"
            default: return t
            }
        }
    }

    // MARK: Avatar

    /// Circular avatar thumbnail, as the official apps attach to their banners.
    private static func avatarAttachment(_ source: String?, id: String) async -> UNNotificationAttachment? {
        guard let source, !source.isEmpty else { return nil }
        let png: Data? = await withTaskGroup(of: Data?.self) { group in
            group.addTask { @MainActor in
                guard let img = await ImageLoader.shared.load(source) else { return nil }
                return circularPNG(img, side: 128)
            }
            group.addTask { try? await Task.sleep(nanoseconds: 1_500_000_000); return nil }
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }
        guard let png else { return nil }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ChatSpaceNotifications", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("\(abs(id.hashValue)).png")
        guard (try? png.write(to: url)) != nil else { return nil }
        return try? UNNotificationAttachment(identifier: "avatar", url: url, options: nil)
    }

    private static func circularPNG(_ image: NSImage, side: Int) -> Data? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let rect = NSRect(x: 0, y: 0, width: side, height: side)
        NSBezierPath(ovalIn: rect).addClip()
        let s = image.size
        let k = max(CGFloat(side) / max(s.width, 1), CGFloat(side) / max(s.height, 1))
        let w = s.width * k, h = s.height * k
        image.draw(in: NSRect(x: (CGFloat(side) - w) / 2, y: (CGFloat(side) - h) / 2, width: w, height: h))
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}
