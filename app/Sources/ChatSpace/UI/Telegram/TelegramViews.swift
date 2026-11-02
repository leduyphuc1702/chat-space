import SwiftUI

/// Telegram for macOS look: light theme, blue accent, green outgoing bubbles on the pattern wallpaper.
enum TG {
    static let accent = Color(hex: 0x3390EC)
    static let outBubble = Color(hex: 0xEFFDDE)
    static let inBubble = Color.white
    static let outMeta = Color(hex: 0x4FAE4E)
    static let inMeta = Color(hex: 0x9AA3AC)
    static let secondary = Color(hex: 0x8E8E93)
    static let separator = Color(hex: 0xE7E7E7)
    static let listBackground = Color.white
    static let unreadMuted = Color(hex: 0xB8B8BD)

    static let avatarGradients: [(UInt32, UInt32)] = [
        (0xFF885E, 0xFF516A), (0xFFCD6A, 0xFFA85C), (0x82B1FF, 0x665FFF), (0xA0DE7E, 0x54CB68),
        (0x53EDD6, 0x28C9B7), (0x72D5FD, 0x2A9EF1), (0xE0A2F3, 0xD669ED),
    ]
    static let nameColors: [UInt32] = [0xFC5C51, 0xFA790F, 0x895DD5, 0x0FB297, 0x0FC9D6, 0x3CA5EC, 0xD54FAF]

    static func nameColor(_ id: String) -> Color { Color(hex: nameColors[id.stableHash % nameColors.count]) }

    static func dayTitle(_ d: Date) -> String {
        MessageGrouping.dayTitle(d, today: "Today", yesterday: "Yesterday", locale: Locale(identifier: "en_US"), format: "MMMM d")
    }

    static func listTime(_ d: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(d) { return Fmt.time(d) }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US")
        if let days = cal.dateComponents([.day], from: d, to: Date()).day, days < 7 { f.dateFormat = "EEE" }
        else { f.dateFormat = "dd.MM.yy" }
        return f.string(from: d)
    }

    static let fallbackWallpaper = DoodleWallpaper(
        background: AnyShapeStyle(LinearGradient(
            colors: [Color(hex: 0xDBDDBB), Color(hex: 0x88B884), Color(hex: 0xD5D88D), Color(hex: 0x6BA587)],
            startPoint: .topLeading, endPoint: .bottomTrailing)),
        glyphColor: .black.opacity(0.09),
        symbols: ["paperplane", "cat", "star", "heart", "music.note", "camera", "gift", "moon", "cloud", "leaf", "balloon", "bicycle", "cup.and.saucer", "airplane"],
        spacing: 70, glyphSize: 22)
}

/// Telegram's default "classic" look: the green-yellow gradient with its own builtin doodle pattern
/// (read from the installed Telegram.app), or a drawn stand-in.
struct TelegramWallpaper: View {
    var body: some View {
        if let pattern = NativeAssets.shared.telegramPattern() {
            LinearGradient(colors: [Color(hex: 0xDBDDBB), Color(hex: 0x88B884), Color(hex: 0xD5D88D), Color(hex: 0x6BA587)],
                           startPoint: .topLeading, endPoint: .bottomTrailing)
                .overlay(Image(nsImage: pattern).renderingMode(.template).resizable(resizingMode: .tile)
                    .foregroundStyle(Color.black.opacity(0.13)))
                .clipped()
        } else {
            TG.fallbackWallpaper
        }
    }
}

struct TGIcon: View {
    let name: String
    var fallback: String
    var size: CGFloat? = nil
    var template = true
    var body: some View { NativeIcon(name: name, catalog: .telegram, fallback: fallback, size: size, template: template) }
}

struct TelegramAvatar: View {
    let id: String
    let title: String
    let source: String?
    var size: CGFloat = 50
    var isSaved = false

    var body: some View {
        if isSaved {
            Circle().fill(LinearGradient(colors: [Color(hex: 0x72D5FD), Color(hex: 0x2A9EF1)], startPoint: .top, endPoint: .bottom))
                .frame(width: size, height: size)
                .overlay(TGIcon(name: "Icon_SavedMessages", fallback: "bookmark.fill", size: size * 0.42).foregroundStyle(.white))
        } else {
            AvatarView(source: source, size: size) {
                let g = TG.avatarGradients[id.stableHash % TG.avatarGradients.count]
                Circle().fill(LinearGradient(colors: [Color(hex: g.0), Color(hex: g.1)], startPoint: .top, endPoint: .bottom))
                    .overlay(Text(title.initials).font(.system(size: size * 0.38, weight: .semibold)).foregroundStyle(.white))
            }
        }
    }
}

struct TelegramRootView: View {
    let session: PlatformSession

    var body: some View {
        Group {
            if session.authState == .loggedIn {
                HStack(spacing: 0) {
                    if !session.folders.isEmpty {
                        TelegramFolderSidebar(session: session)
                        Rectangle().fill(TG.separator).frame(width: 1)
                    }
                    TelegramChatList(session: session)
                        .frame(width: 330)
                    Rectangle().fill(TG.separator).frame(width: 1)
                    if let chat = session.selectedChat {
                        TelegramConversation(session: session, chat: chat).id(chat.id)
                    } else {
                        ZStack {
                            TelegramWallpaper()
                            Text("Select a chat to start messaging")
                                .font(.system(size: 13, weight: .medium)).foregroundStyle(.white)
                                .padding(.horizontal, 10).padding(.vertical, 4)
                                .background(Capsule().fill(.black.opacity(0.25)))
                        }
                    }
                }
            } else if session.authState == .connecting || (session.bridgeError != nil && session.authState != .error) {
                BridgeStatusView(session: session, tint: TG.accent)
            } else {
                TelegramLogin(session: session)
            }
        }
        .background(Color.white)
        .environment(\.colorScheme, .light)
    }
}

// MARK: Chat list

struct TelegramChatList: View {
    let session: PlatformSession
    @State private var query = ""

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                HStack(spacing: 6) {
                    TGIcon(name: "Icon_SearchField", fallback: "magnifyingglass", size: 12).foregroundStyle(TG.secondary)
                    TextField("Search", text: $query).textFieldStyle(.plain).font(.system(size: 13))
                }
                .padding(.horizontal, 10).frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 15).fill(Color(hex: 0xF1F1F4)))
                TGIcon(name: "Icon_NewMessage", fallback: "square.and.pencil", size: 26).foregroundStyle(TG.accent)
                    .tool("New Message") { ChatTools.nativeOnly(.telegram, "Tạo tin nhắn mới") }
            }
            .padding(.horizontal, 12)
            .padding(.top, 30).padding(.bottom, 8)

            if session.connection != "online" && session.connection != "connecting" {
                Text(session.connection == "offline" ? "Waiting for network…" : "Connecting…")
                    .font(.system(size: 12)).foregroundStyle(TG.secondary).padding(.bottom, 6)
            }

            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filtered) { chat in
                        TelegramChatRow(session: session, chat: chat, selected: chat.id == session.selectedChatId)
                            .onTapGesture { session.select(chat.id) }
                            .chatContextMenu(platform: session.platform, chat: chat)
                    }
                }
            }
        }
        .background(TG.listBackground)
    }

    private var filtered: [Chat] {
        // Like Telegram, search covers every chat regardless of the selected folder.
        guard !query.isEmpty else { return session.visibleChats }
        return session.chats.filter { $0.title.localizedCaseInsensitiveContains(query) }
    }
}

/// Telegram for macOS "folders on the left": All Chats + each dialog filter with its icon and badge.
struct TelegramFolderSidebar: View {
    let session: PlatformSession

    var body: some View {
        VStack(spacing: 0) {
            Color.clear.frame(height: 28)
            ScrollView(.vertical, showsIndicators: false) {
                VStack(spacing: 2) {
                    item(nil)
                    ForEach(session.folders) { item($0) }
                }
                .padding(.vertical, 4)
            }
            Spacer(minLength: 0)
            TGIcon(name: "Icon_LeftSidebarEditFolders", fallback: "slider.horizontal.3", size: 24)
                .foregroundStyle(TG.secondary)
                .tool("Edit Folders", size: 40) { ChatTools.nativeOnly(.telegram, "Sửa thư mục chat") }
                .padding(.bottom, 12)
        }
        .frame(width: 78)
        .background(Color(hex: 0xF4F4F5))
    }

    private func item(_ f: ChatFolder?) -> some View {
        let selected = session.selectedFolderId == f?.id
        let badge = session.folderBadge(f)
        let tint = selected ? TG.accent : TG.secondary
        return Button {
            session.selectedFolderId = f?.id
        } label: {
            VStack(spacing: 3) {
                TGIcon(name: f?.iconName ?? "Icon_Sidebar_AllChats", fallback: f == nil ? "bubble.left.and.bubble.right.fill" : "folder.fill", size: 30)
                    .foregroundStyle(tint)
                    .overlay(alignment: .topTrailing) {
                        if badge.count > 0 {
                            Text(badge.count > 99 ? "99+" : "\(badge.count)")
                                .font(.system(size: 10.5, weight: .semibold)).foregroundStyle(.white)
                                .padding(.horizontal, 4.5).frame(minWidth: 18, minHeight: 18)
                                .background(Capsule().fill(badge.muted ? TG.unreadMuted : TG.accent))
                                .overlay(Capsule().stroke(Color(hex: 0xF4F4F5), lineWidth: 1.5))
                                .offset(x: 11, y: -5)
                        }
                    }
                Text(f?.title ?? "All Chats")
                    .font(.system(size: 11, weight: selected ? .semibold : .regular))
                    .foregroundStyle(tint)
                    .lineLimit(2).multilineTextAlignment(.center)
                    .frame(maxWidth: 70)
            }
            .padding(.vertical, 7)
            .frame(width: 74)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .focusEffectDisabled()
        .contextMenu {
            Button("Mark All as Read") {
                for c in session.chats where c.hasUnread && (f?.contains(c) ?? true) { session.markRead(c.id) }
            }
        }
    }
}

struct TelegramChatRow: View {
    let session: PlatformSession
    let chat: Chat
    let selected: Bool

    var body: some View {
        let fg: Color = selected ? .white : .black
        let sub: Color = selected ? .white.opacity(0.85) : TG.secondary
        HStack(spacing: 10) {
            TelegramAvatar(id: chat.id, title: chat.title, source: chat.avatar, size: 50, isSaved: chat.kind == .self)
                .overlay(alignment: .bottomTrailing) {
                    if chat.online == true && chat.kind == .direct {
                        Circle().fill(Color(hex: 0x4DCD5E)).frame(width: 12, height: 12)
                            .overlay(Circle().stroke(selected ? TG.accent : .white, lineWidth: 2))
                    }
                }
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    if chat.kind == .group { Image(systemName: "person.2.fill").font(.system(size: 11)).foregroundStyle(fg) }
                    if chat.kind == .channel { Image(systemName: "megaphone.fill").font(.system(size: 11)).foregroundStyle(fg) }
                    Text(chat.kind == .self ? "Saved Messages" : chat.title)
                        .font(.system(size: 14, weight: .semibold)).foregroundStyle(fg).lineLimit(1)
                    if chat.isMuted { TGIcon(name: "Icon_DialogMute", fallback: "speaker.slash.fill", size: 10).foregroundStyle(sub) }
                    Spacer(minLength: 4)
                    if let m = chat.lastMessage, m.fromMe { TelegramChecks(status: m.status, color: selected ? .white : TG.outMeta) }
                    Text(TG.listTime(chat.date)).font(.system(size: 12)).foregroundStyle(sub)
                }
                HStack(alignment: .top, spacing: 4) {
                    preview(fg: fg, sub: sub)
                        .font(.system(size: 13)).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
                    if chat.unread > 0 {
                        Text("\(chat.unread)").font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(selected ? TG.accent : .white)
                            .padding(.horizontal, 6).frame(minWidth: 20, minHeight: 20)
                            .background(Capsule().fill(selected ? .white : (chat.isMuted ? TG.unreadMuted : TG.accent)))
                    } else if chat.markedUnread == true {
                        Circle().fill(selected ? .white : (chat.isMuted ? TG.unreadMuted : TG.accent)).frame(width: 20, height: 20)
                    } else if chat.isPinned {
                        TGIcon(name: "Icon_ChatListPinned", fallback: "pin.fill", size: 20).foregroundStyle(sub)
                    }
                }
            }
        }
        .padding(.horizontal, 10).frame(height: 66)
        .background(selected ? TG.accent : Color.clear)
        .contentShape(Rectangle())
        .onAppear { session.requestAvatar(for: chat) }
    }

    private func preview(fg: Color, sub: Color) -> Text {
        if let t = session.typing[chat.id], !t.isEmpty {
            let who = chat.isGroup ? (t.first?.name ?? "") + " is " : ""
            return Text(who + "typing…").foregroundColor(selected ? .white : TG.accent)
        }
        guard let m = chat.lastMessage else { return Text("") }
        var prefix = Text("")
        if chat.isGroup || chat.kind == .channel {
            if m.fromMe { prefix = Text("You: ").foregroundColor(fg) }
            else if let n = m.senderName, chat.kind == .group { prefix = Text(n.components(separatedBy: " ").first! + ": ").foregroundColor(fg) }
        }
        let isMedia = [.image, .video, .voice, .audio, .file, .sticker, .gif].contains(m.kind) && (m.text ?? "").isEmpty
        let body = Text(m.kind == .system ? (m.text ?? "") : m.preview())
            .foregroundColor(isMedia ? (selected ? .white : TG.accent) : sub)
        return prefix + body
    }
}

struct TelegramChecks: View {
    let status: DeliveryStatus?
    var color: Color

    var body: some View {
        switch status {
        case .pending: TGIcon(name: "Icon_ChatStateSending", fallback: "clock", size: 12).foregroundStyle(color)
        case .failed: TGIcon(name: "Icon_MessageSentFailed", fallback: "exclamationmark.circle.fill", size: 14, template: false).foregroundStyle(.red)
        case .read:
            ZStack {
                TGIcon(name: "Icon_MessageCheckMark1", fallback: "checkmark", size: 12).offset(x: -2.5)
                TGIcon(name: "Icon_MessageCheckmark2", fallback: "checkmark", size: 12).offset(x: 2.5)
            }.font(.system(size: 9, weight: .bold)).foregroundStyle(color).frame(width: 17)
        default: TGIcon(name: "Icon_MessageCheckMark1", fallback: "checkmark", size: 12).font(.system(size: 9, weight: .bold)).foregroundStyle(color).frame(width: 12)
        }
    }
}

// MARK: Conversation

struct TelegramConversation: View {
    @Environment(\.compactChat) private var compact
    let session: PlatformSession
    let chat: Chat
    @State private var showSearch = false
    @State private var showInfo = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Rectangle().fill(TG.separator).frame(height: 1)
            ZStack {
                TelegramWallpaper()
                ChatScroll(session: session, chatId: chat.id, spacing: 0,
                           padding: EdgeInsets(top: 10, leading: 0, bottom: 10, trailing: 0)) { rows in
                    ForEach(rows) { row in
                        if row.showDayHeader {
                            TelegramServicePill(text: TG.dayTitle(row.message.date)).padding(.vertical, 8)
                        }
                        TelegramMessageRow(session: session, chat: chat, row: row)
                            .padding(.top, row.isFirstInGroup ? 6 : 2)
                    }
                }
            }
            TelegramComposer(session: session, chat: chat)
                    .mentionSuggestions(session: session, chat: chat, accent: TG.accent)
        }
    }

    private var header: some View {
        HStack(spacing: 10) {
            TelegramAvatar(id: chat.id, title: chat.title, source: chat.avatar, size: 36, isSaved: chat.kind == .self)
            VStack(alignment: .leading, spacing: 1) {
                Text(chat.kind == .self ? "Saved Messages" : chat.title).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                subtitle.font(.system(size: 12.5)).lineLimit(1)
            }
            Spacer()
            Group {
                TGIcon(name: "Icon_Entertainment_Search", fallback: "magnifyingglass", size: 26)
                    .toolPopover("Search", isPresented: $showSearch) { InChatSearch(session: session, chatId: chat.id, placeholder: "Search") }
                if chat.kind == .direct {
                    TGIcon(name: "Icon_Peer_AudioCall", fallback: "phone", size: 26)
                        .tool("Call") { ChatTools.call(.telegram, video: false) }
                }
                TGIcon(name: "Icon_ChatActionInfo", fallback: "ellipsis", size: 26)
                    .toolPopover("Info", isPresented: $showInfo) { ChatInfoPanel(session: session, chat: chat) }
            }
            .font(.system(size: 16)).foregroundStyle(TG.secondary).padding(.leading, 12)
        }
        .padding(.horizontal, compact ? 12 : 16).frame(height: compact ? 50 : 56).padding(.top, compact ? 0 : 8)
        .background(Color.white)
    }

    private var subtitle: Text {
        if let t = session.typing[chat.id], !t.isEmpty {
            return Text(chat.isGroup ? "\(t.first?.name ?? "") is typing…" : "typing…").foregroundColor(TG.accent)
        }
        switch chat.kind {
        case .group: return Text(chat.memberCount.map { "\($0) members" } ?? "group").foregroundColor(TG.secondary)
        case .channel: return Text(chat.memberCount.map { "\($0) subscribers" } ?? "channel").foregroundColor(TG.secondary)
        case .bot: return Text("bot").foregroundColor(TG.secondary)
        case .self: return Text("")
        case .direct:
            if chat.online == true { return Text("online").foregroundColor(TG.accent) }
            if let ls = chat.lastSeen {
                let d = Date(timeIntervalSince1970: ls / 1000)
                let f = RelativeDateTimeFormatter()
                f.locale = Locale(identifier: "en_US")
                return Text("last seen \(f.localizedString(for: d, relativeTo: Date()))").foregroundColor(TG.secondary)
            }
            return Text("last seen recently").foregroundColor(TG.secondary)
        }
    }
}

struct TelegramServicePill: View {
    let text: String
    var body: some View {
        Text(text).font(.system(size: 13, weight: .medium)).foregroundStyle(.white)
            .multilineTextAlignment(.center)
            .padding(.horizontal, 9).padding(.vertical, 3)
            .background(Capsule().fill(Color(hex: 0x4A6A3A).opacity(0.45)))
            .frame(maxWidth: .infinity)
    }
}

struct TelegramMessageRow: View {
    let session: PlatformSession
    let chat: Chat
    let row: MessageRow
    @State private var hovered = false
    @State private var picking = false
    @Environment(\.compactChat) private var compact

    var body: some View {
        let m = row.message
        if m.kind == .system {
            TelegramServicePill(text: m.text ?? "").padding(.vertical, 4)
        } else {
            HStack(alignment: .bottom, spacing: 6) {
                if m.fromMe { Spacer(minLength: ChatMetrics.gap(80, compact)) }
                if !m.fromMe && chat.kind == .group {
                    if row.isLastInGroup {
                        TelegramAvatar(id: m.senderId, title: m.senderName ?? "?", source: m.senderAvatar, size: 34)
                    } else {
                        Color.clear.frame(width: 34, height: 1)
                    }
                }
                if m.fromMe { replyButton(m); translateButton(m); reactButton(m) }
                TelegramBubble(session: session, chat: chat, row: row)
                    .messageContextMenu(session: session, message: m, reactions: ReactionSets.telegram)
                if !m.fromMe { reactButton(m); translateButton(m); replyButton(m) }
                if !m.fromMe { Spacer(minLength: ChatMetrics.gap(80, compact)) }
            }
            .padding(.horizontal, compact ? 10 : 14)
            .onHover { hovered = $0 }
        }
    }

    /// Telegram Desktop's reaction button beside a hovered bubble; the slot is kept so bubbles don't shift.
    private func reactButton(_ m: Message) -> some View {
        let show = (hovered || picking) && !m.isDeleted && session.can("reactions")
        return Button { picking = true } label: {
            Image(systemName: "heart").font(.system(size: 13, weight: .semibold)).foregroundStyle(TG.accent)
                .frame(width: 28, height: 28)
                .background(Circle().fill(.white).shadow(color: .black.opacity(0.18), radius: 1.5, y: 0.5))
                .contentShape(Circle())
        }
        .buttonStyle(.plain).help("React")
        .opacity(show ? 1 : 0).allowsHitTesting(show)
        .reactionPicker($picking, session: session, message: m, emojis: ReactionSets.telegram)
        .padding(.bottom, 2)
    }

    private func replyButton(_ m: Message) -> some View {
        let show = (hovered || picking) && !m.isDeleted && session.can("reply")
        return Button { session.startReply(m) } label: {
            TGIcon(name: "Icon_ChatAction_ReplyMessage", fallback: "arrowshape.turn.up.left", size: 20).foregroundStyle(TG.accent)
                .frame(width: 28, height: 28)
                .background(Circle().fill(.white).shadow(color: .black.opacity(0.18), radius: 1.5, y: 0.5))
                .contentShape(Circle())
        }
        .buttonStyle(.plain).help("Reply")
        .opacity(show ? 1 : 0).allowsHitTesting(show)
        .padding(.bottom, 2)
    }

    private func translateButton(_ m: Message) -> some View {
        let show = (hovered || picking) && Translator.canTranslate(m)
        return TranslateToggle(message: m) { on in
            Image(systemName: "translate").font(.system(size: 12, weight: .semibold)).foregroundStyle(on ? TG.accent : TG.secondary)
                .frame(width: 28, height: 28)
                .background(Circle().fill(on ? Color(hex: 0xE8F2FC) : .white).shadow(color: .black.opacity(0.18), radius: 1.5, y: 0.5))
                .contentShape(Circle())
        }
        .opacity(show ? 1 : 0).allowsHitTesting(show)
        .padding(.bottom, 2)
    }
}

struct TelegramBubble: View {
    let session: PlatformSession
    let chat: Chat
    let row: MessageRow

    private var m: Message { row.message }
    private var metaColor: Color { m.fromMe ? TG.outMeta : TG.inMeta }

    var body: some View {
        let mediaOnly = (m.kind == .image || m.kind == .video || m.kind == .gif) && (m.text ?? "").isEmpty
        let isSticker = m.kind == .sticker || (m.kind == .text && isJumboEmoji(m.text))

        if isSticker {
            VStack(alignment: m.fromMe ? .trailing : .leading, spacing: 4) {
                if m.kind == .sticker { MediaContent(session: session, message: m) }
                else { Text(m.text ?? "").font(.system(size: 56)) }
                meta.padding(.horizontal, 6).padding(.vertical, 2)
                    .background(Capsule().fill(.black.opacity(0.25)))
                    .environment(\.colorScheme, .dark)
            }
        } else {
            BubbleColumn(spacing: 4) {
                if !m.fromMe && chat.kind == .group && row.isFirstInGroup && !mediaOnly {
                    Text(m.senderName ?? "").font(.system(size: 13, weight: .semibold)).foregroundStyle(TG.nameColor(m.senderId))
                }
                if m.forwarded == true {
                    Text("Forwarded message").font(.system(size: 13)).foregroundStyle(m.fromMe ? TG.outMeta : TG.accent)
                }
                if let r = m.replyTo { reply(r).bubbleFill() }
                content(mediaOnly: mediaOnly)
                if Translator.shared.isShown(m) {
                    TranslationQuote(message: m, bar: m.fromMe ? TG.outMeta : TG.accent, color: .black.opacity(0.7))
                        .padding(.horizontal, mediaOnly ? 8 : 0).padding(.bottom, mediaOnly ? 6 : 0)
                }
                if let rs = m.reactions, !rs.isEmpty { reactions(rs) }
            }
            .padding(.horizontal, mediaOnly ? 0 : 10)
            .padding(.vertical, mediaOnly ? 0 : 6)
            .background(bubbleShape.fill(m.fromMe ? TG.outBubble : TG.inBubble))
            .clipShape(bubbleShape)
            .background(alignment: m.fromMe ? .bottomTrailing : .bottomLeading) {
                if row.isLastInGroup && !mediaOnly {
                    BubbleTail(corner: m.fromMe ? .bottomRight : .bottomLeft)
                        .fill(m.fromMe ? TG.outBubble : TG.inBubble)
                        .frame(width: 10, height: 14)
                        .offset(x: m.fromMe ? 6 : -6)
                }
            }
            .shadow(color: .black.opacity(0.12), radius: 0.5, y: 0.5)
            .modifier(CapWidthModifier(max: 480))
        }
    }

    private var bubbleShape: BubbleShape {
        let big: CGFloat = 16, small: CGFloat = 6
        let tail = row.isLastInGroup
        return m.fromMe
            ? BubbleShape(topLeft: big, topRight: row.isFirstInGroup ? big : small, bottomLeft: big, bottomRight: tail ? 2 : small)
            : BubbleShape(topLeft: row.isFirstInGroup ? big : small, topRight: big, bottomLeft: tail ? 2 : small, bottomRight: big)
    }

    @ViewBuilder
    private func content(mediaOnly: Bool) -> some View {
        if m.isDeleted {
            Text("Message deleted").italic().font(.system(size: 14)).foregroundStyle(TG.secondary)
        } else {
            switch m.kind {
            case .image, .video, .gif:
                VStack(alignment: .leading, spacing: 4) {
                    MediaContent(session: session, message: m, maxWidth: 360, cornerRadius: mediaOnly ? 0 : 10)
                        .overlay(alignment: .bottomTrailing) {
                            if mediaOnly {
                                meta.environment(\.colorScheme, .dark).padding(.horizontal, 6).padding(.vertical, 2)
                                    .background(Capsule().fill(.black.opacity(0.4))).padding(6)
                            }
                        }
                    if !mediaOnly { textWithMeta }
                }
            case .file, .audio, .voice:
                VStack(alignment: .leading, spacing: 4) {
                    FileContent(session: session, message: m, iconColor: m.fromMe ? TG.outMeta : TG.accent,
                                subtitleColor: metaColor)
                    if let t = m.text, !t.isEmpty, m.kind == .file, m.media?.fileName != nil, t != m.media?.fileName { textWithMeta }
                    else { HStack { Spacer(); meta } }
                }
            default:
                textWithMeta
            }
        }
    }

    /// Text with the time/checks tucked into the last line, as Telegram does.
    private var textWithMeta: some View {
        let link = m.fromMe ? Color(hex: 0x3A8E26) : TG.accent
        let reserve = Text("  " + (m.isEdited ? "edited " : "") + Fmt.time(m.date) + (m.fromMe ? "  ✓✓" : ""))
            .font(.system(size: 11.5)).foregroundColor(.clear)
        return ZStack(alignment: .bottomTrailing) {
            (Text(linkified(m.text ?? "", linkColor: link)) + reserve)
                .font(.system(size: 14.5)).foregroundStyle(.black).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            meta.offset(y: 2)
        }
    }

    private var meta: some View {
        HStack(spacing: 3) {
            if m.isEdited { Text("edited") }
            Text(Fmt.time(m.date))
            if m.fromMe { TelegramChecks(status: m.status, color: metaColor) }
        }
        .font(.system(size: 11.5)).foregroundStyle(metaColor)
    }

    private func reply(_ r: ReplyRef) -> some View {
        let color = m.fromMe ? TG.outMeta : TG.nameColor(r.senderName ?? r.id)
        return HStack(spacing: 6) {
            RoundedRectangle(cornerRadius: 1.5).fill(color).frame(width: 3)
            VStack(alignment: .leading, spacing: 1) {
                Text(r.senderName ?? "Reply").font(.system(size: 13, weight: .semibold)).foregroundStyle(color)
                Text(r.text ?? (r.kind.map { $0.rawValue.capitalized } ?? "")).font(.system(size: 13)).foregroundStyle(.black).lineLimit(1)
            }
        }
        .padding(.vertical, 3).padding(.trailing, 8)
        .frame(minWidth: 100, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 4).fill(color.opacity(0.12)))
        .contentShape(Rectangle())
        .onTapGesture {
            session.scrollTarget[m.chatId] = nil
            DispatchQueue.main.async { session.scrollTarget[m.chatId] = r.id }
        }
    }

    private func reactions(_ rs: [Reaction]) -> some View {
        let tint = m.fromMe ? TG.outMeta : TG.accent
        return HStack(spacing: 4) {
            ForEach(rs, id: \.emoji) { r in
                HStack(spacing: 3) {
                    Text(r.emoji).font(.system(size: 14))
                    Text("\(r.count)").font(.system(size: 13, weight: .semibold)).foregroundStyle(r.fromMe ? .white : tint)
                }
                .padding(.horizontal, 7).padding(.vertical, 3)
                .background(Capsule().fill(r.fromMe ? tint : tint.opacity(0.14)))
                .onTapGesture { session.react(m, emoji: r.emoji) }
            }
        }
    }
}

struct TelegramComposer: View {
    let session: PlatformSession
    let chat: Chat
    @Environment(\.compactChat) private var compact

    var body: some View {
        let draft = Binding(get: { session.drafts[chat.id] ?? "" }, set: { session.drafts[chat.id] = $0 })
        let collapsed = compact && !draft.wrappedValue.isEmpty
        VStack(spacing: 0) {
            Rectangle().fill(TG.separator).frame(height: 1)
            if let e = session.editing[chat.id] {
                HStack(spacing: 10) {
                    TGIcon(name: "Icon_ChatAction_EditMessage", fallback: "pencil", size: 26).foregroundStyle(TG.accent)
                    RoundedRectangle(cornerRadius: 1).fill(TG.accent).frame(width: 2, height: 32)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Edit Message").font(.system(size: 13, weight: .semibold)).foregroundStyle(TG.accent)
                        if let err = session.editError[chat.id] {
                            Text(err).font(.system(size: 13)).lineLimit(2).foregroundStyle(.red)
                        } else {
                            Text(e.preview()).font(.system(size: 13)).lineLimit(1).foregroundStyle(.black)
                        }
                    }
                    Spacer()
                    Button { session.cancelEdit(chat.id) } label: { TGIcon(name: "Icon_ChatAction_Close", fallback: "xmark", size: 22).foregroundStyle(TG.secondary) }
                        .buttonStyle(.plain).help("Cancel editing (Esc)")
                }
                .padding(.horizontal, 16).padding(.top, 8)
            } else if let r = session.replyingTo[chat.id] {
                HStack(spacing: 10) {
                    TGIcon(name: "Icon_ChatAction_ReplyMessage", fallback: "arrowshape.turn.up.left", size: 26).foregroundStyle(TG.accent)
                    RoundedRectangle(cornerRadius: 1).fill(TG.accent).frame(width: 2, height: 32)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Reply to \(r.fromMe ? "yourself" : (r.senderName ?? chat.title))").font(.system(size: 13, weight: .semibold)).foregroundStyle(TG.accent)
                        Text(r.preview()).font(.system(size: 13)).lineLimit(1).foregroundStyle(.black)
                    }
                    Spacer()
                    Button { session.replyingTo[chat.id] = nil } label: { TGIcon(name: "Icon_ChatAction_Close", fallback: "xmark", size: 22).foregroundStyle(TG.secondary) }
                        .buttonStyle(.plain)
                }
                .padding(.horizontal, 16).padding(.top, 8)
            }
            TranslationNotice(session: session, chatId: chat.id)
            MathSendPreview(session: session, chatId: chat.id, accent: TG.accent)
            ComposerAttachments(session: session, chatId: chat.id, accent: TG.accent)
            HStack(alignment: .bottom, spacing: compact ? 10 : 14) {
                if !collapsed {
                    Button { if let f = pickFile() { session.send(draft.wrappedValue, in: chat.id, filePath: f) } } label: {
                        TGIcon(name: "Icon_ChatAttach", fallback: "paperclip", size: 30).foregroundStyle(TG.secondary)
                    }.buttonStyle(.plain).padding(.bottom, 1)
                }
                ComposerTextView(text: draft, placeholder: chat.kind == .channel ? "Broadcast a message…" : "Write a message…",
                                 font: .systemFont(ofSize: 14), placeholderColor: NSColor(TG.secondary),
                                 maxHeight: compact ? 220 : 140,
                                 onSubmit: { session.send(draft.wrappedValue, in: chat.id) },
                                 onTypingChange: { session.setTyping(chat.id, $0) },
                                 mentionSession: session, mentionChatId: chat.isGroup ? chat.id : nil,
                                 onFiles: { ChatTools.sendPasted($0, session: session, chatId: chat.id) },
                                 onCommand: { session.composerCommand($0, in: chat.id) })
                    .padding(.vertical, 4)
                    .composerTapArea()
                OutgoingLanguageButton(session: session, chatId: chat.id, tint: TG.accent, idle: TG.secondary, iconSize: 18, toolSize: 30,
                                       hideWhenIdle: collapsed)
                    .padding(.bottom, 1)
                if !collapsed {
                    TGIcon(name: "Icon_Entertainment_Emoji", fallback: "face.smiling", size: 30).foregroundStyle(TG.secondary)
                        .tool("Emoji") { Composer.emoji() }.padding(.bottom, 1)
                }
                if session.editing[chat.id] != nil {
                    Button { session.send(draft.wrappedValue, in: chat.id) } label: {
                        TGIcon(name: "Icon_SaveEditedMessage", fallback: "checkmark.circle.fill", size: 30).foregroundStyle(TG.accent)
                    }.buttonStyle(.plain).padding(.bottom, 1).help("Save")
                } else if draft.wrappedValue.isEmpty && session.attachments[chat.id] == nil {
                    TGIcon(name: "Icon_RecordVoice", fallback: "mic", size: 30).foregroundStyle(TG.secondary)
                        .voiceTool(session: session, chatId: chat.id).padding(.bottom, 1)
                } else {
                    Button { session.send(draft.wrappedValue, in: chat.id) } label: {
                        TGIcon(name: "Icon_SendMessage", fallback: "paperplane.fill", size: 26, template: false).foregroundStyle(TG.accent)
                    }.buttonStyle(.plain).padding(.bottom, 3)
                }
            }
            .padding(.horizontal, 16).padding(.vertical, 10)
        }
        .background(Color.white)
    }
}

// MARK: Login

struct TelegramLogin: View {
    let session: PlatformSession
    @State private var phone = ""
    @State private var code = ""
    @State private var password = ""
    @State private var usePhone = false
    @State private var apiId = AppSettings.telegramApiId
    @State private var apiHash = AppSettings.telegramApiHash

    var body: some View {
        VStack(spacing: 18) {
            Spacer()
            switch session.authState {
            case .awaitingPassword: passwordStep
            case .awaitingCode: codeStep
            default:
                if needsApiKeys { apiStep }
                else if usePhone { phoneStep }
                else { qrStep }
            }
            if let e = session.authError, !needsApiKeys {
                Text(e).font(.system(size: 13)).foregroundStyle(.red).multilineTextAlignment(.center).frame(maxWidth: 360)
            }
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.white)
    }

    private var needsApiKeys: Bool {
        AppSettings.telegramApiId.isEmpty && (session.authError?.contains("API") ?? false)
    }

    private var qrStep: some View {
        VStack(spacing: 20) {
            ZStack {
                RoundedRectangle(cornerRadius: 16).fill(.white).frame(width: 240, height: 240)
                    .shadow(color: .black.opacity(0.08), radius: 8)
                if let img = session.qrImage {
                    Image(nsImage: img).interpolation(.none).resizable().frame(width: 210, height: 210)
                    Circle().fill(TG.accent).frame(width: 46, height: 46)
                        .overlay(Image(systemName: "paperplane.fill").foregroundStyle(.white).font(.system(size: 20)))
                        .overlay(Circle().stroke(.white, lineWidth: 4))
                    QRExpiryOverlay(session: session, tint: TG.accent, label: "Tap to generate a new QR code")
                        .frame(width: 210, height: 210)
                } else {
                    ProgressView()
                }
            }
            Text("Log in to Telegram by QR Code").font(.system(size: 20, weight: .semibold))
            VStack(alignment: .leading, spacing: 10) {
                step(1, "Open Telegram on your phone")
                step(2, "Go to **Settings** → **Devices** → **Link Desktop Device**")
                step(3, "Point your phone at this screen to confirm login")
            }
            .frame(width: 330, alignment: .leading)
            Button("Log in by phone Number") { usePhone = true }
                .buttonStyle(.plain).foregroundStyle(TG.accent).font(.system(size: 14, weight: .medium))
        }
        .onAppear { session.ensureQR() }
        .onDisappear { session.stopWantingQR() }
    }

    private func step(_ n: Int, _ s: LocalizedStringKey) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text("\(n)").font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                .frame(width: 20, height: 20).background(Circle().fill(TG.accent))
            Text(s).font(.system(size: 14))
        }
    }

    private var phoneStep: some View {
        VStack(spacing: 14) {
            TelegramLogoCircle()
            Text("Your Phone Number").font(.system(size: 20, weight: .semibold))
            Text("Please confirm your country code\nand enter your phone number.").multilineTextAlignment(.center).foregroundStyle(TG.secondary)
            tgField("+84 …", text: $phone)
            tgButton("Next") { session.login(method: "phone", phone: phone) }
            Button("Log in by QR Code") { usePhone = false }.buttonStyle(.plain).foregroundStyle(TG.accent)
        }
    }

    private var codeStep: some View {
        VStack(spacing: 14) {
            TelegramLogoCircle()
            Text(phone.isEmpty ? "Enter Code" : phone).font(.system(size: 20, weight: .semibold))
            Text("We've sent the code to the Telegram app\non your other device.").multilineTextAlignment(.center).foregroundStyle(TG.secondary)
            tgField("Code", text: $code)
            tgButton("Next") { session.submit(code: code) }
        }
    }

    private var passwordStep: some View {
        VStack(spacing: 14) {
            TGIcon(name: "Icon_FastSettingsLock", fallback: "lock.fill", size: 40).foregroundStyle(TG.accent)
            Text("Enter Password").font(.system(size: 20, weight: .semibold))
            Text("Your account is protected with\nan additional password.").multilineTextAlignment(.center).foregroundStyle(TG.secondary)
            SecureField("Password", text: $password).textFieldStyle(.plain).padding(10).frame(width: 300)
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(TG.accent, lineWidth: 1.5))
                .onSubmit { session.submit(password: password) }
            tgButton("Next") { session.submit(password: password) }
        }
    }

    private var apiStep: some View {
        VStack(spacing: 14) {
            TelegramLogoCircle()
            Text("Telegram API").font(.system(size: 20, weight: .semibold))
            Text("Bên thứ ba cần API ID/Hash riêng của bạn.\nTạo tại my.telegram.org → API development tools.")
                .multilineTextAlignment(.center).foregroundStyle(TG.secondary)
            tgField("API ID", text: $apiId)
            tgField("API Hash", text: $apiHash)
            tgButton("Save") {
                AppSettings.telegramApiId = apiId.trimmingCharacters(in: .whitespaces)
                AppSettings.telegramApiHash = apiHash.trimmingCharacters(in: .whitespaces)
                session.authError = nil
                session.restart()
            }
        }
    }

    private func tgField(_ title: String, text: Binding<String>) -> some View {
        TextField(title, text: text).textFieldStyle(.plain).font(.system(size: 15)).padding(10).frame(width: 300)
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color(hex: 0xDADCE0), lineWidth: 1))
    }

    private func tgButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title.uppercased()).font(.system(size: 14, weight: .semibold)).foregroundStyle(.white)
                .frame(width: 300, height: 44).background(RoundedRectangle(cornerRadius: 10).fill(TG.accent))
        }.buttonStyle(.plain)
    }
}

struct TelegramLogoCircle: View {
    var body: some View {
        Circle().fill(LinearGradient(colors: [Color(hex: 0x37BBFE), Color(hex: 0x007DBB)], startPoint: .top, endPoint: .bottom))
            .frame(width: 96, height: 96)
            .overlay(Image(systemName: "paperplane.fill").font(.system(size: 42)).foregroundStyle(.white).offset(x: -3, y: 2))
    }
}
