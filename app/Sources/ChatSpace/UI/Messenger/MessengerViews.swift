import SwiftUI

/// Messenger desktop look (messenger.com / Messenger for Mac), Vietnamese strings.
enum MS {
    static let blue = Color(hex: 0x0A7CFF)
    static let brand = Color(hex: 0x0866FF)
    static let text = Color(hex: 0x050505)
    static let secondary = Color(hex: 0x65676B)
    static let inBubble = Color(hex: 0xF0F0F0)
    static let field = Color(hex: 0xF0F2F5)
    static let hover = Color(hex: 0xF2F2F2)
    static let selected = Color(hex: 0xEBF5FF)
    static let online = Color(hex: 0x31A24C)
    static let outGradient = LinearGradient(colors: [Color(hex: 0x0A7CFF), Color(hex: 0x0A7CFF)], startPoint: .top, endPoint: .bottom)

    static func relative(_ d: Date) -> String {
        let s = Date().timeIntervalSince(d)
        if s < 60 { return "Vừa xong" }
        if s < 3600 { return "\(Int(s / 60)) phút" }
        if s < 86400 { return "\(Int(s / 3600)) giờ" }
        if s < 7 * 86400 { return "\(Int(s / 86400)) ngày" }
        if s < 52 * 7 * 86400 { return "\(Int(s / (7 * 86400))) tuần" }
        return "\(Int(s / (365 * 86400))) năm"
    }

    static func gapTitle(_ d: Date) -> String {
        let cal = Calendar.current
        let f = DateFormatter()
        f.locale = Locale(identifier: "vi_VN")
        if cal.isDateInToday(d) { return Fmt.time(d) }
        if let days = cal.dateComponents([.day], from: d, to: Date()).day, days < 7 {
            f.dateFormat = "EEE HH:mm"
        } else {
            f.dateFormat = "HH:mm, dd 'Tháng' M, yyyy"
        }
        return f.string(from: d)
    }
}

struct MessengerAvatar: View {
    let source: String?
    var size: CGFloat = 56
    var online = false

    var body: some View {
        AvatarView(source: source, size: size) {
            Circle().fill(Color(hex: 0xE4E6EB))
                .overlay(Image(systemName: "person.fill").font(.system(size: size * 0.55)).foregroundStyle(Color(hex: 0xBCC0C4)).offset(y: size * 0.1))
                .clipShape(Circle())
        }
        .overlay(alignment: .bottomTrailing) {
            if online {
                Circle().fill(MS.online).frame(width: size * 0.26, height: size * 0.26)
                    .overlay(Circle().stroke(.white, lineWidth: size * 0.05))
                    .offset(x: -size * 0.02, y: -size * 0.02)
            }
        }
    }
}

struct MessengerRootView: View {
    let session: PlatformSession

    var body: some View {
        Group {
            if session.authState == .loggedIn {
                HStack(spacing: 0) {
                    MessengerNavBar(session: session)
                    MessengerChatList(session: session).frame(width: 360)
                    Rectangle().fill(Color.black.opacity(0.08)).frame(width: 1)
                    if let chat = session.selectedChat {
                        MessengerConversation(session: session, chat: chat).id(chat.id)
                    } else {
                        VStack(spacing: 10) {
                            Image(systemName: "bubble.left.and.bubble.right").font(.system(size: 54, weight: .thin)).foregroundStyle(MS.secondary)
                            Text("Chưa chọn đoạn chat nào").font(.system(size: 20, weight: .bold)).foregroundStyle(MS.text)
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }
            } else if session.authState == .connecting || (session.bridgeError != nil && session.authState != .error) {
                BridgeStatusView(session: session, tint: MS.blue)
            } else {
                MessengerLogin(session: session)
            }
        }
        .background(Color.white)
        .environment(\.colorScheme, .light)
    }
}

struct MessengerNavBar: View {
    let session: PlatformSession

    var body: some View {
        VStack(spacing: 8) {
            Color.clear.frame(height: 34)
            icon("message.fill", selected: true)
            icon("storefront")
            icon("bubble.left.and.text.bubble.right")
            icon("archivebox")
            Spacer()
            MessengerAvatar(source: session.selfInfo?.avatar, size: 32).padding(.bottom, 16)
        }
        .frame(width: 60)
        .background(Color.white)
        .overlay(alignment: .trailing) { Rectangle().fill(Color.black.opacity(0.06)).frame(width: 1) }
    }

    private func icon(_ name: String, selected: Bool = false) -> some View {
        Image(systemName: name).font(.system(size: 18)).foregroundStyle(selected ? MS.text : MS.secondary)
            .frame(width: 44, height: 44)
            .background(RoundedRectangle(cornerRadius: 8).fill(selected ? Color.black.opacity(0.05) : .clear))
    }
}

// MARK: Chat list

struct MessengerChatList: View {
    let session: PlatformSession
    @State private var query = ""
    @State private var tab = 0

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Đoạn chat").font(.system(size: 24, weight: .bold)).foregroundStyle(MS.text)
                Spacer()
                circleIcon("ellipsis").tool("Đánh dấu tất cả là đã đọc", size: 36) { session.markAllRead() }
                circleIcon("square.and.pencil").tool("Tin nhắn mới", size: 36) { ChatTools.nativeOnly(.messenger, "Tin nhắn mới") }
            }
            .padding(.horizontal, 16).padding(.top, 34).padding(.bottom, 10)

            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").font(.system(size: 14)).foregroundStyle(MS.secondary)
                TextField("Tìm kiếm trên Messenger", text: $query).textFieldStyle(.plain).font(.system(size: 15))
            }
            .padding(.horizontal, 12).frame(height: 36)
            .background(Capsule().fill(MS.field))
            .padding(.horizontal, 16)

            HStack(spacing: 6) {
                ForEach(Array(["Hộp thư", "Cộng đồng"].enumerated()), id: \.offset) { i, t in
                    Text(t).font(.system(size: 15, weight: .semibold))
                        .foregroundStyle(tab == i ? MS.brand : MS.secondary)
                        .padding(.horizontal, 12).padding(.vertical, 7)
                        .background(Capsule().fill(tab == i ? MS.selected : .clear))
                        .onTapGesture { tab = i }
                }
                Spacer()
            }
            .padding(.horizontal, 12).padding(.vertical, 10)

            if session.connection == "offline" {
                Text(session.connectionDetail ?? "Đang kết nối lại…").font(.system(size: 13)).foregroundStyle(MS.secondary).padding(.bottom, 6)
            }
            if session.historyBackup == .locked {
                MessengerHistoryBanner(session: session).padding(.horizontal, 16).padding(.bottom, 8)
            }

            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filtered) { chat in
                        MessengerChatRow(session: session, chat: chat, selected: chat.id == session.selectedChatId)
                            .onTapGesture { session.select(chat.id) }
                            .chatContextMenu(platform: session.platform, chat: chat)
                    }
                }
                .padding(.horizontal, 8)
            }
        }
        .task { await session.checkHistoryBackup() }
    }

    private var filtered: [Chat] {
        session.chats.filter { query.isEmpty || $0.title.localizedCaseInsensitiveContains(query) }
            .filter { tab == 0 ? $0.kind != .channel : $0.kind == .channel }
    }

    private func circleIcon(_ name: String) -> some View {
        Image(systemName: name).font(.system(size: 15, weight: .semibold)).foregroundStyle(MS.text)
            .frame(width: 36, height: 36).background(Circle().fill(MS.field)).padding(.leading, 6)
    }
}

/// Offers the secure storage PIN while encrypted chats have no history on this device.
struct MessengerHistoryBanner: View {
    let session: PlatformSession
    @State private var asking = false

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "lock.fill").font(.system(size: 15)).foregroundStyle(MS.brand)
                .frame(width: 32, height: 32).background(Circle().fill(MS.selected))
            VStack(alignment: .leading, spacing: 4) {
                Text("Khôi phục đoạn chat được mã hóa").font(.system(size: 14, weight: .semibold)).foregroundStyle(MS.text)
                Text("Nhập mã PIN Messenger để xem tin nhắn cũ trong các đoạn chat mã hóa đầu cuối.")
                    .font(.system(size: 12.5)).foregroundStyle(MS.secondary).fixedSize(horizontal: false, vertical: true)
                Button("Nhập mã PIN") { asking = true }
                    .buttonStyle(.plain).font(.system(size: 13, weight: .semibold)).foregroundStyle(MS.brand)
                    .padding(.top, 2)
            }
            Spacer(minLength: 0)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(MS.field))
        .sheet(isPresented: $asking) { MessengerPINSheet(session: session) }
    }
}

struct MessengerPINSheet: View {
    let session: PlatformSession
    @Environment(\.dismiss) private var dismiss
    @State private var pin = ""
    @State private var busy = false
    @State private var error: String?
    @State private var noBackup = false

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "lock.shield.fill").font(.system(size: 34)).foregroundStyle(MS.brand).padding(.top, 6)
            Text("Nhập mã PIN của bạn").font(.system(size: 20, weight: .bold)).foregroundStyle(MS.text)
            Text("Tin nhắn cũ trong đoạn chat mã hóa đầu cuối nằm trong kho lưu trữ an toàn của Messenger. Nhập mã PIN 6 số bạn đã tạo cho kho lưu trữ này để khôi phục chúng trên máy này.")
                .font(.system(size: 13)).foregroundStyle(MS.secondary).multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            MessengerField(placeholder: "Mã PIN", text: $pin, secure: true, large: true) { submit() }
                .onChange(of: pin) { _, v in
                    let digits = String(v.filter(\.isASCIIDigit).prefix(6))
                    if digits != v { pin = digits }
                }
            if let error {
                Text(error).font(.system(size: 13)).foregroundStyle(Color(hex: 0xE41E3F)).multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Text("Messenger chỉ cho nhập sai một số lần. Mã PIN không được lưu trên máy.")
                .font(.system(size: 11.5)).foregroundStyle(MS.secondary).multilineTextAlignment(.center)
            MessengerPrimaryButton(title: "Khôi phục", busy: busy, disabled: pin.count != 6) { submit() }
            Button("Để sau") { dismiss() }
                .buttonStyle(.plain).font(.system(size: 14, weight: .semibold)).foregroundStyle(MS.brand)
                .disabled(busy)
        }
        .padding(24)
        .frame(width: 380)
        .onDisappear { if noBackup { session.historyBackup = .none } }
        .background(Color.white)
        .environment(\.colorScheme, .light)
    }

    private func submit() {
        guard pin.count == 6, !busy else { return }
        busy = true
        error = nil
        Task {
            let result = await session.restoreHistory(pin: pin)
            busy = false
            switch result {
            case .restored:
                dismiss()
            case .wrongPin(let left):
                pin = ""
                switch left {
                case 0?: error = "Mã PIN không đúng và đã hết lượt thử. Hãy đặt lại mã PIN trong ứng dụng Messenger."
                case let n?: error = "Mã PIN không đúng. Còn \(n) lần thử."
                case nil: error = "Mã PIN không đúng."
                }
            case .noBackup:
                noBackup = true
                error = "Tài khoản này chưa bật kho lưu trữ an toàn trong Messenger nên không có tin nhắn cũ để khôi phục."
            case .failed(let message):
                error = "Không khôi phục được: \(message)"
            }
        }
    }
}

private extension Character {
    var isASCIIDigit: Bool { ("0"..."9").contains(self) }
}

struct MessengerChatRow: View {
    let session: PlatformSession
    let chat: Chat
    let selected: Bool
    @State private var hovered = false

    var body: some View {
        let unread = chat.hasUnread
        HStack(spacing: 12) {
            MessengerAvatar(source: chat.avatar, size: 56, online: chat.online == true)
            VStack(alignment: .leading, spacing: 3) {
                Text(chat.title).font(.system(size: 15, weight: unread ? .semibold : .regular)).foregroundStyle(MS.text).lineLimit(1)
                HStack(spacing: 0) {
                    previewText.lineLimit(1)
                    Text(" · " + MS.relative(chat.date)).fixedSize()
                }
                .font(.system(size: 13, weight: unread ? .semibold : .regular))
                .foregroundStyle(unread ? MS.text : MS.secondary)
            }
            Spacer(minLength: 4)
            if chat.isMuted { Image(systemName: "bell.slash.fill").font(.system(size: 12)).foregroundStyle(Color(hex: 0xBCC0C4)) }
            if unread {
                Circle().fill(chat.isMuted ? Color(hex: 0xBCC0C4) : MS.brand).frame(width: 12, height: 12)
            } else if let m = chat.lastMessage, m.fromMe {
                MessengerStatusIcon(status: m.status, avatar: chat.avatar)
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(selected ? MS.selected : (hovered ? MS.hover : .clear)))
        .contentShape(Rectangle())
        .onHover { hovered = $0 }
        .onAppear { session.requestAvatar(for: chat) }
    }

    private var previewText: Text {
        if let t = session.typing[chat.id], !t.isEmpty {
            return Text(chat.isGroup ? "\(t.first?.name ?? "") đang nhập…" : "Đang nhập…")
        }
        guard let m = chat.lastMessage else { return Text(chat.encrypted == true ? "Tin nhắn được mã hóa" : "") }
        if m.isDeleted { return Text(m.fromMe ? "Bạn đã thu hồi một tin nhắn" : "\(m.senderName ?? "Ai đó") đã thu hồi một tin nhắn") }
        let who = m.fromMe ? "Bạn: " : (chat.isGroup ? "\(m.senderName?.components(separatedBy: " ").last ?? ""): " : "")
        let body: String
        switch m.kind {
        case .image: body = m.fromMe ? "Bạn đã gửi một ảnh." : "\(m.senderName ?? "") đã gửi một ảnh."
        case .video: body = m.fromMe ? "Bạn đã gửi một video." : "\(m.senderName ?? "") đã gửi một video."
        case .sticker: body = m.fromMe ? "Bạn đã gửi một nhãn dán." : "\(m.senderName ?? "") đã gửi một nhãn dán."
        case .voice, .audio: body = m.fromMe ? "Bạn đã gửi một tin nhắn thoại." : "\(m.senderName ?? "") đã gửi một tin nhắn thoại."
        case .file: body = m.fromMe ? "Bạn đã gửi một file đính kèm." : "\(m.senderName ?? "") đã gửi một file đính kèm."
        default: return Text(who + (m.text ?? ""))
        }
        return Text(body)
    }
}

struct MessengerStatusIcon: View {
    let status: DeliveryStatus?
    var avatar: String?

    var body: some View {
        switch status {
        case .read: MessengerAvatar(source: avatar, size: 14)
        case .delivered: Image(systemName: "checkmark.circle.fill").font(.system(size: 13)).foregroundStyle(Color(hex: 0xBCC0C4))
        case .pending: Image(systemName: "circle").font(.system(size: 13)).foregroundStyle(Color(hex: 0xBCC0C4))
        case .failed: Image(systemName: "exclamationmark.circle.fill").font(.system(size: 13)).foregroundStyle(.red)
        default: Image(systemName: "checkmark.circle").font(.system(size: 13)).foregroundStyle(Color(hex: 0xBCC0C4))
        }
    }
}

// MARK: Conversation

struct MessengerConversation: View {
    @Environment(\.compactChat) private var compact
    let session: PlatformSession
    let chat: Chat
    @State private var showSearch = false
    @State private var showInfo = false
    @State private var askingPIN = false

    var body: some View {
        VStack(spacing: 0) {
            header
            ChatScroll(session: session, chatId: chat.id, spacing: 0,
                       padding: EdgeInsets(top: 12, leading: 0, bottom: 8, trailing: 0)) { rows in
                if session.reachedStart.contains(chat.id) { intro }
                let lastOut = rows.last(where: { $0.message.fromMe })?.id
                ForEach(rows) { row in
                    if row.showTimeGap {
                        Text(MS.gapTitle(row.message.date)).font(.system(size: 12, weight: .medium)).foregroundStyle(MS.secondary)
                            .frame(maxWidth: .infinity).padding(.vertical, 12)
                    }
                    MessengerMessageRow(session: session, chat: chat, row: row, showStatus: row.id == lastOut)
                        .padding(.top, row.isFirstInGroup && !row.showTimeGap ? 8 : 1)
                    if row.id == chat.encryptedAfter { encryptionDivider }
                }
                if let t = session.typing[chat.id], !t.isEmpty { typingBubble }
            }
            MessengerComposer(session: session, chat: chat)
                    .mentionSuggestions(session: session, chat: chat, accent: MS.blue)
        }
        .background(Color.white)
    }

    private var header: some View {
        HStack(spacing: 10) {
            MessengerAvatar(source: chat.avatar, size: 40, online: chat.online == true)
            VStack(alignment: .leading, spacing: 1) {
                Text(chat.title).font(.system(size: 15, weight: .semibold)).foregroundStyle(MS.text).lineLimit(1)
                Text(status).font(.system(size: 13)).foregroundStyle(MS.secondary).lineLimit(1)
            }
            Spacer()
            HStack(spacing: 8) {
                Image(systemName: "phone.fill").tool("Bắt đầu gọi thoại") { ChatTools.call(.messenger, video: false) }
                Image(systemName: "video.fill").tool("Bắt đầu gọi video") { ChatTools.call(.messenger, video: true) }
                Image(systemName: "magnifyingglass")
                    .toolPopover("Tìm kiếm trong cuộc trò chuyện", isPresented: $showSearch) { InChatSearch(session: session, chatId: chat.id) }
                Image(systemName: "info.circle.fill")
                    .toolPopover("Thông tin về cuộc trò chuyện", isPresented: $showInfo) { ChatInfoPanel(session: session, chat: chat) }
            }
            .font(.system(size: 18)).foregroundStyle(MS.blue)
        }
        .padding(.horizontal, compact ? 12 : 16).frame(height: compact ? 54 : 64).padding(.top, compact ? 0 : 6)
        .background(Color.white.shadow(.drop(color: .black.opacity(0.08), radius: 1, y: 1)))
        .zIndex(1)
    }

    private var status: String {
        if chat.online == true { return "Đang hoạt động" }
        if let ls = chat.lastSeen { return "Hoạt động \(MS.relative(Date(timeIntervalSince1970: ls / 1000))) trước" }
        if chat.isGroup, let n = chat.memberCount { return "\(n) thành viên" }
        return chat.kind == .direct ? "Messenger" : ""
    }

    private var intro: some View {
        VStack(spacing: 6) {
            MessengerAvatar(source: chat.avatar, size: 60)
            Text(chat.title).font(.system(size: 17, weight: .semibold)).foregroundStyle(MS.text)
            Text(chat.isGroup ? "Nhóm trên Messenger" : "Facebook").font(.system(size: 13)).foregroundStyle(MS.secondary)
            if session.historyBackup == .locked {
                VStack(spacing: 6) {
                    Text("Tin nhắn cũ của đoạn chat mã hóa đầu cuối cần mã PIN Messenger để hiển thị.")
                        .font(.system(size: 12.5)).foregroundStyle(MS.secondary).multilineTextAlignment(.center)
                    Button("Nhập mã PIN") { askingPIN = true }
                        .buttonStyle(.plain).font(.system(size: 13, weight: .semibold)).foregroundStyle(MS.brand)
                }
                .frame(maxWidth: 300).padding(.top, 8)
                .sheet(isPresented: $askingPIN) { MessengerPINSheet(session: session) }
            }
            if chat.encrypted == true {
                VStack(spacing: 6) {
                    Label("Đoạn chat được mã hóa đầu cuối", systemImage: "lock.fill")
                        .font(.system(size: 12.5, weight: .medium)).foregroundStyle(MS.secondary)
                    if session.historyBackup != .restored {
                        Text("Tin nhắn mã hóa gửi trước khi ChatSpace đăng nhập không có trên thiết bị này.")
                            .font(.system(size: 12.5)).foregroundStyle(MS.secondary).multilineTextAlignment(.center)
                    }
                    if !chat.isGroup, session.can("open_history"), !session.openHistoryDone.contains(chat.id) {
                        if session.loadingOpenHistory.contains(chat.id) {
                            ProgressView().controlSize(.small)
                        } else {
                            Button("Tải tin nhắn từ trước khi mã hóa") { session.loadOpenHistory(chat.id) }
                                .buttonStyle(.plain).font(.system(size: 13, weight: .semibold)).foregroundStyle(MS.brand)
                        }
                    }
                }
                .frame(maxWidth: 300).padding(.top, 8)
            }
        }
        .frame(maxWidth: .infinity).padding(.vertical, 24)
    }

    private var encryptionDivider: some View {
        VStack(spacing: 4) {
            Label("Đoạn chat đã chuyển sang mã hóa đầu cuối", systemImage: "lock.fill")
                .font(.system(size: 12, weight: .medium))
            if session.historyBackup != .restored {
                Text("Tin nhắn mã hóa gửi trước khi ChatSpace đăng nhập không có trên thiết bị này.")
                    .font(.system(size: 12)).multilineTextAlignment(.center)
            }
        }
        .foregroundStyle(MS.secondary).frame(maxWidth: 340).frame(maxWidth: .infinity).padding(.vertical, 14)
    }

    private var typingBubble: some View {
        HStack(alignment: .bottom, spacing: 8) {
            MessengerAvatar(source: chat.avatar, size: 28)
            HStack(spacing: 4) {
                ForEach(0..<3) { _ in Circle().fill(Color(hex: 0x90949C)).frame(width: 7, height: 7) }
            }
            .padding(.horizontal, 14).padding(.vertical, 12)
            .background(Capsule().fill(MS.inBubble))
            Spacer()
        }
        .padding(.horizontal, 12).padding(.top, 6)
    }
}

struct MessengerMessageRow: View {
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
            Text(m.text ?? "").font(.system(size: 12)).foregroundStyle(MS.secondary)
                .multilineTextAlignment(.center).frame(maxWidth: .infinity).padding(.vertical, 6)
        } else {
            VStack(alignment: m.fromMe ? .trailing : .leading, spacing: 2) {
                if !m.fromMe && chat.isGroup && row.isFirstInGroup {
                    Text(m.senderName ?? "").font(.system(size: 12)).foregroundStyle(MS.secondary).padding(.leading, 48)
                }
                if let r = m.replyTo { replyHeader(r) }
                HStack(alignment: .bottom, spacing: 8) {
                    if m.fromMe { Spacer(minLength: ChatMetrics.gap(100, compact)) }
                    if !m.fromMe {
                        if row.isLastInGroup { MessengerAvatar(source: m.senderAvatar ?? (chat.isGroup ? nil : chat.avatar), size: 28) }
                        else { Color.clear.frame(width: 28, height: 1) }
                    }
                    if m.fromMe && active { actions }
                    MessengerBubble(session: session, row: row, picking: $picking)
                        .messageContextMenu(session: session, message: m, labels: .vietnamese, reactions: ReactionSets.messenger)
                    if !m.fromMe && active { actions }
                    if !m.fromMe { Spacer(minLength: ChatMetrics.gap(100, compact)) }
                    if m.fromMe {
                        Group {
                            if showStatus { MessengerStatusIcon(status: m.status, avatar: chat.avatar) }
                            else { Color.clear }
                        }
                        .frame(width: 14, height: 14)
                    }
                }
            }
            .padding(.horizontal, 12)
            .onHover { hovered = $0 }
        }
    }

    private var actions: some View {
        HStack(spacing: 6) {
            if session.can("reactions") && !m.isDeleted {
                Button { picking = true } label: { Image(systemName: "face.smiling").frame(width: 24, height: 24).contentShape(Rectangle()) }
                    .help("Bày tỏ cảm xúc")
                    .reactionPicker(m.hasReactions ? .constant(false) : $picking, session: session, message: m, emojis: ReactionSets.messenger)
            }
            if Translator.canTranslate(m) {
                TranslateToggle(message: m) { on in
                    Image(systemName: "translate").foregroundStyle(on ? MS.blue : Color(hex: 0x8A8D91))
                        .frame(width: 24, height: 24).contentShape(Rectangle())
                }
            }
            Button { session.startReply(m) } label: { Image(systemName: "arrowshape.turn.up.left.fill").frame(width: 24, height: 24).contentShape(Rectangle()) }
                .help("Trả lời")
        }
        .buttonStyle(.plain).font(.system(size: 14)).foregroundStyle(Color(hex: 0x8A8D91))
        .padding(.bottom, 8)
    }

    private func replyHeader(_ r: ReplyRef) -> some View {
        VStack(alignment: m.fromMe ? .trailing : .leading, spacing: 2) {
            Label(m.fromMe ? "Bạn đã trả lời \(r.senderName ?? chat.title)" : "\(m.senderName ?? chat.title) đã trả lời \(r.senderName ?? "bạn")",
                  systemImage: "arrowshape.turn.up.left.fill")
                .font(.system(size: 12)).foregroundStyle(MS.secondary)
            Text(r.text ?? "File đính kèm").font(.system(size: 14)).foregroundStyle(MS.secondary).lineLimit(2)
                .padding(.horizontal, 12).padding(.top, 8).padding(.bottom, 14)
                .background(RoundedRectangle(cornerRadius: 18).fill(MS.field))
                .padding(.bottom, -10)
                .frame(maxWidth: 300, alignment: m.fromMe ? .trailing : .leading)
        }
        .padding(m.fromMe ? .trailing : .leading, m.fromMe ? 22 : 48)
    }
}

struct MessengerBubble: View {
    let session: PlatformSession
    let row: MessageRow
    @Binding var picking: Bool

    private var m: Message { row.message }

    var body: some View {
        let isSticker = m.kind == .sticker || (m.kind == .text && isJumboEmoji(m.text))
        Group {
            if m.isDeleted {
                Text(m.fromMe ? "Bạn đã thu hồi một tin nhắn" : "Tin nhắn đã bị thu hồi")
                    .font(.system(size: 15)).foregroundStyle(MS.secondary)
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .overlay(shape.stroke(Color(hex: 0xCED0D4), lineWidth: 1))
            } else if isSticker {
                if m.kind == .sticker { MediaContent(session: session, message: m, maxWidth: 120) }
                else { Text(m.text ?? "").font(.system(size: 40)) }
            } else {
                switch m.kind {
                case .image, .video, .gif:
                    VStack(alignment: .leading, spacing: 0) {
                        MediaContent(session: session, message: m, maxWidth: 300, maxHeight: 340)
                        if let t = m.text, !t.isEmpty { textBody(t).padding(.horizontal, 12).padding(.vertical, 8) }
                    }
                    .background(m.fromMe ? AnyShapeStyle(MS.outGradient) : AnyShapeStyle(MS.inBubble))
                    .clipShape(shape)
                case .file, .audio, .voice:
                    FileContent(session: session, message: m, iconColor: m.fromMe ? .white.opacity(0.25) : Color(hex: 0xBCC0C4),
                                titleColor: m.fromMe ? .white : MS.text, subtitleColor: m.fromMe ? .white.opacity(0.8) : MS.secondary)
                        .padding(.horizontal, 12).padding(.vertical, 8)
                        .background(shape.fill(m.fromMe ? AnyShapeStyle(MS.outGradient) : AnyShapeStyle(MS.inBubble)))
                default:
                    textBody(m.text ?? "")
                        .padding(.horizontal, 12).padding(.vertical, 8)
                        .background(shape.fill(m.fromMe ? AnyShapeStyle(MS.outGradient) : AnyShapeStyle(MS.inBubble)))
                }
            }
        }
        .help(MS.gapTitle(m.date) + (m.isEdited ? " · Đã chỉnh sửa" : ""))
        .overlay(alignment: m.fromMe ? .bottomLeading : .bottomTrailing) {
            if let rs = m.reactions, !rs.isEmpty {
                Button { if session.can("reactions") { picking = true } } label: {
                    HStack(spacing: 1) {
                        ForEach(rs.prefix(3), id: \.emoji) { Text($0.emoji).font(.system(size: 12)) }
                        let total = rs.reduce(0) { $0 + $1.count }
                        if total > 1 { Text("\(total)").font(.system(size: 11)).foregroundStyle(MS.secondary).padding(.leading, 2) }
                    }
                    .padding(.horizontal, 4).frame(height: 20)
                    .background(Capsule().fill(.white).shadow(color: .black.opacity(0.2), radius: 1.5, y: 1))
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .reactionPicker($picking, session: session, message: m, emojis: ReactionSets.messenger)
                .offset(x: m.fromMe ? 4 : -4, y: 14)
            }
        }
        .padding(.bottom, m.hasReactions ? 14 : 0)
        .modifier(CapWidthModifier(max: 460))
    }

    private func textBody(_ t: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(linkified(t, linkColor: m.fromMe ? .white : MS.text, underline: true))
                .font(.system(size: 15))
                .foregroundStyle(m.fromMe ? .white : MS.text)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            if Translator.shared.isShown(m) {
                TranslationQuote(message: m, bar: m.fromMe ? .white.opacity(0.7) : MS.secondary,
                                 color: m.fromMe ? .white.opacity(0.9) : MS.text.opacity(0.75))
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if m.isEdited { Text("Đã chỉnh sửa").font(.system(size: 11)).foregroundStyle(MS.secondary).offset(y: 22) }
        }
    }

    /// 18pt pill, with 4pt corners where it touches neighbours from the same sender.
    private var shape: BubbleShape {
        let big: CGFloat = 18, small: CGFloat = 4
        let top = row.isFirstInGroup ? big : small
        let bottom = row.isLastInGroup ? big : small
        return m.fromMe
            ? BubbleShape(topLeft: big, topRight: top, bottomLeft: big, bottomRight: bottom)
            : BubbleShape(topLeft: top, topRight: big, bottomLeft: bottom, bottomRight: big)
    }
}

struct MessengerComposer: View {
    let session: PlatformSession
    let chat: Chat
    @Environment(\.compactChat) private var compact
    @State private var showActions = false

    var body: some View {
        let draft = Binding(get: { session.drafts[chat.id] ?? "" }, set: { session.drafts[chat.id] = $0 })
        // Like messenger.com: once typing starts, the actions fold into a chevron so the field gets the width.
        let collapsed = compact && !draft.wrappedValue.isEmpty && !showActions
        VStack(spacing: 0) {
            if let e = session.editing[chat.id] {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Đang chỉnh sửa tin nhắn").font(.system(size: 13, weight: .semibold)).foregroundStyle(MS.text)
                        if let err = session.editError[chat.id] {
                            Text(err).font(.system(size: 12)).foregroundStyle(.red).lineLimit(2)
                        } else {
                            Text(e.preview()).font(.system(size: 12)).foregroundStyle(MS.secondary).lineLimit(1)
                        }
                    }
                    Spacer()
                    Button { session.cancelEdit(chat.id) } label: { Image(systemName: "xmark").foregroundStyle(MS.secondary) }
                        .buttonStyle(.plain).help("Hủy chỉnh sửa (Esc)")
                }
                .padding(.horizontal, 16).padding(.vertical, 8)
                .overlay(alignment: .top) { Rectangle().fill(Color.black.opacity(0.08)).frame(height: 1) }
            } else if let r = session.replyingTo[chat.id] {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Đang trả lời \(r.fromMe ? "chính mình" : (r.senderName ?? chat.title))").font(.system(size: 13, weight: .semibold)).foregroundStyle(MS.text)
                        Text(r.preview()).font(.system(size: 12)).foregroundStyle(MS.secondary).lineLimit(1)
                    }
                    Spacer()
                    Button { session.replyingTo[chat.id] = nil } label: { Image(systemName: "xmark").foregroundStyle(MS.secondary) }.buttonStyle(.plain)
                }
                .padding(.horizontal, 16).padding(.vertical, 8)
                .overlay(alignment: .top) { Rectangle().fill(Color.black.opacity(0.08)).frame(height: 1) }
            }
            TranslationNotice(session: session, chatId: chat.id)
            MathSendPreview(session: session, chatId: chat.id, accent: MS.blue)
            ComposerAttachments(session: session, chatId: chat.id, accent: MS.blue)
            HStack(alignment: .bottom, spacing: compact ? 8 : 12) {
                Group {
                    if collapsed {
                        Image(systemName: "chevron.right").font(.system(size: 17, weight: .semibold))
                            .tool("Hiện các hành động", size: 28) { showActions = true }
                    } else {
                        Image(systemName: "plus.circle.fill").tool("Mở hành động khác", size: 28) { ChatTools.attach(session: session, chatId: chat.id) }
                        Button { if let f = pickFile() { session.send(draft.wrappedValue, in: chat.id, filePath: f) } } label: {
                            Image(systemName: "photo.on.rectangle.angled")
                        }.buttonStyle(.plain)
                        Image(systemName: "note.text").tool("Chọn nhãn dán", size: 28) { Composer.emoji() }
                        Text("GIF").font(.system(size: 11, weight: .heavy)).padding(.horizontal, 3)
                            .overlay(RoundedRectangle(cornerRadius: 3).stroke(MS.blue, lineWidth: 1.6))
                    }
                }
                .font(.system(size: 20)).foregroundStyle(MS.blue).padding(.bottom, collapsed ? 2 : 8)

                HStack(alignment: .bottom) {
                    ComposerTextView(text: draft, placeholder: "Aa", font: .systemFont(ofSize: 15), textColor: NSColor(MS.text),
                                     placeholderColor: NSColor(MS.secondary), maxHeight: compact ? 220 : 140,
                                     onSubmit: { session.send(draft.wrappedValue, in: chat.id) },
                                     onTypingChange: { session.setTyping(chat.id, $0) },
                                 mentionSession: session, mentionChatId: chat.isGroup ? chat.id : nil,
                                 onFiles: { ChatTools.sendPasted($0, session: session, chatId: chat.id) },
                                 onCommand: { session.composerCommand($0, in: chat.id) })
                        .padding(.vertical, 5)
                        .composerTapArea()
                    OutgoingLanguageButton(session: session, chatId: chat.id, tint: MS.blue, idle: MS.secondary, iconSize: 16, toolSize: 28,
                                           hideWhenIdle: collapsed)
                        .padding(.bottom, 2)
                    if !collapsed {
                        Image(systemName: "face.smiling.inverse").font(.system(size: 19)).foregroundStyle(MS.blue)
                            .tool("Chọn biểu tượng cảm xúc", size: 28) { Composer.emoji() }.padding(.bottom, 2)
                    }
                }
                .padding(.horizontal, 12)
                .background(RoundedRectangle(cornerRadius: 18).fill(MS.field))
                .onChange(of: draft.wrappedValue) { old, new in
                    if new.count > old.count || new.isEmpty { showActions = false }
                }

                let editing = session.editing[chat.id] != nil
                let thumbsUp = !editing && draft.wrappedValue.isEmpty && session.attachments[chat.id] == nil
                Button {
                    session.send(thumbsUp ? "👍" : draft.wrappedValue, in: chat.id)
                } label: {
                    Image(systemName: editing ? "checkmark.circle.fill" : thumbsUp ? "hand.thumbsup.fill" : "paperplane.fill")
                        .font(.system(size: 21)).foregroundStyle(MS.blue)
                }
                .buttonStyle(.plain).padding(.bottom, 7)
            }
            .padding(.horizontal, 12).padding(.vertical, 10)
        }
    }
}

// MARK: Login

/// messenger.com-style onboarding: email/phone + password, then whatever extra steps Meta asks for
/// (2FA code, captcha, choice, "approve on another device") as `auth.step` from the bridge.
/// Session cookies remain available as a fallback.
struct MessengerLogin: View {
    let session: PlatformSession
    @State private var username = ""
    @State private var password = ""
    @State private var useCookies = false

    var body: some View {
        VStack(spacing: 0) {
            Spacer(minLength: 30)
            MessengerLogo(size: 72).padding(.bottom, 26)
            Group {
                if let step = session.authStep, session.authState == .awaitingInput {
                    VStack(spacing: 10) {
                        MessengerAuthStepView(session: session, step: step).id(step.stepId)
                        Divider().padding(.vertical, 8)
                        Button("Đăng nhập bằng cookie phiên") { session.abandonStep(); useCookies = true }
                            .buttonStyle(.plain).font(.system(size: 13)).foregroundStyle(MS.secondary)
                    }
                } else if useCookies || !session.can("password_login") {
                    MessengerCookieLogin(session: session, canUsePassword: session.can("password_login")) { useCookies = false }
                } else {
                    credentials
                }
            }
            .frame(width: 360)
            if let e = session.authError ?? session.bridgeError, session.authStep?.error == nil {
                Text(e).font(.system(size: 13)).foregroundStyle(Color(hex: 0xE41E3F))
                    .multilineTextAlignment(.center).frame(maxWidth: 360).padding(.top, 14)
            }
            Spacer(minLength: 30)
            Text("Thông tin đăng nhập chỉ được gửi tới Meta và lưu cục bộ trên máy này.")
                .font(.system(size: 12)).foregroundStyle(MS.secondary).padding(.bottom, 22)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.white)
        .animation(.easeInOut(duration: 0.2), value: session.authStep)
    }

    private var credentials: some View {
        VStack(spacing: 12) {
            Text("Kết nối với những người bạn yêu quý.")
                .font(.system(size: 32, weight: .bold)).multilineTextAlignment(.center)
                .foregroundStyle(MessengerLogo.gradient)
                .fixedSize(horizontal: false, vertical: true).padding(.bottom, 16)
            MessengerField(placeholder: "Email hoặc số điện thoại", text: $username, contentType: .username)
            MessengerField(placeholder: "Mật khẩu", text: $password, secure: true, contentType: .password) { submit() }
            MessengerPrimaryButton(title: "Tiếp tục", busy: session.authBusy,
                                   disabled: username.trimmingCharacters(in: .whitespaces).isEmpty || password.isEmpty) { submit() }
                .padding(.top, 6)
            Button("Bạn quên mật khẩu?") {
                NSWorkspace.shared.open(URL(string: "https://www.facebook.com/login/identify")!)
            }
            .buttonStyle(.plain).font(.system(size: 14)).foregroundStyle(MS.brand).padding(.top, 8)
            Divider().padding(.vertical, 10)
            Button("Đăng nhập bằng cookie phiên") { useCookies = true }
                .buttonStyle(.plain).font(.system(size: 13)).foregroundStyle(MS.secondary)
        }
    }

    private func submit() {
        guard !username.isEmpty, !password.isEmpty, !session.authBusy else { return }
        session.login(method: "password", username: username.trimmingCharacters(in: .whitespaces), password: password)
    }
}

/// One step of Meta's native login flow, rendered from the bridge's `auth.step`.
struct MessengerAuthStepView: View {
    let session: PlatformSession
    let step: AuthStep
    @State private var values: [String: String] = [:]

    var body: some View {
        VStack(spacing: 14) {
            if let t = step.instructions, !t.isEmpty {
                Text(t).font(.system(size: 15)).foregroundStyle(MS.text).multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            ForEach(Array((step.images ?? []).enumerated()), id: \.offset) { _, src in
                if let img = ImageLoader.decodeDataURL(src) {
                    Image(nsImage: img).resizable().scaledToFit().frame(maxWidth: 300, maxHeight: 120)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                }
            }
            if step.kind == "wait" {
                ProgressView().controlSize(.regular).padding(.vertical, 8)
                Text("Đang chờ bạn xác nhận…").font(.system(size: 13)).foregroundStyle(MS.secondary)
            } else {
                ForEach(step.fields ?? []) { field in fieldView(field) }
                MessengerPrimaryButton(title: "Tiếp tục", busy: session.authBusy, disabled: !complete) { submit() }
                    .padding(.top, 4)
            }
            if let e = step.error, !e.isEmpty {
                Text(e).font(.system(size: 13)).foregroundStyle(Color(hex: 0xE41E3F)).multilineTextAlignment(.center)
            }
            if step.canCancel ?? false {
                Button("Quay lại") { session.cancelStep() }
                    .buttonStyle(.plain).font(.system(size: 14, weight: .semibold)).foregroundStyle(MS.brand).padding(.top, 4)
            } else if step.fields?.contains(where: { $0.type == "password" }) != true {
                Button("Dùng tài khoản khác") { session.abandonStep() }
                    .buttonStyle(.plain).font(.system(size: 14, weight: .semibold)).foregroundStyle(MS.brand).padding(.top, 4)
            }
        }
        .onAppear {
            for f in step.fields ?? [] where values[f.id] == nil {
                values[f.id] = f.default ?? (f.type == "select" ? f.options?.first : nil) ?? ""
            }
        }
    }

    @ViewBuilder
    private func fieldView(_ f: AuthStep.Field) -> some View {
        let binding = Binding(get: { values[f.id] ?? "" }, set: { values[f.id] = $0 })
        switch f.type {
        case "select":
            VStack(alignment: .leading, spacing: 6) {
                Text(f.name).font(.system(size: 13, weight: .semibold)).foregroundStyle(MS.secondary)
                VStack(spacing: 0) {
                    ForEach(f.options ?? [], id: \.self) { opt in
                        Button { values[f.id] = opt } label: {
                            HStack {
                                Text(opt).font(.system(size: 15)).foregroundStyle(MS.text)
                                Spacer()
                                Image(systemName: values[f.id] == opt ? "largecircle.fill.circle" : "circle")
                                    .font(.system(size: 18)).foregroundStyle(values[f.id] == opt ? MS.brand : Color(hex: 0xBCC0C4))
                            }
                            .padding(.horizontal, 14).frame(height: 44).contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
                .background(RoundedRectangle(cornerRadius: 12).fill(MS.field))
            }
        case "code":
            MessengerField(placeholder: f.name, text: binding, contentType: .oneTimeCode, large: true) { submit() }
        case "password":
            MessengerField(placeholder: f.name, text: binding, secure: true, contentType: .password) { submit() }
        default:
            MessengerField(placeholder: f.name, text: binding, contentType: f.type == "username" ? .username : nil) { submit() }
        }
    }

    private var complete: Bool {
        (step.fields ?? []).allSatisfy { !(values[$0.id] ?? "").trimmingCharacters(in: .whitespaces).isEmpty }
    }

    private func submit() {
        guard complete, !session.authBusy else { return }
        session.submitStep(values)
    }
}

struct MessengerCookieLogin: View {
    let session: PlatformSession
    var canUsePassword: Bool
    var back: () -> Void
    @State private var cookies = ""

    var body: some View {
        VStack(spacing: 12) {
            Text("Đăng nhập bằng cookie").font(.system(size: 22, weight: .bold)).foregroundStyle(MS.text)
            Text("Đăng nhập messenger.com trên trình duyệt rồi dán cookie phiên (c_user, xs, datr, fr) — dạng `c_user=…; xs=…` hoặc JSON xuất từ tiện ích cookie.")
                .font(.system(size: 13)).foregroundStyle(MS.secondary).multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            TextEditor(text: $cookies)
                .font(.system(size: 12, design: .monospaced))
                .scrollContentBackground(.hidden)
                .padding(8).frame(height: 110)
                .background(RoundedRectangle(cornerRadius: 12).fill(MS.field))
            MessengerPrimaryButton(title: "Tiếp tục", busy: session.authBusy,
                                   disabled: cookies.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) {
                session.login(method: "cookies", cookies: cookies)
            }
            if canUsePassword {
                Button("Đăng nhập bằng email và mật khẩu", action: back)
                    .buttonStyle(.plain).font(.system(size: 14, weight: .semibold)).foregroundStyle(MS.brand).padding(.top, 4)
            }
        }
    }
}

struct MessengerField: View {
    let placeholder: String
    @Binding var text: String
    var secure = false
    var contentType: NSTextContentType? = nil
    var large = false
    var onSubmit: () -> Void = {}
    @FocusState private var focused: Bool

    var body: some View {
        Group {
            if secure { SecureField(placeholder, text: $text) } else { TextField(placeholder, text: $text) }
        }
        .textFieldStyle(.plain)
        .textContentType(contentType)
        .font(.system(size: large ? 22 : 16, weight: large ? .semibold : .regular))
        .multilineTextAlignment(large ? .center : .leading)
        .focused($focused)
        .onSubmit(onSubmit)
        .padding(.horizontal, 16).frame(height: 50)
        .background(RoundedRectangle(cornerRadius: 12).fill(MS.field))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(focused ? MS.brand : .clear, lineWidth: 1.5))
    }
}

struct MessengerPrimaryButton: View {
    let title: String
    var busy = false
    var disabled = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            ZStack {
                RoundedRectangle(cornerRadius: 22).fill(MS.brand.opacity(disabled ? 0.45 : 1))
                if busy { ProgressView().controlSize(.small).tint(.white) }
                else { Text(title).font(.system(size: 16, weight: .semibold)).foregroundStyle(.white) }
            }
            .frame(height: 44)
        }
        .buttonStyle(.plain)
        .disabled(disabled || busy)
    }
}

/// The Messenger mark: gradient round speech bubble with the white zigzag.
struct MessengerLogo: View {
    var size: CGFloat
    static let gradient = LinearGradient(colors: [Color(hex: 0x0099FF), Color(hex: 0xA033FF), Color(hex: 0xFF5280), Color(hex: 0xFF7061)],
                                         startPoint: .bottomLeading, endPoint: .topTrailing)

    var body: some View {
        ZStack {
            MessengerBubbleShape().fill(Self.gradient)
            MessengerZigzag().fill(.white)
        }
        .frame(width: size, height: size)
    }
}

struct MessengerBubbleShape: Shape {
    func path(in r: CGRect) -> Path {
        let w = r.width, h = r.height
        var p = Path(ellipseIn: CGRect(x: r.minX, y: r.minY, width: w, height: h * 0.94))
        var tail = Path()
        tail.move(to: CGPoint(x: r.minX + w * 0.17, y: r.minY + h * 0.70))
        tail.addLine(to: CGPoint(x: r.minX + w * 0.42, y: r.minY + h * 0.86))
        tail.addLine(to: CGPoint(x: r.minX + w * 0.22, y: r.minY + h * 1.0))
        tail.addQuadCurve(to: CGPoint(x: r.minX + w * 0.17, y: r.minY + h * 0.96), control: CGPoint(x: r.minX + w * 0.17, y: r.minY + h * 1.01))
        tail.closeSubpath()
        p.addPath(tail)
        return p.normalized(eoFill: false)
    }
}

struct MessengerZigzag: Shape {
    func path(in r: CGRect) -> Path {
        func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: r.minX + r.width * x, y: r.minY + r.height * y) }
        var p = Path()
        p.move(to: pt(0.18, 0.62))
        p.addLine(to: pt(0.42, 0.33))
        p.addLine(to: pt(0.56, 0.45))
        p.addLine(to: pt(0.82, 0.30))
        p.addLine(to: pt(0.58, 0.60))
        p.addLine(to: pt(0.44, 0.48))
        p.closeSubpath()
        return p
    }
}
