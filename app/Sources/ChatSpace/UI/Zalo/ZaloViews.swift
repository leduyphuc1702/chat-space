import SwiftUI

/// Zalo PC look: blue nav bar, "Ưu tiên / Khác" tabs, light-blue outgoing bubbles.
enum ZL {
    static let blue = Color(hex: 0x0068FF)
    static let navBar = Color(hex: 0x005AE0)
    static let navSelected = Color(hex: 0x004BC2)
    static let text = Color(hex: 0x081C36)
    static let secondary = Color(hex: 0x7589A3)
    static let outBubble = Color(hex: 0xE5EFFF)
    static let outBorder = Color(hex: 0xC7E0FF)
    static let inBubble = Color.white
    static let chatBackground = Color(hex: 0xEEF0F1)
    static let selectedRow = Color(hex: 0xE5EFFF)
    static let hoverRow = Color(hex: 0xF3F5F6)
    static let search = Color(hex: 0xEAEDF0)
    static let badge = Color(hex: 0xFA5555)
    static let divider = Color(hex: 0xDBDDE1)

    static let avatarColors: [(UInt32, UInt32)] = [
        (0x4A9DFF, 0x0068FF), (0xFFB257, 0xFF8A00), (0x7ED8A8, 0x2DB971), (0xC59BFF, 0x8A4FFF), (0xFF8FA3, 0xF04466), (0x6FD6E8, 0x15A3BF),
    ]

    static func listTime(_ d: Date) -> String {
        let s = Date().timeIntervalSince(d)
        if s < 60 { return "Vài giây" }
        if s < 3600 { return "\(Int(s / 60)) phút" }
        if s < 86400 && Calendar.current.isDateInToday(d) { return "\(Int(s / 3600)) giờ" }
        if Calendar.current.isDateInYesterday(d) { return "Hôm qua" }
        if s < 7 * 86400 { return "\(Int(s / 86400)) ngày" }
        let f = DateFormatter()
        f.dateFormat = "dd/MM"
        return f.string(from: d)
    }

    static func separator(_ d: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(d) { return "\(Fmt.time(d)) Hôm nay" }
        if cal.isDateInYesterday(d) { return "\(Fmt.time(d)) Hôm qua" }
        let f = DateFormatter()
        f.dateFormat = "HH:mm dd/MM/yyyy"
        return f.string(from: d)
    }
}

struct ZaloAvatar: View {
    let id: String
    let title: String
    let source: String?
    var size: CGFloat = 48
    var group = false

    var body: some View {
        AvatarView(source: source, size: size) {
            let c = ZL.avatarColors[id.stableHash % ZL.avatarColors.count]
            Circle().fill(LinearGradient(colors: [Color(hex: c.0), Color(hex: c.1)], startPoint: .topLeading, endPoint: .bottomTrailing))
                .overlay {
                    if group && title.isEmpty {
                        ZaloGlyph(code: ZaloGlyphs.group, fallback: "person.3.fill", size: size * 0.45).foregroundStyle(.white)
                    } else {
                        Text(title.initials).font(.zalo(size * 0.36, .semibold)).foregroundStyle(.white)
                    }
                }
        }
    }
}

struct ZaloRootView: View {
    let session: PlatformSession

    var body: some View {
        Group {
            if session.authState == .loggedIn {
                HStack(spacing: 0) {
                    ZaloNavBar(session: session)
                    ZaloChatList(session: session).frame(width: 344)
                    Rectangle().fill(ZL.divider).frame(width: 1)
                    if let chat = session.selectedChat {
                        ZaloConversation(session: session, chat: chat).id(chat.id)
                    } else {
                        ZaloWelcome(session: session)
                    }
                }
            } else if session.authState == .connecting || (session.bridgeError != nil && session.authState != .error) {
                BridgeStatusView(session: session, tint: ZL.blue)
            } else {
                ZaloLogin(session: session)
            }
        }
        .background(Color.white)
        .environment(\.colorScheme, .light)
    }
}

struct ZaloNavBar: View {
    let session: PlatformSession

    var body: some View {
        VStack(spacing: 4) {
            Color.clear.frame(height: 30)
            ZaloAvatar(id: session.selfInfo?.id ?? "me", title: session.selfInfo?.name ?? "", source: session.selfInfo?.avatar, size: 48)
                .overlay(Circle().stroke(.white.opacity(0.9), lineWidth: 1.5))
                .padding(.bottom, 14)
            item(ZaloGlyphs.messageFilled, "message.fill", selected: true, badge: session.unreadTotal)
            item(ZaloGlyphs.contactLine, "person.crop.square.filled.and.at.rectangle")
            item(ZaloGlyphs.todoLine, "checkmark.square")
            Spacer()
            item(ZaloGlyphs.cloudLine, "cloud")
            item(ZaloGlyphs.briefcase, "briefcase")
            item(ZaloGlyphs.settingLine, "gearshape")
                .padding(.bottom, 10)
        }
        .frame(width: 64)
        .background(ZL.navBar)
    }

    private func item(_ glyph: UInt32, _ symbol: String, selected: Bool = false, badge: Int = 0) -> some View {
        ZaloGlyph(code: glyph, fallback: symbol, size: 28).foregroundStyle(.white)
            .frame(width: 64, height: 56)
            .background(selected ? ZL.navSelected : .clear)
            .overlay(alignment: .topTrailing) {
                if badge > 0 {
                    Text(badge > 5 ? "5+" : "\(badge)").font(.zalo(10, .bold)).foregroundStyle(.white)
                        .padding(.horizontal, 4).frame(minWidth: 18, minHeight: 18)
                        .background(Capsule().fill(ZL.badge)).offset(x: -8, y: 8)
                }
            }
    }
}

struct ZaloWelcome: View {
    let session: PlatformSession
    @State private var slide = 0
    private let slides = ["inapp-welcome-screen-01.", "inapp-welcome-screen-02.", "inapp-welcome-screen-03."]
    private let timer = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(spacing: 14) {
            Spacer()
            Text("Chào mừng đến với **Zalo PC**!").font(.zalo(22)).foregroundStyle(ZL.text)
            Text("Khám phá những tiện ích hỗ trợ làm việc và trò chuyện cùng\nngười thân, bạn bè được tối ưu hoá cho máy tính của bạn.")
                .font(.zalo(14)).foregroundStyle(ZL.text.opacity(0.8)).multilineTextAlignment(.center)
            if let art = NativeAssets.shared.zaloImage(slides[slide]) {
                Image(nsImage: art).resizable().scaledToFit().frame(maxWidth: 380).padding(.top, 24)
                    .id(slide).transition(.opacity)
                HStack(spacing: 8) {
                    ForEach(slides.indices, id: \.self) { i in
                        Circle().fill(i == slide ? ZL.blue : Color(hex: 0xC5CED9)).frame(width: 8, height: 8)
                            .onTapGesture { withAnimation { slide = i } }
                    }
                }
                .padding(.bottom, 20)
            } else {
                Image(systemName: "bubble.left.and.text.bubble.right.fill")
                    .font(.zalo(110)).foregroundStyle(ZL.blue.opacity(0.18)).padding(.vertical, 30)
            }
            if session.connection == "offline" {
                Label(session.connectionDetail ?? "Mất kết nối", systemImage: "exclamationmark.triangle.fill")
                    .font(.zalo(13)).foregroundStyle(.orange)
            }
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.white)
        .onReceive(timer) { _ in withAnimation(.easeInOut(duration: 0.4)) { slide = (slide + 1) % slides.count } }
    }
}

// MARK: Chat list

struct ZaloChatList: View {
    let session: PlatformSession
    @State private var query = ""
    @State private var tab = 0
    @State private var moreMenu = false
    @State private var syncHidden = false
    @AppStorage("zaloPhoneSyncedAt") private var syncedAt: Double = 0

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                HStack(spacing: 6) {
                    ZaloGlyph(code: ZaloGlyphs.search, fallback: "magnifyingglass", size: 16).foregroundStyle(ZL.secondary)
                    TextField("Tìm kiếm", text: $query).textFieldStyle(.plain).font(.zalo(14))
                }
                .padding(.horizontal, 10).frame(height: 32)
                .background(RoundedRectangle(cornerRadius: 6).fill(ZL.search))
                ZaloGlyph(code: ZaloGlyphs.addUser, fallback: "person.badge.plus", size: 20)
                    .tool("Thêm bạn") { ChatTools.nativeOnly(.zalo, "Thêm bạn") }
                ZaloGlyph(code: ZaloGlyphs.createGroup, fallback: "person.2.badge.plus", size: 20)
                    .tool("Tạo nhóm chat") { ChatTools.nativeOnly(.zalo, "Tạo nhóm chat") }
            }
            .font(.zalo(15)).foregroundStyle(ZL.text)
            .padding(.horizontal, 16).padding(.top, 30).padding(.bottom, 8)

            HStack(spacing: 18) {
                tabButton("Ưu tiên", 0)
                tabButton("Khác", 1)
                Spacer()
                HStack(spacing: 3) {
                    Text("Phân loại")
                    ZaloGlyph(code: ZaloGlyphs.chevronDown, fallback: "chevron.down", size: 14)
                }
                .font(.zalo(13)).foregroundStyle(ZL.text)
                ZaloGlyph(code: ZaloGlyphs.moreH, fallback: "ellipsis", size: 18).foregroundStyle(ZL.text)
                    .toolPopover("Thêm", size: 26, isPresented: $moreMenu) {
                        VStack(alignment: .leading, spacing: 0) {
                            menuRow("Đánh dấu tất cả đã đọc") { session.markAllRead() }
                            if session.can("phone_sync") {
                                menuRow("Đồng bộ tin nhắn từ điện thoại") {
                                    syncHidden = false
                                    session.startPhoneSync()
                                }
                                .disabled(session.phoneSync?.running == true)
                            }
                        }
                        .padding(.vertical, 6).frame(width: 260)
                        .preferredColorScheme(.light)
                    }
            }
            .padding(.horizontal, 16).frame(height: 36)
            .overlay(alignment: .bottom) { Rectangle().fill(ZL.divider).frame(height: 1) }

            if session.connection == "offline" {
                HStack(spacing: 8) {
                    Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.orange)
                    Text(session.connectionDetail ?? "Mất kết nối, đang thử kết nối lại…").font(.zalo(12.5)).foregroundStyle(ZL.text)
                    Spacer()
                }
                .padding(.horizontal, 16).padding(.vertical, 8).background(Color(hex: 0xFFF6E0))
            }

            if session.can("phone_sync"), !syncHidden, showSyncBanner {
                ZaloSyncBanner(session: session) { syncHidden = true }
            }

            let list = filtered
            if list.isEmpty && query.isEmpty && tab == 0 && !session.chats.isEmpty {
                VStack(spacing: 10) {
                    ZaloGlyph(code: ZaloGlyphs.messageLine, fallback: "bubble.left.and.bubble.right", size: 34).foregroundStyle(ZL.secondary)
                    Text("Chưa có tin nhắn mới").font(.zalo(14, .semibold)).foregroundStyle(ZL.text)
                    Text(session.can("phone_sync")
                         ? "Lịch sử chat Zalo nằm trên điện thoại. Đồng bộ tin nhắn từ điện thoại để xem lại các cuộc trò chuyện cũ; tin nhắn mới sẽ tự hiện ở đây."
                         : "Zalo lưu lịch sử chat trên điện thoại và không cho máy tính mới tải lại tin cũ, nên ChatSpace hiện các đoạn chat ở đây khi có tin nhắn mới đến. Bạn vẫn nhắn được cho mọi bạn bè và nhóm.")
                        .font(.zalo(12.5)).foregroundStyle(ZL.secondary).multilineTextAlignment(.center)
                    Button("Xem tất cả bạn bè và nhóm") { tab = 1 }
                        .buttonStyle(.plain).font(.zalo(13, .medium)).foregroundStyle(ZL.blue)
                }
                .padding(28)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(list) { chat in
                            ZaloChatRow(session: session, chat: chat, selected: chat.id == session.selectedChatId)
                                .onTapGesture { session.select(chat.id) }
                                .chatContextMenu(platform: session.platform, chat: chat)
                        }
                        if !query.isEmpty {
                            ChatListMessageResults(session: session, query: query, accent: ZL.blue)
                        }
                    }
                }
            }
        }
        .background(Color.white)
        .task { await session.loadPhoneSyncStatus() }
    }

    /// Offered until the first successful sync; afterwards only while one runs or just ended.
    private var showSyncBanner: Bool {
        guard let p = session.phoneSync else { return syncedAt == 0 }
        return p.state == .idle || p.state == .cancelled ? syncedAt == 0 : true
    }

    private func menuRow(_ title: String, action: @escaping () -> Void) -> some View {
        ZaloMenuRow(title: title) {
            moreMenu = false
            action()
        }
    }

    private func tabButton(_ title: String, _ i: Int) -> some View {
        Text(title).font(.zalo(13.5, tab == i ? .semibold : .regular))
            .foregroundStyle(tab == i ? ZL.blue : ZL.secondary)
            .frame(height: 36)
            .overlay(alignment: .bottom) { if tab == i { Rectangle().fill(ZL.blue).frame(height: 2) } }
            .onTapGesture { tab = i }
    }

    /// "Khác" holds chats with strangers/unknown names and muted chats, approximating Zalo's split.
    private var filtered: [Chat] {
        let words = SearchText.terms(query)
        return session.chats.filter { c in
            let other = c.isMuted || c.lastMessage == nil
            return (words.isEmpty || SearchText.contains(c.title, all: words)) && (query.isEmpty ? (tab == 0 ? !other : other) : true)
        }
    }
}

private struct ZaloMenuRow: View {
    let title: String
    let action: () -> Void
    @Environment(\.isEnabled) private var enabled
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            Text(title).font(.zalo(13.5)).foregroundStyle(enabled ? ZL.text : ZL.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 14).padding(.vertical, 8)
                .background(hovered && enabled ? ZL.hoverRow : .clear)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
    }
}

/// "Đồng bộ tin nhắn" banner at the top of the chat list, like Zalo PC's: offer, phone prompt, progress, result.
struct ZaloSyncBanner: View {
    let session: PlatformSession
    var onClose: () -> Void

    var body: some View {
        let p = session.phoneSync ?? PhoneSyncProgress(state: .idle)
        HStack(alignment: .top, spacing: 10) {
            icon(p)
            VStack(alignment: .leading, spacing: 4) {
                Text(title(p)).font(.zalo(13.5, .semibold)).foregroundStyle(ZL.text)
                if let d = detail(p) {
                    Text(d).font(.zalo(12.5)).foregroundStyle(ZL.secondary).fixedSize(horizontal: false, vertical: true)
                }
                if p.state == .messages || p.state == .paused, let total = p.chats, total > 0 {
                    ProgressView(value: Double(p.chatsDone ?? 0), total: Double(total))
                        .progressViewStyle(.linear).tint(ZL.blue).padding(.top, 2)
                }
                HStack(spacing: 14) {
                    switch p.state {
                    case .idle, .cancelled:
                        action("Đồng bộ ngay") { session.startPhoneSync() }
                    case .failed:
                        action((p.chatsDone ?? 0) > 0 ? "Tiếp tục" : "Thử lại") { session.startPhoneSync() }
                    case .preparing, .waiting, .paused, .chats, .messages:
                        action("Huỷ", tint: ZL.secondary) { session.cancelPhoneSync() }
                    case .done:
                        EmptyView()
                    }
                }
                .padding(.top, 2)
            }
            Spacer(minLength: 0)
            if !p.running {
                Button(action: onClose) {
                    Image(systemName: "xmark").font(.system(size: 11, weight: .semibold)).foregroundStyle(ZL.secondary)
                        .frame(width: 20, height: 20).contentShape(Rectangle())
                }
                .buttonStyle(.plain).help("Ẩn")
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color(hex: 0xF0F6FF)))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(ZL.outBorder, lineWidth: 1))
        .padding(.horizontal, 12).padding(.vertical, 8)
    }

    @ViewBuilder private func icon(_ p: PhoneSyncProgress) -> some View {
        Group {
            switch p.state {
            case .preparing, .waiting, .chats, .messages:
                ProgressView().controlSize(.small)
            case .paused:
                Image(systemName: "pause.circle.fill").foregroundStyle(.orange)
            case .done:
                Image(systemName: "checkmark.circle.fill").foregroundStyle(Color(hex: 0x2DB971))
            case .failed:
                Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.orange)
            case .idle, .cancelled:
                Image(systemName: "iphone.and.arrow.forward").foregroundStyle(ZL.blue)
            }
        }
        .font(.system(size: 18)).frame(width: 28, height: 28)
    }

    private func title(_ p: PhoneSyncProgress) -> String {
        switch p.state {
        case .idle, .cancelled: return "Đồng bộ tin nhắn từ điện thoại"
        case .preparing: return "Đang chuẩn bị đồng bộ…"
        case .waiting: return "Xác nhận trên điện thoại"
        case .paused: return "Đồng bộ đang tạm dừng"
        case .chats: return "Đang tải danh sách trò chuyện…"
        case .messages: return "Đang đồng bộ tin nhắn"
        case .done: return "Đã đồng bộ xong"
        case .failed: return "Chưa đồng bộ được"
        }
    }

    private func detail(_ p: PhoneSyncProgress) -> String? {
        let n = NumberFormatter.localizedString(from: NSNumber(value: p.messages ?? 0), number: .decimal)
        switch p.state {
        case .idle, .cancelled:
            return "Tải lịch sử trò chuyện từ Zalo trên điện thoại về máy này. Điện thoại cần mở Zalo và có mạng."
        case .preparing:
            return nil
        case .waiting:
            return "Mở Zalo trên điện thoại và chọn Đồng ý để cho phép ChatSpace nhận tin nhắn."
        case .paused:
            return "Zalo trên điện thoại đang chạy nền nên ngừng gửi tin. Mở Zalo trên điện thoại (giữ màn hình sáng) để tiếp tục. \(p.chatsDone ?? 0)/\(p.chats ?? 0) cuộc trò chuyện · \(n) tin nhắn."
        case .chats:
            return "Giữ Zalo trên điện thoại mở cho đến khi xong."
        case .messages:
            return "\(p.chatsDone ?? 0)/\(p.chats ?? 0) cuộc trò chuyện · \(n) tin nhắn. Giữ Zalo trên điện thoại mở cho đến khi xong."
        case .done:
            var s = "Đã thêm \(n) tin nhắn từ \(p.chats ?? 0) cuộc trò chuyện."
            if let k = p.skipped, k > 0 { s += " \(k) cuộc trò chuyện không xác định được người nhận nên được bỏ qua." }
            return s
        case .failed:
            let resume = (p.chatsDone ?? 0) > 0 ? " Đã lưu \(p.chatsDone ?? 0)/\(p.chats ?? 0) cuộc trò chuyện; bấm Tiếp tục để đồng bộ phần còn lại." : ""
            return (failure(p) ?? "Đã có lỗi xảy ra.") + resume
        }
    }

    private func failure(_ p: PhoneSyncProgress) -> String? {
        switch p.code {
        case "rejected": return "Yêu cầu đã bị từ chối trên điện thoại."
        case "busy": return p.error.map { "Điện thoại đang bận (\($0))." } ?? "Điện thoại đang bận, thử lại sau."
        case "timeout": return "Điện thoại không phản hồi. Mở Zalo trên điện thoại rồi thử lại."
        case "disconnected": return "Mất kết nối Zalo. Thử lại khi đã kết nối."
        case "unavailable": return "Cần cài Zalo PC trên máy này: ChatSpace dùng thư viện giải mã của Zalo PC."
        default: return p.error
        }
    }

    private func action(_ title: String, tint: Color = ZL.blue, _ run: @escaping () -> Void) -> some View {
        Button(title, action: run).buttonStyle(.plain).font(.zalo(13, .semibold)).foregroundStyle(tint)
    }
}

struct ZaloChatRow: View {
    let session: PlatformSession
    let chat: Chat
    let selected: Bool
    @State private var hovered = false

    var body: some View {
        let unread = chat.hasUnread
        HStack(spacing: 12) {
            ZaloAvatar(id: chat.id, title: chat.title, source: chat.avatar, size: 48, group: chat.isGroup)
            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 4) {
                    if chat.isGroup { ZaloGlyph(code: ZaloGlyphs.group, fallback: "person.2.fill", size: 15).foregroundStyle(ZL.secondary) }
                    Text(chat.title).font(.zalo(15, unread ? .semibold : .regular)).foregroundStyle(ZL.text).lineLimit(1)
                    Spacer(minLength: 4)
                    Text(ZL.listTime(chat.date)).font(.zalo(12)).foregroundStyle(ZL.secondary)
                }
                HStack(spacing: 4) {
                    preview.font(.zalo(13.5, unread ? .semibold : .regular))
                        .foregroundStyle(unread ? ZL.text : ZL.secondary).lineLimit(1)
                    Spacer(minLength: 4)
                    if chat.isMuted { ZaloGlyph(code: ZaloGlyphs.bellSlash, fallback: "bell.slash", size: 14).foregroundStyle(ZL.secondary) }
                    if chat.isPinned { ZaloGlyph(code: ZaloGlyphs.pinFilled, fallback: "pin.fill", size: 13).foregroundStyle(ZL.secondary) }
                    if chat.unread > 0 {
                        Text(chat.unread > 5 ? "5+" : "\(chat.unread)").font(.zalo(11, .bold)).foregroundStyle(.white)
                            .padding(.horizontal, 4).frame(minWidth: 18, minHeight: 18)
                            .background(Capsule().fill(chat.isMuted ? ZL.secondary : ZL.badge))
                    } else if chat.markedUnread == true {
                        Circle().fill(chat.isMuted ? ZL.secondary : ZL.badge).frame(width: 10, height: 10)
                    }
                }
            }
        }
        .padding(.horizontal, 16).frame(height: 74)
        .background(selected ? ZL.selectedRow : (hovered ? ZL.hoverRow : .white))
        .contentShape(Rectangle())
        .onHover { hovered = $0 }
        .onAppear { session.requestAvatar(for: chat) }
    }

    private var preview: Text {
        if let t = session.typing[chat.id], !t.isEmpty {
            return Text(chat.isGroup ? "\(t.first?.name ?? "") đang soạn tin…" : "Đang soạn tin…")
        }
        guard let m = chat.lastMessage else { return Text("") }
        if m.isDeleted { return Text(m.fromMe ? "Bạn: Tin nhắn đã được thu hồi" : "Tin nhắn đã được thu hồi") }
        let who = m.fromMe ? "Bạn: " : (chat.kind == .group ? m.senderName.map { "\($0.components(separatedBy: " ").last ?? $0): " } ?? "" : "")
        let body: String
        switch m.kind {
        case .image: body = "[Hình ảnh]"
        case .video: body = "[Video]"
        case .sticker: body = "[Sticker]"
        case .voice, .audio: body = "[Tin nhắn thoại]"
        case .file: body = "[File] " + (m.media?.fileName ?? "")
        case .link: body = "[Link] " + (m.text ?? "")
        case .system: return Text(m.text ?? "")
        default: body = m.text ?? ""
        }
        return Text(who + body)
    }
}

// MARK: Conversation

struct ZaloConversation: View {
    let session: PlatformSession
    let chat: Chat
    @State private var showSearch = false
    @State private var showInfo = false
    @Environment(\.compactChat) private var compact

    var body: some View {
        VStack(spacing: 0) {
            header
            PinnedBar(session: session, chat: chat)
            ZStack {
                ZL.chatBackground
                ChatScroll(session: session, chatId: chat.id, spacing: 0,
                           padding: EdgeInsets(top: 12, leading: 0, bottom: 12, trailing: 0)) { rows in
                    let lastOut = rows.last(where: { $0.message.fromMe })?.id
                    ForEach(rows) { row in
                        if row.showTimeGap {
                            Text(ZL.separator(row.message.date)).font(.zalo(12)).foregroundStyle(.white)
                                .padding(.horizontal, 10).padding(.vertical, 3)
                                .background(Capsule().fill(Color(hex: 0x8A99AD).opacity(0.75)))
                                .frame(maxWidth: .infinity).padding(.vertical, 12)
                        }
                        if let n = row.unreadDivider { UnreadDivider(platform: .zalo, count: n) }
                        ZaloMessageRow(session: session, chat: chat, row: row, showStatus: row.id == lastOut)
                            .revealHighlight(session, chatId: chat.id, row: row)
                            .padding(.top, row.isFirstInGroup && !row.showTimeGap ? 10 : 3)
                    }
                }
            }
            ZaloComposer(session: session, chat: chat)
                    .mentionSuggestions(session: session, chat: chat, accent: ZL.blue)
        }
        .fileDropTarget(session: session, chatId: chat.id, accent: ZL.blue)
    }

    private var header: some View {
        HStack(spacing: compact ? 10 : 12) {
            ZaloAvatar(id: chat.id, title: chat.title, source: chat.avatar, size: compact ? 38 : 48, group: chat.isGroup)
            VStack(alignment: .leading, spacing: 3) {
                Text(chat.title).font(.zalo(compact ? 15 : 17, .semibold)).foregroundStyle(ZL.text).lineLimit(1)
                subtitle.lineLimit(1)
            }
            Spacer(minLength: 8)
            HStack(spacing: compact ? 12 : 18) {
                ZaloGlyph(code: ZaloGlyphs.addGroupMember, fallback: "person.2.badge.plus", size: 22)
                    .tool(chat.isGroup ? "Thêm thành viên" : "Tạo nhóm trò chuyện") { ChatTools.nativeOnly(.zalo, chat.isGroup ? "Thêm thành viên" : "Tạo nhóm") }
                ZaloGlyph(code: ZaloGlyphs.search, fallback: "magnifyingglass", size: 20)
                    .toolPopover("Tìm tin nhắn", isPresented: $showSearch) { InChatSearch(session: session, chatId: chat.id) }
                ZaloGlyph(code: ZaloGlyphs.video, fallback: "video", size: 22)
                    .tool("Cuộc gọi video") { ChatTools.call(.zalo, video: true) }
                ZaloGlyph(code: ZaloGlyphs.sidebar, fallback: "sidebar.right", size: 20)
                    .toolPopover("Thông tin hội thoại", isPresented: $showInfo) { ChatInfoPanel(session: session, chat: chat) }
            }
            .font(.zalo(16)).foregroundStyle(ZL.text)
        }
        .padding(.horizontal, compact ? 12 : 16).frame(height: compact ? 56 : 68).padding(.top, compact ? 0 : 4)
        .background(Color.white)
        .overlay(alignment: .bottom) { Rectangle().fill(ZL.divider).frame(height: 1) }
    }

    @ViewBuilder
    private var subtitle: some View {
        if let t = session.typing[chat.id], !t.isEmpty {
            Text(chat.isGroup ? "\(t.first?.name ?? "") đang soạn tin…" : "Đang soạn tin…").font(.zalo(13)).foregroundStyle(ZL.blue)
        } else if chat.isGroup {
            Label(chat.memberCount.map { "\($0) thành viên" } ?? "Nhóm", systemImage: "person")
                .font(.zalo(13)).foregroundStyle(ZL.secondary)
        } else if chat.online == true {
            HStack(spacing: 4) {
                Circle().fill(Color(hex: 0x2DB971)).frame(width: 8, height: 8)
                Text("Vừa truy cập").font(.zalo(13)).foregroundStyle(ZL.secondary)
            }
        } else if let ls = chat.lastSeen {
            Text("Truy cập \(ZL.listTime(Date(timeIntervalSince1970: ls / 1000))) trước").font(.zalo(13)).foregroundStyle(ZL.secondary)
        } else {
            Text("Bấm để xem thông tin").font(.zalo(13)).foregroundStyle(ZL.secondary)
        }
    }
}

struct ZaloMessageRow: View {
    let session: PlatformSession
    let chat: Chat
    let row: MessageRow
    let showStatus: Bool
    @State private var hovered = false
    @State private var picking = false
    @Environment(\.compactChat) private var compact

    private var m: Message { row.message }

    var body: some View {
        let active = hovered || picking
        if m.kind == .system {
            Text(m.text ?? "").font(.zalo(12.5)).foregroundStyle(ZL.secondary).multilineTextAlignment(.center)
                .padding(.horizontal, 10).padding(.vertical, 4)
                .background(Capsule().fill(.white.opacity(0.85)))
                .frame(maxWidth: .infinity).padding(.vertical, 6)
        } else {
            VStack(alignment: m.fromMe ? .trailing : .leading, spacing: 4) {
                HStack(alignment: .top, spacing: compact ? 8 : 10) {
                    if m.fromMe { Spacer(minLength: ChatMetrics.gap(120, compact)) }
                    if !m.fromMe {
                        if row.isFirstInGroup {
                            ZaloAvatar(id: m.senderId, title: m.senderName ?? chat.title, source: m.senderAvatar ?? (chat.isGroup ? nil : chat.avatar), size: compact ? 32 : 40)
                                .memberProfile(session: session, chat: chat, message: m)
                        } else {
                            Color.clear.frame(width: compact ? 32 : 40, height: 1)
                        }
                    }
                    ZaloBubble(session: session, chat: chat, row: row, hovered: active, picking: $picking,
                               reserveBadge: showStatus && m.fromMe && m.status != nil)
                        .messageContextMenu(session: session, message: m, labels: .vietnamese, reactions: ReactionSets.zalo)
                        .besideBubble(fromMe: m.fromMe, spacing: 8, vertical: .top) {
                            if active { hoverActions }
                        }
                    if !m.fromMe { Spacer(minLength: ChatMetrics.gap(120, compact)) }
                }
                if showStatus, m.fromMe, let s = m.status {
                    Text(statusText(s)).font(.zalo(11)).foregroundStyle(.white)
                        .padding(.horizontal, 8).padding(.vertical, 2)
                        .background(Capsule().fill(s == .failed ? Color.red : Color(hex: 0x8A99AD).opacity(0.8)))
                }
            }
            .padding(.horizontal, compact ? 10 : 16)
            .onHover { hovered = $0 }
        }
    }

    private func statusText(_ s: DeliveryStatus) -> String {
        switch s {
        case .pending: "Đang gửi"
        case .sent: "Đã gửi"
        case .delivered: "Đã nhận"
        case .read: "Đã xem"
        case .failed: "Gửi lỗi"
        }
    }

    private var hoverActions: some View {
        Button { session.startReply(m) } label: {
            ZaloGlyph(code: ZaloGlyphs.quoteLine, fallback: "quote.opening", size: 15)
                .frame(width: 26, height: 26).background(Circle().fill(.white)).contentShape(Circle())
        }
        .buttonStyle(.plain).foregroundStyle(ZL.secondary).help("Trả lời")
        .padding(.top, 8)
    }
}

struct ZaloBubble: View {
    let session: PlatformSession
    let chat: Chat
    let row: MessageRow
    let hovered: Bool
    @Binding var picking: Bool
    /// Keep room under the bubble for the hanging reaction button so it never covers the status pill.
    var reserveBadge = false
    @Environment(\.compactChat) private var compact

    private var m: Message { row.message }

    var body: some View {
        let mediaOnly = row.album != nil ? row.albumCaption == nil && row.albumOthers.isEmpty
            : (m.kind == .image || m.kind == .video || m.kind == .gif) && (m.text ?? "").isEmpty
        let isSticker = row.album == nil && (m.kind == .sticker || (m.kind == .text && isJumboEmoji(m.text)))

        Group {
            if row.album != nil && mediaOnly && !m.isDeleted {
                AlbumGrid(session: session, items: row.albumMedia, width: 340, spacing: 3, cornerRadius: 8)
            } else if isSticker && !m.isDeleted {
                if m.kind == .sticker { MediaContent(session: session, message: m, maxWidth: 130) }
                else { Text(m.text ?? "").font(.zalo(44)) }
            } else if mediaOnly && !m.isDeleted {
                MediaContent(session: session, message: m, maxWidth: 340, cornerRadius: 8)
            } else {
                BubbleColumn(spacing: 4) {
                    if !m.fromMe && chat.isGroup && row.isFirstInGroup {
                        Text(m.senderName ?? "").font(.zalo(12.5)).foregroundStyle(ZL.secondary)
                            .memberProfile(session: session, chat: chat, message: m)
                    }
                    if let r = m.replyTo, !m.isDeleted { reply(r).bubbleFill() }
                    content
                    if Translator.shared.isShown(m) {
                        TranslationQuote(message: m, bar: ZL.blue, color: ZL.text.opacity(0.75))
                    }
                    if row.isLastInGroup || m.kind != .text {
                        Text(Fmt.time(m.date)).font(.zalo(11.5)).foregroundStyle(ZL.secondary)
                    }
                }
                .padding(12)
                .background(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .fill(m.fromMe ? ZL.outBubble : ZL.inBubble)
                        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(m.fromMe ? ZL.outBorder : Color(hex: 0xDDE2E8), lineWidth: 0.6)))
            }
        }
        .overlay(alignment: .bottomTrailing) {
            HStack(spacing: 4) {
                if hovered && Translator.canTranslate(m) {
                    TranslateToggle(message: m) { on in
                        Image(systemName: "translate").font(.system(size: 11, weight: .medium)).foregroundStyle(on ? ZL.blue : ZL.secondary)
                            .frame(width: 22, height: 22).background(Circle().fill(.white).shadow(color: .black.opacity(0.15), radius: 1))
                            .contentShape(Circle())
                    }
                }
                reactionBadge
            }
            .alignmentGuide(.bottom) { $0[VerticalAlignment.center] }
            .alignmentGuide(.trailing) { $0[.trailing] + 8 }
        }
        .padding(.bottom, m.hasReactions || reserveBadge ? 11 : 0)
        .modifier(CapWidthModifier(max: 520))
    }

    @ViewBuilder
    private var content: some View {
        if m.isDeleted {
            Text("Tin nhắn đã được thu hồi").font(.zalo(15)).foregroundStyle(ZL.secondary)
        } else if row.album != nil {
            if !row.albumMedia.isEmpty { AlbumGrid(session: session, items: row.albumMedia, width: 320, spacing: 3, cornerRadius: 6) }
            ForEach(row.albumOthers) { f in
                FileContent(session: session, message: f, iconColor: ZL.blue, titleColor: ZL.text, subtitleColor: ZL.secondary)
            }
            if let t = row.albumCaption { textBody(t) }
        } else {
            switch m.kind {
            case .image, .video, .gif:
                MediaContent(session: session, message: m, maxWidth: 320, cornerRadius: 6)
                if let t = m.text, !t.isEmpty { textBody(t) }
            case .file, .audio, .voice:
                FileContent(session: session, message: m, iconColor: ZL.blue, titleColor: ZL.text, subtitleColor: ZL.secondary)
            case .call:
                CallContent(message: m, titleColor: ZL.text, subtitleColor: ZL.secondary, font: { .zalo($0, $1) })
            case .link:
                VStack(alignment: .leading, spacing: 6) {
                    if let thumb = m.media?.thumbnail ?? m.media?.url {
                        RemoteImage(source: thumb, maxPixel: 640) { Color.clear }.frame(maxWidth: compact ? ChatMetrics.compactMediaWidth : 320, maxHeight: 170).clipped()
                    }
                    textBody(m.text ?? "")
                }
            default:
                textBody(m.text ?? "")
            }
        }
    }

    private func textBody(_ t: String) -> some View {
        BodyText(text: t, links: m.links, font: .zalo(15), nsFont: .zalo(15), color: ZL.text, linkColor: ZL.blue)
    }

    private func reply(_ r: ReplyRef) -> some View {
        HStack(spacing: 8) {
            Rectangle().fill(ZL.blue).frame(width: 2)
            VStack(alignment: .leading, spacing: 2) {
                Text(r.senderName ?? "Tin nhắn").font(.zalo(13, .semibold)).foregroundStyle(ZL.text).lineLimit(1)
                Text(replyPreview(r)).font(.zalo(13)).foregroundStyle(ZL.secondary).lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.vertical, 6).padding(.trailing, 10)
        }
        .frame(minWidth: 120, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 4).fill(m.fromMe ? Color(hex: 0xC7E0FF).opacity(0.6) : Color(hex: 0xEEF0F1)))
        .clipShape(RoundedRectangle(cornerRadius: 4))
        .onTapGesture {
            session.scrollTarget[m.chatId] = nil
            DispatchQueue.main.async { session.scrollTarget[m.chatId] = r.id }
        }
    }

    private func replyPreview(_ r: ReplyRef) -> String {
        if let t = r.text, !t.isEmpty { return t }
        switch r.kind {
        case .image?, .gif?: return "[Hình ảnh]"
        case .video?: return "[Video]"
        case .sticker?: return "[Sticker]"
        case .file?: return "[File]"
        case .voice?, .audio?: return "[Tin nhắn thoại]"
        case .link?: return "[Link]"
        default: return "[Tin nhắn]"
        }
    }

    @ViewBuilder
    private var reactionBadge: some View {
        let canReact = !m.isDeleted && session.can("reactions")
        if let rs = m.reactions, !rs.isEmpty {
            Button { if canReact { picking = true } } label: {
                HStack(spacing: 2) {
                    ForEach(rs.prefix(3), id: \.emoji) { Text($0.emoji).font(.zalo(12)) }
                    let total = rs.reduce(0) { $0 + $1.count }
                    Text("\(total)").font(.zalo(11)).foregroundStyle(ZL.secondary).padding(.leading, 1)
                }
                .padding(.horizontal, 6).frame(height: 20)
                .background(Capsule().fill(.white).shadow(color: .black.opacity(0.15), radius: 1, y: 0.5))
                .contentShape(Capsule())
            }
            .buttonStyle(.plain).help("Bày tỏ cảm xúc")
            .reactionPicker($picking, session: session, message: m, emojis: ReactionSets.zalo)
        } else if (hovered || picking) && canReact {
            Button { picking = true } label: {
                ZaloGlyph(code: ZaloGlyphs.like, fallback: "hand.thumbsup", size: 13).foregroundStyle(ZL.secondary)
                    .frame(width: 22, height: 22).background(Circle().fill(.white).shadow(color: .black.opacity(0.15), radius: 1))
                    .contentShape(Circle())
            }
            .buttonStyle(.plain).help("Bày tỏ cảm xúc")
            .reactionPicker($picking, session: session, message: m, emojis: ReactionSets.zalo)
        }
    }
}

struct ZaloComposer: View {
    let session: PlatformSession
    let chat: Chat
    @State private var showMore = false
    @Environment(\.compactChat) private var compact

    private func moreItem(_ title: String, _ icon: String, _ action: @escaping () -> Void) -> some View {
        Button { showMore = false; action() } label: {
            Label(title, systemImage: icon).font(.zalo(14)).frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10).frame(height: 32).contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    var body: some View {
        let draft = Binding(get: { session.drafts[chat.id] ?? "" }, set: { session.drafts[chat.id] = $0 })
        VStack(spacing: 0) {
            let icon: CGFloat = compact ? 19 : 22
            HStack(spacing: compact ? 6 : 8) {
                ZaloGlyph(code: ZaloGlyphs.sticker, fallback: "face.smiling", size: icon)
                    .tool("Gửi Sticker") { Composer.emoji() }
                Button { if let f = pickFile() { session.send(draft.wrappedValue, in: chat.id, filePath: f) } } label: { ZaloGlyph(code: ZaloGlyphs.photo, fallback: "photo", size: icon) }
                    .buttonStyle(.plain)
                Button { if let f = pickFile() { session.send(draft.wrappedValue, in: chat.id, filePath: f) } } label: { ZaloGlyph(code: ZaloGlyphs.attach, fallback: "paperclip", size: icon) }
                    .buttonStyle(.plain)
                if !compact {
                    ZaloGlyph(code: ZaloGlyphs.screenshot, fallback: "camera.viewfinder", size: icon)
                        .tool("Chụp màn hình và gửi") { ChatTools.screenshot(session: session, chatId: chat.id) }
                    ZaloGlyph(code: ZaloGlyphs.namecard, fallback: "person.crop.rectangle", size: icon)
                        .tool("Gửi danh thiếp") { ChatTools.nativeOnly(.zalo, "Gửi danh thiếp") }
                    ZaloGlyph(code: ZaloGlyphs.textFormat, fallback: "textformat", size: icon)
                        .tool("Định dạng tin nhắn") { ChatTools.nativeOnly(.zalo, "Định dạng tin nhắn") }
                }
                OutgoingLanguageButton(session: session, chatId: chat.id, tint: ZL.blue, idle: ZL.text, iconSize: icon - 4)
                ZaloGlyph(code: ZaloGlyphs.moreH, fallback: "ellipsis", size: icon)
                    .toolPopover("Thêm", isPresented: $showMore) {
                        VStack(alignment: .leading, spacing: 2) {
                            moreItem("Ghi âm tin nhắn thoại", "mic") { VoiceRecorder.shared.toggle(ChatRef(platform: .zalo, chatId: chat.id)) }
                            moreItem("Gửi tệp", "paperclip") { ChatTools.attach(session: session, chatId: chat.id) }
                            moreItem("Chụp màn hình", "camera.viewfinder") { ChatTools.screenshot(session: session, chatId: chat.id) }
                        }
                        .padding(6).frame(width: 220)
                    }
                if VoiceRecorder.shared.isRecording(ChatRef(platform: .zalo, chatId: chat.id)) {
                    Color.clear.frame(width: 1).voiceTool(session: session, chatId: chat.id)
                }
                Spacer()
            }
            .font(.zalo(compact ? 15 : 17)).foregroundStyle(ZL.text)
            .padding(.horizontal, compact ? 10 : 16).frame(height: compact ? 36 : 44)
            .overlay(alignment: .top) { Rectangle().fill(ZL.divider).frame(height: 1) }
            .overlay(alignment: .bottom) { Rectangle().fill(ZL.divider).frame(height: 1) }

            if let r = session.replyingTo[chat.id] {
                HStack(spacing: 8) {
                    Rectangle().fill(ZL.blue).frame(width: 2)
                    VStack(alignment: .leading, spacing: 2) {
                        Label { Text("Trả lời **\(r.fromMe ? "bạn" : (r.senderName ?? chat.title))**") } icon: {
                            ZaloGlyph(code: ZaloGlyphs.quoteLine, fallback: "quote.opening", size: 14)
                        }
                            .font(.zalo(13)).foregroundStyle(ZL.text)
                        Text(r.preview()).font(.zalo(13)).foregroundStyle(ZL.secondary).lineLimit(1)
                    }
                    Spacer()
                    Button { session.replyingTo[chat.id] = nil } label: { ZaloGlyph(code: ZaloGlyphs.close, fallback: "xmark", size: 16).foregroundStyle(ZL.secondary) }.buttonStyle(.plain)
                }
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color(hex: 0xF3F5F6)))
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, compact ? 10 : 16).padding(.top, 8)
            }

            TranslationNotice(session: session, chatId: chat.id)
            MathSendPreview(session: session, chatId: chat.id, accent: ZL.blue)
            ComposerAttachments(session: session, chatId: chat.id, accent: ZL.blue)
            HStack(alignment: .bottom, spacing: compact ? 10 : 12) {
                ComposerTextView(text: draft, placeholder: "Nhập @, tin nhắn tới \(chat.title)", font: .zalo(15),
                                 textColor: NSColor(ZL.text), placeholderColor: NSColor(ZL.secondary),
                                 maxHeight: compact ? 220 : 140,
                                 onSubmit: { session.send(draft.wrappedValue, in: chat.id) },
                                 onTypingChange: { session.setTyping(chat.id, $0) },
                                 mentionSession: session, mentionChatId: chat.isGroup ? chat.id : nil,
                                 onFiles: { ChatTools.sendPasted($0, session: session, chatId: chat.id) })
                    .padding(.vertical, compact ? 8 : 10)
                    .composerTapArea()
                ZaloGlyph(code: ZaloGlyphs.emojiLine, fallback: "face.smiling", size: icon).foregroundStyle(ZL.text)
                    .tool("Biểu tượng cảm xúc") { Composer.emoji() }.padding(.bottom, 5)
                if draft.wrappedValue.isEmpty && session.attachments[chat.id] == nil {
                    Button { session.send("👍", in: chat.id) } label: { Text("👍").font(.zalo(22)) }
                        .buttonStyle(.plain).padding(.bottom, 9)
                } else {
                    Button { session.send(draft.wrappedValue, in: chat.id) } label: {
                        Text("GỬI").font(.zalo(14, .bold)).foregroundStyle(ZL.blue)
                    }
                    .buttonStyle(.plain).padding(.bottom, 13)
                }
            }
            .padding(.horizontal, compact ? 10 : 16)
        }
        .background(Color.white)
    }
}

// MARK: Login

struct ZaloLogin: View {
    let session: PlatformSession

    var body: some View {
        VStack(spacing: 10) {
            Spacer()
            if let logo = NativeAssets.shared.zaloImage("logo-new.") {
                Image(nsImage: logo).resizable().scaledToFit().frame(width: 114).padding(.bottom, 6)
            } else {
                Text("Zalo").font(.zalo(56, .heavy)).foregroundStyle(ZL.blue)
            }
            Text("Đăng nhập tài khoản Zalo\nđể kết nối với ứng dụng Zalo PC").multilineTextAlignment(.center)
                .font(.zalo(17)).foregroundStyle(ZL.text.opacity(0.85)).padding(.bottom, 16)

            VStack(spacing: 0) {
                Text("Đăng nhập qua mã QR").font(.zalo(15, .semibold)).foregroundStyle(ZL.text)
                    .frame(maxWidth: .infinity).frame(height: 52)
                    .overlay(alignment: .bottom) { Rectangle().fill(ZL.divider).frame(height: 1) }
                VStack(spacing: 18) {
                    ZStack {
                        RoundedRectangle(cornerRadius: 16).stroke(ZL.divider, lineWidth: 1).frame(width: 240, height: 240)
                        if let img = session.qrImage {
                            Image(nsImage: img).interpolation(.none).resizable().frame(width: 210, height: 210)
                                .overlay(QRExpiryOverlay(session: session, tint: ZL.blue, label: "Mã QR hết hạn\nLấy mã mới"))
                        } else {
                            ProgressView()
                        }
                    }
                    Text("Chỉ dùng để đăng nhập").font(.zalo(14)).foregroundStyle(ZL.blue)
                    Text("Zalo trên máy tính").font(.zalo(14)).foregroundStyle(ZL.text.opacity(0.7)).padding(.top, -14)
                    Text("Mở ứng dụng Zalo trên điện thoại, chọn biểu tượng quét mã QR để đăng nhập.")
                        .font(.zalo(13)).foregroundStyle(ZL.secondary).multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                    if let e = session.authError {
                        Text(e).font(.zalo(13)).foregroundStyle(.red).multilineTextAlignment(.center)
                        Button("Lấy mã mới") { session.refreshQR() }.buttonStyle(.plain).foregroundStyle(ZL.blue)
                    }
                }
                .padding(28)
            }
            .frame(width: 400)
            .background(RoundedRectangle(cornerRadius: 8).fill(.white).shadow(color: Color(hex: 0x1B60C5).opacity(0.12), radius: 10, y: 4))
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(hex: 0xE8F3FF))
        .onAppear { session.ensureQR() }
        .onDisappear { session.stopWantingQR() }
    }
}
