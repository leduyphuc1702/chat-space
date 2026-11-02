import SwiftUI

/// One message search: bridge `messages.search` on each platform plus the photo text index, merged newest first
/// and paged per source. Used by the global search, the platform chat lists and the in-chat search.
@MainActor
@Observable
final class MessageSearch {
    enum Kind: String, CaseIterable, Identifiable {
        case all, photos, files, links
        var id: String { rawValue }
        var title: String {
            switch self {
            case .all: "Tất cả"
            case .photos: "Ảnh"
            case .files: "Tệp"
            case .links: "Liên kết"
            }
        }
        var icon: String {
            switch self {
            case .all: "text.magnifyingglass"
            case .photos: "photo"
            case .files: "doc"
            case .links: "link"
            }
        }
        var kinds: [MessageKind]? {
            switch self {
            case .all: nil
            case .photos: [.image]
            case .files: [.file]
            case .links: [.link]
            }
        }
    }

    struct Hit: Identifiable {
        let platform: Platform
        var message: Message
        /// Recognised text of a photo, when that is what matched.
        var imageText: String?
        var id: String { platform.rawValue + "|" + message.chatId + "|" + message.id }
    }

    private enum Source: Hashable {
        case bridge(Platform)
        case photos
    }

    var query = ""
    var kind: Kind = .all
    /// Empty = every signed-in platform.
    var platforms: Set<Platform> = []
    /// Limits the search to one chat (with exactly one platform in `platforms`).
    var chatId: String?

    private(set) var hits: [Hit] = []
    private(set) var searching = false
    private(set) var loadingMore = false
    private(set) var searchedQuery = ""
    private(set) var failures: [Platform: String] = [:]
    private var cursors: [Source: Double] = [:]
    private var open: Set<Source> = []
    private var generation = 0
    private let pageSize: Int

    init(platforms: Set<Platform> = [], chatId: String? = nil, pageSize: Int = 30) {
        self.platforms = platforms
        self.chatId = chatId
        self.pageSize = pageSize
    }

    var terms: [String] { SearchText.terms(searchedQuery) }
    var canLoadMore: Bool { !open.isEmpty }
    /// Changes whenever the results must be recomputed (drives a debounced `.task(id:)`).
    var signature: String { [query, kind.rawValue, platforms.map(\.rawValue).sorted().joined(), chatId ?? ""].joined(separator: "|") }

    static func available(_ p: Platform) -> Bool {
        DemoData.enabled || AppModel.shared.session(p).authState == .loggedIn
    }

    private var scope: [Platform] {
        Platform.allCases.filter { (platforms.isEmpty || platforms.contains($0)) && Self.available($0) }
    }

    private var sources: [Source] {
        var out = scope.map(Source.bridge)
        if (kind == .all || kind == .photos) && ImageTextIndexer.shared.store != nil { out.append(.photos) }
        return out
    }

    func clear() {
        generation += 1
        hits = []
        searchedQuery = ""
        open = []
        cursors = [:]
        failures = [:]
        searching = false
    }

    func run() async {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { clear(); return }
        generation += 1
        let gen = generation
        searching = true
        let srcs = sources
        let pages = await fetch(srcs, query: q, cursors: [:])
        guard gen == generation else { return }
        searchedQuery = q
        hits = []
        cursors = [:]
        open = []
        failures = [:]
        absorb(pages)
        searching = false
    }

    func loadMore() async {
        guard !open.isEmpty, !loadingMore, !searchedQuery.isEmpty else { return }
        let gen = generation
        loadingMore = true
        defer { loadingMore = false }
        let pages = await fetch(Array(open), query: searchedQuery, cursors: cursors)
        guard gen == generation else { return }
        absorb(pages)
    }

    private struct Page {
        var source: Source
        var hits: [Hit]
        var error: String?
    }

    private func fetch(_ srcs: [Source], query: String, cursors: [Source: Double]) async -> [Page] {
        let kinds = kind.kinds
        let chatId = chatId
        let limit = pageSize
        let photoPlatforms = Set(scope)
        return await withTaskGroup(of: Page.self) { group in
            for src in srcs {
                let before = cursors[src]
                group.addTask { @MainActor in
                    switch src {
                    case .bridge(let p):
                        do {
                            let list = try await AppModel.shared.session(p).search(query, chatId: chatId, kinds: kinds, before: before, limit: limit)
                            return Page(source: src, hits: list.map { Hit(platform: p, message: $0) })
                        } catch {
                            return Page(source: src, hits: [], error: error.localizedDescription)
                        }
                    case .photos:
                        let list = await ImageTextIndexer.shared.search(query, platforms: photoPlatforms, chatId: chatId, before: before, limit: limit)
                        return Page(source: src, hits: list.map { Hit(platform: $0.platform, message: $0.message, imageText: $0.text) })
                    }
                }
            }
            var out: [Page] = []
            for await page in group { out.append(page) }
            return out
        }
    }

    private func absorb(_ pages: [Page]) {
        var byId = Dictionary(hits.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        for page in pages {
            if case .bridge(let p) = page.source, let e = page.error { failures[p] = e }
            if page.hits.count >= pageSize, let oldest = page.hits.map(\.message.timestamp).min() {
                open.insert(page.source)
                cursors[page.source] = oldest
            } else {
                open.remove(page.source)
            }
            for h in page.hits {
                if var existing = byId[h.id] {
                    // The same photo can match by caption and by its text; keep the live copy and the text.
                    if case .bridge = page.source { existing.message = h.message }
                    existing.imageText = existing.imageText ?? h.imageText
                    byId[h.id] = existing
                } else {
                    byId[h.id] = h
                }
            }
        }
        hits = byId.values.sorted { $0.message.timestamp > $1.message.timestamp }
    }

    /// Chats whose title has every word, across the searched platforms (global search only).
    func chatMatches(limit: Int = 6) -> [ChatRef] {
        let words = SearchText.terms(query)
        guard !words.isEmpty, chatId == nil, kind == .all else { return [] }
        return scope.flatMap { p in
            AppModel.shared.session(p).chats.filter { SearchText.contains($0.title, all: words) }
                .map { (ChatRef(platform: p, chatId: $0.id), $0.timestamp) }
        }
        .sorted { $0.1 > $1.1 }.prefix(limit).map(\.0)
    }
}

extension AppModel {
    /// Shows a search result: the platform's own UI with the chat open and scrolled to the message.
    func reveal(_ m: Message, on p: Platform) {
        rail = .platform(p)
        let s = session(p)
        if s.selectedChatId != m.chatId { s.select(m.chatId) }
        Task { await s.reveal(m) }
    }
}

// MARK: Rows

/// One message found by a search: where it is, who sent it, and the matching words.
struct SearchHitRow: View {
    let hit: MessageSearch.Hit
    let terms: [String]
    var accent: Color = .accentColor
    var showChat = true
    var selected = false
    let action: () -> Void
    @State private var hovered = false

    var body: some View {
        let m = hit.message
        let chat = AppModel.shared.chat(ChatRef(platform: hit.platform, chatId: m.chatId))
        Button(action: action) {
            HStack(alignment: .top, spacing: 10) {
                if showChat {
                    AvatarView(source: chat?.avatar, size: 34) {
                        Circle().fill(Color.gray.opacity(0.25))
                            .overlay(Text(String((chat?.title ?? "?").prefix(1)).uppercased())
                                .font(.system(size: 14, weight: .semibold)).foregroundStyle(.white))
                    }
                    .overlay(alignment: .bottomTrailing) {
                        PlatformIcon(platform: hit.platform, size: 14)
                            .background(Circle().fill(Color(nsColor: .windowBackgroundColor)).padding(-1.5))
                            .offset(x: 3, y: 3)
                    }
                }
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 4) {
                        if showChat {
                            Text(chat?.title ?? m.chatId).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                        }
                        if !showChat || sender != chat?.title {
                            Text(sender).font(.system(size: showChat ? 12 : 12.5, weight: showChat ? .regular : .semibold))
                                .foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer(minLength: 6)
                        Text(Self.when(m.date)).font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    if let body = bodyText {
                        Text(SearchText.highlighted(SearchText.snippet(body, terms: terms), terms: terms, color: accent))
                            .font(.system(size: 13)).lineLimit(2).foregroundStyle(.primary)
                    }
                    if let t = hit.imageText, !t.isEmpty, SearchText.contains(t, all: terms) {
                        HStack(alignment: .firstTextBaseline, spacing: 4) {
                            Image(systemName: "text.viewfinder").font(.system(size: 11)).foregroundStyle(accent)
                            Text(SearchText.highlighted(SearchText.snippet(t, terms: terms), terms: terms, color: accent))
                                .font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(2)
                        }
                        .help("Chữ trong ảnh")
                    }
                }
                if let thumb {
                    RemoteImage(source: thumb, maxPixel: 120) { RoundedRectangle(cornerRadius: 6).fill(Color.gray.opacity(0.15)) }
                        .frame(width: 46, height: 46).clipShape(RoundedRectangle(cornerRadius: 6))
                }
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .background(RoundedRectangle(cornerRadius: 8).fill(selected ? accent.opacity(0.14) : hovered ? Color.primary.opacity(0.05) : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
    }

    private var sender: String {
        hit.message.fromMe ? "Bạn" : (hit.message.senderName ?? "")
    }

    private var bodyText: String? {
        let m = hit.message
        let t = SearchText.haystack(m).trimmingCharacters(in: .whitespacesAndNewlines)
        if !t.isEmpty { return t }
        return hit.imageText == nil ? m.preview(photo: "Hình ảnh", video: "Video", voice: "Tin nhắn thoại", sticker: "Sticker", file: "Tệp") : nil
    }

    private var thumb: String? {
        let m = hit.message
        guard [.image, .gif, .video, .sticker].contains(m.kind) else { return nil }
        if let p = m.media?.path, FileManager.default.fileExists(atPath: p) { return p }
        return m.media?.thumbnail ?? m.media?.url
    }

    static func when(_ d: Date) -> String {
        let cal = Calendar.current
        let f = DateFormatter()
        f.locale = Locale(identifier: "vi_VN")
        if cal.isDateInToday(d) { f.dateFormat = "HH:mm" }
        else if cal.isDate(d, equalTo: Date(), toGranularity: .year) { f.dateFormat = "dd/MM" }
        else { f.dateFormat = "dd/MM/yyyy" }
        return f.string(from: d)
    }
}

/// "Tải thêm" button / spinner at the end of a result list.
struct SearchMoreFooter: View {
    let search: MessageSearch
    var accent: Color = .accentColor

    var body: some View {
        if search.loadingMore {
            ProgressView().controlSize(.small).frame(maxWidth: .infinity).padding(8)
        } else if search.canLoadMore {
            Button("Xem thêm kết quả") { Task { await search.loadMore() } }
                .buttonStyle(.plain).font(.system(size: 12.5, weight: .medium)).foregroundStyle(accent)
                .frame(maxWidth: .infinity).padding(8)
        }
    }
}

// MARK: Global search

/// ⌘K: messages, files, links and text in photos across every platform.
struct GlobalSearchOverlay: View {
    @Environment(AppModel.self) private var model
    @State private var search = MessageSearch()
    @State private var selection = 0
    @FocusState private var focused: Bool

    var body: some View {
        ZStack(alignment: .top) {
            Color.black.opacity(0.28).ignoresSafeArea()
                .onTapGesture { model.searchOpen = false }
            panel
                .frame(width: 780)
                .background(Color.white, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                .environment(\.colorScheme, .light)
                .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.primary.opacity(0.1)))
                .shadow(color: .black.opacity(0.25), radius: 30, y: 12)
                .padding(.top, 70)
        }
        .onExitCommand { model.searchOpen = false }
        .onAppear { focused = true }
        .task(id: search.signature) {
            selection = 0
            try? await Task.sleep(nanoseconds: 250_000_000)
            guard !Task.isCancelled else { return }
            await search.run()
        }
    }

    private var chats: [ChatRef] { search.chatMatches() }

    private var panel: some View {
        @Bindable var s = search
        return VStack(spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: "magnifyingglass").font(.system(size: 18)).foregroundStyle(.secondary)
                TextField("Tìm tin nhắn, tệp, liên kết và chữ trong ảnh", text: $s.query)
                    .textFieldStyle(.plain).font(.system(size: 18)).focused($focused)
                    .onSubmit { open(selection) }
                    .onKeyPress(.downArrow) { move(1); return .handled }
                    .onKeyPress(.upArrow) { move(-1); return .handled }
                if search.searching { ProgressView().controlSize(.small) }
                if !s.query.isEmpty {
                    Button { s.query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                        .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 16).frame(height: 54)
            filters.padding(.horizontal, 14).padding(.bottom, 10)
            Divider()
            results
            Divider()
            status.padding(.horizontal, 14).padding(.vertical, 8)
        }
    }

    private var filters: some View {
        HStack(spacing: 6) {
            chip("Mọi nền tảng", platform: nil, on: search.platforms.isEmpty) { search.platforms = [] }
            ForEach(Platform.allCases.filter(MessageSearch.available)) { p in
                chip(p.displayName, platform: p, on: search.platforms.contains(p)) {
                    if search.platforms.contains(p) { search.platforms.remove(p) } else { search.platforms.insert(p) }
                }
            }
            Spacer(minLength: 12)
            Rectangle().fill(Color.primary.opacity(0.12)).frame(width: 1, height: 16)
            ForEach(MessageSearch.Kind.allCases) { k in
                Button { search.kind = k } label: {
                    Label(k.title, systemImage: k.icon).labelStyle(.titleAndIcon).fixedSize()
                        .font(.system(size: 11.5, weight: search.kind == k ? .semibold : .regular))
                        .padding(.horizontal, 8).frame(height: 22)
                        .background(Capsule().fill(search.kind == k ? Color.accentColor.opacity(0.18) : .clear))
                        .foregroundStyle(search.kind == k ? Color.accentColor : .secondary)
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
    }

    private func chip(_ title: String, platform: Platform?, on: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 4) {
                if let platform { PlatformIcon(platform: platform, size: 13) }
                Text(title).font(.system(size: 11.5, weight: on ? .semibold : .regular)).fixedSize()
            }
            .padding(.horizontal, 8).frame(height: 22)
            .background(Capsule().fill(on ? Color.accentColor.opacity(0.18) : Color.primary.opacity(0.05)))
            .foregroundStyle(on ? Color.accentColor : .primary)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder private var results: some View {
        let chats = chats
        let hits = search.hits
        if search.query.trimmingCharacters(in: .whitespaces).isEmpty {
            tips
        } else if chats.isEmpty && hits.isEmpty {
            VStack(spacing: 6) {
                if search.searching || search.searchedQuery.isEmpty {
                    ProgressView()
                } else {
                    Image(systemName: "magnifyingglass").font(.system(size: 26)).foregroundStyle(.tertiary)
                    Text("Không tìm thấy kết quả cho “\(search.searchedQuery)”").font(.system(size: 13)).foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity).frame(height: 220)
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        if !chats.isEmpty {
                            header("Đoạn chat")
                            ForEach(Array(chats.enumerated()), id: \.element) { i, ref in
                                chatRow(ref, selected: selection == i).id(i)
                            }
                        }
                        if !hits.isEmpty {
                            header("Tin nhắn")
                            ForEach(Array(hits.enumerated()), id: \.element.id) { i, hit in
                                SearchHitRow(hit: hit, terms: search.terms, selected: selection == chats.count + i) {
                                    open(chats.count + i)
                                }
                                .id(chats.count + i)
                            }
                            SearchMoreFooter(search: search)
                        }
                    }
                    .padding(6)
                }
                .onChange(of: selection) { _, i in proxy.scrollTo(i) }
            }
            .frame(height: 440)
        }
    }

    private func header(_ title: String) -> some View {
        Text(title).font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            .padding(.horizontal, 10).padding(.top, 8).padding(.bottom, 2)
    }

    private func chatRow(_ ref: ChatRef, selected: Bool) -> some View {
        let chat = model.chat(ref)
        return Button { model.searchOpen = false; model.open(platform: ref.platform, chatId: ref.chatId) } label: {
            HStack(spacing: 10) {
                AvatarView(source: chat?.avatar, size: 30) {
                    Circle().fill(Color.gray.opacity(0.25))
                        .overlay(Text(String((chat?.title ?? "?").prefix(1)).uppercased()).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white))
                }
                .overlay(alignment: .bottomTrailing) {
                    PlatformIcon(platform: ref.platform, size: 13)
                        .background(Circle().fill(Color(nsColor: .windowBackgroundColor)).padding(-1.5)).offset(x: 3, y: 3)
                }
                Text(SearchText.highlighted(chat?.title ?? ref.chatId, terms: search.terms, color: .accentColor))
                    .font(.system(size: 13.5)).lineLimit(1)
                Spacer()
                Text(ref.platform.displayName).font(.system(size: 11)).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 10).padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 8).fill(selected ? Color.accentColor.opacity(0.14) : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var tips: some View {
        VStack(alignment: .leading, spacing: 10) {
            Label("Tìm theo nội dung, tên tệp hoặc chữ xuất hiện trong ảnh (hoá đơn, ảnh chụp màn hình…).", systemImage: "text.viewfinder")
            Label("Không cần gõ dấu: “hop dong” tìm được “Hợp đồng”. Nhiều từ thì tin nhắn phải chứa đủ các từ.", systemImage: "character.cursor.ibeam")
            Label("↑↓ để chọn, Enter để mở tin nhắn trong đoạn chat, Esc để đóng.", systemImage: "keyboard")
        }
        .font(.system(size: 12.5)).foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(20)
    }

    private var status: some View {
        @Bindable var ocr = ImageTextIndexer.shared
        return HStack(spacing: 8) {
            Toggle("Nhận dạng chữ trong ảnh", isOn: $ocr.enabled).toggleStyle(.switch).controlSize(.mini)
                .font(.system(size: 11.5))
            Text(ocrStatus).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
            Spacer()
            if !search.failures.isEmpty {
                Text("Lỗi tìm trên " + search.failures.keys.map(\.displayName).sorted().joined(separator: ", "))
                    .font(.system(size: 11)).foregroundStyle(.orange)
                    .help(search.failures.map { "\($0.key.displayName): \($0.value)" }.joined(separator: "\n"))
            }
        }
    }

    private var ocrStatus: String {
        let o = ImageTextIndexer.shared
        var s = "\(o.indexed) ảnh có chữ đã được lập chỉ mục"
        if o.pending > 0 { s += o.enabled ? " · đang quét \(o.pending) ảnh" : " · \(o.pending) ảnh chờ quét" }
        return s
    }

    private var count: Int { chats.count + search.hits.count }

    private func move(_ d: Int) {
        guard count > 0 else { return }
        selection = min(max(selection + d, 0), count - 1)
    }

    private func open(_ i: Int) {
        let chats = chats
        if i < chats.count {
            model.searchOpen = false
            model.open(platform: chats[i].platform, chatId: chats[i].chatId)
        } else if search.hits.indices.contains(i - chats.count) {
            let hit = search.hits[i - chats.count]
            model.searchOpen = false
            model.reveal(hit.message, on: hit.platform)
        }
    }
}

// MARK: Platform chat lists

/// Messages matching the chat-list search box, shown under the matching chats.
struct ChatListMessageResults: View {
    let session: PlatformSession
    let query: String
    var title = "Tin nhắn"
    var accent: Color = .accentColor
    @State private var search: MessageSearch

    init(session: PlatformSession, query: String, title: String = "Tin nhắn", accent: Color = .accentColor) {
        self.session = session
        self.query = query
        self.title = title
        self.accent = accent
        _search = State(initialValue: MessageSearch(platforms: [session.platform]))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(title).font(.system(size: 12.5, weight: .semibold)).foregroundStyle(.secondary)
                if search.searching { ProgressView().controlSize(.mini) }
                Spacer()
            }
            .padding(.horizontal, 16).padding(.top, 12).padding(.bottom, 4)
            if search.hits.isEmpty && !search.searching && !search.searchedQuery.isEmpty {
                Text("Không có tin nhắn phù hợp").font(.system(size: 12.5)).foregroundStyle(.secondary)
                    .padding(.horizontal, 16).padding(.vertical, 6)
            }
            ForEach(search.hits) { hit in
                SearchHitRow(hit: hit, terms: search.terms, accent: accent) {
                    session.select(hit.message.chatId)
                    Task { await session.reveal(hit.message) }
                }
                .padding(.horizontal, 6)
            }
            SearchMoreFooter(search: search, accent: accent)
        }
        .task(id: query) {
            search.query = query
            try? await Task.sleep(nanoseconds: 300_000_000)
            guard !Task.isCancelled else { return }
            await search.run()
        }
    }
}

// MARK: In-chat search

/// Search one chat's whole history (and its photos' text); picking a result scrolls the conversation to it.
struct InChatSearch: View {
    let session: PlatformSession
    let chatId: String
    var placeholder = "Tìm tin nhắn"
    @State private var search: MessageSearch
    @FocusState private var focused: Bool
    @Environment(\.dismiss) private var dismiss

    init(session: PlatformSession, chatId: String, placeholder: String = "Tìm tin nhắn") {
        self.session = session
        self.chatId = chatId
        self.placeholder = placeholder
        _search = State(initialValue: MessageSearch(platforms: [session.platform], chatId: chatId))
    }

    var body: some View {
        @Bindable var s = search
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                TextField(placeholder, text: $s.query).textFieldStyle(.roundedBorder).focused($focused)
                if search.searching { ProgressView().controlSize(.small) }
            }
            Picker("", selection: $s.kind) {
                ForEach(MessageSearch.Kind.allCases) { k in Text(k.title).tag(k) }
            }
            .pickerStyle(.segmented).labelsHidden()
            if !search.searchedQuery.isEmpty {
                Text(search.hits.isEmpty ? "Không tìm thấy" : "\(search.hits.count)\(search.canLoadMore ? "+" : "") kết quả")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if !search.hits.isEmpty {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(search.hits) { hit in
                            SearchHitRow(hit: hit, terms: search.terms, showChat: false) {
                                dismiss()
                                Task { await session.reveal(hit.message) }
                            }
                        }
                        SearchMoreFooter(search: search)
                    }
                }
                .frame(height: 300)
            }
        }
        .padding(12)
        .frame(width: 360)
        .preferredColorScheme(.light)
        .onAppear { focused = true }
        .task(id: search.signature) {
            try? await Task.sleep(nanoseconds: 250_000_000)
            guard !Task.isCancelled else { return }
            await search.run()
        }
    }
}
