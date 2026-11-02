import AppKit
import CoreImage.CIFilterBuiltins
import SwiftUI
import UserNotifications

enum AppSettings {
    static var telegramApiId: String {
        get { UserDefaults.standard.string(forKey: "telegram.apiId") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "telegram.apiId") }
    }

    static var telegramApiHash: String {
        get { UserDefaults.standard.string(forKey: "telegram.apiHash") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "telegram.apiHash") }
    }

    static func bridgeConfig(for platform: Platform) -> [String: JSONValue] {
        switch platform {
        case .telegram where !telegramApiId.isEmpty && !telegramApiHash.isEmpty:
            return ["apiId": .number(Double(telegramApiId) ?? 0), "apiHash": .string(telegramApiHash)]
        default:
            return [:]
        }
    }
}

enum QRCode {
    static func image(for payload: String, scale: CGFloat = 10) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(payload.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: scale, y: scale)) else { return nil }
        let rep = NSCIImageRep(ciImage: output)
        let image = NSImage(size: rep.size)
        image.addRepresentation(rep)
        return image
    }
}

/// Loads avatars/media from http(s) URLs, local paths and data: URLs, with an in-memory cache.
/// Images are decoded straight to the size they are shown at: a full-resolution photo is ~48 MB of pixels.
@MainActor
final class ImageLoader {
    static let shared = ImageLoader()
    private let cache: NSCache<NSString, NSImage> = {
        let c = NSCache<NSString, NSImage>()
        c.totalCostLimit = 96 << 20
        return c
    }()
    private var inflight: [String: Task<Data?, Never>] = [:]

    /// Largest dimension, in pixels, an image is decoded to unless the view asks for less.
    nonisolated static let defaultMaxPixel: CGFloat = 1024

    nonisolated static func decodeDataURL(_ s: String) -> NSImage? {
        decodeDataURLBytes(s).flatMap(NSImage.init(data:))
    }

    nonisolated private static func decodeDataURLBytes(_ s: String) -> Data? {
        guard s.hasPrefix("data:"), let comma = s.firstIndex(of: ",") else { return nil }
        return Data(base64Encoded: String(s[s.index(after: comma)...]), options: .ignoreUnknownCharacters)
    }

    /// Decodes at most `maxPixel` on the long side (never upscales); falls back to NSImage for formats ImageIO lacks.
    nonisolated static func downsample(_ data: Data, maxPixel: CGFloat) -> NSImage? {
        if let src = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
           CGImageSourceGetCount(src) > 0 {
            let opts: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
                kCGImageSourceThumbnailMaxPixelSize: max(16, Int(maxPixel.rounded(.up))),
            ]
            if let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary) {
                return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
            }
        }
        return NSImage(data: data)
    }

    /// Freshly decoded and not shared yet, so it may cross from the decoding task to the main actor.
    private struct Decoded: @unchecked Sendable { let image: NSImage? }

    private static func key(_ source: String, _ maxPixel: CGFloat) -> NSString { "\(Int(maxPixel))|\(source)" as NSString }

    func cached(_ source: String, maxPixel: CGFloat = defaultMaxPixel) -> NSImage? {
        cache.object(forKey: Self.key(source, maxPixel))
    }

    /// Raw bytes behind a data: URL, local path or http(s) URL.
    func data(for source: String) async -> Data? {
        if source.hasPrefix("data:") { return Self.decodeDataURLBytes(source) }
        if source.hasPrefix("/") || source.hasPrefix("file://") {
            let url = source.hasPrefix("/") ? URL(fileURLWithPath: source) : URL(string: source)
            return await Task.detached(priority: .userInitiated) { url.flatMap { try? Data(contentsOf: $0, options: .mappedIfSafe) } }.value
        }
        let task = inflight[source] ?? Task<Data?, Never> {
            guard let url = URL(string: source) else { return nil }
            var req = URLRequest(url: url)
            req.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)", forHTTPHeaderField: "User-Agent")
            guard let (data, resp) = try? await URLSession.shared.data(for: req),
                  (resp as? HTTPURLResponse)?.statusCode ?? 200 < 400 else { return nil }
            return data
        }
        inflight[source] = task
        defer { inflight[source] = nil }
        return await task.value
    }

    func load(_ source: String, maxPixel: CGFloat = defaultMaxPixel) async -> NSImage? {
        let key = Self.key(source, maxPixel)
        if let img = cache.object(forKey: key) { return img }
        guard let data = await data(for: source) else { return nil }
        let img = await Task.detached(priority: .userInitiated) { Decoded(image: Self.downsample(data, maxPixel: maxPixel)) }.value.image
        if let img {
            let px = img.representations.first.map { $0.pixelsWide * $0.pixelsHigh } ?? Int(img.size.width * img.size.height)
            cache.setObject(img, forKey: key, cost: max(1, px) * 4)
        }
        return img
    }
}

/// Image from any source string; shows `placeholder` until loaded or on failure.
/// `maxPixel` is the long side to decode to, about the displayed size in points × 2.
struct RemoteImage<Placeholder: View>: View {
    let source: String?
    var contentMode: ContentMode = .fill
    var maxPixel: CGFloat = ImageLoader.defaultMaxPixel
    @ViewBuilder var placeholder: () -> Placeholder

    @State private var image: NSImage?
    @State private var loadedSource: String?

    var body: some View {
        Group {
            if let image, loadedSource == source {
                Image(nsImage: image).resizable().aspectRatio(contentMode: contentMode)
            } else {
                placeholder()
            }
        }
        .task(id: source) {
            guard let source, !source.isEmpty else { image = nil; return }
            if let c = ImageLoader.shared.cached(source, maxPixel: maxPixel) {
                image = c; loadedSource = source; return
            }
            let img = await ImageLoader.shared.load(source, maxPixel: maxPixel)
            image = img
            loadedSource = source
        }
        .onDisappear { image = nil; loadedSource = nil }
    }
}

extension Color {
    init(hex: UInt32, alpha: Double = 1) {
        self.init(.sRGB,
                  red: Double((hex >> 16) & 0xFF) / 255,
                  green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255,
                  opacity: alpha)
    }
}

extension String {
    /// Up to two initials, the way most messengers render avatar placeholders.
    var initials: String {
        let words = split(whereSeparator: { $0 == " " || $0 == "-" || $0 == "_" }).filter { !$0.isEmpty }
        let letters = words.prefix(2).compactMap { $0.first(where: { $0.isLetter || $0.isNumber }) }
        if letters.isEmpty { return first.map { String($0).uppercased() } ?? "?" }
        return String(letters).uppercased()
    }

    var stableHash: Int {
        var h: UInt64 = 5381
        for b in utf8 { h = (h &* 33) &+ UInt64(b) }
        return Int(h % 1_000_000)
    }
}

/// Text with URLs turned into clickable links.
func linkified(_ text: String, linkColor: Color? = nil, underline: Bool = false) -> AttributedString {
    var attr = AttributedString(text)
    guard let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) else { return attr }
    let ns = text as NSString
    for match in detector.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
        guard let url = match.url, let range = Range(match.range, in: text),
              let lower = AttributedString.Index(range.lowerBound, within: attr),
              let upper = AttributedString.Index(range.upperBound, within: attr) else { continue }
        attr[lower..<upper].link = url
        if let linkColor { attr[lower..<upper].foregroundColor = linkColor }
        if underline { attr[lower..<upper].underlineStyle = .single }
    }
    return attr
}

/// Emoji-only short messages are shown big and bubble-less by every platform.
func isJumboEmoji(_ text: String?) -> Bool {
    guard let t = text?.trimmingCharacters(in: .whitespacesAndNewlines), !t.isEmpty, t.count <= 3 else { return false }
    return t.unicodeScalars.allSatisfy { $0.properties.isEmojiPresentation || $0.properties.isEmoji && $0.value > 0x238C || $0.value == 0xFE0F || $0.value == 0x200D }
}

enum Fmt {
    static func time(_ d: Date) -> String {
        let f = DateFormatter()
        f.dateFormat = "HH:mm"
        return f.string(from: d)
    }

    static func dayKey(_ d: Date) -> String {
        let c = Calendar.current.dateComponents([.year, .month, .day], from: d)
        return "\(c.year!)-\(c.month!)-\(c.day!)"
    }
}

/// Picks a file with the standard open panel.
@MainActor
func pickFile() -> String? {
    let panel = NSOpenPanel()
    panel.canChooseFiles = true
    panel.canChooseDirectories = false
    panel.allowsMultipleSelection = false
    return panel.runModal() == .OK ? panel.url?.path : nil
}

/// Multi-line text input that sends on Return and inserts a newline on Shift/Option+Return,
/// matching the desktop behaviour of all four platforms. Grows with its text up to `maxHeight`.
struct ComposerTextView: View {
    @Binding var text: String
    var placeholder: String
    var font: NSFont = .systemFont(ofSize: 14)
    var textColor: NSColor = .labelColor
    var placeholderColor: NSColor = .placeholderTextColor
    var maxHeight: CGFloat = 140
    var onSubmit: () -> Void
    var onTypingChange: ((Bool) -> Void)? = nil
    /// Group composers pass their session/chat to get the "@" member list.
    var mentionSession: PlatformSession? = nil
    var mentionChatId: String? = nil
    /// Images or files pasted or dropped into the composer.
    var onFiles: (([URL]) -> Void)? = nil
    /// Key commands (↑, Esc…) the mention list didn't take; true = handled.
    var onCommand: (@MainActor (Selector) -> Bool)? = nil
    /// SwiftUI doesn't re-ask an NSViewRepresentable for its size on every edit, so the text view
    /// reports its laid-out height and the frame follows it.
    @State private var height: CGFloat?

    var body: some View {
        ComposerTextArea(text: $text, placeholder: placeholder, font: font, textColor: textColor,
                         placeholderColor: placeholderColor, maxHeight: maxHeight, onSubmit: onSubmit,
                         onTypingChange: onTypingChange, mentionSession: mentionSession, mentionChatId: mentionChatId,
                         onFiles: onFiles, onCommand: onCommand, onHeight: { h in if abs(h - (height ?? 0)) > 0.5 { height = h } })
            .frame(height: height)
    }
}

private struct ComposerTextArea: NSViewRepresentable {
    @Binding var text: String
    var placeholder: String
    var font: NSFont
    var textColor: NSColor
    var placeholderColor: NSColor
    var maxHeight: CGFloat
    var onSubmit: () -> Void
    var onTypingChange: ((Bool) -> Void)?
    var mentionSession: PlatformSession?
    var mentionChatId: String?
    var onFiles: (([URL]) -> Void)?
    var onCommand: (@MainActor (Selector) -> Bool)?
    var onHeight: (CGFloat) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = ComposerScrollView()
        scroll.onTile = { [weak coordinator = context.coordinator, weak scroll] in
            if let scroll { coordinator?.reportHeight(scroll) }
        }
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .noBorder

        let tv = PlaceholderTextView()
        tv.delegate = context.coordinator
        tv.isRichText = false
        tv.allowsUndo = true
        tv.drawsBackground = false
        tv.font = font
        tv.textColor = textColor
        tv.insertionPointColor = textColor
        tv.placeholder = placeholder
        tv.placeholderColor = placeholderColor
        tv.textContainerInset = NSSize(width: 0, height: 4)
        tv.textContainer?.lineFragmentPadding = 0
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.minSize = NSSize(width: 0, height: font.pointSize * 1.25 + 8)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        tv.textContainer?.widthTracksTextView = true
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.onSubmit = { context.coordinator.parent.onSubmit() }
        tv.interceptReturn = { [weak coordinator = context.coordinator] in
            coordinator?.mentionCommand(#selector(NSResponder.insertNewline(_:))) ?? false
        }
        tv.registerForDraggedTypes([.fileURL, .png, .tiff])
        scroll.documentView = tv
        context.coordinator.textView = tv
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
            // Focus the composer when a chat opens, like the official apps, unless the user is
            // already typing elsewhere (search field) in the same window.
            guard let w = tv.window, w.isVisible else { return }
            if let r = w.firstResponder as? NSTextView, r !== tv, r.isFieldEditor { return }
            w.makeFirstResponder(tv)
        }
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        context.coordinator.parent = self
        guard let tv = scroll.documentView as? PlaceholderTextView else { return }
        if let s = mentionSession, let c = mentionChatId { Mentions.shared.register(tv, for: ChatRef(platform: s.platform, chatId: c)) }
        if tv.string != text {
            tv.string = text
            tv.needsDisplay = true
            context.coordinator.reportHeight(scroll)
        }
        tv.placeholder = placeholder
        tv.onFiles = onFiles
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSScrollView, context: Context) -> CGSize? {
        guard let tv = nsView.documentView as? NSTextView else { return nil }
        let width = proposal.width.flatMap { $0.isFinite ? $0 : nil } ?? 300
        return CGSize(width: width, height: Self.height(of: tv, width: width, maxHeight: maxHeight))
    }

    /// Height of the text laid out at `width`, between one line and `maxHeight`.
    static func height(of tv: NSTextView, width: CGFloat, maxHeight: CGFloat) -> CGFloat {
        let line = (tv.font?.pointSize ?? 14) * 1.25 + tv.textContainerInset.height * 2
        guard let lm = tv.layoutManager, let text = tv.textStorage, width > 0 else { return line }
        let storage = NSTextStorage(attributedString: text)
        let measure = NSLayoutManager()
        let container = NSTextContainer(size: NSSize(width: width, height: .greatestFiniteMagnitude))
        container.lineFragmentPadding = tv.textContainer?.lineFragmentPadding ?? 0
        measure.typesetterBehavior = lm.typesetterBehavior
        measure.addTextContainer(container)
        storage.addLayoutManager(measure)
        measure.ensureLayout(for: container)
        let h = ceil(measure.usedRect(for: container).height) + tv.textContainerInset.height * 2
        return min(max(h, line), maxHeight)
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: ComposerTextArea
        weak var textView: NSTextView?
        private var typing = false

        init(_ parent: ComposerTextArea) { self.parent = parent }

        func reportHeight(_ scroll: NSScrollView) {
            guard let tv = scroll.documentView as? NSTextView else { return }
            let h = ComposerTextArea.height(of: tv, width: scroll.contentSize.width, maxHeight: parent.maxHeight)
            let report = parent.onHeight
            DispatchQueue.main.async { report(h) }
        }

        func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            parent.text = tv.string
            let now = !tv.string.isEmpty
            if now != typing {
                typing = now
                parent.onTypingChange?(now)
            }
            if let scroll = tv.enclosingScrollView { reportHeight(scroll) }
            updateMention(tv)
        }

        func textViewDidChangeSelection(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            updateMention(tv)
        }

        func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            mentionCommand(commandSelector) || (parent.onCommand?(commandSelector) ?? false)
        }

        @MainActor
        func mentionCommand(_ selector: Selector) -> Bool {
            guard let s = parent.mentionSession, let c = parent.mentionChatId else { return false }
            return Mentions.shared.handle(selector, session: s, chatId: c)
        }

        @MainActor
        private func updateMention(_ tv: NSTextView) {
            guard let s = parent.mentionSession, let c = parent.mentionChatId else { return }
            Mentions.shared.update(tv, session: s, chatId: c)
        }
    }
}

/// The whole composer area is one click target: the text view always fills the visible height,
/// and clicks on the insets (or below a short line) still land in it.
final class ComposerScrollView: NSScrollView {
    /// A legacy scroller would take width the height measurement in `sizeThatFits` doesn't know about,
    /// so the last line wraps out of view.
    override var scrollerStyle: NSScroller.Style {
        get { .overlay }
        set { super.scrollerStyle = .overlay }
    }

    var onTile: (() -> Void)?
    private var tiledWidth: CGFloat = -1

    override func tile() {
        super.tile()
        if contentSize.width != tiledWidth {
            tiledWidth = contentSize.width
            onTile?()
        }
        guard let tv = documentView as? NSTextView else { return }
        let h = contentSize.height
        if tv.minSize.height != h {
            tv.minSize = NSSize(width: 0, height: h)
            if tv.frame.height < h { tv.setFrameSize(NSSize(width: tv.frame.width, height: h)) }
        }
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let hit = super.hitTest(point)
        if hit is NSScroller { return hit }
        guard let tv = documentView, frame.contains(point) else { return hit }
        return hit === self || hit === contentView ? tv : (hit ?? tv)
    }

    override var acceptsFirstResponder: Bool { true }
    override func becomeFirstResponder() -> Bool {
        guard let tv = documentView else { return false }
        return window?.makeFirstResponder(tv) ?? false
    }
}

enum Composer {
    /// Focuses the chat composer of the key window (used by emoji/sticker buttons).
    @MainActor static func focus(in target: NSWindow? = nil) {
        let window = target ?? NSApp.keyWindow
        guard let root = window?.contentView, let tv = find(in: root) else { return }
        window?.makeFirstResponder(tv)
    }

    /// Focuses the composer and opens the system emoji picker, which inserts into it.
    @MainActor static func emoji() {
        focus()
        NSApp.orderFrontCharacterPalette(nil)
    }

    private static func find(in v: NSView) -> PlaceholderTextView? {
        if let t = v as? PlaceholderTextView, !t.isHiddenOrHasHiddenAncestor { return t }
        for s in v.subviews { if let t = find(in: s) { return t } }
        return nil
    }
}

final class PlaceholderTextView: NSTextView {
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    var placeholder = ""
    var placeholderColor: NSColor = .placeholderTextColor
    var onSubmit: (() -> Void)?
    /// Lets an open "@" list take Return before it sends the message.
    var interceptReturn: (() -> Bool)?

    override func keyDown(with event: NSEvent) {
        let isReturn = event.keyCode == 36 || event.keyCode == 76
        if isReturn && event.modifierFlags.intersection([.shift, .option]).isEmpty && !hasMarkedText() {
            if interceptReturn?() == true { return }
            onSubmit?()
            return
        }
        super.keyDown(with: event)
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard string.isEmpty, !placeholder.isEmpty else { return }
        let attrs: [NSAttributedString.Key: Any] = [.font: font ?? .systemFont(ofSize: 14), .foregroundColor: placeholderColor]
        let origin = NSPoint(x: textContainerInset.width + (textContainer?.lineFragmentPadding ?? 0), y: textContainerInset.height)
        (placeholder as NSString).draw(at: origin, withAttributes: attrs)
    }

    override func didChangeText() {
        super.didChangeText()
        needsDisplay = true
    }

    // MARK: Pasting and dropping images/files

    var onFiles: (([URL]) -> Void)?

    override func paste(_ sender: Any?) {
        if let onFiles, let files = Self.attachments(in: .general) {
            onFiles(files)
            return
        }
        super.paste(sender)
    }

    /// A plain-text view disables Paste when the clipboard holds only an image.
    override func validateUserInterfaceItem(_ item: NSValidatedUserInterfaceItem) -> Bool {
        if item.action == #selector(paste(_:)), onFiles != nil, Self.hasAttachment(NSPasteboard.general) { return true }
        return super.validateUserInterfaceItem(item)
    }

    override func validateMenuItem(_ item: NSMenuItem) -> Bool {
        if item.action == #selector(paste(_:)), onFiles != nil, Self.hasAttachment(NSPasteboard.general) { return true }
        return super.validateMenuItem(item)
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        if onFiles != nil, Self.hasAttachment(sender.draggingPasteboard) { return .copy }
        return super.draggingEntered(sender)
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        if onFiles != nil, Self.hasAttachment(sender.draggingPasteboard) { return .copy }
        return super.draggingUpdated(sender)
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        if let onFiles, let files = Self.attachments(in: sender.draggingPasteboard) {
            // Confirming runs a modal alert; not inside the drag session, which would stall the source app.
            DispatchQueue.main.async { onFiles(files) }
            return true
        }
        return super.performDragOperation(sender)
    }

    private static let imageTypes: [NSPasteboard.PasteboardType] = [.png, .tiff, NSPasteboard.PasteboardType("public.jpeg"),
                                                                    NSPasteboard.PasteboardType("public.heic")]

    private static func fileURLs(_ pb: NSPasteboard) -> [URL] {
        let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        return urls.filter { !$0.hasDirectoryPath }
    }

    /// Text copied from Office and similar apps also carries a picture of itself; that is a text paste.
    /// An image copied from a browser may come with just its address as text.
    private static func imageType(_ pb: NSPasteboard) -> NSPasteboard.PasteboardType? {
        guard let type = pb.availableType(from: imageTypes) else { return nil }
        if let s = pb.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty {
            let isLink = !s.contains(where: \.isWhitespace) && (s.hasPrefix("http://") || s.hasPrefix("https://"))
            if !isLink { return nil }
        }
        return type
    }

    static func hasAttachment(_ pb: NSPasteboard) -> Bool {
        !fileURLs(pb).isEmpty || imageType(pb) != nil
    }

    /// Files on the pasteboard, or its image written to a temporary file; nil means paste text as usual.
    static func attachments(in pb: NSPasteboard) -> [URL]? {
        let files = fileURLs(pb)
        if !files.isEmpty { return files }
        guard let type = imageType(pb) else { return nil }
        var data = pb.data(forType: type)
        var ext = type == .png ? "png" : type.rawValue == "public.jpeg" ? "jpg" : type.rawValue == "public.heic" ? "heic" : "png"
        if type == .tiff, let tiff = data {
            data = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:])
            ext = "png"
        }
        guard let data else { return nil }
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("chatspace-\(UUID().uuidString).\(ext)")
        guard (try? data.write(to: url)) != nil else { return nil }
        return [url]
    }
}
