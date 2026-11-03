import SwiftUI

/// WhatsApp Desktop (light) look, Vietnamese strings as in the official localization.
enum WA {
    static let green = Color(hex: 0x1DAA61)
    static let teal = Color(hex: 0x008069)
    static let outBubble = Color(hex: 0xD9FDD3)
    static let inBubble = Color.white
    static let text = Color(hex: 0x111B21)
    static let secondary = Color(hex: 0x667781)
    static let icon = Color(hex: 0x54656F)
    static let panel = Color(hex: 0xF0F2F5)
    static let separator = Color(hex: 0xE9EDEF)
    static let blueTick = Color(hex: 0x53BDEB)
    static let chatBackground = Color(hex: 0xEFEAE2)
    static let chipSelected = Color(hex: 0xD9FDD3)
    static let chipSelectedText = Color(hex: 0x15603E)

    static let nameColors: [UInt32] = [0xE542A3, 0x1F7AEC, 0xD3A01B, 0x02A698, 0xFA6533, 0x7F66FF, 0x00A884, 0xC4532D]
    static func nameColor(_ id: String) -> Color { Color(hex: nameColors[id.stableHash % nameColors.count]) }

    static func dayTitle(_ d: Date) -> String {
        MessageGrouping.dayTitle(d, today: "HÔM NAY", yesterday: "HÔM QUA", locale: Locale(identifier: "vi_VN"), format: "dd/MM").uppercased()
    }

    static func listTime(_ d: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(d) { return Fmt.time(d) }
        if cal.isDateInYesterday(d) { return "Hôm qua" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "vi_VN")
        if let days = cal.dateComponents([.day], from: d, to: Date()).day, days < 7 { f.dateFormat = "EEEE" }
        else { f.dateFormat = "dd/MM/yyyy" }
        return f.string(from: d)
    }

    static let fallbackWallpaper = DoodleWallpaper(
        background: AnyShapeStyle(chatBackground),
        glyphColor: Color(hex: 0xC9BFB1).opacity(0.32),
        symbols: ["camera", "music.note", "heart", "gift", "phone", "cloud", "cup.and.saucer", "bicycle", "umbrella", "star", "moon", "house", "headphones", "lightbulb", "pencil", "airplane"],
        spacing: 56, glyphSize: 18)
}

/// WhatsApp's own doodle (DoodleOverlay from the installed app) over its beige, or a drawn stand-in.
struct WhatsAppWallpaper: View {
    var body: some View {
        if let doodle = NativeAssets.shared.image("DoodleOverlay", .whatsappShared) {
            WA.chatBackground.overlay(
                Image(nsImage: doodle).resizable(resizingMode: .tile).opacity(0.9))
                .clipped()
        } else {
            WA.fallbackWallpaper
        }
    }
}

/// Icon from the installed WhatsApp (main or SharedModules catalog), tinted like the original.
struct WAIcon: View {
    let name: String
    var shared = false
    var fallback: String
    var size: CGFloat = 20
    var body: some View {
        NativeIcon(name: name, catalog: shared ? .whatsappShared : .whatsapp, fallback: fallback, size: size)
    }
}

struct WhatsAppAvatar: View {
    let source: String?
    var group = false
    var size: CGFloat = 49

    var body: some View {
        AvatarView(source: source, size: size) {
            Circle().fill(Color(hex: 0xDFE5E7))
                .overlay {
                    if let img = NativeAssets.shared.image(group ? "GroupChatPlaceholderV2" : "PersonalChatPlaceholderV2", .whatsappShared) {
                        Image(nsImage: img).resizable().scaledToFit()
                    } else {
                        Image(systemName: group ? "person.2.fill" : "person.fill")
                            .font(.system(size: size * (group ? 0.4 : 0.55))).foregroundStyle(.white)
                            .offset(y: group ? 0 : size * 0.08)
                    }
                }
                .clipShape(Circle())
        }
    }
}

struct WhatsAppTicks: View {
    let status: DeliveryStatus?
    var size: CGFloat = 11

    var body: some View {
        switch status {
        case .pending: WAIcon(name: "clock", shared: true, fallback: "clock", size: size).foregroundStyle(WA.secondary)
        case .failed: Image(systemName: "exclamationmark.circle").font(.system(size: size)).foregroundStyle(.red)
        case .sent: WAIcon(name: "checkmark", fallback: "checkmark", size: size + 5).foregroundStyle(WA.secondary)
        default:
            if NativeAssets.shared.image("wds_ic_read", .whatsapp) != nil {
                WAIcon(name: "wds_ic_read", fallback: "checkmark", size: size + 7)
                    .foregroundStyle(status == .read ? WA.blueTick : WA.secondary)
            } else {
                ZStack {
                    Image(systemName: "checkmark").offset(x: -2.5)
                    Image(systemName: "checkmark").offset(x: 2.5)
                }
                .font(.system(size: size - 1, weight: .semibold))
                .foregroundStyle(status == .read ? WA.blueTick : WA.secondary)
                .frame(width: size + 6)
            }
        }
    }
}

struct WhatsAppRootView: View {
    let session: PlatformSession

    var body: some View {
        Group {
            if session.authState == .loggedIn {
                HStack(spacing: 0) {
                    WhatsAppNavBar(session: session)
                    Rectangle().fill(WA.separator).frame(width: 1)
                    WhatsAppChatList(session: session).frame(width: 360)
                    Rectangle().fill(WA.separator).frame(width: 1)
                    if let chat = session.selectedChat {
                        WhatsAppConversation(session: session, chat: chat).id(chat.id)
                    } else {
                        WhatsAppPlaceholder()
                    }
                }
            } else if session.authState == .connecting || (session.bridgeError != nil && session.authState != .error) {
                BridgeStatusView(session: session, tint: WA.teal)
            } else {
                WhatsAppLogin(session: session)
            }
        }
        .background(Color.white)
        .environment(\.colorScheme, .light)
    }
}

struct WhatsAppNavBar: View {
    let session: PlatformSession

    var body: some View {
        VStack(spacing: 6) {
            Color.clear.frame(height: 34)
            navIcon("TabBarChatsActive", "message.fill", selected: true, badge: session.unreadTotal)
            navIcon("TabBarCalls", "phone")
            navIcon("TabBarUpdates", "circle.dashed")
            navIcon("TabBarCommunities", "person.3")
            Spacer()
            navIcon("IconStar", "star")
            navIcon("ArchivedEntryIcon", "archivebox")
            Rectangle().fill(WA.separator).frame(width: 30, height: 1).padding(.vertical, 4)
            navIcon("TabBarSettings", "gearshape")
            WhatsAppAvatar(source: session.selfInfo?.avatar, size: 30).padding(.bottom, 14).padding(.top, 4)
        }
        .frame(width: 60)
        .background(Color(hex: 0xF7F5F3))
    }

    private func navIcon(_ name: String, _ fallback: String, selected: Bool = false, badge: Int = 0) -> some View {
        WAIcon(name: name, fallback: fallback, size: 24).foregroundStyle(WA.icon)
            .frame(width: 40, height: 40)
            .background(Circle().fill(selected ? Color.black.opacity(0.08) : .clear))
            .overlay(alignment: .topTrailing) {
                if badge > 0 {
                    Text("\(badge)").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                        .padding(.horizontal, 4).frame(minWidth: 17, minHeight: 17)
                        .background(Capsule().fill(WA.green)).offset(x: 2, y: -2)
                }
            }
    }
}

struct WhatsAppPlaceholder: View {
    var body: some View {
        VStack(spacing: 18) {
            if let logo = NativeAssets.shared.image("PlaceholderLogo", .whatsapp) {
                Image(nsImage: logo).resizable().scaledToFit().frame(width: 168).opacity(0.6)
            } else {
                Image(systemName: "laptopcomputer.and.iphone").font(.system(size: 90, weight: .ultraLight)).foregroundStyle(Color(hex: 0xB5C4CB))
            }
            Text("WhatsApp cho Mac").font(.system(size: 30, weight: .light)).foregroundStyle(Color(hex: 0x41525D))
            Text("Gửi và nhận tin nhắn mà không cần giữ điện thoại trực tuyến.\nSử dụng WhatsApp trên tối đa 4 thiết bị liên kết và 1 điện thoại cùng lúc.")
                .font(.system(size: 14)).multilineTextAlignment(.center).foregroundStyle(WA.secondary)
            Spacer().frame(height: 30)
            HStack(spacing: 5) {
                WAIcon(name: "lock-e2ee", fallback: "lock.fill", size: 11)
                Text("Được mã hóa đầu cuối")
            }
            .font(.system(size: 13)).foregroundStyle(Color(hex: 0x8696A0))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(hex: 0xF0F2F5))
        .overlay(alignment: .bottom) { Rectangle().fill(WA.green).frame(height: 6) }
    }
}

// MARK: Chat list

struct WhatsAppChatList: View {
    let session: PlatformSession
    @State private var query = ""
    @State private var filter = 0

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Đoạn chat").font(.system(size: 22, weight: .bold)).foregroundStyle(WA.text)
                Spacer()
                WAIcon(name: "IconNewChat", shared: true, fallback: "square.and.pencil", size: 22)
                    .tool("Đoạn chat mới") { ChatTools.nativeOnly(.whatsapp, "Bắt đầu đoạn chat mới") }
                WAIcon(name: "IconFilterLandscape", shared: true, fallback: "line.3.horizontal.decrease", size: 20)
                    .tool("Đánh dấu tất cả đã đọc") { session.markAllRead() }.padding(.leading, 6)
            }
            .foregroundStyle(WA.icon)
            .padding(.horizontal, 16).padding(.top, 34).padding(.bottom, 10)

            HStack(spacing: 10) {
                WAIcon(name: "ListSearchGlyphMask", fallback: "magnifyingglass", size: 13).foregroundStyle(WA.icon)
                TextField("Tìm kiếm hoặc bắt đầu đoạn chat mới", text: $query).textFieldStyle(.plain).font(.system(size: 14))
            }
            .padding(.horizontal, 12).frame(height: 35)
            .background(RoundedRectangle(cornerRadius: 8).fill(WA.panel))
            .padding(.horizontal, 12)

            HStack(spacing: 8) {
                ForEach(Array(["Tất cả", "Chưa đọc", "Yêu thích", "Nhóm"].enumerated()), id: \.offset) { i, title in
                    Text(title).font(.system(size: 14)).lineLimit(1).fixedSize()
                        .foregroundStyle(filter == i ? WA.chipSelectedText : WA.icon)
                        .padding(.horizontal, 12).padding(.vertical, 6)
                        .background(Capsule().fill(filter == i ? WA.chipSelected : WA.panel))
                        .onTapGesture { filter = i }
                }
                Spacer()
            }
            .padding(.horizontal, 12).padding(.vertical, 10)

            if session.connection == "offline" {
                HStack(spacing: 12) {
                    Image(systemName: "wifi.slash").font(.system(size: 20)).foregroundStyle(.white)
                        .frame(width: 44, height: 44).background(Circle().fill(Color(hex: 0xFFD279)))
                    VStack(alignment: .leading) {
                        Text("Máy tính không có kết nối").font(.system(size: 15)).foregroundStyle(WA.text)
                        Text(session.connectionDetail ?? "Đang kết nối lại…").font(.system(size: 13)).foregroundStyle(WA.secondary)
                    }
                    Spacer()
                }
                .padding(12).background(Color(hex: 0xFFF7D9))
            }

            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filtered) { chat in
                        WhatsAppChatRow(session: session, chat: chat, selected: chat.id == session.selectedChatId)
                            .onTapGesture { session.select(chat.id) }
                            .chatContextMenu(platform: session.platform, chat: chat)
                    }
                    if !query.isEmpty {
                        ChatListMessageResults(session: session, query: query, accent: WA.teal)
                    }
                }
            }
        }
        .background(Color.white)
    }

    private var filtered: [Chat] {
        let words = SearchText.terms(query)
        return session.chats.filter { c in
            (words.isEmpty || SearchText.contains(c.title, all: words)) &&
            (filter != 1 || c.hasUnread) && (filter != 3 || c.isGroup) && (filter != 2 || c.isPinned)
        }
    }
}

struct WhatsAppChatRow: View {
    let session: PlatformSession
    let chat: Chat
    let selected: Bool
    @State private var hovered = false

    var body: some View {
        HStack(spacing: 13) {
            WhatsAppAvatar(source: chat.avatar, group: chat.isGroup, size: 49)
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline) {
                    Text(chat.kind == .self ? "\(chat.title) (Bạn)" : chat.title)
                        .font(.system(size: 16)).foregroundStyle(WA.text).lineLimit(1)
                    Spacer(minLength: 6)
                    Text(WA.listTime(chat.date)).font(.system(size: 12))
                        .foregroundStyle(chat.hasUnread && !chat.isMuted ? WA.green : WA.secondary)
                }
                HStack(spacing: 4) {
                    preview
                    Spacer(minLength: 4)
                    if chat.isMuted { WAIcon(name: "MuteIndicatorV2", shared: true, fallback: "speaker.slash.fill", size: 17).foregroundStyle(Color(hex: 0x8696A0)) }
                    if chat.isPinned { WAIcon(name: "ChatCellPinned", shared: true, fallback: "pin.fill", size: 17).foregroundStyle(Color(hex: 0x8696A0)) }
                    if chat.unread > 0 {
                        Text("\(chat.unread)").font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                            .padding(.horizontal, 5).frame(minWidth: 20, minHeight: 20)
                            .background(Capsule().fill(chat.isMuted ? Color(hex: 0x8696A0) : WA.green))
                    } else if chat.markedUnread == true {
                        Circle().fill(chat.isMuted ? Color(hex: 0x8696A0) : WA.green).frame(width: 20, height: 20)
                    }
                }
            }
            .padding(.vertical, 12)
            .overlay(alignment: .bottom) { Rectangle().fill(WA.separator).frame(height: 1) }
        }
        .padding(.leading, 13).padding(.trailing, 15)
        .background(selected ? WA.panel : (hovered ? Color(hex: 0xF5F6F6) : .white))
        .contentShape(Rectangle())
        .onHover { hovered = $0 }
        .onAppear { session.requestAvatar(for: chat) }
    }

    @ViewBuilder
    private var preview: some View {
        if let t = session.typing[chat.id], !t.isEmpty {
            Text(chat.isGroup ? "\(t.first?.name ?? "") đang soạn tin…" : "đang soạn tin…")
                .font(.system(size: 14)).foregroundStyle(WA.green).lineLimit(1)
        } else if let m = chat.lastMessage {
            HStack(spacing: 3) {
                if m.fromMe { WhatsAppTicks(status: m.status, size: 12) }
                if chat.isGroup && !m.fromMe, let n = m.senderName { Text(n + ":").lineLimit(1).layoutPriority(-1) }
                if m.isDeleted { Image(systemName: "nosign").font(.system(size: 12)) }
                Text(m.isDeleted ? (m.fromMe ? "Bạn đã xóa tin nhắn này" : "Tin nhắn này đã bị xóa")
                     : m.preview(photo: "📷 Ảnh", video: "🎥 Video", voice: "🎤 Tin nhắn thoại", sticker: "Nhãn dán", file: "📄 Tài liệu"))
                    .lineLimit(1)
            }
            .font(.system(size: 14)).foregroundStyle(WA.secondary)
        }
    }
}

// MARK: Conversation

struct WhatsAppConversation: View {
    @Environment(\.compactChat) private var compact
    let session: PlatformSession
    let chat: Chat
    @State private var showSearch = false
    @State private var showInfo = false

    var body: some View {
        VStack(spacing: 0) {
            header
            PinnedBar(session: session, chat: chat)
            ZStack {
                WhatsAppWallpaper()
                ChatScroll(session: session, chatId: chat.id, spacing: 0,
                           padding: EdgeInsets(top: 12, leading: 0, bottom: 12, trailing: 0)) { rows in
                    if session.reachedStart.contains(chat.id) {
                        Text("🔒 Tin nhắn và cuộc gọi được mã hóa đầu cuối. Chỉ những người tham gia đoạn chat này mới có thể đọc, nghe hoặc chia sẻ. Nhấp để tìm hiểu thêm.")
                            .font(.system(size: 12.5)).foregroundStyle(Color(hex: 0x54656F)).multilineTextAlignment(.center)
                            .padding(.horizontal, 12).padding(.vertical, 6)
                            .background(RoundedRectangle(cornerRadius: 7.5).fill(Color(hex: 0xFFEECD)))
                            .frame(maxWidth: 500).padding(.bottom, 8)
                    }
                    ForEach(rows) { row in
                        if row.showDayHeader {
                            WhatsAppSystemChip(text: WA.dayTitle(row.message.date)).padding(.vertical, 8)
                        }
                        WhatsAppMessageRow(session: session, chat: chat, row: row)
                            .revealHighlight(session, chatId: chat.id, id: row.id)
                            .padding(.top, row.isFirstInGroup ? 10 : 2)
                    }
                }
            }
            WhatsAppComposer(session: session, chat: chat)
                    .mentionSuggestions(session: session, chat: chat, accent: WA.teal)
        }
        .fileDropTarget(session: session, chatId: chat.id, accent: WA.teal)
    }

    private var header: some View {
        HStack(spacing: 14) {
            WhatsAppAvatar(source: chat.avatar, group: chat.isGroup, size: 40)
            VStack(alignment: .leading, spacing: 1) {
                Text(chat.title).font(.system(size: 16)).foregroundStyle(WA.text).lineLimit(1)
                subtitle.font(.system(size: 13)).lineLimit(1)
            }
            Spacer()
            HStack(spacing: 6) {
                WAIcon(name: "VideoCamera", shared: true, fallback: "video", size: 20)
                    .tool("Cuộc gọi video") { ChatTools.call(.whatsapp, video: true) }
                WAIcon(name: "Phone", shared: true, fallback: "phone", size: 20)
                    .tool("Cuộc gọi thoại") { ChatTools.call(.whatsapp, video: false) }
                WAIcon(name: "IconSearch", shared: true, fallback: "magnifyingglass", size: 18)
                    .toolPopover("Tìm kiếm", isPresented: $showSearch) { InChatSearch(session: session, chatId: chat.id, placeholder: "Tìm kiếm...") }
            }
            .font(.system(size: 17)).foregroundStyle(WA.icon)
        }
        .padding(.horizontal, compact ? 12 : 16).frame(height: compact ? 54 : 60).padding(.top, compact ? 0 : 6)
        .background(WA.panel)
        .overlay(alignment: .bottom) { Rectangle().fill(WA.separator).frame(height: 1) }
    }

    private var subtitle: Text {
        if let t = session.typing[chat.id], !t.isEmpty {
            return Text(chat.isGroup ? "\(t.first?.name ?? "") đang soạn tin…" : "đang soạn tin…").foregroundColor(WA.green)
        }
        if chat.isGroup { return Text("bấm vào đây để xem thông tin nhóm").foregroundColor(WA.secondary) }
        if chat.online == true { return Text("trực tuyến").foregroundColor(WA.secondary) }
        if let ls = chat.lastSeen {
            let d = Date(timeIntervalSince1970: ls / 1000)
            let day = Calendar.current.isDateInToday(d) ? "hôm nay" : (Calendar.current.isDateInYesterday(d) ? "hôm qua" : WA.listTime(d))
            return Text("truy cập lần cuối \(day) lúc \(Fmt.time(d))").foregroundColor(WA.secondary)
        }
        return Text("bấm vào đây để xem thông tin liên hệ").foregroundColor(WA.secondary)
    }
}

struct WhatsAppSystemChip: View {
    let text: String
    var body: some View {
        Text(text).font(.system(size: 12.5)).foregroundStyle(Color(hex: 0x54656F)).multilineTextAlignment(.center)
            .padding(.horizontal, 12).padding(.vertical, 5)
            .background(RoundedRectangle(cornerRadius: 7.5).fill(.white).shadow(color: Color(hex: 0x0B141A).opacity(0.13), radius: 0.5, y: 1))
            .frame(maxWidth: .infinity)
    }
}

struct WhatsAppMessageRow: View {
    let session: PlatformSession
    let chat: Chat
    let row: MessageRow
    @State private var hovered = false
    @State private var picking = false
    @Environment(\.compactChat) private var compact

    var body: some View {
        let m = row.message
        if m.kind == .system {
            WhatsAppSystemChip(text: m.text ?? "").padding(.vertical, 4)
        } else {
            HStack(spacing: 8) {
                if m.fromMe { Spacer(minLength: ChatMetrics.gap(120, compact)) }
                if m.fromMe { translateButton(m); reactButton(m) }
                WhatsAppBubble(session: session, chat: chat, row: row, picking: $picking)
                    .messageContextMenu(session: session, message: m, labels: .vietnamese, reactions: ReactionSets.whatsApp)
                if !m.fromMe { reactButton(m); translateButton(m) }
                if !m.fromMe { Spacer(minLength: ChatMetrics.gap(120, compact)) }
            }
            .padding(.horizontal, compact ? 12 : 60)
            .onHover { hovered = $0 }
        }
    }

    /// WhatsApp's grey smiley beside a hovered bubble; the slot is kept so bubbles don't shift.
    @ViewBuilder
    private func reactButton(_ m: Message) -> some View {
        let show = (hovered || picking) && !m.isDeleted && session.can("reactions")
        Button { picking = true } label: {
            Image(systemName: "face.smiling").font(.system(size: 15)).foregroundStyle(WA.icon)
                .frame(width: 26, height: 26).background(Circle().fill(Color.black.opacity(0.05))).contentShape(Circle())
        }
        .buttonStyle(.plain).help("Bày tỏ cảm xúc")
        .opacity(show ? 1 : 0).allowsHitTesting(show)
        .reactionPicker(m.hasReactions ? .constant(false) : $picking, session: session, message: m, emojis: ReactionSets.whatsApp)
    }

    private func translateButton(_ m: Message) -> some View {
        let show = (hovered || picking) && Translator.canTranslate(m)
        return TranslateToggle(message: m) { on in
            Image(systemName: "translate").font(.system(size: 13)).foregroundStyle(on ? WA.teal : WA.icon)
                .frame(width: 26, height: 26).background(Circle().fill(Color.black.opacity(0.05))).contentShape(Circle())
        }
        .opacity(show ? 1 : 0).allowsHitTesting(show)
    }
}

struct WhatsAppBubble: View {
    let session: PlatformSession
    let chat: Chat
    let row: MessageRow
    @Binding var picking: Bool

    private var m: Message { row.message }

    var body: some View {
        let mediaOnly = (m.kind == .image || m.kind == .video || m.kind == .gif) && (m.text ?? "").isEmpty
        let isSticker = m.kind == .sticker || (m.kind == .text && isJumboEmoji(m.text))
        let fill = m.fromMe ? WA.outBubble : WA.inBubble

        Group {
            if isSticker {
                VStack(alignment: .trailing, spacing: 2) {
                    if m.kind == .sticker { MediaContent(session: session, message: m) }
                    else { Text(m.text ?? "").font(.system(size: 48)) }
                    meta.padding(.horizontal, 5).padding(.vertical, 1)
                        .background(RoundedRectangle(cornerRadius: 6).fill(.white.opacity(0.9)))
                }
            } else {
                BubbleColumn(spacing: 3) {
                    if !m.fromMe && chat.isGroup && row.isFirstInGroup {
                        Text(m.senderName ?? "").font(.system(size: 12.8, weight: .semibold)).foregroundStyle(WA.nameColor(m.senderId))
                            .padding(.horizontal, mediaOnly ? 4 : 0)
                    }
                    if m.forwarded == true {
                        Label("Đã chuyển tiếp", systemImage: "arrowshape.turn.up.right").font(.system(size: 12.5).italic()).foregroundStyle(WA.secondary)
                    }
                    if let r = m.replyTo { reply(r).bubbleFill() }
                    content(mediaOnly: mediaOnly)
                    if Translator.shared.isShown(m) {
                        TranslationQuote(message: m, bar: WA.teal, color: WA.text.opacity(0.75), size: 13.5)
                            .padding(.horizontal, mediaOnly ? 4 : 0).padding(.top, 2)
                    }
                }
                .padding(mediaOnly ? 3 : 0)
                .padding(.horizontal, mediaOnly ? 0 : 9).padding(.top, mediaOnly ? 0 : 6).padding(.bottom, mediaOnly ? 0 : 7)
                .background(
                    RoundedRectangle(cornerRadius: 7.5, style: .continuous).fill(fill)
                        .shadow(color: Color(hex: 0x0B141A).opacity(0.13), radius: 0.5, y: 1))
                .overlay(alignment: m.fromMe ? .topTrailing : .topLeading) {
                    if row.isFirstInGroup {
                        BubbleTail(corner: m.fromMe ? .topRight : .topLeft).fill(fill)
                            .frame(width: 8, height: 13)
                            .offset(x: m.fromMe ? 8 : -8)
                    }
                }
            }
        }
        .overlay(alignment: m.fromMe ? .bottomLeading : .bottomTrailing) {
            if let rs = m.reactions, !rs.isEmpty {
                Button { if session.can("reactions") { picking = true } } label: {
                    HStack(spacing: 2) {
                        ForEach(rs, id: \.emoji) { Text($0.emoji).font(.system(size: 13)) }
                        let total = rs.reduce(0) { $0 + $1.count }
                        if total > 1 { Text("\(total)").font(.system(size: 12)).foregroundStyle(WA.secondary) }
                    }
                    .padding(.horizontal, 6).frame(height: 22)
                    .background(Capsule().fill(.white).shadow(color: .black.opacity(0.15), radius: 1, y: 0.5))
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .reactionPicker($picking, session: session, message: m, emojis: ReactionSets.whatsApp)
                .offset(x: m.fromMe ? 10 : -10, y: 18)
            }
        }
        .padding(.bottom, m.hasReactions ? 18 : 0)
        .modifier(CapWidthModifier(max: 520))
    }

    @ViewBuilder
    private func content(mediaOnly: Bool) -> some View {
        if m.isDeleted {
            HStack(spacing: 5) {
                Image(systemName: "nosign")
                Text(m.fromMe ? "Bạn đã xóa tin nhắn này" : "Tin nhắn này đã bị xóa").italic()
                meta.padding(.leading, 6)
            }
            .font(.system(size: 14)).foregroundStyle(WA.secondary)
        } else {
            switch m.kind {
            case .image, .video, .gif:
                VStack(alignment: .leading, spacing: 4) {
                    MediaContent(session: session, message: m, maxWidth: 330, cornerRadius: 6)
                        .overlay(alignment: .bottomTrailing) {
                            if mediaOnly {
                                meta.environment(\.colorScheme, .dark).foregroundStyle(.white)
                                    .padding(.horizontal, 6).padding(.vertical, 3)
                                    .background(LinearGradient(colors: [.clear, .black.opacity(0.35)], startPoint: .top, endPoint: .bottom))
                                    .padding(4)
                            }
                        }
                    if !mediaOnly { textWithMeta.padding(.horizontal, 2) }
                }
            case .file, .audio, .voice:
                VStack(alignment: .leading, spacing: 4) {
                    FileContent(session: session, message: m, iconColor: m.kind == .voice ? WA.teal : Color(hex: 0x8696A0),
                                titleColor: WA.text, subtitleColor: WA.secondary)
                        .padding(8)
                        .background(RoundedRectangle(cornerRadius: 6).fill(Color.black.opacity(0.04)))
                    HStack { Spacer(); meta }
                }
            default:
                textWithMeta
            }
        }
    }

    private var textWithMeta: some View {
        let reserve = Text("    " + (m.isEdited ? "Đã chỉnh sửa " : "") + Fmt.time(m.date) + (m.fromMe ? "  ✓✓" : ""))
            .font(.system(size: 11)).foregroundColor(.clear)
        return ZStack(alignment: .bottomTrailing) {
            (Text(linkified(m.text ?? "", linkColor: Color(hex: 0x027EB5))) + reserve)
                .font(.system(size: 14.2)).foregroundStyle(WA.text).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            meta.offset(y: 3)
        }
    }

    private var meta: some View {
        HStack(spacing: 3) {
            if m.isEdited { Text("Đã chỉnh sửa") }
            Text(Fmt.time(m.date))
            if m.fromMe { WhatsAppTicks(status: m.status) }
        }
        .font(.system(size: 11)).foregroundStyle(WA.secondary)
    }

    private func reply(_ r: ReplyRef) -> some View {
        let color = WA.nameColor(r.senderName ?? r.id)
        return HStack(spacing: 0) {
            Rectangle().fill(color).frame(width: 4)
            VStack(alignment: .leading, spacing: 2) {
                Text(r.senderName ?? "Bạn").font(.system(size: 12.8, weight: .semibold)).foregroundStyle(color)
                Text(r.text ?? "📷 Ảnh").font(.system(size: 13)).foregroundStyle(WA.secondary).lineLimit(2)
            }
            .padding(.horizontal, 8).padding(.vertical, 5)
        }
        .frame(minWidth: 120, alignment: .leading)
        .background(Color.black.opacity(0.05))
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
        .onTapGesture {
            session.scrollTarget[m.chatId] = nil
            DispatchQueue.main.async { session.scrollTarget[m.chatId] = r.id }
        }
    }
}

struct WhatsAppComposer: View {
    let session: PlatformSession
    let chat: Chat
    @Environment(\.compactChat) private var compact

    var body: some View {
        let draft = Binding(get: { session.drafts[chat.id] ?? "" }, set: { session.drafts[chat.id] = $0 })
        let collapsed = compact && !draft.wrappedValue.isEmpty
        VStack(spacing: 0) {
            if let e = session.editing[chat.id] {
                HStack(spacing: 0) {
                    Rectangle().fill(WA.green).frame(width: 4)
                    WAIcon(name: "Pencil", fallback: "pencil", size: 16).foregroundStyle(WA.green).padding(.leading, 10)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Chỉnh sửa tin nhắn").font(.system(size: 12.8, weight: .semibold)).foregroundStyle(WA.green)
                        if let err = session.editError[chat.id] {
                            Text(err).font(.system(size: 13)).foregroundStyle(.red).lineLimit(2)
                        } else {
                            Text(e.preview()).font(.system(size: 13)).foregroundStyle(WA.secondary).lineLimit(1)
                        }
                    }
                    .padding(8)
                    Spacer()
                    Button { session.cancelEdit(chat.id) } label: {
                        Image(systemName: "xmark").foregroundStyle(WA.icon).padding(10)
                    }.buttonStyle(.plain).help("Hủy chỉnh sửa (Esc)")
                }
                .fixedSize(horizontal: false, vertical: true)
                .background(RoundedRectangle(cornerRadius: 7).fill(Color(hex: 0xF0F2F5)))
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .padding(.horizontal, 60).padding(.top, 8)
            } else if let r = session.replyingTo[chat.id] {
                HStack(spacing: 0) {
                    Rectangle().fill(WA.nameColor(r.senderId)).frame(width: 4)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(r.fromMe ? "Bạn" : (r.senderName ?? chat.title)).font(.system(size: 12.8, weight: .semibold)).foregroundStyle(WA.nameColor(r.senderId))
                        Text(r.preview()).font(.system(size: 13)).foregroundStyle(WA.secondary).lineLimit(1)
                    }
                    .padding(8)
                    Spacer()
                    Button { session.replyingTo[chat.id] = nil } label: {
                        Image(systemName: "xmark").foregroundStyle(WA.icon).padding(10)
                    }.buttonStyle(.plain)
                }
                .background(RoundedRectangle(cornerRadius: 7).fill(Color(hex: 0xF0F2F5)))
                .clipShape(RoundedRectangle(cornerRadius: 7))
                .padding(.horizontal, 60).padding(.top, 8)
            }
            TranslationNotice(session: session, chatId: chat.id)
            MathSendPreview(session: session, chatId: chat.id, accent: WA.green)
            ComposerAttachments(session: session, chatId: chat.id, accent: WA.green)
            HStack(alignment: .bottom, spacing: 8) {
                Button { if let f = pickFile() { session.send(draft.wrappedValue, in: chat.id, filePath: f) } } label: {
                    WAIcon(name: "ButtonAttachCatalyst", fallback: "plus", size: 24).foregroundStyle(WA.icon).frame(width: 40, height: 40)
                }.buttonStyle(.plain)
                HStack(alignment: .bottom, spacing: 10) {
                    if !collapsed {
                        WAIcon(name: "ChatBarIconEmoji", shared: true, fallback: "face.smiling", size: 22).foregroundStyle(WA.icon)
                            .tool("Biểu tượng cảm xúc", size: 30) { Composer.emoji() }.padding(.bottom, 4)
                    }
                    OutgoingLanguageButton(session: session, chatId: chat.id, tint: WA.teal, idle: WA.icon, iconSize: 17, toolSize: 30,
                                           hideWhenIdle: collapsed)
                        .padding(.bottom, 4)
                    ComposerTextView(text: draft, placeholder: "Nhập tin nhắn", font: .systemFont(ofSize: 15),
                                     textColor: NSColor(WA.text), placeholderColor: NSColor(Color(hex: 0x8696A0)),
                                     maxHeight: compact ? 220 : 140,
                                     onSubmit: { session.send(draft.wrappedValue, in: chat.id) },
                                     onTypingChange: { session.setTyping(chat.id, $0) },
                                 mentionSession: session, mentionChatId: chat.isGroup ? chat.id : nil,
                                 onFiles: { ChatTools.sendPasted($0, session: session, chatId: chat.id) },
                                 onCommand: { session.composerCommand($0, in: chat.id) })
                        .padding(.vertical, 7)
                        .composerTapArea()
                }
                .padding(.horizontal, 12)
                .background(RoundedRectangle(cornerRadius: 8).fill(.white))
                if session.editing[chat.id] != nil {
                    Button { session.send(draft.wrappedValue, in: chat.id) } label: {
                        Image(systemName: "checkmark").font(.system(size: 17, weight: .bold))
                            .foregroundStyle(.white).frame(width: 40, height: 40)
                            .background(Circle().fill(WA.green))
                            .contentShape(Circle())
                    }.buttonStyle(.plain).help("Lưu")
                } else if draft.wrappedValue.isEmpty && session.attachments[chat.id] == nil {
                    WAIcon(name: "ChatBarMicrophone", shared: true, fallback: "mic", size: 22)
                        .foregroundStyle(WA.icon).frame(width: 40, height: 40)
                        .voiceTool(session: session, chatId: chat.id)
                } else {
                    Button { session.send(draft.wrappedValue, in: chat.id) } label: {
                        WAIcon(name: "IconSend", shared: true, fallback: "paperplane.fill", size: 20)
                            .foregroundStyle(.white).frame(width: 40, height: 40)
                            .background(Circle().fill(WA.green))
                            .contentShape(Circle())
                    }.buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 6)
        }
        .background(WA.panel)
    }
}

// MARK: Login

struct WhatsAppLogin: View {
    let session: PlatformSession

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                PlatformIcon(platform: .whatsapp, size: 30)
                Text("WhatsApp").font(.system(size: 16, weight: .semibold)).foregroundStyle(WA.green)
                Spacer()
            }
            .padding(.horizontal, 40).padding(.top, 40).padding(.bottom, 24)

            HStack(alignment: .top, spacing: 50) {
                VStack(alignment: .leading, spacing: 22) {
                    Text("Các bước đăng nhập").font(.system(size: 28, weight: .light)).foregroundStyle(Color(hex: 0x41525D))
                    step(1, "Mở WhatsApp trên điện thoại của bạn")
                    step(2, "Trên Android, nhấn vào **Menu** ⋮ · Trên iPhone, nhấn vào **Cài đặt** ⚙︎")
                    step(3, "Nhấn vào **Thiết bị đã liên kết**, sau đó nhấn vào **Liên kết thiết bị**")
                    step(4, "Quét mã QR để xác nhận")
                    if let e = session.authError {
                        Text(e).font(.system(size: 13)).foregroundStyle(.red)
                    }
                    Spacer().frame(height: 30)
                    Label("Tin nhắn cá nhân của bạn được mã hóa đầu cuối", systemImage: "lock.fill")
                        .font(.system(size: 13)).foregroundStyle(WA.secondary)
                }
                .frame(maxWidth: 420, alignment: .leading)

                ZStack {
                    if let img = session.qrImage {
                        Image(nsImage: img).interpolation(.none).resizable().frame(width: 264, height: 264)
                        Circle().fill(.white).frame(width: 64, height: 64)
                            .overlay(PlatformIcon(platform: .whatsapp, size: 48).clipShape(Circle()))
                        QRExpiryOverlay(session: session, tint: Color(hex: 0x008069)).frame(width: 264, height: 264)
                    } else {
                        ProgressView().frame(width: 264, height: 264)
                    }
                }
                .padding(10)
                .background(.white)
            }
            .padding(50)
            .background(RoundedRectangle(cornerRadius: 24).fill(.white).overlay(RoundedRectangle(cornerRadius: 24).stroke(Color(hex: 0xE9EDEF))))
            .frame(maxWidth: 900)
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(hex: 0xFCF5EB))
        .onAppear { session.ensureQR() }
        .onDisappear { session.stopWantingQR() }
    }

    private func step(_ n: Int, _ s: LocalizedStringKey) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Text("\(n)").font(.system(size: 14)).foregroundStyle(WA.text)
                .frame(width: 26, height: 26).overlay(Circle().stroke(WA.text, lineWidth: 1))
            Text(s).font(.system(size: 17)).foregroundStyle(Color(hex: 0x3B4A54))
        }
    }
}
