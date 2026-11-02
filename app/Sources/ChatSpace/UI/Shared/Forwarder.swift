import AppKit
import ImageIO
import SwiftUI
import UniformTypeIdentifiers

/// Forwards one message to any number of chats on any platform. Within a platform that has capability
/// `forward` the platform's own forward is used (its "Forwarded" label, nothing re-uploaded); everywhere
/// else the text is sent again and the media file is downloaded from the source and uploaded to the target.
@MainActor
@Observable
final class Forwarder {
    static let shared = Forwarder()

    enum Status: Equatable {
        case sending, sent
        case failed(String)
    }

    private(set) var message: Message?
    private(set) var sourcePlatform: Platform?
    private(set) var picked: [ChatRef] = []
    private(set) var status: [ChatRef: Status] = [:]
    private(set) var running = false
    var note = ""
    var query = ""
    var filter: Platform? {
        didSet { if filter != .telegram { telegramFolder = nil } }
    }
    /// Telegram folder the list is narrowed to, as in Telegram's own forward dialog.
    var telegramFolder: String?
    /// Chat activity when the picker opened: the list keeps that order while the forwards themselves
    /// bump the picked chats to the top of their platforms.
    private(set) var recency: [ChatRef: Double] = [:]

    @ObservationIgnored private var window: NSPanel?
    @ObservationIgnored private var prepared: Task<PreparedFile, Error>?
    @ObservationIgnored private var noteSent: Set<ChatRef> = []

    static func canForward(_ m: Message) -> Bool {
        guard !m.isDeleted, m.status != .pending, m.status != .failed else { return false }
        switch m.kind {
        case .system, .call, .unsupported: return false
        default: return carriesFile(m) || !(m.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
    }

    private static func carriesFile(_ m: Message) -> Bool {
        guard m.media != nil else { return false }
        switch m.kind {
        case .image, .video, .gif, .audio, .voice, .file, .sticker: return true
        default: return false
        }
    }

    // MARK: Window

    func show(_ message: Message, from session: PlatformSession) {
        guard Self.canForward(message) else { return }
        if !running {
            self.message = message
            sourcePlatform = session.platform
            picked = []
            status = [:]
            note = ""
            query = ""
            filter = nil
            telegramFolder = nil
            noteSent = []
            prepared = nil
            recency = [:]
            for p in Platform.allCases {
                for c in AppModel.shared.session(p).chats { recency[ChatRef(platform: p, chatId: c.id)] = c.timestamp }
            }
        }
        let panel = window ?? makeWindow()
        window = panel
        // Above the floating chat bubble when opened from there.
        panel.level = ChatHeads.shared.expanded != nil ? .floating : .normal
        if !panel.isVisible { panel.center() }
        panel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func close() {
        window?.orderOut(nil)
    }

    private func makeWindow() -> NSPanel {
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 440, height: 620),
                        styleMask: [.titled, .closable, .resizable],
                        backing: .buffered, defer: false)
        p.identifier = ChatHeads.keepsChatOpenIdentifier
        p.title = "Chuyển tiếp tin nhắn"
        p.isReleasedWhenClosed = false
        p.hidesOnDeactivate = false
        p.becomesKeyOnlyIfNeeded = false
        p.minSize = NSSize(width: 380, height: 460)
        p.collectionBehavior = [.moveToActiveSpace]
        p.contentView = NSHostingView(rootView: ForwardView(forwarder: self))
        return p
    }

    // MARK: Picking and sending

    func toggle(_ ref: ChatRef) {
        guard !running, status[ref] != .sent else { return }
        if let i = picked.firstIndex(of: ref) {
            picked.remove(at: i)
            status[ref] = nil
        } else {
            picked.append(ref)
        }
    }

    /// Picked chats that still need sending (not yet sent, or failed).
    var outstanding: [ChatRef] { picked.filter { status[$0] != .sent } }
    var hasFailures: Bool { status.values.contains { if case .failed = $0 { return true }; return false } }

    func send() {
        guard let message, let sourcePlatform, !running else { return }
        let targets = outstanding
        guard !targets.isEmpty else { return }
        let source = AppModel.shared.session(sourcePlatform)
        let note = note.trimmingCharacters(in: .whitespacesAndNewlines)
        running = true
        for t in targets { status[t] = .sending }
        Task {
            // One after another: a burst of parallel sends looks like spam to the platforms.
            for t in targets {
                do {
                    try await deliver(message, from: source, to: t, note: note)
                    status[t] = .sent
                } catch {
                    status[t] = .failed(error.localizedDescription)
                }
            }
            running = false
            guard picked.allSatisfy({ status[$0] == .sent }) else { return }
            try? await Task.sleep(nanoseconds: 700_000_000)
            if !running, self.message?.id == message.id, picked.allSatisfy({ status[$0] == .sent }) { close() }
        }
    }

    private func deliver(_ m: Message, from source: PlatformSession, to ref: ChatRef, note: String) async throws {
        let target = AppModel.shared.session(ref.platform)
        guard target.authState == .loggedIn else { throw ForwardError("\(ref.platform.displayName) chưa đăng nhập") }
        if !note.isEmpty && !noteSent.contains(ref) {
            try await target.post(note, in: ref.chatId)
            noteSent.insert(ref)
        }
        if ref.platform == source.platform && source.can("forward") {
            try await source.forwardNatively(m, to: ref.chatId)
            return
        }
        let text = (m.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard Self.carriesFile(m) else {
            try await target.post(text, in: ref.chatId)
            return
        }
        let file = try await preparedFile(m, from: source)
        // A sticker's text is its emoji and a voice note's a transcript: neither is a caption.
        let caption = m.kind == .sticker || m.kind == .voice ? "" : text
        try await target.post(caption, in: ref.chatId, filePath: file.path, kind: file.kind)
    }

    /// The media is fetched once per forward, however many chats it goes to.
    private func preparedFile(_ m: Message, from source: PlatformSession) async throws -> PreparedFile {
        if let prepared { return try await prepared.value }
        let task = Task { try await ForwardFile.prepare(m, from: source) }
        prepared = task
        do {
            return try await task.value
        } catch {
            prepared = nil
            throw error
        }
    }
}

struct ForwardError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

struct PreparedFile: Sendable {
    let path: String
    let kind: MessageKind
}

/// Turns a message's media into a local file another platform will accept: the original file name, and
/// photos as JPEG/PNG (cached WhatsApp/Telegram photos may be HEIC, stickers WebP, which other platforms
/// would deliver as a document).
enum ForwardFile {
    static let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ChatSpace-forward", isDirectory: true)

    @MainActor
    static func prepare(_ m: Message, from session: PlatformSession) async throws -> PreparedFile {
        guard let source = await session.mediaSource(m) else {
            throw ForwardError("Không tải được tệp gốc từ \(session.platform.displayName)")
        }
        let media = m.media ?? Media()
        let videoGif = m.kind == .gif && ((media.mime ?? "").hasPrefix("video") || source.lowercased().contains(".mp4"))
        let kind: MessageKind = switch m.kind {
        case .image, .sticker: .image
        case .gif: videoGif ? .video : .image
        case .video: .video
        case .voice: .voice
        case .audio: .audio
        default: .file
        }
        let stamp = m.date.formatted(.iso8601.year().month().day().time(includingFractionalSeconds: false))
            .replacingOccurrences(of: ":", with: "")
        let baseName = media.fileName?.trimmingCharacters(in: .whitespacesAndNewlines).nonEmpty ?? "ChatSpace \(stamp)"
        let mimeExt = media.mime.flatMap { UTType(mimeType: $0)?.preferredFilenameExtension }
        let fallbackExt = switch kind {
        case .image: "jpg"
        case .video: "mp4"
        case .voice: "ogg"
        case .audio: "m4a"
        default: "bin"
        }
        let path = try await Task.detached(priority: .userInitiated) {
            let local = try await fetch(source)
            let dir = directory.appendingPathComponent(UUID().uuidString, isDirectory: true)
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let ext = [(baseName as NSString).pathExtension, local.pathExtension, mimeExt ?? ""]
                .map { $0.lowercased() }.first { (1...5).contains($0.count) } ?? fallbackExt
            if kind == .image, let out = try writeImage(local, in: dir, stem: stem(baseName)) { return out.path }
            let name = (baseName as NSString).pathExtension.isEmpty ? "\(safe(baseName)).\(ext)" : safe(baseName)
            let dest = dir.appendingPathComponent(name)
            try FileManager.default.copyItem(at: local, to: dest)
            return dest.path
        }.value
        return PreparedFile(path: path, kind: kind)
    }

    /// Local file for a path or an http URL (Zalo and Messenger serve media straight from their CDN).
    private static func fetch(_ source: String) async throws -> URL {
        if source.hasPrefix("/") { return URL(fileURLWithPath: source) }
        guard let url = URL(string: source) else { throw ForwardError("Đường dẫn tệp không hợp lệ") }
        if url.isFileURL { return url }
        let (tmp, response) = try await URLSession.shared.download(from: url)
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw ForwardError("Tải tệp gốc thất bại (HTTP \(http.statusCode))")
        }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let ext = url.pathExtension.count <= 5 ? url.pathExtension : ""
        let kept = directory.appendingPathComponent(UUID().uuidString).appendingPathExtension(ext)
        try FileManager.default.moveItem(at: tmp, to: kept)
        return kept
    }

    /// JPEG, PNG and GIF go as they are; any other image format is re-encoded (PNG when it has transparency).
    /// Nil when the file is not a decodable image, so it is sent as a plain file instead.
    private static func writeImage(_ local: URL, in dir: URL, stem: String) throws -> URL? {
        guard let src = CGImageSourceCreateWithURL(local as CFURL, nil),
              let typeId = CGImageSourceGetType(src) as String?, let type = UTType(typeId) else { return nil }
        if [UTType.jpeg, .png, .gif].contains(where: type.conforms(to:)) {
            let dest = dir.appendingPathComponent(stem).appendingPathExtension(type.preferredFilenameExtension ?? "jpg")
            try FileManager.default.copyItem(at: local, to: dest)
            return dest
        }
        guard let image = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return nil }
        let alpha = ![.none, .noneSkipFirst, .noneSkipLast].contains(image.alphaInfo)
        let out: UTType = alpha ? .png : .jpeg
        let dest = dir.appendingPathComponent(stem).appendingPathExtension(alpha ? "png" : "jpg")
        guard let writer = CGImageDestinationCreateWithURL(dest as CFURL, out.identifier as CFString, 1, nil) else { return nil }
        let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any]
        var options: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: 0.9]
        if let o = props?[kCGImagePropertyOrientation] { options[kCGImagePropertyOrientation] = o }
        CGImageDestinationAddImage(writer, image, options as CFDictionary)
        return CGImageDestinationFinalize(writer) ? dest : nil
    }

    private static func stem(_ name: String) -> String {
        let s = safe((name as NSString).deletingPathExtension)
        return s.isEmpty ? "image" : s
    }

    private static func safe(_ name: String) -> String {
        name.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
    }
}

private extension String {
    var nonEmpty: String? { isEmpty ? nil : self }
}

// MARK: Picker

struct ForwardView: View {
    let forwarder: Forwarder
    @FocusState private var searchFocused: Bool

    var body: some View {
        @Bindable var f = forwarder
        VStack(spacing: 0) {
            if let m = f.message, let p = f.sourcePlatform { ForwardPreview(message: m, platform: p).padding(12) }
            VStack(spacing: 8) {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Tìm cuộc trò chuyện trên mọi nền tảng", text: $f.query)
                        .textFieldStyle(.plain).focused($searchFocused)
                    if !f.query.isEmpty {
                        Button { f.query = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                            .buttonStyle(.plain)
                    }
                }
                .padding(.horizontal, 10).frame(height: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.primary.opacity(0.06)))
                platformFilter
                if showsTelegramFolders { telegramFolders }
            }
            .padding(.horizontal, 12).padding(.bottom, 8)
            if !f.picked.isEmpty { pickedStrip }
            Divider()
            chatList
            Divider()
            footer
        }
        .frame(minWidth: 380, minHeight: 460)
        .onAppear { searchFocused = true }
        .onExitCommand { forwarder.close() }
    }

    private var loggedIn: [Platform] {
        Platform.allCases.filter { AppModel.shared.session($0).authState == .loggedIn }
    }

    private var platformFilter: some View {
        HStack(spacing: 6) {
            chip("Tất cả", icon: nil, on: forwarder.filter == nil) { forwarder.filter = nil }
            ForEach(loggedIn, id: \.self) { p in
                chip(p.displayName, icon: p, on: forwarder.filter == p) { forwarder.filter = forwarder.filter == p ? nil : p }
            }
            Spacer(minLength: 0)
        }
    }

    private func chip(_ title: String, icon: Platform?, on: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 4) {
                if let icon { PlatformIcon(platform: icon, size: 13) }
                Text(title).font(.system(size: 11.5, weight: on ? .semibold : .regular))
            }
            .padding(.horizontal, 8).frame(height: 22)
            .background(Capsule().fill(on ? Color.accentColor.opacity(0.18) : Color.primary.opacity(0.05)))
            .foregroundStyle(on ? Color.accentColor : .primary)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }

    private var telegram: PlatformSession { AppModel.shared.session(.telegram) }

    /// Telegram's folder tabs, once Telegram is picked (or with every platform shown, picking a folder picks Telegram).
    private var showsTelegramFolders: Bool {
        (forwarder.filter == nil || forwarder.filter == .telegram) && loggedIn.contains(.telegram) && !telegram.folders.isEmpty
    }

    private var telegramFolders: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 2) {
                folderTab(nil, title: forwarder.filter == .telegram ? "Tất cả chat" : "Thư mục Telegram")
                ForEach(telegram.folders) { f in
                    folderTab(f.id, title: [f.emoticon, f.title].compactMap { $0 }.joined(separator: " "))
                }
            }
            .padding(.horizontal, 2)
        }
        .overlay(alignment: .bottom) { Rectangle().fill(Color.primary.opacity(0.08)).frame(height: 1) }
    }

    private func folderTab(_ id: String?, title: String) -> some View {
        let on = forwarder.filter == .telegram && forwarder.telegramFolder == id
        let badge = id.map { id in telegram.folderBadge(telegram.folders.first { $0.id == id }) }
        return Button {
            forwarder.filter = .telegram
            forwarder.telegramFolder = id
        } label: {
            HStack(spacing: 4) {
                if id == nil && forwarder.filter != .telegram { PlatformIcon(platform: .telegram, size: 12) }
                Text(title).font(.system(size: 12, weight: on ? .semibold : .regular)).lineLimit(1)
                if let b = badge, b.count > 0 {
                    Text("\(b.count)").font(.system(size: 10, weight: .semibold)).foregroundStyle(.white)
                        .padding(.horizontal, 4).frame(minWidth: 16, minHeight: 16)
                        .background(Capsule().fill(b.muted ? Color.secondary.opacity(0.6) : Color(hex: 0x3390EC)))
                }
            }
            .foregroundStyle(on ? Color(hex: 0x3390EC) : .secondary)
            .padding(.horizontal, 8).frame(height: 28)
            .overlay(alignment: .bottom) {
                if on { Capsule().fill(Color(hex: 0x3390EC)).frame(height: 2.5).padding(.horizontal, 4) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(id == nil ? "Mọi đoạn chat Telegram" : "Chỉ các đoạn chat trong thư mục \(title)")
    }

    private var candidates: [(ref: ChatRef, chat: Chat)] {
        let needle = CustomGroup.normalize(forwarder.query)
        if forwarder.filter == .telegram, let folder = forwarder.telegramFolder {
            // Telegram's own order inside a folder: its pins first, then recent activity.
            return telegram.chats(inFolder: folder)
                .filter { needle.isEmpty || CustomGroup.normalize($0.title).contains(needle) }
                .map { (ref: ChatRef(platform: .telegram, chatId: $0.id), chat: $0) }
        }
        let platforms = loggedIn.filter { forwarder.filter == nil || forwarder.filter == $0 }
        let all = platforms.flatMap { p in AppModel.shared.session(p).chats.map { (ref: ChatRef(platform: p, chatId: $0.id), chat: $0) } }
        return Array(all.filter { needle.isEmpty || CustomGroup.normalize($0.chat.title).contains(needle) }
            .sorted { (forwarder.recency[$0.ref] ?? $0.chat.timestamp) > (forwarder.recency[$1.ref] ?? $1.chat.timestamp) }
            .prefix(300))
    }

    private var chatList: some View {
        let list = candidates
        return ScrollView {
            LazyVStack(spacing: 0) {
                ForEach(list, id: \.ref) { item in
                    ForwardRow(ref: item.ref, chat: item.chat, picked: forwarder.picked.contains(item.ref),
                               status: forwarder.status[item.ref]) { forwarder.toggle(item.ref) }
                }
            }
            .padding(.vertical, 4).padding(.horizontal, 6)
        }
        .overlay {
            if list.isEmpty {
                Text(loggedIn.isEmpty ? "Chưa đăng nhập nền tảng nào" : "Không có cuộc trò chuyện phù hợp")
                    .font(.system(size: 13)).foregroundStyle(.secondary)
            }
        }
    }

    private var pickedStrip: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(forwarder.picked, id: \.self) { ref in
                    let title = AppModel.shared.session(ref.platform).chats.first { $0.id == ref.chatId }?.title ?? ref.chatId
                    Button { forwarder.toggle(ref) } label: {
                        HStack(spacing: 4) {
                            PlatformIcon(platform: ref.platform, size: 13)
                            Text(title).font(.system(size: 11.5)).lineLimit(1)
                            if !forwarder.running && forwarder.status[ref] != .sent {
                                Image(systemName: "xmark").font(.system(size: 8, weight: .bold)).foregroundStyle(.secondary)
                            }
                        }
                        .padding(.horizontal, 8).frame(height: 22)
                        .background(Capsule().fill(Color.accentColor.opacity(0.12)))
                        .contentShape(Capsule())
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 12)
        }
        .padding(.bottom, 8)
    }

    private var crossPlatform: Bool {
        guard let source = forwarder.sourcePlatform else { return false }
        let native = AppModel.shared.session(source).can("forward")
        return forwarder.picked.contains { $0.platform != source || !native }
    }

    private var footer: some View {
        @Bindable var f = forwarder
        let n = f.outstanding.count
        return VStack(alignment: .leading, spacing: 6) {
            if crossPlatform {
                Text("Nội dung và tệp sẽ được gửi lại như tin nhắn mới ở những nơi không hỗ trợ chuyển tiếp gốc.")
                    .font(.system(size: 11)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 8) {
                TextField("Thêm lời nhắn (không bắt buộc)", text: $f.note)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { f.send() }
                Button {
                    f.send()
                } label: {
                    Text(f.running ? "Đang gửi…" : f.hasFailures && n > 0 ? "Gửi lại (\(n))" : n > 0 ? "Gửi (\(n))" : "Gửi")
                        .frame(minWidth: 70)
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
                .disabled(n == 0 || f.running)
            }
        }
        .padding(12)
    }
}

private struct ForwardPreview: View {
    let message: Message
    let platform: Platform

    var body: some View {
        let media = message.media
        HStack(spacing: 10) {
            Group {
                if [.image, .sticker, .gif, .video].contains(message.kind),
                   let src = media?.path ?? media?.url ?? media?.thumbnail {
                    RemoteImage(source: src, maxPixel: 96) { Color.gray.opacity(0.2) }
                        .frame(width: 44, height: 44).clipShape(RoundedRectangle(cornerRadius: 6))
                } else if media != nil {
                    RoundedRectangle(cornerRadius: 6).fill(Color.accentColor.opacity(0.15))
                        .overlay(Image(systemName: icon).foregroundStyle(Color.accentColor))
                        .frame(width: 44, height: 44)
                }
            }
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    PlatformIcon(platform: platform, size: 13)
                    Text(message.fromMe ? "Tin nhắn của bạn" : "Tin nhắn của \(message.senderName ?? "người gửi")")
                        .font(.system(size: 11.5, weight: .semibold)).foregroundStyle(.secondary).lineLimit(1)
                }
                Text(message.preview(photo: "Hình ảnh", video: "Video", voice: "Tin nhắn thoại", sticker: "Sticker", file: "Tệp"))
                    .font(.system(size: 13)).lineLimit(2)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(0.05)))
        .overlay(alignment: .leading) {
            RoundedRectangle(cornerRadius: 1.5).fill(Color.accentColor).frame(width: 3).padding(.vertical, 8)
        }
    }

    private var icon: String {
        switch message.kind {
        case .voice, .audio: "waveform"
        case .video, .gif: "play.rectangle.fill"
        default: "doc.fill"
        }
    }
}

private struct ForwardRow: View {
    let ref: ChatRef
    let chat: Chat
    let picked: Bool
    let status: Forwarder.Status?
    let toggle: () -> Void
    @State private var hovered = false

    var body: some View {
        Button(action: toggle) {
            HStack(spacing: 10) {
                AvatarView(source: chat.avatar, size: 36) {
                    Circle().fill(Color.gray.opacity(0.25))
                        .overlay(Text(String(chat.title.prefix(1)).uppercased()).font(.system(size: 15, weight: .semibold)).foregroundStyle(.white))
                }
                .overlay(alignment: .bottomTrailing) {
                    PlatformIcon(platform: ref.platform, size: 14)
                        .background(Circle().fill(Color(nsColor: .windowBackgroundColor)).padding(-1.5))
                        .offset(x: 3, y: 3)
                }
                .onAppear { AppModel.shared.session(ref.platform).requestAvatar(for: chat) }
                VStack(alignment: .leading, spacing: 2) {
                    Text(chat.title).font(.system(size: 13.5)).lineLimit(1)
                    Text(subtitle).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 6)
                trailing
            }
            .padding(.horizontal, 8).padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 8).fill(picked ? Color.accentColor.opacity(0.10) : hovered ? Color.primary.opacity(0.05) : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
    }

    private var subtitle: String {
        let kind = switch chat.kind {
        case .direct: "Cá nhân"
        case .group: chat.memberCount.map { "Nhóm · \($0) thành viên" } ?? "Nhóm"
        case .channel: "Kênh"
        case .bot: "Bot"
        case .self: "Tin nhắn đã lưu"
        }
        return ref.platform.displayName + " · " + kind
    }

    @ViewBuilder private var trailing: some View {
        switch status {
        case .sending:
            ProgressView().controlSize(.small).frame(width: 20)
        case .sent:
            Image(systemName: "checkmark.circle.fill").font(.system(size: 18)).foregroundStyle(.green).help("Đã gửi")
        case .failed(let e):
            Image(systemName: "exclamationmark.circle.fill").font(.system(size: 18)).foregroundStyle(.red).help(e)
        case nil:
            Image(systemName: picked ? "checkmark.circle.fill" : "circle")
                .font(.system(size: 18)).foregroundStyle(picked ? Color.accentColor : Color.secondary.opacity(0.5))
        }
    }
}
