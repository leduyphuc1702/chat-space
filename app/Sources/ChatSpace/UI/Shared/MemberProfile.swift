import SwiftUI

extension View {
    /// Clicking opens the sender's card: what they shared in this chat, a search through it (photo text
    /// included) and a way to message them privately.
    func memberProfile(session: PlatformSession, chat: Chat, message: Message) -> some View {
        modifier(MemberProfileTrigger(session: session, chat: chat, message: message))
    }
}

private struct MemberProfileTrigger: ViewModifier {
    let session: PlatformSession
    let chat: Chat
    let message: Message
    @State private var shown = false

    func body(content: Content) -> some View {
        if message.fromMe || message.senderId.isEmpty {
            content
        } else {
            content
                .contentShape(Rectangle())
                .onTapGesture { shown = true }
                .pointingHandCursor()
                .popover(isPresented: $shown, arrowEdge: .trailing) {
                    MemberProfileView(session: session, chat: chat, member: Member(message), close: { shown = false })
                        .preferredColorScheme(.light)
                }
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
    let member: Member
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
    private let pageSize = 60

    init(session: PlatformSession, chatId: String, member: Member) {
        self.session = session
        self.chatId = chatId
        self.member = member
    }

    var signature: String { tab.rawValue + "|" + query }
    var canLoadMore: Bool { bridgeOpen || photosOpen }
    var terms: [String] { SearchText.terms(query) }

    func reload() async {
        generation += 1
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

    func loadMore() async {
        guard !loading, canLoadMore else { return }
        let gen = generation
        loading = true
        defer { if gen == generation { loading = false } }
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        var page: [MessageSearch.Hit] = []
        if bridgeOpen {
            do {
                let list = try await session.search(q, chatId: chatId, kinds: tab.kinds, senderId: member.id, before: cursor, limit: pageSize)
                guard gen == generation else { return }
                // A bridge without sender filtering returns everyone's messages.
                let mine = list.filter { $0.senderId == member.id && !$0.fromMe }
                page += mine.map { MessageSearch.Hit(platform: session.platform, message: $0) }
                cursor = list.map(\.timestamp).min()
                bridgeOpen = list.count >= pageSize
            } catch {
                guard gen == generation else { return }
                failed = error.localizedDescription
                bridgeOpen = false
            }
        }
        if photosOpen {
            let hits = await ImageTextIndexer.shared.search(q, platforms: [session.platform], chatId: chatId, before: photoCursor, limit: pageSize)
            guard gen == generation else { return }
            page += hits.filter { $0.message.senderId == member.id && !$0.message.fromMe }
                .map { MessageSearch.Hit(platform: $0.platform, message: $0.message, imageText: $0.text) }
            photoCursor = hits.map(\.message.timestamp).min()
            photosOpen = hits.count >= pageSize
        }
        var byId = Dictionary(items.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        for h in page {
            if var e = byId[h.id] { e.imageText = e.imageText ?? h.imageText; byId[h.id] = e } else { byId[h.id] = h }
        }
        items = byId.values.sorted { $0.message.timestamp > $1.message.timestamp }
    }
}

struct MemberProfileView: View {
    let session: PlatformSession
    let chat: Chat
    let member: Member
    let close: () -> Void
    @State private var model: MemberMedia?
    @State private var searching = false
    @State private var opening = false
    @State private var cannotOpen = false
    @FocusState private var searchFocused: Bool

    private var english: Bool { session.platform == .telegram }
    private var accent: Color {
        switch session.platform {
        case .telegram: TG.accent
        case .whatsapp: WA.teal
        case .messenger: MS.blue
        case .zalo: ZL.blue
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            if let model {
                tabs(model)
                Divider()
                results(model)
            }
        }
        .frame(width: 380, height: 560)
        .background(Color.white)
        .task {
            let m = MemberMedia(session: session, chatId: chat.id, member: member)
            model = m
            await m.reload()
        }
        .task(id: model?.signature) {
            guard let model else { return }
            try? await Task.sleep(nanoseconds: 250_000_000)
            if Task.isCancelled { return }
            await model.reload()
        }
    }

    private var header: some View {
        VStack(spacing: 10) {
            AvatarView(source: member.avatar, size: 72) {
                Circle().fill(accent.opacity(0.85))
                    .overlay(Text(String(member.name.prefix(1)).uppercased()).font(.system(size: 28, weight: .semibold)).foregroundStyle(.white))
            }
            Text(member.name).font(.system(size: 16, weight: .semibold)).lineLimit(1)
            HStack(spacing: 10) {
                if chat.isGroup || session.directChat(with: member.id)?.id != chat.id {
                    action(english ? "Message" : "Nhắn tin riêng", icon: "bubble.left.fill") { openDirect() }
                        .disabled(opening)
                }
                action(english ? "Search" : "Tìm kiếm", icon: "magnifyingglass") {
                    searching.toggle()
                    if searching { searchFocused = true } else { model?.query = "" }
                }
            }
            if cannotOpen {
                Text(english ? "No private chat with \(member.name) yet" : "Chưa có cuộc trò chuyện riêng với \(member.name)")
                    .font(.system(size: 12)).foregroundStyle(.secondary)
            }
            if searching, let model {
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
                .font(.system(size: 13))
                .padding(.horizontal, 10).padding(.vertical, 7)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.black.opacity(0.05)))
                .padding(.horizontal, 16)
            }
        }
        .padding(.vertical, 16)
    }

    private func action(_ title: String, icon: String, _ run: @escaping () -> Void) -> some View {
        Button(action: run) {
            VStack(spacing: 4) {
                Image(systemName: icon).font(.system(size: 15, weight: .semibold))
                Text(title).font(.system(size: 11.5, weight: .medium))
            }
            .foregroundStyle(accent)
            .frame(width: 110, height: 48)
            .background(RoundedRectangle(cornerRadius: 10).fill(accent.opacity(0.1)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func tabs(_ model: MemberMedia) -> some View {
        HStack(spacing: 0) {
            ForEach(MemberMedia.Tab.allCases) { t in
                Button { model.tab = t } label: {
                    VStack(spacing: 6) {
                        Text(title(t)).font(.system(size: 13, weight: model.tab == t ? .semibold : .regular))
                            .foregroundStyle(model.tab == t ? accent : .secondary)
                        Capsule().fill(model.tab == t ? accent : .clear).frame(height: 2.5)
                    }
                    .padding(.top, 8)
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
        ScrollView {
            if model.tab == .media && model.query.trimmingCharacters(in: .whitespaces).isEmpty {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 2), count: 3), spacing: 2) {
                    ForEach(model.items) { h in
                        MemberMediaTile(session: session, message: h.message) { show(h.message) }
                    }
                    footer(model).gridCellColumns(3)
                }
                .padding(2)
            } else {
                LazyVStack(spacing: 0) {
                    ForEach(model.items) { h in
                        SearchHitRow(hit: h, terms: model.terms, accent: accent, showChat: false) { show(h.message) }
                    }
                    footer(model)
                }
                .padding(.horizontal, 6).padding(.vertical, 4)
            }
        }
        .overlay {
            if model.items.isEmpty && !model.loading {
                Text(emptyText(model)).font(.system(size: 13)).foregroundStyle(.secondary)
                    .multilineTextAlignment(.center).padding(24)
            }
        }
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
            return english ? "Type to search \(member.name)'s messages" : "Nhập để tìm trong tin nhắn của \(member.name)"
        }
        return english ? "Nothing here yet" : "Chưa có gì"
    }

    private func show(_ m: Message) {
        close()
        if [.image, .video, .gif].contains(m.kind), model?.query.isEmpty ?? true {
            session.openMedia(m)
        } else {
            AppModel.shared.reveal(m, on: session.platform)
        }
    }

    private func openDirect() {
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
        let media = message.media ?? Media()
        let source = media.path ?? media.thumbnail ?? media.url
        Color.gray.opacity(0.12)
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                RemoteImage(source: source, contentMode: .fill, maxPixel: 240) { Color.clear }
            }
            .overlay(alignment: .bottomLeading) {
                if message.kind == .video || message.kind == .gif {
                    Image(systemName: "play.fill").font(.system(size: 10)).foregroundStyle(.white)
                        .padding(4).background(Circle().fill(.black.opacity(0.45))).padding(4)
                }
            }
            .clipped()
            .contentShape(Rectangle())
            .onTapGesture(perform: action)
            .onAppear {
                if media.path == nil && media.url == nil && media.thumbnail == nil && Workspace.shared.storage.autoDownloads(message) {
                    session.ensureMedia(message)
                }
            }
    }
}
