import SwiftUI

/// What an aggregate list collects.
enum AggregateSource: Hashable {
    /// Unread chats from every platform (minus ignored ones).
    case inbox
    /// A user group: chats whose title matches its keywords.
    case group(UUID)

    var rail: RailItem {
        switch self {
        case .inbox: .inbox
        case .group(let id): .group(id)
        }
    }
}

/// ChatSpace's own screen: a merged list on the left, and on the right the chat rendered by its
/// platform's original conversation UI.
struct AggregateView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings
    let source: AggregateSource
    @State private var filter: Platform?
    @State private var query = ""
    /// The open chat stays listed after it is read, until the user picks another one.
    @State private var sticky: ChatRef?
    /// Whether the sticky chat was unread when opened; it keeps sorting as such so it doesn't jump.
    @State private var stickyWasUnread = false
    @State private var editing: CustomGroup?

    var body: some View {
        HStack(spacing: 0) {
            VStack(spacing: 0) {
                header
                list
            }
            .frame(width: 340)
            .background(Color(nsColor: .controlBackgroundColor))
            Rectangle().fill(Color.black.opacity(0.1)).frame(width: 1)
            if let ref = selection, let chat = model.chat(ref) {
                VStack(spacing: 0) {
                    sourceBar(ref, chat)
                    PlatformConversation(ref: ref, chat: chat).id(ref.id)
                }
            } else {
                empty
            }
        }
        .sheet(item: $editing) { GroupEditor(group: $0) }
        .onAppear { if let s = selection { sticky = s } }
    }

    private var selection: ChatRef? { model.aggregateSelection[source.rail] }

    private var group: CustomGroup? {
        if case .group(let id) = source { return Workspace.shared.group(id) }
        return nil
    }

    private var refs: [ChatRef] {
        var list: [ChatRef]
        switch source {
        case .inbox: list = model.inboxRefs
        case .group: list = group.map { model.groupRefs($0) } ?? []
        }
        for s in [sticky, selection].compactMap({ $0 }) where model.chat(s) != nil {
            list.removeAll { $0 == s }
            list.insert(s, at: min(position(of: s, in: list), list.count))
        }
        if let f = filter { list = list.filter { $0.platform == f } }
        let q = CustomGroup.normalize(query)
        if !q.isEmpty { list = list.filter { CustomGroup.normalize(model.chat($0)?.title ?? "").contains(q) } }
        return list
    }

    // MARK: Header

    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                if let g = group {
                    GroupIcon(group: g, size: 30)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(g.name).font(.system(size: 17, weight: .bold))
                        Text("Từ khoá: " + g.keywords.joined(separator: ", ")).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Button { editing = g } label: {
                        Image(systemName: "slider.horizontal.3").font(.system(size: 14)).frame(width: 28, height: 28).contentShape(Rectangle())
                    }
                    .buttonStyle(.plain).foregroundStyle(.secondary).help("Sửa nhóm")
                } else {
                    ChatSpaceIcon(size: 30)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Chưa đọc").font(.system(size: 17, weight: .bold))
                        Text("Tất cả nền tảng").font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if !Workspace.shared.ignored.isEmpty {
                        Button {
                            UserDefaults.standard.set(SettingsSection.ignored.rawValue, forKey: "settings.section")
                            openSettings()
                        } label: {
                            Label("\(Workspace.shared.ignored.count) bỏ qua", systemImage: "eye.slash")
                                .font(.system(size: 11)).foregroundStyle(.secondary).contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help("Khôi phục trong Cài đặt → Đã bỏ qua")
                    }
                }
            }
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(.system(size: 12)).foregroundStyle(.secondary)
                TextField("Tìm kiếm", text: $query).textFieldStyle(.plain).font(.system(size: 13))
            }
            .padding(.horizontal, 10).frame(height: 30)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    chip(nil, "Tất cả", count: countFor(nil))
                    ForEach(Platform.allCases) { p in
                        let n = countFor(p)
                        if n > 0 || filter == p { chip(p, p.displayName, count: n) }
                    }
                }
            }
        }
        .padding(.horizontal, 14).padding(.top, 38).padding(.bottom, 10)
    }

    private func countFor(_ p: Platform?) -> Int {
        let base: [ChatRef]
        switch source {
        case .inbox: base = model.inboxRefs
        case .group: base = group.map { model.groupRefs($0) } ?? []
        }
        return base.filter { p == nil || $0.platform == p }.count
    }

    private func chip(_ p: Platform?, _ title: String, count: Int) -> some View {
        let on = filter == p
        return Button { filter = p } label: {
            HStack(spacing: 5) {
                if let p { PlatformIcon(platform: p, size: 14) }
                Text(title).font(.system(size: 12, weight: .medium))
                Text("\(count)").font(.system(size: 11)).foregroundStyle(on ? .white.opacity(0.8) : .secondary)
            }
            .padding(.horizontal, 10).frame(height: 26)
            .foregroundStyle(on ? .white : .primary)
            .background(Capsule().fill(on ? Color.accentColor : Color.primary.opacity(0.07)))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }

    // MARK: List

    @ViewBuilder private var list: some View {
        let items = refs
        if items.isEmpty {
            VStack(spacing: 10) {
                Image(systemName: source == .inbox ? "checkmark.circle" : "tray").font(.system(size: 40, weight: .light)).foregroundStyle(.tertiary)
                Text(source == .inbox ? "Đã đọc hết" : "Chưa có đoạn chat nào khớp từ khoá").font(.system(size: 14, weight: .medium)).foregroundStyle(.secondary)
                if source == .inbox {
                    Text("Tin chưa đọc của Zalo, Messenger, WhatsApp và Telegram sẽ hiện ở đây.")
                        .font(.system(size: 12)).foregroundStyle(.tertiary).multilineTextAlignment(.center)
                }
            }
            .padding(30)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView {
                LazyVStack(spacing: 0, pinnedViews: source == .inbox && filter == nil ? [.sectionHeaders] : []) {
                    if source == .inbox && filter == nil {
                        ForEach(Platform.allCases) { p in
                            let rows = items.filter { $0.platform == p }
                            if !rows.isEmpty {
                                Section {
                                    ForEach(rows) { row($0) }
                                } header: {
                                    sectionHeader(p, rows.count)
                                }
                            }
                        }
                    } else {
                        ForEach(items) { row($0) }
                    }
                }
                .padding(.horizontal, 8).padding(.bottom, 8)
            }
        }
    }

    private func sectionHeader(_ p: Platform, _ n: Int) -> some View {
        HStack(spacing: 7) {
            PlatformIcon(platform: p, size: 16)
            Text(p.displayName.uppercased()).font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            Text("\(n)").font(.system(size: 11)).foregroundStyle(.tertiary)
            Spacer()
            Button("Xem") { model.rail = .platform(p) }.buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(Color.accentColor)
        }
        .padding(.horizontal, 8).padding(.top, 10).padding(.bottom, 4)
        .background(Color(nsColor: .controlBackgroundColor))
    }

    @ViewBuilder private func row(_ ref: ChatRef) -> some View {
        if let chat = model.chat(ref) {
            AggregateRow(ref: ref, chat: chat, selected: selection == ref)
                .onTapGesture { open(ref) }
                .chatContextMenu(platform: ref.platform, chat: chat, inInbox: source == .inbox)
        }
    }

    /// Where `ref` sorts among the others, treating it as still unread if it was when opened.
    private func position(of ref: ChatRef, in list: [ChatRef]) -> Int {
        guard let c = model.chat(ref) else { return 0 }
        let unread = c.hasUnread || (ref == sticky && stickyWasUnread)
        let unreadFirst = source != .inbox
        return list.firstIndex { other in
            guard let o = model.chat(other) else { return false }
            if unreadFirst && o.hasUnread != unread { return unread }
            return o.timestamp < c.timestamp
        } ?? list.count
    }

    private func open(_ ref: ChatRef) {
        stickyWasUnread = model.chat(ref)?.hasUnread ?? false
        sticky = ref
        model.aggregateSelection[source.rail] = ref
        model.session(ref.platform).prepare(ref.chatId)
    }

    private func sourceBar(_ ref: ChatRef, _ chat: Chat) -> some View {
        HStack(spacing: 8) {
            PlatformIcon(platform: ref.platform, size: 16)
            Text(ref.platform.displayName).font(.system(size: 12, weight: .semibold))
            Text("·").foregroundStyle(.tertiary)
            Text(chat.title).font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(1)
            Spacer()
            Button { ChatHeads.shared.show(ref, expand: true) } label: {
                Label("Bong bóng", systemImage: "bubble.left.circle").font(.system(size: 11)).contentShape(Rectangle())
            }
            .buttonStyle(.plain).foregroundStyle(.secondary).help("Mở đoạn chat này thành bong bóng nổi")
            Button { model.open(platform: ref.platform, chatId: ref.chatId) } label: {
                Label("Mở trong \(ref.platform.displayName)", systemImage: "arrow.up.forward.app").font(.system(size: 11)).contentShape(Rectangle())
            }
            .buttonStyle(.plain).foregroundStyle(Color.accentColor)
        }
        .padding(.horizontal, 12).frame(height: 30)
        .background(Color(nsColor: .controlBackgroundColor))
        .overlay(alignment: .bottom) { Rectangle().fill(Color.black.opacity(0.08)).frame(height: 1) }
    }

    private var empty: some View {
        VStack(spacing: 12) {
            if let g = group { GroupIcon(group: g, size: 64) } else { ChatSpaceIcon(size: 64) }
            Text(group?.name ?? "ChatSpace").font(.system(size: 20, weight: .semibold))
            Text(group == nil ? "Chọn một đoạn chat để trả lời ngay tại đây, bằng giao diện gốc của nền tảng."
                              : "Mọi đoạn chat có tên khớp \(group!.keywords.map { "“\($0)”" }.joined(separator: ", ")) trên các nền tảng.")
                .font(.system(size: 13)).foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth: 380)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

struct AggregateRow: View {
    let ref: ChatRef
    let chat: Chat
    let selected: Bool
    @State private var hovered = false

    var body: some View {
        let session = AppModel.shared.session(ref.platform)
        HStack(spacing: 10) {
            AvatarView(source: chat.avatar, size: 46) {
                Circle().fill(Color.gray.opacity(0.25))
                    .overlay(Text(String(chat.title.prefix(1)).uppercased()).font(.system(size: 18, weight: .semibold)).foregroundStyle(.white))
            }
            .overlay(alignment: .bottomTrailing) {
                PlatformIcon(platform: ref.platform, size: 17)
                    .background(Circle().fill(Color(nsColor: .controlBackgroundColor)).padding(-2))
                    .offset(x: 3, y: 3)
            }
            .onAppear { session.requestAvatar(for: chat) }
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    Text(chat.title).font(.system(size: 14, weight: chat.hasUnread ? .semibold : .regular)).lineLimit(1)
                    if chat.isMuted { Image(systemName: "speaker.slash.fill").font(.system(size: 10)).foregroundStyle(.tertiary) }
                    Spacer(minLength: 4)
                    Text(timeText).font(.system(size: 11)).foregroundStyle(chat.hasUnread && !chat.isMuted ? Color.accentColor : .secondary)
                }
                HStack(spacing: 4) {
                    Text(preview).font(.system(size: 12.5)).foregroundStyle(.secondary).lineLimit(1)
                    Spacer(minLength: 4)
                    if chat.unread > 0 {
                        Text(chat.unread > 999 ? "999+" : "\(chat.unread)").font(.system(size: 11, weight: .semibold)).foregroundStyle(.white)
                            .padding(.horizontal, 6).frame(minWidth: 20, minHeight: 20)
                            .background(Capsule().fill(chat.isMuted ? Color.gray.opacity(0.6) : Color.accentColor))
                    } else if chat.markedUnread == true {
                        Circle().fill(chat.isMuted ? Color.gray.opacity(0.6) : Color.accentColor).frame(width: 10, height: 10)
                    }
                }
            }
        }
        .padding(.horizontal, 8).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 9).fill(selected ? Color.accentColor.opacity(0.16) : (hovered ? Color.primary.opacity(0.05) : .clear)))
        .contentShape(Rectangle())
        .onHover { hovered = $0 }
    }

    private var preview: String {
        guard let m = chat.lastMessage else { return "" }
        let body = m.preview(photo: "Hình ảnh", video: "Video", voice: "Tin nhắn thoại", sticker: "Sticker", file: "Tệp", deleted: "Tin nhắn đã thu hồi")
        if m.fromMe { return "Bạn: " + body }
        if chat.isGroup, let n = m.senderName { return n + ": " + body }
        return body
    }

    private var timeText: String {
        let d = chat.date
        if Calendar.current.isDateInToday(d) { return Fmt.time(d) }
        let f = DateFormatter()
        f.locale = Locale(identifier: "vi_VN")
        f.dateFormat = Date().timeIntervalSince(d) < 6 * 86400 ? "EEE" : "dd/MM"
        return f.string(from: d)
    }
}

/// A chat rendered by its own platform's conversation UI.
struct PlatformConversation: View {
    let ref: ChatRef
    let chat: Chat

    var body: some View {
        let s = AppModel.shared.session(ref.platform)
        Group {
            switch ref.platform {
            case .zalo: ZaloConversation(session: s, chat: chat)
            case .messenger: MessengerConversation(session: s, chat: chat)
            case .whatsapp: WhatsAppConversation(session: s, chat: chat)
            case .telegram: TelegramConversation(session: s, chat: chat)
            }
        }
        .environment(\.colorScheme, .light)
    }
}

// MARK: Context menu shared by every chat list

extension View {
    /// Right-click actions ChatSpace adds to chats in any list.
    func chatContextMenu(platform: Platform, chat: Chat, inInbox: Bool = false) -> some View {
        contextMenu {
            ChatMenuItems(ref: ChatRef(platform: platform, chatId: chat.id), chat: chat, inInbox: inInbox)
        }
    }
}

struct ChatMenuItems: View {
    let ref: ChatRef
    let chat: Chat
    var inInbox = false

    var body: some View {
        let ws = Workspace.shared
        let session = AppModel.shared.session(ref.platform)
        Button("Mở bong bóng chat") { ChatHeads.shared.show(ref, expand: true) }
        if chat.hasUnread {
            Button("Đánh dấu đã đọc") { session.markRead(chat.id) }
        }
        MuteMenuItems(session: session, chat: chat)
        Divider()
        if ws.isIgnored(ref) {
            Button("Hiện lại trong mục Chưa đọc của ChatSpace") { ws.restore(ref) }
        } else {
            Button(inInbox ? "Bỏ qua cho lần sau" : "Bỏ qua trong mục Chưa đọc của ChatSpace") {
                ws.ignore(ref, title: chat.title)
                if AppModel.shared.aggregateSelection[.inbox] == ref { AppModel.shared.aggregateSelection[.inbox] = nil }
            }
        }
        if !inInbox || AppModel.shared.rail != .platform(ref.platform) {
            Button("Mở trong \(ref.platform.displayName)") { AppModel.shared.open(platform: ref.platform, chatId: chat.id) }
        }
        Group {
            Menu("Thêm vào nhóm tuỳ chỉnh") {
                ForEach(ws.groups) { g in
                    Button(g.name) {
                        var g = g
                        if !g.matches(chat, on: ref.platform) { g.keywords.append(chat.title) }
                        ws.upsert(g)
                    }
                }
                Divider()
                Button("Nhóm mới từ tên này…") {
                    let base = chat.title.split(whereSeparator: { !$0.isLetter }).first.map(String.init) ?? chat.title
                    var g = CustomGroup(name: base, keywords: [base], match: .word)
                    g.colorHex = [0x5E5CE6, 0x0A84FF, 0x30D158, 0xFF9F0A, 0xFF375F, 0xBF5AF2].randomElement()!
                    ws.upsert(g)
                    AppModel.shared.rail = .group(g.id)
                }
            }
        }
    }
}
