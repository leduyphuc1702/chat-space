import SwiftUI

extension View {
    /// Clicking opens the sender's page over the conversation, the way Telegram opens a member's info: what they
    /// shared in this chat, a search through it (photo text included) and a way to message them privately.
    func memberProfile(session: PlatformSession, chat: Chat, message: Message) -> some View {
        modifier(MemberProfileTrigger(session: session, chat: chat, message: message))
    }

    /// Clicking the chat's own avatar or name in the header opens the same page for the whole chat:
    /// everything anyone shared there.
    func chatPage(session: PlatformSession, chat: Chat) -> some View {
        contentShape(Rectangle())
            .onTapGesture {
                withAnimation(.easeOut(duration: 0.2)) { session.memberPage = MemberPage(chatId: chat.id, member: nil) }
            }
            .pointingHandCursor()
    }

    /// Where a conversation shows the member or chat page.
    func memberPage(session: PlatformSession, chat: Chat) -> some View {
        overlay {
            ZStack {
                if let page = session.memberPage, page.chatId == chat.id {
                    MemberProfileView(session: session, chat: chat, member: page.member) {
                        withAnimation(.easeOut(duration: 0.2)) { session.memberPage = nil }
                    }
                    .id(page.member?.id ?? "chat")
                    .transition(.move(edge: .trailing))
                }
            }
            .clipped()
        }
    }
}

/// The page a conversation is showing: one member's, or the chat's own when `member` is nil.
struct MemberPage: Equatable {
    var chatId: String
    var member: Member?
}

private struct MemberProfileTrigger: ViewModifier {
    let session: PlatformSession
    let chat: Chat
    let message: Message

    func body(content: Content) -> some View {
        if message.fromMe || message.senderId.isEmpty {
            content
        } else {
            content
                .contentShape(Rectangle())
                .onTapGesture {
                    withAnimation(.easeOut(duration: 0.2)) {
                        session.memberPage = MemberPage(chatId: chat.id, member: Member(message))
                    }
                }
                .pointingHandCursor()
        }
    }
}

extension View {
    func pointingHandCursor() -> some View {
        onHover { inside in
            if inside { NSCursor.pointingHand.push() } else { NSCursor.pop() }
        }
    }
}

struct Member: Equatable {
    var id: String
    var name: String
    var avatar: String?

    init(_ m: Message) {
        id = m.senderId
        name = m.senderName.flatMap { $0.isEmpty ? nil : $0 } ?? m.senderId
        avatar = m.senderAvatar
    }
}

/// What one person shared in a conversation, Telegram's Media / Files / Links tabs, with a search over
/// their messages and the text found in their photos.
@MainActor
@Observable
final class MemberMedia {
    enum Tab: String, CaseIterable, Identifiable {
        case media, files, links, messages
        var id: String { rawValue }
        var kinds: [MessageKind]? {
            switch self {
            case .media: [.image, .video, .gif]
            case .files: [.file, .audio, .voice]
            case .links: [.link]
            case .messages: nil
            }
        }
    }

    let session: PlatformSession
    let chatId: String
    /// nil: everyone in the chat, yourself included.
    let member: Member?
    var tab: Tab = .media
    var query = ""
    private(set) var items: [MessageSearch.Hit] = []
    private(set) var loading = false
    private(set) var failed: String?
    private var cursor: Double?
    private var bridgeOpen = true
    private var photoCursor: Double?
    private var photosOpen = false
    private var generation = 0
    private(set) var loadedSignature: String?
    private let pageSize = 60

    init(session: PlatformSession, chatId: String, member: Member?) {
        self.session = session
        self.chatId = chatId
        self.member = member
    }

    var signature: String { tab.rawValue + "|" + query }
    var canLoadMore: Bool { bridgeOpen || photosOpen }
    var terms: [String] { SearchText.terms(query) }

    func reload() async {
        generation += 1
        loadedSignature = signature
        loading = false // a request still running belongs to the old generation and is dropped
        items = []
        cursor = nil
        photoCursor = nil
        bridgeOpen = true
        failed = nil
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        photosOpen = !q.isEmpty && (tab == .media || tab == .messages) && ImageTextIndexer.shared.store != nil
        if tab == .messages && q.isEmpty { bridgeOpen = false; return }
        await loadMore()
    }

    private func belongs(_ m: Message) -> Bool {
        guard let member else { return true }
        return m.senderId == member.id && !m.fromMe
    }

    /// The next page. A page can come back empty while there is more history (the bridge scanned a stretch
    /// where this person shared nothing), so it keeps reading a few pages until something new turns up.
    func loadMore() async {
        guard !loading, canLoadMore else { return }
        let gen = generation
        loading = true
        defer { if gen == generation { loading = false } }
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let before = items.count
        for _ in 0..<6 {
            var page: [MessageSearch.Hit] = []
            if bridgeOpen {
                do {
                    let r = try await session.searchPage(q, chatId: chatId, kinds: tab.kinds, senderId: member?.id, before: cursor, limit: pageSize)
                    guard gen == generation else { return }
                    // A bridge without sender filtering returns everyone's messages.
                    page += r.messages.filter(belongs).map { MessageSearch.Hit(platform: session.platform, message: $0) }
                    if let next = r.next, cursor.map({ next < $0 }) ?? true {
                        cursor = next
                    } else {
                        bridgeOpen = false
                    }
                } catch {
                    guard gen == generation else { return }
                    failed = error.localizedDescription
                    bridgeOpen = false
                }
            }
            if photosOpen {
                let hits = await ImageTextIndexer.shared.search(q, platforms: [session.platform], chatId: chatId, before: photoCursor, limit: pageSize)
                guard gen == generation else { return }
                page += hits.filter { belongs($0.message) }
                    .map { MessageSearch.Hit(platform: $0.platform, message: $0.message, imageText: $0.text) }
                photoCursor = hits.map(\.message.timestamp).min()
                photosOpen = hits.count >= pageSize
            }
            var byId = Dictionary(items.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
            for h in page {
                if var e = byId[h.id] { e.imageText = e.imageText ?? h.imageText; byId[h.id] = e } else { byId[h.id] = h }
            }
            items = byId.values.sorted { $0.message.timestamp > $1.message.timestamp }
            if items.count > before || !canLoadMore { return }
        }
    }
}

/// A member's (or, with no member, the chat's own) info page, laid out like Telegram's: back bar, big avatar and
/// name, action buttons, then the shared media in tabs, month by month. Photos open in the viewer while the page stays.
struct MemberProfileView: View {
    let session: PlatformSession
    let chat: Chat
    let member: Member?
    let close: () -> Void
    @State private var model: MemberMedia?
    @State private var searching = false
    @State private var opening = false
    @State private var cannotOpen = false
    @FocusState private var searchFocused: Bool
    @State private var escapeMonitor: Any?

    private var english: Bool { session.platform == .telegram }
    private var accent: Color {
        switch session.platform {
        case .telegram: TG.accent
        case .whatsapp: WA.teal
        case .messenger: MS.blue
        case .zalo: ZL.blue
        }
    }
    /// Telegram's grouped background behind the white cards.
    private static let pageBackground = Color(red: 0.937, green: 0.937, blue: 0.957)
    private var name: String { member?.name ?? (chat.kind == .self ? "Saved Messages" : chat.title) }

    var body: some View {
        VStack(spacing: 0) {
            navigationBar
            Divider()
            ScrollView {
                VStack(spacing: 14) {
                    header
                    actions
                    if searching { searchField }
                    if let model { sharedMedia(model) }
                }
                .frame(maxWidth: 700)
                .padding(.horizontal, 20).padding(.top, 22).padding(.bottom, 20)
                .frame(maxWidth: .infinity)
            }
        }
        .background(Self.pageBackground)
        .onAppear { watchEscape() }
        .onDisappear { stopWatchingEscape() }
        .task {
            let m = MemberMedia(session: session, chatId: chat.id, member: member)
            model = m
            await m.reload()
        }
        .task(id: model?.signature) {
            guard let model, model.loadedSignature != model.signature else { return }
            try? await Task.sleep(nanoseconds: 250_000_000)
            if Task.isCancelled { return }
            await model.reload()
        }
    }

    private var navigationBar: some View {
        ZStack {
            Text(english ? "Info" : "Thông tin").font(.system(size: 14, weight: .semibold))
            HStack {
                Button(action: close) {
                    HStack(spacing: 3) {
                        Image(systemName: "chevron.left").font(.system(size: 15, weight: .semibold))
                        Text(english ? "Back" : "Quay lại").font(.system(size: 14))
                    }
                    .foregroundStyle(accent)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(english ? "Back to the chat (Esc)" : "Quay lại cuộc trò chuyện (Esc)")
                Spacer()
            }
        }
        .padding(.horizontal, 16)
        .frame(height: 56)
        .background(Color.white)
    }

    private var header: some View {
        VStack(spacing: 6) {
            AvatarView(source: member?.avatar ?? chat.avatar, size: 100) {
                Circle().fill(accent.opacity(0.85))
                    .overlay(Text(String(name.prefix(1)).uppercased()).font(.system(size: 40, weight: .semibold)).foregroundStyle(.white))
            }
            .padding(.bottom, 6)
            Text(name).font(.system(size: 20, weight: .semibold)).lineLimit(1)
            if let subtitle {
                Text(subtitle).font(.system(size: 13)).foregroundStyle(.secondary).lineLimit(1)
            }
            if cannotOpen {
                Text(english ? "No private chat with \(name) yet" : "Chưa có cuộc trò chuyện riêng với \(name)")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            }
        }
    }

    private var subtitle: String? {
        if member != nil {
            guard chat.isGroup else { return nil }
            return english ? "member of \(chat.title)" : "thành viên \(chat.title)"
        }
        switch chat.kind {
        case .group: return chat.memberCount.map { english ? "\($0) members" : "\($0) thành viên" } ?? (english ? "group" : "nhóm")
        case .channel: return chat.memberCount.map { english ? "\($0) subscribers" : "\($0) người theo dõi" } ?? (english ? "channel" : "kênh")
        default: return nil
        }
    }

    private var actions: some View {
        HStack(spacing: 10) {
            if let member, chat.isGroup || session.directChat(with: member.id)?.id != chat.id {
                action(english ? "Message" : "Nhắn tin", icon: "bubble.left.fill") { openDirect() }
                    .disabled(opening)
            }
            action(english ? "Search" : "Tìm kiếm", icon: "magnifyingglass", on: searching) {
                searching.toggle()
                if searching { searchFocused = true } else { model?.query = "" }
            }
        }
    }

    private func action(_ title: String, icon: String, on: Bool = false, _ run: @escaping () -> Void) -> some View {
        Button(action: run) {
            VStack(spacing: 5) {
                Image(systemName: icon).font(.system(size: 18, weight: .medium))
                Text(title).font(.system(size: 12, weight: .medium))
            }
            .foregroundStyle(accent)
            .frame(maxWidth: .infinity, minHeight: 58)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(on ? accent.opacity(0.12) : Color.white))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private var searchField: some View {
        if let model {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField(english ? "Search messages and text in photos" : "Tìm tin nhắn và chữ trong ảnh",
                          text: Binding(get: { model.query }, set: { model.query = $0 }))
                    .textFieldStyle(.plain).focused($searchFocused)
                    .onSubmit { Task { await model.reload() } }
                if !model.query.isEmpty {
                    Button { model.query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                        .buttonStyle(.plain)
                }
            }
            .font(.system(size: 13.5))
            .padding(.horizontal, 12).padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.white))
        }
    }

    private func sharedMedia(_ model: MemberMedia) -> some View {
        VStack(spacing: 0) {
            tabs(model)
            Divider()
            results(model)
        }
        .background(Color.white)
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func tabs(_ model: MemberMedia) -> some View {
        HStack(spacing: 0) {
            ForEach(MemberMedia.Tab.allCases) { t in
                Button { model.tab = t } label: {
                    VStack(spacing: 7) {
                        Text(title(t)).font(.system(size: 13.5, weight: model.tab == t ? .semibold : .regular))
                            .foregroundStyle(model.tab == t ? accent : .secondary)
                        Capsule().fill(model.tab == t ? accent : .clear).frame(width: 36, height: 3)
                    }
                    .padding(.top, 11)
                    .frame(maxWidth: .infinity)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 8)
    }

    private func title(_ t: MemberMedia.Tab) -> String {
        switch t {
        case .media: english ? "Media" : "Ảnh/Video"
        case .files: english ? "Files" : "Tệp"
        case .links: english ? "Links" : "Liên kết"
        case .messages: english ? "Messages" : "Tin nhắn"
        }
    }

    @ViewBuilder
    private func results(_ model: MemberMedia) -> some View {
        if model.items.isEmpty && !model.loading {
            Text(emptyText(model)).font(.system(size: 13)).foregroundStyle(.secondary)
                .multilineTextAlignment(.center).padding(.vertical, 48).padding(.horizontal, 24)
                .frame(maxWidth: .infinity)
        } else if model.tab == .media && model.query.trimmingCharacters(in: .whitespaces).isEmpty {
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 2), count: 4), spacing: 2) {
                ForEach(months(model.items), id: \.title) { month in
                    Section {
                        ForEach(month.items) { h in
                            MemberMediaTile(session: session, message: h.message) { show(h.message) }
                        }
                    } header: {
                        monthHeader(month.title)
                    }
                }
                // Inside the lazy grid so it appears only when scrolled to.
                Section {} footer: { footer(model) }
            }
            .padding(.horizontal, 2)
        } else {
            LazyVStack(spacing: 0) {
                ForEach(months(model.items), id: \.title) { month in
                    Section {
                        ForEach(month.items) { h in
                            SearchHitRow(hit: h, terms: model.terms, accent: accent, showChat: false) { show(h.message) }
                        }
                    } header: {
                        monthHeader(month.title)
                    }
                }
                footer(model)
            }
            .padding(.horizontal, 6).padding(.bottom, 4)
        }
    }

    /// Newest month first, the way Telegram's shared media reads.
    private func months(_ items: [MessageSearch.Hit]) -> [(title: String, items: [MessageSearch.Hit])] {
        var out: [(title: String, items: [MessageSearch.Hit])] = []
        for h in items {
            let title = monthTitle(h.message.date)
            if out.last?.title == title { out[out.count - 1].items.append(h) } else { out.append((title, [h])) }
        }
        return out
    }

    private func monthTitle(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: english ? "en_US" : "vi_VN")
        f.dateFormat = english ? "LLLL yyyy" : "'Tháng' M, yyyy"
        return f.string(from: date)
    }

    private func monthHeader(_ title: String) -> some View {
        Text(title).font(.system(size: 13, weight: .semibold)).foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 10).padding(.top, 12).padding(.bottom, 6)
    }

    @ViewBuilder
    private func footer(_ model: MemberMedia) -> some View {
        if model.loading {
            ProgressView().controlSize(.small).padding(12).frame(maxWidth: .infinity)
        } else if model.canLoadMore && !model.items.isEmpty {
            Color.clear.frame(height: 1).onAppear { Task { await model.loadMore() } }
        }
    }

    private func emptyText(_ model: MemberMedia) -> String {
        if let e = model.failed { return e }
        if model.tab == .messages && model.query.isEmpty {
            if member == nil { return english ? "Type to search this chat" : "Nhập để tìm trong cuộc trò chuyện này" }
            return english ? "Type to search \(name)'s messages" : "Nhập để tìm trong tin nhắn của \(name)"
        }
        return english ? "Nothing here yet" : "Chưa có gì"
    }

    /// Photos open in the viewer as one gallery of everything this person sent, browsable and kept open;
    /// anything else jumps to the message in the chat.
    private func show(_ m: Message) {
        guard let model, MediaViewer.canShow(m), model.query.trimmingCharacters(in: .whitespaces).isEmpty else {
            close()
            AppModel.shared.reveal(m, on: session.platform)
            return
        }
        MediaViewer.shared.show(session: session, message: m, gallery: Self.gallery(model), title: name) {
            guard model.canLoadMore else { return nil }
            await model.loadMore()
            return Self.gallery(model)
        }
    }

    /// Oldest first, the way the viewer steps through a chat.
    private static func gallery(_ model: MemberMedia) -> [Message] {
        model.items.map(\.message).filter(MediaViewer.canShow).reversed()
    }

    /// Esc goes back from anywhere in the main window, the way Telegram's info page does; the search
    /// closes first. onExitCommand alone only fires when something inside the page holds focus.
    private func watchEscape() {
        guard escapeMonitor == nil else { return }
        escapeMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard event.keyCode == 53, let window = event.window, window === NSApp.mainWindow, !(window is NSPanel) else { return event }
            // Esc while typing Vietnamese cancels the composition instead.
            if let tv = window.firstResponder as? NSTextView, tv.hasMarkedText() { return event }
            MainActor.assumeIsolated {
                if searching {
                    searching = false
                    model?.query = ""
                } else {
                    close()
                }
            }
            return nil
        }
    }

    private func stopWatchingEscape() {
        if let m = escapeMonitor { NSEvent.removeMonitor(m) }
        escapeMonitor = nil
    }

    private func openDirect() {
        guard let member else { return }
        opening = true
        Task {
            let ok = await session.openDirect(userId: member.id, name: member.name, avatar: member.avatar)
            opening = false
            if ok { close() } else { cannotOpen = true }
        }
    }
}

private struct MemberMediaTile: View {
    let session: PlatformSession
    let message: Message
    let action: () -> Void

    var body: some View {
        Color.gray.opacity(0.12)
            .aspectRatio(1, contentMode: .fit)
            .overlay { MediaThumb(session: session, message: message, maxPixel: 400) }
            .overlay(alignment: .bottomLeading) {
                if message.kind == .video || message.kind == .gif {
                    Image(systemName: "play.fill").font(.system(size: 10)).foregroundStyle(.white)
                        .padding(4).background(Circle().fill(.black.opacity(0.45))).padding(4)
                }
            }
            .clipped()
            // So photos of white documents don't run together.
            .overlay(Rectangle().stroke(Color.black.opacity(0.07), lineWidth: 0.5))
            .contentShape(Rectangle())
            .onTapGesture(perform: action)
    }
}
