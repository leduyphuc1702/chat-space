import AppKit

/// Draws the unread count onto the Dock icon itself. `NSDockTile.badgeLabel` is hidden by macOS whenever the
/// app's notification permission (or its "Badge app icon" switch) is off, which made the count vanish.
@MainActor
enum DockBadge {
    private static let view = DockBadgeView()

    static func show(_ count: Int) {
        let tile = NSApp.dockTile
        let label = count <= 0 ? nil : (count > 99 ? "99+" : "\(count)")
        if tile.contentView !== view {
            view.frame = NSRect(origin: .zero, size: tile.size)
            tile.contentView = view
        }
        tile.badgeLabel = nil
        guard view.label != label else { return }
        view.label = label
        tile.display()
    }
}

private final class DockBadgeView: NSView {
    var label: String?

    override func draw(_ dirtyRect: NSRect) {
        let b = bounds
        NSApp.applicationIconImage?.draw(in: b)
        guard let label else { return }

        let font = NSFont.systemFont(ofSize: b.height * 0.2, weight: .medium)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.white]
        let text = NSAttributedString(string: label, attributes: attrs)
        let ts = text.size()
        let h = b.height * 0.3
        let w = max(h, ts.width + h * 0.6)
        let rect = NSRect(x: b.maxX - w - b.width * 0.01, y: b.maxY - h - b.height * 0.01, width: w, height: h)

        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.3)
        shadow.shadowBlurRadius = h * 0.08
        shadow.shadowOffset = NSSize(width: 0, height: -h * 0.03)
        shadow.set()
        NSColor(red: 1, green: 0.23, blue: 0.19, alpha: 1).setFill()
        NSBezierPath(roundedRect: rect, xRadius: h / 2, yRadius: h / 2).fill()
        NSGraphicsContext.restoreGraphicsState()

        text.draw(at: NSPoint(x: rect.midX - ts.width / 2, y: rect.midY - ts.height / 2))
    }
}
