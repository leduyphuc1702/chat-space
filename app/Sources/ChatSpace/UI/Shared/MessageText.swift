import AppKit
import SwiftUI

/// A message's text. Bodies with links are drawn by `MessageText` (link colour, hand cursor, hover
/// underline); the rest stay plain SwiftUI text, which is much lighter in a long chat.
struct BodyText: View {
    let text: String
    var links: [TextLink]? = nil
    let font: Font
    let nsFont: NSFont
    let color: Color
    let linkColor: Color
    var underlineLinks = false
    /// Invisible text after the body (the tucked-in time), in `reserveFont`.
    var reserve: String? = nil
    var reserveSize: CGFloat = 11.5

    var body: some View {
        if !(links ?? []).isEmpty || MessageText.hasLink(text) {
            MessageText(text: text, links: links, font: nsFont, color: NSColor(color), linkColor: NSColor(linkColor),
                        underlineLinks: underlineLinks, reserve: reserve, reserveFont: .systemFont(ofSize: reserveSize))
        } else {
            (Text(text) + Text(reserve ?? "").font(.system(size: reserveSize)).foregroundColor(.clear))
                .font(font).foregroundStyle(color).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// Selectable message body drawn by AppKit. SwiftUI's selectable `Text` draws links like the words around
/// them and without a hand cursor; here they get the platform's link colour, the pointing hand and an
/// underline under the pointer, as in the official apps.
struct MessageText: NSViewRepresentable {
    let text: String
    var links: [TextLink]? = nil
    var font: NSFont
    var color: NSColor
    var linkColor: NSColor
    /// Messenger underlines links all the time; the others only under the pointer.
    var underlineLinks = false
    /// Invisible text after the body: Telegram and WhatsApp tuck the time into the last line.
    var reserve: String? = nil
    var reserveFont: NSFont? = nil

    func makeNSView(context: Context) -> MessageTextNSView {
        let v = MessageTextNSView()
        v.apply(self)
        return v
    }

    func updateNSView(_ v: MessageTextNSView, context: Context) { v.apply(self) }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: MessageTextNSView, context: Context) -> CGSize? {
        let w = proposal.width.map { $0.isFinite ? $0 : 100_000 } ?? 100_000
        return nsView.fit(width: max(w, 1))
    }

    private static let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue)

    static func hasLink(_ text: String) -> Bool {
        guard text.contains("."), let detector else { return false }
        return detector.firstMatch(in: text, range: NSRange(location: 0, length: (text as NSString).length)) != nil
    }

    /// Body text with every link (written URLs and the platform's hidden text links) marked.
    static func attributed(_ text: String, links: [TextLink]?, font: NSFont, color: NSColor) -> NSAttributedString {
        let out = NSMutableAttributedString(string: text, attributes: [.font: font, .foregroundColor: color])
        let ns = text as NSString
        if let detector {
            for match in detector.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
                if let url = match.url { out.addAttribute(.link, value: url, range: match.range) }
            }
        }
        for l in links ?? [] where l.offset >= 0 && l.length > 0 && l.offset + l.length <= ns.length {
            if let url = URL(string: l.url) ?? URL(string: "https://" + l.url) {
                out.addAttribute(.link, value: url, range: NSRange(location: l.offset, length: l.length))
            }
        }
        return out
    }
}

final class MessageTextNSView: NSTextView {
    private struct Key: Equatable {
        var text: String, links: [TextLink]?, font: NSFont, color: NSColor, linkColor: NSColor, underline: Bool, reserve: String?
    }

    private var key: Key?
    /// Measures off-screen so SwiftUI can ask for sizes at any width without disturbing what is drawn.
    private let measureStorage = NSTextStorage()
    private let measureLayout = NSLayoutManager()
    private let measureContainer = NSTextContainer(size: NSSize(width: 100, height: CGFloat.greatestFiniteMagnitude))
    private var fits: [CGFloat: CGSize] = [:]
    private var hoveredLink: NSRange?
    /// Length of the real text; the reserve after it can't be selected or copied.
    private var bodyLength = 0
    private var tracking: NSTrackingArea?

    convenience init() {
        let storage = NSTextStorage()
        let layout = NSLayoutManager()
        storage.addLayoutManager(layout)
        let container = NSTextContainer(size: NSSize(width: 100, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        container.lineFragmentPadding = 0
        layout.addTextContainer(container)
        self.init(frame: .zero, textContainer: container)
        isEditable = false
        isSelectable = true
        isRichText = true
        drawsBackground = false
        textContainerInset = .zero
        isVerticallyResizable = false
        isHorizontallyResizable = false
        measureContainer.lineFragmentPadding = 0
        measureLayout.addTextContainer(measureContainer)
        measureStorage.addLayoutManager(measureLayout)
    }

    func apply(_ p: MessageText) {
        let k = Key(text: p.text, links: p.links, font: p.font, color: p.color, linkColor: p.linkColor,
                    underline: p.underlineLinks, reserve: p.reserve)
        guard k != key else { return }
        key = k
        let body = NSMutableAttributedString(attributedString: MessageText.attributed(p.text, links: p.links, font: p.font, color: p.color))
        if let r = p.reserve, !r.isEmpty {
            body.append(NSAttributedString(string: r, attributes: [.font: p.reserveFont ?? p.font, .foregroundColor: NSColor.clear]))
        }
        linkTextAttributes = [
            .foregroundColor: p.linkColor,
            .cursor: NSCursor.pointingHand,
            .underlineStyle: p.underlineLinks ? NSUnderlineStyle.single.rawValue : 0,
        ]
        hoveredLink = nil
        bodyLength = (p.text as NSString).length
        textStorage?.setAttributedString(body)
        measureStorage.setAttributedString(body)
        fits = [:]
        invalidateIntrinsicContentSize()
    }

    func fit(width: CGFloat) -> CGSize {
        let w = width.rounded(.down)
        if let s = fits[w] { return s }
        measureContainer.size = NSSize(width: w, height: CGFloat.greatestFiniteMagnitude)
        measureLayout.ensureLayout(for: measureContainer)
        let used = measureLayout.usedRect(for: measureContainer)
        let lineHeight = (key?.font).map { measureLayout.defaultLineHeight(for: $0) } ?? 17
        let size = CGSize(width: min(w, ceil(used.width) + 1), height: max(ceil(used.height), ceil(lineHeight)))
        if fits.count > 16 { fits.removeAll() }
        fits[w] = size
        return size
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        textContainer?.size = NSSize(width: newSize.width, height: CGFloat.greatestFiniteMagnitude)
    }

    override func selectionRange(forProposedRange proposed: NSRange, granularity: NSSelectionGranularity) -> NSRange {
        let r = super.selectionRange(forProposedRange: proposed, granularity: granularity)
        let end = min(NSMaxRange(r), bodyLength)
        return NSRange(location: min(r.location, end), length: end - min(r.location, end))
    }

    // The message's own menu (reply, react, forward…) belongs to the bubble around the text.
    override func menu(for event: NSEvent) -> NSMenu? { nil }
    override func rightMouseDown(with event: NSEvent) { nextResponder?.rightMouseDown(with: event) }

    // Scrolling over a message belongs to the chat list around it.
    override func scrollWheel(with event: NSEvent) { nextResponder?.scrollWheel(with: event) }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let t = NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeInActiveApp, .inVisibleRect], owner: self)
        addTrackingArea(t)
        tracking = t
    }

    override func mouseMoved(with event: NSEvent) {
        super.mouseMoved(with: event)
        setHovered(link(at: convert(event.locationInWindow, from: nil)))
    }

    override func mouseExited(with event: NSEvent) {
        super.mouseExited(with: event)
        setHovered(nil)
    }

    private func link(at point: NSPoint) -> NSRange? {
        guard let lm = layoutManager, let tc = textContainer, let storage = textStorage, storage.length > 0 else { return nil }
        var fraction: CGFloat = 0
        let glyph = lm.glyphIndex(for: point, in: tc, fractionOfDistanceThroughGlyph: &fraction)
        guard lm.boundingRect(forGlyphRange: NSRange(location: glyph, length: 1), in: tc).insetBy(dx: -1, dy: -1).contains(point) else { return nil }
        let i = lm.characterIndexForGlyph(at: glyph)
        guard i < storage.length else { return nil }
        var range = NSRange()
        return storage.attribute(.link, at: i, effectiveRange: &range) == nil ? nil : range
    }

    private func setHovered(_ r: NSRange?) {
        guard r != hoveredLink, let lm = layoutManager else { return }
        if let old = hoveredLink { lm.removeTemporaryAttribute(.underlineStyle, forCharacterRange: old) }
        hoveredLink = r
        if let r, key?.underline != true {
            lm.addTemporaryAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue, forCharacterRange: r)
        }
    }
}
