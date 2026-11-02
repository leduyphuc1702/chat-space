import AppKit
import AVKit
import SwiftUI
import UniformTypeIdentifiers
import VisionKit

/// Photo/video viewer for every platform. Photos get Live Text: drag across text to select and copy it,
/// or copy everything the image contains at once.
@MainActor
@Observable
final class MediaViewer {
    static let shared = MediaViewer()

    struct Item: Identifiable, Equatable {
        let id: String
        let message: Message
    }

    private(set) var items: [Item] = []
    private(set) var index = 0
    private(set) var image: NSImage?
    private(set) var videoURL: URL?
    private(set) var loading = false
    private(set) var failed = false
    private(set) var analysis: ImageAnalysis?
    private(set) var zoom: CGFloat = 1
    var copiedNotice = false
    /// The recognised text listed beside the photo, selectable like any document.
    var showText = false {
        didSet { if showText { showCost = false } }
    }
    /// The VietFlow purchase-cost form beside an invoice photo.
    var showCost = false {
        didSet {
            if showCost { showText = false; prepareCostEntry() }
        }
    }
    private(set) var costEntries: [String: PurchaseCostEntry] = [:]
    var costEntry: PurchaseCostEntry? { current.flatMap { costEntries[$0.id] } }

    @ObservationIgnored private weak var session: PlatformSession?
    @ObservationIgnored private var window: NSPanel?
    @ObservationIgnored private var keyMonitor: Any?
    @ObservationIgnored private var loadTask: Task<Void, Never>?
    @ObservationIgnored private var source: String?
    @ObservationIgnored private let analyzer = ImageAnalyzer()
    @ObservationIgnored weak var overlay: ImageAnalysisOverlayView?

    /// Long side photos are decoded to: sharp at full screen, and still small enough to zoom smoothly.
    nonisolated private static let maxPixel: CGFloat = 4096

    static func canShow(_ m: Message) -> Bool {
        guard m.media != nil, !m.isDeleted else { return false }
        return m.kind == .image || m.kind == .video || m.kind == .gif
    }

    var current: Message? { items.indices.contains(index) ? items[index].message : nil }
    var isVideo: Bool { current.map(Self.isVideo) ?? false }
    var transcript: String { analysis?.transcript ?? "" }

    private static func isVideo(_ m: Message) -> Bool {
        if m.kind == .video { return true }
        guard m.kind == .gif else { return false }
        let mime = m.media?.mime ?? ""
        let name = (m.media?.fileName ?? m.media?.url ?? "").lowercased()
        return mime.hasPrefix("video") || name.hasSuffix(".mp4") || name.contains(".mp4?")
    }

    // MARK: Showing

    func show(session: PlatformSession, message: Message, cost: Bool = false) {
        if self.session !== session { costEntries = [:] }
        self.session = session
        let all = (session.messages[message.chatId] ?? []).filter(Self.canShow)
        items = (all.contains { $0.id == message.id } ? all : [message]).map { Item(id: $0.id, message: $0) }
        index = items.firstIndex { $0.id == message.id } ?? 0
        let panel = window ?? makeWindow()
        window = panel
        // Above the floating chat bubble when opened from there.
        panel.level = ChatHeads.shared.expanded != nil ? .floating : .normal
        if !panel.isVisible { panel.center() }
        panel.title = title
        panel.makeKeyAndOrderFront(nil)
        if panel.level == .normal { NSApp.activate(ignoringOtherApps: true) }
        watchKeys()
        loadCurrent()
        if cost { showCost = true }
    }

    func close() {
        window?.orderOut(nil)
        loadTask?.cancel()
        image = nil
        videoURL = nil
        analysis = nil
        items = []
        costEntries = [:]
        showCost = false
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
    }

    func step(_ delta: Int) {
        let next = index + delta
        guard items.indices.contains(next) else { return }
        index = next
        window?.title = title
        loadCurrent()
    }

    func setZoom(_ z: CGFloat) { zoom = min(max(z, 1), 8) }

    private var title: String {
        guard let m = current else { return "" }
        let who = m.fromMe ? "Bạn" : (m.senderName ?? session?.chats.first { $0.id == m.chatId }?.title ?? "")
        let date = m.date.formatted(.dateTime.day().month().year().hour().minute().locale(Locale(identifier: "vi_VN")))
        return items.count > 1 ? "\(who) · \(date) · \(index + 1)/\(items.count)" : "\(who) · \(date)"
    }

    private func loadCurrent() {
        loadTask?.cancel()
        image = nil
        videoURL = nil
        analysis = nil
        failed = false
        zoom = 1
        source = nil
        guard let m = current, let session else { return }
        if showCost { prepareCostEntry() }
        loading = true
        let video = isVideo
        loadTask = Task { [weak self] in
            let src = await session.mediaSource(m)
            guard let self, !Task.isCancelled, self.current?.id == m.id else { return }
            self.source = src
            guard let src else { self.loading = false; self.failed = true; return }
            if video {
                self.videoURL = src.hasPrefix("/") ? URL(fileURLWithPath: src) : URL(string: src)
                self.loading = false
                self.failed = self.videoURL == nil
                return
            }
            let data = await ImageLoader.shared.data(for: src)
            guard !Task.isCancelled, self.current?.id == m.id else { return }
            let animated = m.kind == .gif || (m.media?.mime ?? "").contains("gif")
            let img = await Task.detached(priority: .userInitiated) { [data] in
                ViewerImage(image: data.flatMap { animated ? NSImage(data: $0) : ImageLoader.downsample($0, maxPixel: Self.maxPixel) })
            }.value.image
            guard !Task.isCancelled, self.current?.id == m.id else { return }
            self.image = img
            self.loading = false
            self.failed = img == nil
            if let img { await self.analyze(img, id: m.id) }
        }
    }

    private func analyze(_ img: NSImage, id: String) async {
        guard ImageAnalyzer.isSupported else { return }
        let config = ImageAnalyzer.Configuration([.text, .machineReadableCode])
        guard let a = try? await analyzer.analyze(img, orientation: .up, configuration: config),
              current?.id == id else { return }
        analysis = a
        costEntries[id]?.applyTranscript(a.transcript)
    }

    /// Kept per photo while the viewer is open, so stepping to another photo and back keeps what was typed.
    private func prepareCostEntry() {
        guard let m = current, !isVideo, let session else { return }
        let entry = costEntries[m.id] ?? PurchaseCostEntry(message: m, session: session)
        costEntries[m.id] = entry
        if let a = analysis { entry.applyTranscript(a.transcript) }
    }

    // MARK: Actions

    func copyAllText() {
        let t = transcript
        guard !t.isEmpty else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(t, forType: .string)
        flashCopied()
    }

    func copyImage() {
        guard let image else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([image])
        flashCopied()
    }

    private func flashCopied() {
        copiedNotice = true
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            MediaViewer.shared.copiedNotice = false
        }
    }

    func save() {
        guard let m = current, let src = source, let window else { return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = suggestedName(m, src)
        panel.directoryURL = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
        panel.beginSheetModal(for: window) { response in
            guard response == .OK, let dest = panel.url else { return }
            Task { @MainActor in
                guard let data = await ImageLoader.shared.data(for: src) else { return }
                try? data.write(to: dest)
            }
        }
    }

    func openExternally() {
        guard let m = current else { return }
        session?.openExternally(m)
    }

    private func suggestedName(_ m: Message, _ src: String) -> String {
        if let n = m.media?.fileName, !n.isEmpty { return n }
        var ext = URL(string: src)?.pathExtension ?? (src as NSString).pathExtension
        if ext.isEmpty || ext.count > 5 {
            ext = m.media?.mime.flatMap { UTType(mimeType: $0)?.preferredFilenameExtension } ?? (isVideo ? "mp4" : "jpg")
        }
        let stamp = m.date.formatted(.iso8601.year().month().day().time(includingFractionalSeconds: false)).replacingOccurrences(of: ":", with: "")
        return "ChatSpace \(stamp).\(ext)"
    }

    // MARK: Window

    private func makeWindow() -> NSPanel {
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 960, height: 720),
                        styleMask: [.titled, .closable, .resizable, .miniaturizable, .fullSizeContentView, .nonactivatingPanel],
                        backing: .buffered, defer: false)
        p.identifier = ChatHeads.keepsChatOpenIdentifier
        p.titlebarAppearsTransparent = true
        p.titleVisibility = .visible
        p.appearance = NSAppearance(named: .darkAqua)
        p.backgroundColor = .black
        p.isReleasedWhenClosed = false
        p.hidesOnDeactivate = false
        p.becomesKeyOnlyIfNeeded = false
        p.isFloatingPanel = false
        p.collectionBehavior = [.fullScreenPrimary, .moveToActiveSpace]
        p.minSize = NSSize(width: 420, height: 320)
        p.setFrameAutosaveName("ChatSpaceMediaViewer")
        p.contentView = NSHostingView(rootView: MediaViewerView(viewer: self))
        p.delegate = WindowDelegate.shared
        return p
    }

    private final class WindowDelegate: NSObject, NSWindowDelegate {
        static let shared = WindowDelegate()
        func windowWillClose(_ notification: Notification) {
            MainActor.assumeIsolated { MediaViewer.shared.close() }
        }
    }

    private func watchKeys() {
        guard keyMonitor == nil else { return }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            let window = event.window.map(ObjectIdentifier.init)
            let key = event.keyCode, cmd = event.modifierFlags.contains(.command)
            let handled = MainActor.assumeIsolated { MediaViewer.shared.handleKey(key, command: cmd, in: window) }
            return handled ? nil : event
        }
    }

    private func handleKey(_ key: UInt16, command cmd: Bool, in window: ObjectIdentifier?) -> Bool {
        guard let w = self.window, window == ObjectIdentifier(w) else { return false }
        // Typing in the cost form: arrows move the caret and Esc stays with the field.
        if (w.firstResponder as? NSTextView)?.isEditable == true, [53, 123, 124].contains(key) { return false }
        switch (key, cmd) {
        case (53, _): close()                                   // Esc
        case (123, false): step(-1)                             // ←
        case (124, false): step(1)                              // →
        case (24, true), (69, true): setZoom(zoom * 1.5)        // ⌘+
        case (27, true), (78, true): setZoom(zoom / 1.5)        // ⌘−
        case (29, true): setZoom(1)                             // ⌘0
        case (8, true):                                         // ⌘C: the selected text, else the photo
            if let tv = self.window?.firstResponder as? NSTextView, tv.selectedRange().length > 0 {
                return false
            } else if let o = overlay, o.hasActiveTextSelection, !o.selectedText.isEmpty {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(o.selectedText, forType: .string)
                flashCopied()
            } else if image != nil {
                copyImage()
            } else {
                return false
            }
        default: return false
        }
        return true
    }
}

/// Freshly decoded on a background task and not shared yet.
private struct ViewerImage: @unchecked Sendable { let image: NSImage? }

// MARK: - Views

struct MediaViewerView: View {
    let viewer: MediaViewer
    @State private var hovering = true

    var body: some View {
        HStack(spacing: 0) {
            ZStack {
                Color.black.ignoresSafeArea()
                content
                if viewer.items.count > 1 { arrows }
                VStack {
                    Spacer()
                    toolbar.opacity(hovering ? 1 : 0)
                }
            }
            if viewer.showText && !viewer.isVideo {
                textPanel.frame(width: 300)
            }
            if viewer.showCost && !viewer.isVideo, let entry = viewer.costEntry {
                PurchaseCostPanel(entry: entry) { viewer.showCost = false }
                    .id(entry.message.id)
                    .frame(width: 360)
            }
        }
        .overlay {
            if viewer.copiedNotice {
                Text("Đã sao chép").font(.system(size: 13, weight: .medium)).foregroundStyle(.white)
                    .padding(.horizontal, 14).padding(.vertical, 8)
                    .background(Capsule().fill(.black.opacity(0.75)))
                    .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: 0.15), value: viewer.showText)
        .animation(.easeOut(duration: 0.15), value: viewer.showCost)
        .animation(.easeOut(duration: 0.15), value: hovering)
        .animation(.easeOut(duration: 0.2), value: viewer.copiedNotice)
        .onContinuousHover { phase in
            if case .active = phase { hovering = true } else { hovering = false }
        }
        .ignoresSafeArea()
    }

    @ViewBuilder
    private var content: some View {
        if let url = viewer.videoURL {
            VideoPlayerView(url: url).id(url)
        } else if let image = viewer.image {
            LiveTextImageView(image: image, analysis: viewer.analysis, zoom: viewer.zoom) { viewer.setZoom($0) }
        } else if viewer.failed {
            VStack(spacing: 8) {
                Image(systemName: "exclamationmark.triangle").font(.system(size: 28))
                Text("Không tải được nội dung này").font(.system(size: 13))
            }
            .foregroundStyle(.white.opacity(0.7))
        } else if let thumb = viewer.current?.media?.thumbnail {
            RemoteImage(source: thumb, contentMode: .fit, maxPixel: 256) { Color.clear }
                .blur(radius: 12).opacity(0.6)
                .overlay(ProgressView().controlSize(.large).tint(.white))
                .padding(40)
        } else {
            ProgressView().controlSize(.large).tint(.white)
        }
    }

    private var arrows: some View {
        HStack {
            arrow("chevron.left", enabled: viewer.index > 0) { viewer.step(-1) }
            Spacer()
            arrow("chevron.right", enabled: viewer.index < viewer.items.count - 1) { viewer.step(1) }
        }
        .padding(.horizontal, 14)
        .opacity(hovering ? 1 : 0)
    }

    private func arrow(_ symbol: String, enabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 18, weight: .semibold)).foregroundStyle(.white)
                .frame(width: 40, height: 40).background(Circle().fill(.black.opacity(0.5)))
        }
        .buttonStyle(.plain).disabled(!enabled).opacity(enabled ? 1 : 0)
    }

    private var toolbar: some View {
        HStack(spacing: 4) {
            if !viewer.isVideo {
                tool("minus.magnifyingglass", "Thu nhỏ (⌘−)") { viewer.setZoom(viewer.zoom / 1.5) }.disabled(viewer.zoom <= 1)
                tool("plus.magnifyingglass", "Phóng to (⌘+)") { viewer.setZoom(viewer.zoom * 1.5) }.disabled(viewer.image == nil)
                Divider().frame(height: 18).overlay(Color.white.opacity(0.25))
                tool("text.viewfinder", viewer.transcript.isEmpty ? "Không tìm thấy chữ trong ảnh" : "Văn bản trong ảnh: bôi đen để sao chép") { viewer.showText.toggle() }
                    .disabled(viewer.transcript.isEmpty && !viewer.showText)
                    .background(RoundedRectangle(cornerRadius: 6).fill(viewer.showText ? Color.white.opacity(0.18) : .clear))
                tool("doc.on.doc", "Sao chép ảnh") { viewer.copyImage() }.disabled(viewer.image == nil)
                tool("dollarsign.square", "Nhập chi phí PO (VietFlow)") { viewer.showCost.toggle() }
                    .background(RoundedRectangle(cornerRadius: 6).fill(viewer.showCost ? Color.white.opacity(0.18) : .clear))
            }
            tool("square.and.arrow.down", "Lưu về máy…") { viewer.save() }.disabled(viewer.loading || viewer.failed)
            tool("arrow.up.forward.app", "Mở bằng ứng dụng khác") { viewer.openExternally() }
        }
        .padding(.horizontal, 8).padding(.vertical, 6)
        .background(Capsule().fill(.black.opacity(0.6)).overlay(Capsule().stroke(.white.opacity(0.12))))
        .padding(.bottom, 18)
    }

    private var textPanel: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("Văn bản trong ảnh").font(.system(size: 13, weight: .semibold))
                Spacer()
                Button("Sao chép tất cả") { viewer.copyAllText() }
                    .buttonStyle(.borderless).font(.system(size: 12)).disabled(viewer.transcript.isEmpty)
            }
            .padding(.horizontal, 14).padding(.top, 34).padding(.bottom, 10)
            Divider().overlay(Color.white.opacity(0.1))
            if viewer.analysis == nil && viewer.image != nil {
                ProgressView().controlSize(.small).frame(maxWidth: .infinity).padding(20)
                Spacer()
            } else if viewer.transcript.isEmpty {
                Text("Không tìm thấy chữ trong ảnh này").font(.system(size: 12.5)).foregroundStyle(.secondary).padding(14)
                Spacer()
            } else {
                SelectableTextView(text: viewer.transcript)
            }
            Text("Bôi đen rồi nhấn ⌘C, hoặc kéo chọn chữ ngay trên ảnh.")
                .font(.system(size: 11)).foregroundStyle(.secondary).padding(14)
        }
        .foregroundStyle(.white)
        .background(Color(white: 0.11))
    }

    private func tool(_ symbol: String, _ help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 15)).foregroundStyle(.white)
                .frame(width: 34, height: 30).contentShape(Rectangle())
        }
        .buttonStyle(.plain).help(help)
    }
}

/// Zoomable photo with VisionKit's Live Text overlay (select text, data detectors, QR codes).
struct LiveTextImageView: NSViewRepresentable {
    let image: NSImage
    let analysis: ImageAnalysis?
    let zoom: CGFloat
    let onZoom: (CGFloat) -> Void

    final class Coordinator {
        var onZoom: (CGFloat) -> Void
        weak var scroll: NSScrollView?
        weak var overlay: ImageAnalysisOverlayView?
        var observer: NSObjectProtocol?
        init(onZoom: @escaping (CGFloat) -> Void) { self.onZoom = onZoom }
        deinit { observer.map(NotificationCenter.default.removeObserver) }
    }

    func makeCoordinator() -> Coordinator { Coordinator(onZoom: onZoom) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = CenteringScrollView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = false
        scroll.hasHorizontalScroller = false
        scroll.allowsMagnification = true
        scroll.minMagnification = 1
        scroll.maxMagnification = 8
        scroll.contentView = CenteringClipView()
        scroll.contentView.drawsBackground = false

        let imageView = NSImageView()
        imageView.imageScaling = .scaleProportionallyUpOrDown
        imageView.animates = true
        imageView.autoresizingMask = [.width, .height]
        let overlay = ImageAnalysisOverlayView()
        overlay.trackingImageView = imageView
        // Not .automatic: its subject lifting turns a drag across text into dragging the photo out.
        overlay.preferredInteractionTypes = [.textSelection, .dataDetectors]
        overlay.supplementaryInterfaceContentInsets = NSEdgeInsets(top: 0, left: 0, bottom: 64, right: 0)
        overlay.autoresizingMask = [.width, .height]
        imageView.addSubview(overlay)
        scroll.documentView = imageView
        MediaViewer.shared.overlay = overlay

        context.coordinator.scroll = scroll
        context.coordinator.overlay = overlay
        context.coordinator.observer = NotificationCenter.default.addObserver(
            forName: NSScrollView.didEndLiveMagnifyNotification, object: scroll, queue: .main
        ) { [weak coordinator = context.coordinator] _ in
            MainActor.assumeIsolated {
                guard let c = coordinator, let s = c.scroll else { return }
                c.onZoom(s.magnification)
            }
        }
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        context.coordinator.onZoom = onZoom
        // Found via the coordinator: NSImageView puts its own content view first among its subviews.
        guard let imageView = scroll.documentView as? NSImageView,
              let overlay = context.coordinator.overlay else { return }
        if imageView.image !== image {
            imageView.image = image
            scroll.magnification = 1
            imageView.frame = scroll.contentView.bounds
            overlay.frame = imageView.bounds
        }
        if overlay.analysis !== analysis {
            overlay.analysis = analysis
            overlay.preferredInteractionTypes = [.textSelection, .dataDetectors]
        }
        if abs(scroll.magnification - zoom) > 0.01 {
            let center = NSPoint(x: scroll.contentView.bounds.midX, y: scroll.contentView.bounds.midY)
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.18
                scroll.animator().setMagnification(zoom, centeredAt: center)
            }
        }
    }
}

/// Read-only text the user can select and copy, used for the photo's recognised text.
struct SelectableTextView: NSViewRepresentable {
    let text: String

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.drawsBackground = false
        guard let tv = scroll.documentView as? NSTextView else { return scroll }
        tv.isEditable = false
        tv.isSelectable = true
        tv.drawsBackground = false
        tv.font = .systemFont(ofSize: 14)
        tv.textColor = .white
        tv.textContainerInset = NSSize(width: 10, height: 10)
        tv.string = text
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let tv = scroll.documentView as? NSTextView, tv.string != text else { return }
        tv.string = text
    }
}

/// Keeps the photo filling the window at 100 % while the window is resized.
private final class CenteringScrollView: NSScrollView {
    override func tile() {
        super.tile()
        guard let doc = documentView, magnification <= 1.001 else { return }
        let size = contentView.bounds.size
        if doc.frame.size != size { doc.frame = NSRect(origin: .zero, size: size) }
    }
}

/// Centers the photo when it is smaller than the visible area.
private final class CenteringClipView: NSClipView {
    override func constrainBoundsRect(_ proposedBounds: NSRect) -> NSRect {
        var rect = super.constrainBoundsRect(proposedBounds)
        guard let doc = documentView else { return rect }
        if rect.width > doc.frame.width { rect.origin.x = (doc.frame.width - rect.width) / 2 }
        if rect.height > doc.frame.height { rect.origin.y = (doc.frame.height - rect.height) / 2 }
        return rect
    }
}

struct VideoPlayerView: NSViewRepresentable {
    let url: URL

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .floating
        view.showsFullScreenToggleButton = true
        view.allowsPictureInPicturePlayback = true
        let player = AVPlayer(url: url)
        view.player = player
        player.play()
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {}

    static func dismantleNSView(_ view: AVPlayerView, coordinator: ()) {
        view.player?.pause()
        view.player = nil
    }
}
