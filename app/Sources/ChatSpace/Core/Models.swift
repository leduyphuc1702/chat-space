import Foundation

// Mirrors docs/PROTOCOL.md.

enum AuthState: String, Codable, Sendable {
    case loggedOut = "logged_out"
    case connecting
    case awaitingQR = "awaiting_qr"
    case awaitingPassword = "awaiting_password"
    case awaitingCode = "awaiting_code"
    case awaitingInput = "awaiting_input"
    case loggedIn = "logged_in"
    case error
}

struct SelfInfo: Codable, Sendable, Equatable {
    var id: String
    var name: String
    var avatar: String?
    var phone: String?
}

enum ChatKind: String, Codable, Sendable {
    case direct, group, channel, bot, `self`
}

struct Chat: Codable, Sendable, Identifiable, Equatable {
    var id: String
    var title: String
    var avatar: String?
    var kind: ChatKind
    var unread: Int
    var markedUnread: Bool?
    var pinned: Bool?
    var muted: Bool?
    var mutedUntil: Double?
    var timestamp: Double
    var lastMessage: Message?
    var memberCount: Int?
    var online: Bool?
    var lastSeen: Double?
    var isContact: Bool?
    var archived: Bool?
    var encrypted: Bool?
    /// Last message from before the chat switched to end-to-end encryption; encrypted ones after it may be missing.
    var encryptedAfter: String?

    var isGroup: Bool { kind == .group || kind == .channel }
    var isPinned: Bool { pinned ?? false }
    var isMuted: Bool {
        if let until = mutedUntil { return until < 0 || until > Date().timeIntervalSince1970 * 1000 }
        return muted ?? false
    }
    /// Unread messages, or manually marked as unread.
    var hasUnread: Bool { unread > 0 || (markedUnread ?? false) }
    var date: Date { Date(timeIntervalSince1970: timestamp / 1000) }
}

/// Telegram dialog filter (`chats.folders` / `folders.update`).
struct ChatFolder: Codable, Sendable, Identifiable, Equatable {
    var id: String
    var title: String
    var emoticon: String?
    var includeChatIds: [String] = []
    var excludeChatIds: [String] = []
    var pinnedChatIds: [String] = []
    var contacts: Bool?
    var nonContacts: Bool?
    var groups: Bool?
    var channels: Bool?
    var bots: Bool?
    var excludeMuted: Bool?
    var excludeRead: Bool?
    var excludeArchived: Bool?

    init(id: String, title: String, emoticon: String? = nil, includeChatIds: [String] = [], excludeChatIds: [String] = [],
         pinnedChatIds: [String] = [], contacts: Bool? = nil, nonContacts: Bool? = nil, groups: Bool? = nil,
         channels: Bool? = nil, bots: Bool? = nil, excludeMuted: Bool? = nil, excludeRead: Bool? = nil, excludeArchived: Bool? = nil) {
        self.id = id; self.title = title; self.emoticon = emoticon
        self.includeChatIds = includeChatIds; self.excludeChatIds = excludeChatIds; self.pinnedChatIds = pinnedChatIds
        self.contacts = contacts; self.nonContacts = nonContacts; self.groups = groups; self.channels = channels; self.bots = bots
        self.excludeMuted = excludeMuted; self.excludeRead = excludeRead; self.excludeArchived = excludeArchived
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? ""
        emoticon = try c.decodeIfPresent(String.self, forKey: .emoticon)
        includeChatIds = try c.decodeIfPresent([String].self, forKey: .includeChatIds) ?? []
        excludeChatIds = try c.decodeIfPresent([String].self, forKey: .excludeChatIds) ?? []
        pinnedChatIds = try c.decodeIfPresent([String].self, forKey: .pinnedChatIds) ?? []
        contacts = try c.decodeIfPresent(Bool.self, forKey: .contacts)
        nonContacts = try c.decodeIfPresent(Bool.self, forKey: .nonContacts)
        groups = try c.decodeIfPresent(Bool.self, forKey: .groups)
        channels = try c.decodeIfPresent(Bool.self, forKey: .channels)
        bots = try c.decodeIfPresent(Bool.self, forKey: .bots)
        excludeMuted = try c.decodeIfPresent(Bool.self, forKey: .excludeMuted)
        excludeRead = try c.decodeIfPresent(Bool.self, forKey: .excludeRead)
        excludeArchived = try c.decodeIfPresent(Bool.self, forKey: .excludeArchived)
    }

    func contains(_ c: Chat) -> Bool {
        if excludeChatIds.contains(c.id) { return false }
        if pinnedChatIds.contains(c.id) || includeChatIds.contains(c.id) { return true }
        if excludeMuted == true && c.isMuted { return false }
        if excludeRead == true && !c.hasUnread { return false }
        if excludeArchived == true && c.archived == true { return false }
        switch c.kind {
        case .direct, .self: return c.isContact == true ? contacts == true : nonContacts == true
        case .bot: return bots == true
        case .group: return groups == true
        case .channel: return channels == true
        }
    }

    /// Telegram for macOS picks the sidebar icon from the emoticon, else from the folder's rules.
    var iconName: String {
        let byEmoticon: [String: String] = [
            "🐱": "Animal", "📕": "Book", "💰": "Coin", "🎮": "Game", "🏠": "Home", "💡": "Lamp",
            "👌": "Like", "🔒": "Lock", "❤️": "Love", "🎭": "Mask", "➗": "Math", "🎵": "Music",
            "🎨": "Paint", "✈️": "Plane", "⚽️": "Sport", "⭐": "Star", "🎓": "Student", "🛫": "Telegram",
            "👤": "Personal", "👥": "Group", "💬": "AllChats", "✅": "Read", "🔔": "Unmuted",
            "📁": "Folder", "🗂": "Folder", "⚡": "Flash", "📢": "Channel", "🤖": "Bot", "🍷": "Wine",
            "💼": "Work", "🏛": "Work", "📚": "Book", "💸": "Coin",
        ]
        if let e = emoticon, let n = byEmoticon[e] ?? byEmoticon[e.replacingOccurrences(of: "\u{FE0F}", with: "")] {
            return "Icon_Sidebar_" + n
        }
        let flags = [contacts == true || nonContacts == true, groups == true, channels == true, bots == true]
        if includeChatIds.isEmpty && flags.filter({ $0 }).count == 1 {
            if bots == true { return "Icon_Sidebar_Bot" }
            if channels == true { return "Icon_Sidebar_Channel" }
            if groups == true { return "Icon_Sidebar_Group" }
            return "Icon_Sidebar_Personal"
        }
        if excludeRead == true { return "Icon_Sidebar_Unread" }
        if excludeMuted == true { return "Icon_Sidebar_Unmuted" }
        return "Icon_Sidebar_Folder"
    }
}

enum MessageKind: String, Codable, Sendable {
    case text, image, video, audio, voice, file, sticker, gif, location, contact, link, call, system, unsupported

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = MessageKind(rawValue: raw) ?? .unsupported
    }
}

struct Media: Codable, Sendable, Equatable {
    var mime: String?
    var url: String?
    var path: String?
    var thumbnail: String?
    var width: Double?
    var height: Double?
    var duration: Double?
    var fileName: String?
    var size: Double?

    var aspect: CGFloat? {
        guard let w = width, let h = height, w > 0, h > 0 else { return nil }
        return CGFloat(w / h)
    }
}

struct Reaction: Codable, Sendable, Equatable, Hashable {
    var emoji: String
    var count: Int
    var fromMe: Bool

    init(emoji: String, count: Int, fromMe: Bool) {
        self.emoji = emoji.emojiPresentation
        self.count = count
        self.fromMe = fromMe
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(emoji: try c.decode(String.self, forKey: .emoji),
                  count: try c.decode(Int.self, forKey: .count),
                  fromMe: try c.decode(Bool.self, forKey: .fromMe))
    }
}

extension String {
    /// "❤" without U+FE0F renders as a black text glyph; platforms such as Telegram send it bare.
    var emojiPresentation: String {
        guard unicodeScalars.count == 1, let s = unicodeScalars.first,
              s.properties.isEmoji, !s.properties.isEmojiPresentation, s.value > 0x7F else { return self }
        return self + "\u{FE0F}"
    }
}

struct ReplyRef: Codable, Sendable, Equatable {
    var id: String
    var text: String?
    var senderName: String?
    var kind: MessageKind?
}

enum DeliveryStatus: String, Codable, Sendable {
    case pending, sent, delivered, read, failed
}

/// What pressing a bot button does (docs/PROTOCOL.md, capability `bot_buttons`). Unknown kinds decode as `unsupported`.
enum ButtonKind: String, Codable, Sendable {
    case callback, url, login, game, text, copy, unsupported
    case switchInline = "switch_inline"
    case webApp = "web_app"
    case userProfile = "user_profile"

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = ButtonKind(rawValue: raw) ?? .unsupported
    }
}

/// One button of a bot's inline keyboard (`Message.buttons`) or custom keyboard (`ReplyKeyboard.rows`).
struct BotButton: Codable, Sendable, Equatable, Hashable {
    var text: String
    var kind: ButtonKind
    /// url / login / userProfile: what to open. webApp: the bot's page (the bridge adds the launch data).
    var url: String?
    /// switchInline: "@bot query" to put into the composer.
    var insert: String?
    var samePeer: Bool?
    /// copy: the text for the clipboard.
    var copyText: String?

    /// The account itself must answer the bot (messages.clickButton); the other kinds are handled in the app.
    var viaBridge: Bool { kind == .callback || kind == .game || kind == .webApp }
    var isSupported: Bool { kind != .unsupported }
}

/// A bot's custom keyboard shown under the composer (Telegram ReplyKeyboardMarkup).
struct ReplyKeyboard: Codable, Sendable, Equatable {
    var rows: [[BotButton]]
    var resize: Bool?
    /// Hidden after one press; the keyboard button brings it back.
    var oneTime: Bool?
    var persistent: Bool?
    var placeholder: String?
    var selective: Bool?
}

/// The bot asks for a reply to its message (Telegram ReplyKeyboardForceReply).
struct ForceReply: Codable, Sendable, Equatable {
    var placeholder: String?
    var oneTime: Bool?
}

struct Message: Codable, Sendable, Identifiable, Equatable {
    var id: String
    var chatId: String
    var senderId: String
    var senderName: String?
    var senderAvatar: String?
    var fromMe: Bool
    var timestamp: Double
    var kind: MessageKind
    var text: String?
    var media: Media?
    var replyTo: ReplyRef?
    var forwarded: Bool?
    var edited: Bool?
    var deleted: Bool?
    var reactions: [Reaction]?
    var status: DeliveryStatus?
    /// Inline keyboard under the bubble (bots): rows of buttons.
    var buttons: [[BotButton]]?
    /// This message installs a bot keyboard for the chat; `hideKeyboard` removes it.
    var replyKeyboard: ReplyKeyboard?
    var hideKeyboard: Bool?
    var forceReply: ForceReply?

    var date: Date { Date(timeIntervalSince1970: timestamp / 1000) }
    var isDeleted: Bool { deleted ?? false }
    var isEdited: Bool { edited ?? false }
    var hasReactions: Bool { !(reactions ?? []).isEmpty }

    /// One-line preview for chat lists.
    func preview(photo: String = "Photo", video: String = "Video", voice: String = "Voice message",
                 sticker: String = "Sticker", file: String = "File", deleted: String = "Message deleted") -> String {
        if isDeleted { return deleted }
        let t = text?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        switch kind {
        case .image: return t.isEmpty ? photo : "🖼 " + t
        case .video, .gif: return t.isEmpty ? video : "📹 " + t
        case .voice, .audio: return voice
        case .sticker: return t.isEmpty ? sticker : t
        case .file: return media?.fileName ?? (t.isEmpty ? file : t)
        case .location: return "📍 " + (t.isEmpty ? "Location" : t)
        default: return t
        }
    }
}

/// One screen of a multi-step native login (`auth.step`).
struct AuthStep: Codable, Sendable, Equatable {
    struct Field: Codable, Sendable, Equatable, Identifiable {
        var id: String
        var name: String
        var type: String
        var options: [String]?
        var `default`: String?
    }
    var stepId: String
    var kind: String
    var instructions: String?
    var fields: [Field]?
    var images: [String]?
    var canCancel: Bool?
    var error: String?
}

struct TypingInfo: Equatable {
    var userId: String
    var name: String?
}
