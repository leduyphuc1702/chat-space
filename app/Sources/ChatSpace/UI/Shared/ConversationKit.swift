import AppKit
import SwiftUI

/// A message with the layout facts every platform UI needs.
struct MessageRow: Identifiable {
    let message: Message
    let isFirstInGroup: Bool
    let isLastInGroup: Bool
    let showDayHeader: Bool
    /// Messenger/Zalo show a centered time when there's a long gap.
    let showTimeGap: Bool
    var id: String { message.id }
}

enum MessageGrouping {
    static func rows(_ messages: [Message], groupWindow: TimeInterval = 5 * 60, gap: TimeInterval = 15 * 60) -> [MessageRow] {
        var out: [MessageRow] = []
        out.reserveCapacity(messages.count)
        for (i, m) in messages.enumerated() {
            let prev = i > 0 ? messages[i - 1] : nil
            let next = i + 1 < messages.count ? messages[i + 1] : nil
            let newDay = prev.map { Fmt.dayKey($0.date) != Fmt.dayKey(m.date) } ?? true
            let bigGap = prev.map { m.date.timeIntervalSince($0.date) > gap } ?? true
            func sameGroup(_ a: Message?, _ b: Message) -> Bool {
                guard let a, a.kind != .system, b.kind != .system else { return false }
                return a.senderId == b.senderId && a.fromMe == b.fromMe
                    && abs(b.date.timeIntervalSince(a.date)) < groupWindow
                    && Fmt.dayKey(a.date) == Fmt.dayKey(b.date)
            }
            out.append(MessageRow(
                message: m,
                isFirstInGroup: !sameGroup(prev, m) || newDay,
                isLastInGroup: next.map { !sameGroup(m, $0) || Fmt.dayKey($0.date) != Fmt.dayKey(m.date) } ?? true,
                showDayHeader: newDay,
                showTimeGap: bigGap))
        }
        return out
    }

    static func dayTitle(_ d: Date, today: String, yesterday: String, locale: Locale, format: String) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(d) { return today }
        if cal.isDateInYesterday(d) { return yesterday }
        let f = DateFormatter()
        f.locale = locale
        f.dateFormat = cal.isDate(d, equalTo: Date(), toGranularity: .year) ? format : format + ", yyyy"
        return f.string(from: d)
    }
}

/// Bottom-anchored message list with lazy history loading, shared by all platforms.
struct ChatScroll<Content: View>: View {
    let session: PlatformSession
    let chatId: String
    var spacing: CGFloat = 0
    var padding = EdgeInsets(top: 8, leading: 0, bottom: 8, trailing: 0)
    @ViewBuilder var content: ([MessageRow]) -> Content
    /// Older pages load only after the chat has landed on its newest message; before that the top of
    /// the lazy stack is briefly on screen and would page in history, leaving the view somewhere random.
    @State private var settled = false
    /// Whether the reader is at the newest message; arrivals only follow then, so reading history isn't interrupted.
    @State private var atBottom = true

    var body: some View {
        let messages = session.messages[chatId] ?? []
        let rows = MessageGrouping.rows(messages)
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: spacing) {
                    if settled && !session.reachedStart.contains(chatId) && !messages.isEmpty {
                        ProgressView().controlSize(.small).padding(8)
                            .onAppear { session.loadOlder(chatId) }
                    }
                    content(rows)
                    Color.clear.frame(height: 1).id("bottom")
                }
                .padding(padding)
            }
            .defaultScrollAnchor(.bottom)
            .modifier(KeepBottomOnResize())
            .modifier(TrackBottom(atBottom: $atBottom))
            .onChange(of: messages.last?.id) { _, _ in
                // Your own sends always jump down; arrivals follow while you're at the newest message.
                guard !settled || atBottom || messages.last?.fromMe == true else { return }
                Task {
                    // The new row gets its real height a layout pass or two later (images, link previews).
                    for ms in [0, 120] as [UInt64] {
                        try? await Task.sleep(nanoseconds: ms * 1_000_000)
                        withAnimation(.easeOut(duration: 0.15)) { proxy.scrollTo("bottom", anchor: .bottom) }
                    }
                    atBottom = true
                }
            }
            .task(id: chatId) {
                settled = false
                atBottom = true
                // A search result opened in a chat that was not on screen yet lands on that message instead.
                let target = session.scrollTarget[chatId]
                if target != nil { atBottom = false }
                // Lazy rows get their real heights over a few layout passes after the first scroll.
                for ms in [0, 80, 250, 600] as [UInt64] {
                    try? await Task.sleep(nanoseconds: ms * 1_000_000)
                    if Task.isCancelled { return }
                    if let target {
                        proxy.scrollTo(target, anchor: .center)
                    } else if !messages.isEmpty || ms == 600 {
                        proxy.scrollTo("bottom", anchor: .bottom)
                    }
                }
                if session.messages[chatId]?.isEmpty == false { settled = true }
                if target != nil, session.scrollTarget[chatId] == target { session.scrollTarget[chatId] = nil }
            }
            .onChange(of: messages.isEmpty) { _, empty in
                guard !empty, !settled, session.scrollTarget[chatId] == nil else { return }
                Task {
                    for ms in [0, 80, 250] as [UInt64] {
                        try? await Task.sleep(nanoseconds: ms * 1_000_000)
                        proxy.scrollTo("bottom", anchor: .bottom)
                    }
                    settled = true
                }
            }
            .onChange(of: session.scrollTarget[chatId]) { _, id in
                guard let id else { return }
                atBottom = false
                Task {
                    // Rows above the target settle their heights after the first jump (it may be pages back).
                    for ms in [0, 150, 400] as [UInt64] {
                        try? await Task.sleep(nanoseconds: ms * 1_000_000)
                        guard session.scrollTarget[chatId] == id else { return }
                        withAnimation(.easeInOut(duration: ms == 0 ? 0.25 : 0.12)) { proxy.scrollTo(id, anchor: .center) }
                    }
                    settled = true
                }
            }
            .overlay {
                if messages.isEmpty && session.loadingHistory.contains(chatId) {
                    ProgressView()
                }
            }
        }
    }
}

extension View {
    /// Flashes the row a search result just jumped to.
    func revealHighlight(_ session: PlatformSession, chatId: String, id: String) -> some View {
        modifier(RevealHighlight(on: session.highlighted[chatId] == id))
    }
}

private struct RevealHighlight: ViewModifier {
    let on: Bool

    func body(content: Content) -> some View {
        content.background(Color(hex: 0xFFD60A).opacity(on ? 0.3 : 0).animation(.easeInOut(duration: 0.45), value: on))
    }
}

/// Images loading and older pages arriving change the content height; anchoring those changes to the
/// bottom keeps the messages on screen where they are instead of drifting.
private struct KeepBottomOnResize: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 15.0, *) {
            content.defaultScrollAnchor(.bottom, for: .sizeChanges)
        } else {
            content
        }
    }
}

/// Tracks whether the scroll view shows its last rows. Only scrolling clears the flag: content growing
/// under a reader who is at the bottom (the new message itself) must not count as leaving it.
private struct TrackBottom: ViewModifier {
    @Binding var atBottom: Bool

    private struct Edge: Equatable {
        var height: CGFloat
        var near: Bool
    }

    func body(content: Content) -> some View {
        if #available(macOS 15.0, *) {
            content.onScrollGeometryChange(for: Edge.self) { g in
                Edge(height: g.contentSize.height, near: g.visibleRect.maxY >= g.contentSize.height - 80)
            } action: { old, new in
                if new.near { atBottom = true } else if old.height == new.height { atBottom = false }
            }
        } else {
            content
        }
    }
}

/// Caps the width offered to a bubble without stretching it to the cap the way `.frame(maxWidth:)`
/// does, so hover controls laid out beside a short bubble stay right next to it.
struct CapWidth: Layout {
    var max: CGFloat

    init(_ max: CGFloat) { self.max = max }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let s = subviews.first else { return .zero }
        let w = proposal.width.map { Swift.min($0, max) } ?? max
        return s.sizeThatFits(ProposedViewSize(width: w, height: proposal.height))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        subviews.first?.place(at: bounds.origin, proposal: ProposedViewSize(bounds.size))
    }
}

/// Leading-aligned bubble content where every child gets the width of the widest one. Children marked
/// `.bubbleFill()` (reply quotes) count only their natural width, so a quote spans the bubble without
/// widening a short reply to the whole chat.
struct BubbleColumn: Layout {
    var spacing: CGFloat = 4

    private func width(_ proposal: ProposedViewSize, _ subviews: Subviews) -> CGFloat {
        let cap = proposal.width ?? .infinity
        return subviews.reduce(0) { w, s in
            let size = s[BubbleFill.self] ? s.sizeThatFits(.unspecified) : s.sizeThatFits(ProposedViewSize(width: proposal.width, height: nil))
            return Swift.max(w, Swift.min(size.width, cap))
        }
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let w = width(proposal, subviews)
        let heights = subviews.map { $0.sizeThatFits(ProposedViewSize(width: w, height: nil)).height }
        return CGSize(width: w, height: heights.reduce(0, +) + spacing * CGFloat(Swift.max(subviews.count - 1, 0)))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for s in subviews {
            let p = ProposedViewSize(width: bounds.width, height: nil)
            let h = s.sizeThatFits(p).height
            s.place(at: CGPoint(x: bounds.minX, y: y), proposal: ProposedViewSize(width: bounds.width, height: h))
            y += h + spacing
        }
    }
}

/// `CapWidth` as a modifier, for bubbles built as a `Group`.
struct CapWidthModifier: ViewModifier {
    let max: CGFloat
    func body(content: Content) -> some View { CapWidth(max) { content } }
}

private struct BubbleFill: LayoutValueKey {
    static let defaultValue = false
}

extension View {
    /// Inside a `BubbleColumn`: stretch to the bubble's width without setting it.
    func bubbleFill() -> some View {
        frame(maxWidth: .infinity, alignment: .leading).layoutValue(key: BubbleFill.self, value: true)
    }

    /// The emoji strip each app opens from its reaction button or an existing reaction.
    func reactionPicker(_ isPresented: Binding<Bool>, session: PlatformSession, message: Message, emojis: [String]) -> some View {
        popover(isPresented: isPresented, arrowEdge: .top) {
            ReactionPicker(session: session, message: message, emojis: emojis) { isPresented.wrappedValue = false }
        }
    }
}

enum ReactionSets {
    static let zalo = ["❤️", "👍", "😆", "😮", "😭", "😡"]
    static let messenger = ["❤️", "😆", "😮", "😢", "😠", "👍"]
    static let whatsApp = ["👍", "❤️", "😂", "😮", "😢", "🙏"]
    static let telegram = ["👍", "❤️", "🔥", "🥰", "👏", "😁", "🤔", "😢"]
}

struct ReactionPicker: View {
    let session: PlatformSession
    let message: Message
    let emojis: [String]
    var done: () -> Void

    var body: some View {
        let mine = message.reactions?.first(where: \.fromMe)?.emoji
        let others = (message.reactions ?? []).filter { $0.count > 0 }
        VStack(spacing: 4) {
            HStack(spacing: 2) {
                ForEach(emojis, id: \.self) { e in
                    ReactionChoice(emoji: e, selected: mine == e) {
                        session.react(message, emoji: e)
                        done()
                    }
                }
            }
            if !others.isEmpty {
                HStack(spacing: 8) {
                    ForEach(others, id: \.emoji) { r in
                        Text("\(r.emoji) \(r.count)").font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    if mine != nil {
                        Text("· Bấm lại để gỡ").font(.system(size: 11)).foregroundStyle(.tertiary)
                    }
                }
            }
        }
        .padding(6)
        .focusEffectDisabled()
        .preferredColorScheme(.light)
    }
}

private struct ReactionChoice: View {
    let emoji: String
    let selected: Bool
    let action: () -> Void
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            Text(emoji).font(.system(size: 24))
                .scaleEffect(hovered ? 1.25 : 1)
                .frame(width: 38, height: 38)
                .background(Circle().fill(selected ? Color.accentColor.opacity(0.2) : (hovered ? Color.primary.opacity(0.06) : .clear)))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { h in withAnimation(.easeOut(duration: 0.12)) { hovered = h } }
    }
}

/// Right-click actions shared by every platform (labels are localized per platform). Built in AppKit
/// and caught above the bubble, because selectable message text swallows SwiftUI's `.contextMenu`.
extension View {
    func messageContextMenu(session: PlatformSession, message: Message, labels: MenuLabels = .english,
                            reactions: [String] = ReactionSets.whatsApp) -> some View {
        overlay(ContextClickCatcher { MessageMenu.build(session: session, message: message, labels: labels, reactions: reactions) })
    }
}

enum MessageMenu {
    @MainActor
    static func build(session: PlatformSession, message: Message, labels: MenuLabels, reactions: [String]) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        if !message.isDeleted && session.can("reply") {
            menu.addItem(ClosureMenuItem(labels.reply) { session.startReply(message) })
        }
        if session.canEdit(message) {
            menu.addItem(ClosureMenuItem(labels.edit) { session.beginEdit(message) })
        }
        if !message.isDeleted && session.can("reactions") {
            let sub = NSMenu()
            for e in reactions {
                let item = ClosureMenuItem(e) { session.react(message, emoji: e) }
                item.state = message.reactions?.contains { $0.fromMe && $0.emoji == e } == true ? .on : .off
                sub.addItem(item)
            }
            let item = NSMenuItem(title: labels.react, action: nil, keyEquivalent: "")
            item.submenu = sub
            menu.addItem(item)
        }
        if Translator.canTranslate(message) {
            let shown = Translator.shared.isShown(message)
            menu.addItem(ClosureMenuItem(shown ? labels.hideTranslation : labels.translate) { Translator.shared.toggle(message) })
        }
        if let t = message.text, !t.isEmpty {
            menu.addItem(ClosureMenuItem(labels.copy) {
                // A selection inside the message text wins; otherwise copy the whole message.
                let before = NSPasteboard.general.changeCount
                NSApp.sendAction(#selector(NSText.copy(_:)), to: nil, from: nil)
                if NSPasteboard.general.changeCount == before {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(t, forType: .string)
                }
            })
        }
        if message.media != nil {
            menu.addItem(ClosureMenuItem(labels.open) { session.openMedia(message) })
        }
        if message.kind == .image && MediaViewer.canShow(message) {
            menu.addItem(ClosureMenuItem("Nhập chi phí PO (VietFlow)…") {
                MediaViewer.shared.show(session: session, message: message, cost: true)
            })
        }
        if Forwarder.canForward(message) {
            menu.addItem(ClosureMenuItem(labels.forward) { Forwarder.shared.show(message, from: session) })
        }
        if session.can("delete") && !message.isDeleted {
            menu.addItem(.separator())
            if message.fromMe {
                menu.addItem(ClosureMenuItem(labels.deleteForEveryone) { session.delete(message, forEveryone: true) })
            }
            if session.can("delete_for_me") || session.platform != .messenger {
                menu.addItem(ClosureMenuItem(labels.deleteForMe) { session.delete(message, forEveryone: false) })
            }
        }
        return menu
    }
}

final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, _ handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self
    }

    required init(coder: NSCoder) { fatalError("init(coder:) is not used") }

    @objc private func run() { handler() }
}

/// Transparent to every mouse event except a context click (right click or Control-click), so text
/// selection, links and taps underneath keep working.
private struct ContextClickCatcher: NSViewRepresentable {
    let menu: @MainActor () -> NSMenu

    func makeNSView(context: Context) -> CatcherView { CatcherView() }
    func updateNSView(_ view: CatcherView, context: Context) { view.menuProvider = menu }

    final class CatcherView: NSView {
        var menuProvider: (@MainActor () -> NSMenu)?

        private static func isContextClick(_ e: NSEvent?) -> Bool {
            guard let e else { return false }
            switch e.type {
            case .rightMouseDown, .rightMouseUp: return true
            case .leftMouseDown, .leftMouseUp: return e.modifierFlags.contains(.control)
            default: return false
            }
        }

        override func hitTest(_ point: NSPoint) -> NSView? {
            Self.isContextClick(NSApp.currentEvent) ? super.hitTest(point) : nil
        }

        override func menu(for event: NSEvent) -> NSMenu? { menuProvider?() }

        override func rightMouseDown(with event: NSEvent) { show(event) }

        override func mouseDown(with event: NSEvent) {
            if event.modifierFlags.contains(.control) { show(event) } else { super.mouseDown(with: event) }
        }

        private func show(_ event: NSEvent) {
            guard let m = menuProvider?() else { return }
            NSMenu.popUpContextMenu(m, with: event, for: self)
        }
    }
}

struct MenuLabels {
    var reply, edit, react, translate, hideTranslation, copy, open, forward, deleteForEveryone, deleteForMe: String

    static let english = MenuLabels(reply: "Reply", edit: "Edit", react: "React", translate: "Translate", hideTranslation: "Hide Translation",
                                    copy: "Copy Text", open: "Open", forward: "Forward…",
                                    deleteForEveryone: "Delete for Everyone", deleteForMe: "Delete for Me")
    static let vietnamese = MenuLabels(reply: "Trả lời", edit: "Chỉnh sửa", react: "Bày tỏ cảm xúc", translate: "Dịch", hideTranslation: "Ẩn bản dịch",
                                       copy: "Sao chép", open: "Mở", forward: "Chuyển tiếp…",
                                       deleteForEveryone: "Thu hồi", deleteForMe: "Xóa chỉ ở phía tôi")
}

/// Conversations are also shown in the narrow chat-bubble window, where the desktop gaps beside the
/// bubbles and the media widths would push rows past the window edge.
struct CompactChatKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    var compactChat: Bool {
        get { self[CompactChatKey.self] }
        set { self[CompactChatKey.self] = newValue }
    }
}

enum ChatMetrics {
    /// Minimum empty space on the far side of a bubble.
    static func gap(_ regular: CGFloat, _ compact: Bool) -> CGFloat { compact ? 36 : regular }
    static let compactMediaWidth: CGFloat = 220
}

/// Image/video/sticker content inside a bubble; resolves encrypted media on demand.
struct MediaContent: View {
    let session: PlatformSession
    let message: Message
    var maxWidth: CGFloat = 320
    var maxHeight: CGFloat = 360
    var cornerRadius: CGFloat = 0
    @Environment(\.compactChat) private var compact

    private var widthLimit: CGFloat { compact ? min(maxWidth, ChatMetrics.compactMediaWidth) : maxWidth }
    private var heightLimit: CGFloat { compact ? min(maxHeight, 280) : maxHeight }

    var body: some View {
        let media = message.media ?? Media()
        let source = media.path ?? media.url ?? media.thumbnail
        let size = fittedSize(media)
        ZStack {
            RemoteImage(source: source, contentMode: .fill, maxPixel: max(size.width, size.height) * 2) {
                if let thumb = media.thumbnail, thumb != source {
                    RemoteImage(source: thumb, maxPixel: 96) { Color.gray.opacity(0.2) }.blur(radius: 6)
                } else {
                    Rectangle().fill(Color.gray.opacity(0.18))
                        .overlay(ProgressView().controlSize(.small))
                }
            }
            .frame(width: size.width, height: size.height)
            .clipped()
            if message.kind == .video || message.kind == .gif {
                Circle().fill(.black.opacity(0.45)).frame(width: 44, height: 44)
                    .overlay(Image(systemName: "play.fill").foregroundStyle(.white).font(.system(size: 18)))
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        .contentShape(Rectangle())
        .onTapGesture { session.openMedia(message) }
        .onAppear {
            // Settings → Dữ liệu và bộ nhớ → Tự động tải media decides what fetches on sight.
            if media.path == nil && media.url == nil && Workspace.shared.storage.autoDownloads(message) {
                session.ensureMedia(message)
            }
        }
    }

    private func fittedSize(_ m: Media) -> CGSize {
        if message.kind == .sticker { return CGSize(width: compact ? 120 : 160, height: compact ? 120 : 160) }
        guard let aspect = m.aspect else { return CGSize(width: widthLimit * 0.8, height: widthLimit * 0.6) }
        var w = widthLimit
        var h = w / aspect
        if h > heightLimit { h = heightLimit; w = h * aspect }
        return CGSize(width: max(w, 120), height: max(h, 80))
    }
}

/// Generic file attachment row (icon, name, size).
struct FileContent: View {
    let session: PlatformSession
    let message: Message
    var iconColor: Color
    var iconSymbol = "doc.fill"
    var titleColor: Color = .primary
    var subtitleColor: Color = .secondary

    var body: some View {
        let m = message.media
        HStack(spacing: 10) {
            ZStack {
                Circle().fill(iconColor).frame(width: 42, height: 42)
                Image(systemName: message.kind == .voice || message.kind == .audio ? "play.fill" : iconSymbol)
                    .foregroundStyle(.white).font(.system(size: 17, weight: .semibold))
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(title(m)).font(.system(size: 14, weight: .medium)).foregroundStyle(titleColor).lineLimit(1)
                Text(subtitle(m)).font(.system(size: 12)).foregroundStyle(subtitleColor)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { session.openMedia(message) }
    }

    private func title(_ m: Media?) -> String {
        if message.kind == .voice { return "🎤 " + duration(m?.duration) }
        return m?.fileName ?? message.text ?? "File"
    }

    private func subtitle(_ m: Media?) -> String {
        var parts: [String] = []
        if let s = m?.size { parts.append(ByteCountFormatter.string(fromByteCount: Int64(s), countStyle: .file)) }
        if message.kind == .audio, let d = m?.duration { parts.append(duration(d)) }
        if let ext = m?.fileName.map({ ($0 as NSString).pathExtension.uppercased() }), !ext.isEmpty { parts.append(ext) }
        return parts.joined(separator: " · ")
    }

    private func duration(_ d: Double?) -> String {
        guard let d else { return "" }
        return String(format: "%d:%02d", Int(d) / 60, Int(d) % 60)
    }
}

/// Status shown in place of the chat UI while a bridge is starting or has failed.
struct BridgeStatusView: View {
    let session: PlatformSession
    var tint: Color

    var body: some View {
        VStack(spacing: 14) {
            if let err = session.bridgeError {
                Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 34)).foregroundStyle(.orange)
                Text(err).multilineTextAlignment(.center).foregroundStyle(.secondary).frame(maxWidth: 420)
                Button("Khởi động lại bridge") { session.restart() }.tint(tint)
            } else {
                ProgressView()
                Text("Đang kết nối \(session.platform.displayName)…").foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Doodle wallpaper: scattered glyphs over a fill, drawn once per size.
struct DoodleWallpaper: View {
    var background: AnyShapeStyle
    var glyphColor: Color
    var symbols: [String]
    var spacing: CGFloat = 64
    var glyphSize: CGFloat = 18

    var body: some View {
        ZStack {
            Rectangle().fill(background)
            Canvas { ctx, size in
                var rng = SeededRandom(seed: 42)
                let resolved = symbols.map { ctx.resolve(Image(systemName: $0)) }
                var y: CGFloat = 8
                var row = 0
                while y < size.height + spacing {
                    var x: CGFloat = (row % 2 == 0) ? 8 : spacing / 2
                    while x < size.width + spacing {
                        var img = resolved[Int(rng.next() % UInt64(resolved.count))]
                        img.shading = .color(glyphColor)
                        let jitter = CGPoint(x: CGFloat(rng.next() % 16) - 8, y: CGFloat(rng.next() % 16) - 8)
                        var c = ctx
                        c.translateBy(x: x + jitter.x, y: y + jitter.y)
                        c.rotate(by: .degrees(Double(rng.next() % 50) - 25))
                        c.draw(img, in: CGRect(x: -glyphSize / 2, y: -glyphSize / 2, width: glyphSize, height: glyphSize))
                        x += spacing
                    }
                    y += spacing * 0.8
                    row += 1
                }
            }
        }
    }
}

struct SeededRandom {
    private var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> UInt64 {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return state >> 33
    }
}

/// Bubble shape with per-corner radii (Messenger grouping, WhatsApp/Telegram tails).
struct BubbleShape: Shape {
    var topLeft: CGFloat
    var topRight: CGFloat
    var bottomLeft: CGFloat
    var bottomRight: CGFloat

    func path(in r: CGRect) -> Path {
        Path(roundedRect: r, cornerRadii: RectangleCornerRadii(
            topLeading: topLeft, bottomLeading: bottomLeft, bottomTrailing: bottomRight, topTrailing: topRight),
             style: .continuous)
    }
}

/// Small curved tail drawn next to a bubble corner.
struct BubbleTail: Shape {
    enum Corner { case topLeft, topRight, bottomLeft, bottomRight }
    var corner: Corner

    func path(in r: CGRect) -> Path {
        var p = Path()
        switch corner {
        case .topRight: // WhatsApp outgoing
            p.move(to: CGPoint(x: r.minX, y: r.minY))
            p.addLine(to: CGPoint(x: r.maxX, y: r.minY))
            p.addQuadCurve(to: CGPoint(x: r.minX, y: r.maxY), control: CGPoint(x: r.minX + r.width * 0.3, y: r.minY + r.height * 0.2))
        case .topLeft: // WhatsApp incoming
            p.move(to: CGPoint(x: r.maxX, y: r.minY))
            p.addLine(to: CGPoint(x: r.minX, y: r.minY))
            p.addQuadCurve(to: CGPoint(x: r.maxX, y: r.maxY), control: CGPoint(x: r.maxX - r.width * 0.3, y: r.minY + r.height * 0.2))
        case .bottomRight: // Telegram outgoing
            p.move(to: CGPoint(x: r.minX, y: r.minY))
            p.addQuadCurve(to: CGPoint(x: r.maxX, y: r.maxY), control: CGPoint(x: r.minX + r.width * 0.2, y: r.maxY))
            p.addLine(to: CGPoint(x: r.minX, y: r.maxY))
        case .bottomLeft: // Telegram incoming
            p.move(to: CGPoint(x: r.maxX, y: r.minY))
            p.addQuadCurve(to: CGPoint(x: r.minX, y: r.maxY), control: CGPoint(x: r.maxX - r.width * 0.2, y: r.maxY))
            p.addLine(to: CGPoint(x: r.maxX, y: r.maxY))
        }
        p.closeSubpath()
        return p
    }
}

/// Avatar circle from any source with a platform-specific placeholder.
struct AvatarView<Placeholder: View>: View {
    let source: String?
    let size: CGFloat
    @ViewBuilder var placeholder: () -> Placeholder

    var body: some View {
        RemoteImage(source: source, maxPixel: size * 2) { placeholder() }
            .frame(width: size, height: size)
            .clipShape(Circle())
    }
}

/// Hover tracking helper used by list rows and bubbles.
struct Hovering: ViewModifier {
    @Binding var isHovered: Bool
    func body(content: Content) -> some View {
        content.onHover { isHovered = $0 }
    }
}

/// Covers a QR code once it can no longer be scanned. Refreshing is automatic; the button is the
/// fallback the official apps show ("Nhấp để tải lại mã QR") when automatic refreshes ran out.
struct QRExpiryOverlay: View {
    let session: PlatformSession
    var tint: Color
    var label = "Nhấp để tải lại mã QR"

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { _ in
            let expired = session.isQRExpired
            if session.qrRefreshing || expired {
                ZStack {
                    Rectangle().fill(.white.opacity(0.88))
                    if session.qrRefreshing {
                        ProgressView().controlSize(.large)
                    } else {
                        Button { session.refreshQR() } label: {
                            VStack(spacing: 10) {
                                Circle().fill(tint).frame(width: 72, height: 72)
                                    .overlay(Image(systemName: "arrow.clockwise").font(.system(size: 28, weight: .semibold)).foregroundStyle(.white))
                                Text(label).font(.system(size: 13, weight: .medium)).foregroundStyle(.black.opacity(0.75))
                                    .multilineTextAlignment(.center)
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
                .onAppear { if expired && !session.qrRefreshing { session.refreshQR(auto: true) } }
            }
        }
    }
}
