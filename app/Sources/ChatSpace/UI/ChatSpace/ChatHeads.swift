import AppKit
import Observation
import SwiftUI

/// Floating chat heads, after Messenger's and Telegram for Android's bubbles: avatars docked to a screen
/// edge, above every app and Space. Each one is its own window, dragged and dismissed on its own.
/// Tapping one opens that chat in a small window rendered by the platform's own conversation UI;
/// dragging one to the ✕ at the bottom closes it.
@MainActor
@Observable
final class ChatHeads {
    static let shared = ChatHeads()

    struct Preview: Equatable {
        let id = UUID()
        let ref: ChatRef
        let title: String
        let text: String
    }

    private(set) var heads: [ChatRef] = []
    private(set) var expanded: ChatRef?
    /// Bumped per chat on every new message; each bump makes that bubble bounce.
    private(set) var bounces: [ChatRef: Int] = [:]
    private(set) var preview: Preview?
    /// The head being dragged, if any.
    private(set) var dragged: ChatRef?
    var overTrash = false
    var dragging: Bool { dragged != nil }

    @ObservationIgnored private var headPanels: [ChatRef: FloatingPanel] = [:]
    @ObservationIgnored private var chatPanel: FloatingPanel?
    @ObservationIgnored private var trashPanel: FloatingPanel?
    @ObservationIgnored private var previewPanel: FloatingPanel?
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var clickMonitors: [Any] = []

    static let headSize: CGFloat = 54
    static let spacing: CGFloat = 10
    /// Room around the heads for the bounce and the badges.
    static let inset: CGFloat = 16
    private static let chatSize = NSSize(width: 400, height: 580)
    private static let previewSize = NSSize(width: 300, height: 96)
    private static let previewSeconds: UInt64 = 5

    func isFocused(_ ref: ChatRef) -> Bool {
        expanded == ref && (chatPanel?.isKeyWindow ?? false) && (chatPanel?.isVisible ?? false)
    }

    // MARK: Triggers

    /// A live, unmuted incoming message arrived for a chat that is not on screen (after the notification is posted).
    func incoming(platform: Platform, chat: Chat, message: Message) {
        let cfg = Workspace.shared.bubbles
        guard cfg.trigger == .newMessages else { return }
        if cfg.onlyInBackground && NSApp.isActive && !(NSApp.keyWindow is NSPanel) { return }
        if !cfg.includeGroups && chat.isGroup { return }
        let ref = ChatRef(platform: platform, chatId: chat.id)
        let isNew = !heads.contains(ref)
        show(ref, expand: false)
        if !isNew { bounces[ref, default: 0] += 1 }
        guard cfg.showPreview, expanded != ref, !dragging else { return }
        let body = message.preview(photo: "📷 Hình ảnh", video: "🎥 Video", voice: "🎤 Tin nhắn thoại", sticker: "Nhãn dán",
                                   file: "📎 Tệp", deleted: "Tin nhắn đã thu hồi")
        let sender = message.senderName.flatMap { $0.isEmpty ? nil : $0 }
        showPreview(Preview(ref: ref, title: chat.title, text: chat.isGroup && sender != nil ? "\(sender!): \(body)" : body))
    }

    func show(_ ref: ChatRef, expand: Bool) {
        withAnimation(.spring(response: 0.38, dampingFraction: 0.6)) {
            if let i = heads.firstIndex(of: ref) { heads.remove(at: i) }
            heads.insert(ref, at: 0)
            let cap = max(1, Workspace.shared.bubbles.maxBubbles)
            while heads.count > cap, let victim = heads.last(where: { $0 != expanded && $0 != ref }) {
                heads.removeAll { $0 == victim }
                closePanel(victim)
            }
        }
        if headPanels[ref] == nil { placeNewHead(ref) }
        headPanels[ref]?.orderFrontRegardless()
        if expand { open(ref) }
    }

    func toggle(_ ref: ChatRef) {
        if expanded == ref { collapse() } else { open(ref) }
    }

    func open(_ ref: ChatRef) {
        hidePreview()
        expanded = ref
        AppModel.shared.session(ref.platform).prepare(ref.chatId)
        let panel = chatPanel ?? makeChatPanel()
        chatPanel = panel
        positionChatPanel()
        panel.orderFrontRegardless()
        panel.makeKey()
        watchOutsideClicks()
    }

    func collapse() {
        expanded = nil
        chatPanel?.orderOut(nil)
        clickMonitors.forEach(NSEvent.removeMonitor)
        clickMonitors = []
    }

    // MARK: Click outside closes the chat

    /// Windows that belong to the open chat: the bubbles, the chat itself, its popovers and the media viewer.
    static let keepsChatOpenIdentifier = NSUserInterfaceItemIdentifier("chatspace.keepsBubbleOpen")

    private func watchOutsideClicks() {
        guard clickMonitors.isEmpty else { return }
        let mask: NSEvent.EventTypeMask = [.leftMouseDown, .rightMouseDown, .otherMouseDown]
        if let m = NSEvent.addLocalMonitorForEvents(matching: mask, handler: { event in
            MainActor.assumeIsolated {
                if !ChatHeads.shared.belongsToChat(event.window) { ChatHeads.shared.collapse() }
            }
            return event
        }) { clickMonitors.append(m) }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: mask, handler: { _ in
            MainActor.assumeIsolated {
                if !ChatHeads.isSystemHelperUnderMouse() { ChatHeads.shared.collapse() }
            }
        }) { clickMonitors.append(m) }
    }

    private func belongsToChat(_ window: NSWindow?) -> Bool {
        guard let window else { return true }
        if NSApp.modalWindow != nil || window is NSSavePanel { return true }
        if window === chatPanel || window === previewPanel || window === trashPanel { return true }
        if headPanels.values.contains(where: { $0 === window }) { return true }
        if window.identifier == Self.keepsChatOpenIdentifier { return true }
        // Popovers and menus opened from the chat are separate windows parented to it.
        var w: NSWindow? = window
        while let current = w {
            if current === chatPanel { return true }
            w = current.parent
        }
        return String(describing: type(of: window)).contains("Popover")
    }

    /// Emoji picker and the screenshot overlay run in their own processes; clicking them is part of
    /// composing the message, not leaving the chat.
    private static func isSystemHelperUnderMouse() -> Bool {
        let helpers: Set<String> = ["CharacterPalette", "Emoji & Symbols", "screencaptureui", "TextInputMenuAgent", "TextInputSwitcher"]
        guard let primary = NSScreen.screens.first,
              let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]
        else { return false }
        let mouse = NSEvent.mouseLocation
        let point = CGPoint(x: mouse.x, y: primary.frame.maxY - mouse.y)
        for info in list {
            guard let b = info[kCGWindowBounds as String] as? [String: CGFloat],
                  let layer = info[kCGWindowLayer as String] as? Int, layer < 1000 else { continue }
            let rect = CGRect(x: b["X"] ?? 0, y: b["Y"] ?? 0, width: b["Width"] ?? 0, height: b["Height"] ?? 0)
            guard rect.contains(point) else { continue }
            return helpers.contains(info[kCGWindowOwnerName as String] as? String ?? "")
        }
        return false
    }

    func dismiss(_ ref: ChatRef) {
        withAnimation(.easeOut(duration: 0.18)) { heads.removeAll { $0 == ref } }
        bounces[ref] = nil
        if preview?.ref == ref { hidePreview() }
        if expanded == ref { collapse() }
        closePanel(ref)
    }

    func dismissAll() {
        heads.forEach(closePanel)
        heads = []
        bounces = [:]
        hidePreview()
        collapse()
    }

    // MARK: Message preview

    private func showPreview(_ p: Preview) {
        preview = p
        let panel = previewPanel ?? {
            let panel = FloatingPanel(contentRect: NSRect(origin: .zero, size: Self.previewSize), canKey: false)
            panel.contentView = NSHostingView(rootView: ChatHeadPreview().environment(AppModel.shared))
            return panel
        }()
        previewPanel = panel
        positionPreview()
        panel.alphaValue = 1
        panel.orderFrontRegardless()
        Task { @MainActor [id = p.id] in
            try? await Task.sleep(nanoseconds: Self.previewSeconds * 1_000_000_000)
            if ChatHeads.shared.preview?.id == id { ChatHeads.shared.hidePreview() }
        }
    }

    func hidePreview() {
        guard preview != nil else { return }
        preview = nil
        guard let panel = previewPanel, panel.isVisible else { return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.2
            panel.animator().alphaValue = 0
        }, completionHandler: {
            MainActor.assumeIsolated { if ChatHeads.shared.preview == nil { panel.orderOut(nil) } }
        })
    }

    /// Beside the bubble that just popped, on the side facing the screen.
    private func positionPreview() {
        guard let panel = previewPanel, let ref = preview?.ref, let head = headPanels[ref] else { return }
        let vf = Self.visibleFrame(of: head)
        let size = Self.previewSize
        let right = head.frame.midX > vf.midX
        var x = right ? head.frame.minX - size.width + 6 : head.frame.maxX - 6
        x = min(max(x, vf.minX + 4), vf.maxX - size.width - 4)
        var y = head.frame.midY - size.height / 2
        y = min(max(y, vf.minY + 4), vf.maxY - size.height)
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
    }

    var previewOnRight: Bool {
        guard let ref = preview?.ref, let head = headPanels[ref] else { return false }
        return head.frame.midX <= Self.visibleFrame(of: head).midX
    }

    func openInMainWindow(_ ref: ChatRef) {
        collapse()
        NSApp.activate(ignoringOtherApps: true)
        NSApp.windows.first { $0.canBecomeMain && !($0 is NSPanel) }?.makeKeyAndOrderFront(nil)
        AppModel.shared.open(platform: ref.platform, chatId: ref.chatId)
    }

    // MARK: Head windows

    private static let headWindowSize = NSSize(width: headSize + 2 * inset, height: headSize + 2 * inset)
    private static let slot = headSize + spacing

    private static func visibleFrame(of window: NSWindow?) -> NSRect {
        (window?.screen ?? NSScreen.main ?? NSScreen.screens[0]).visibleFrame
    }

    /// A new head goes to the bottom-right corner, or the first free spot above the heads already there;
    /// the top-right corner is where macOS shows notifications.
    private func placeNewHead(_ ref: ChatRef) {
        let panel = FloatingPanel(contentRect: NSRect(origin: .zero, size: Self.headWindowSize), canKey: false)
        panel.contentView = NSHostingView(rootView: ChatHeadSlot(ref: ref).environment(AppModel.shared))
        let vf = Self.visibleFrame(of: nil)
        let size = Self.headWindowSize
        let x = vf.maxX - size.width - 4
        panel.setFrameOrigin(NSPoint(x: x, y: freeY(x: x, from: vf.minY + 24, in: vf, excluding: ref)))
        headPanels[ref] = panel
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.18
            panel.animator().alphaValue = 1
        }
    }

    /// Nearest height to `y` where a head at `x` overlaps no other head.
    private func freeY(x: CGFloat, from y: CGFloat, in vf: NSRect, excluding ref: ChatRef) -> CGFloat {
        let size = Self.headWindowSize
        let top = vf.maxY - size.height, bottom = vf.minY + 8
        let others = headPanels.filter { $0.key != ref }.map(\.value.frame)
        func free(_ y: CGFloat) -> Bool {
            let r = NSRect(x: x, y: y, width: size.width, height: size.height).insetBy(dx: Self.inset, dy: Self.inset - Self.spacing / 2)
            return !others.contains { $0.insetBy(dx: Self.inset, dy: Self.inset - Self.spacing / 2).intersects(r) }
        }
        let start = min(max(y, bottom), top)
        for step in 0..<40 {
            for candidate in [start + CGFloat(step) * Self.slot, start - CGFloat(step) * Self.slot]
            where candidate >= bottom && candidate <= top && free(candidate) {
                return candidate
            }
        }
        return start
    }

    private func closePanel(_ ref: ChatRef) {
        guard let panel = headPanels.removeValue(forKey: ref) else { return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.15
            panel.animator().alphaValue = 0
        }, completionHandler: {
            MainActor.assumeIsolated { panel.orderOut(nil) }
        })
    }

    // MARK: Dragging (called from the SwiftUI gesture with screen coordinates)

    @ObservationIgnored private var dragStart: (ref: ChatRef, mouse: NSPoint, origin: NSPoint)?

    func dragChanged(_ ref: ChatRef) {
        guard let panel = headPanels[ref] else { return }
        let mouse = NSEvent.mouseLocation
        if dragStart?.ref != ref { dragStart = (ref, mouse, panel.frame.origin) }
        guard let start = dragStart else { return }
        let dx = mouse.x - start.mouse.x, dy = mouse.y - start.mouse.y
        if dragged == nil && hypot(dx, dy) < 4 { return }
        if dragged == nil {
            dragged = ref
            hidePreview()
            collapse()
            showTrash(near: panel)
        }
        panel.setFrameOrigin(NSPoint(x: start.origin.x + dx, y: start.origin.y + dy))
        if let t = trashPanel {
            let c = NSPoint(x: t.frame.midX, y: t.frame.midY)
            overTrash = hypot(mouse.x - c.x, mouse.y - c.y) < 70
        }
    }

    /// Returns true if the gesture was a drag (so the tap action must not fire).
    func dragEnded(_ ref: ChatRef) -> Bool {
        defer { dragStart = nil }
        guard dragged == ref else { return false }
        dragged = nil
        trashPanel?.orderOut(nil)
        if overTrash {
            overTrash = false
            dismiss(ref)
            return true
        }
        snapToEdge(ref)
        return true
    }

    private func snapToEdge(_ ref: ChatRef) {
        guard let panel = headPanels[ref] else { return }
        let vf = Self.visibleFrame(of: panel)
        var f = panel.frame
        f.origin.x = f.midX > vf.midX ? vf.maxX - f.width - 4 : vf.minX + 4
        f.origin.y = freeY(x: f.origin.x, from: f.origin.y, in: vf, excluding: ref)
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.22
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().setFrame(f, display: true)
        }
    }

    private func showTrash(near head: NSWindow) {
        let panel = trashPanel ?? {
            let p = FloatingPanel(contentRect: NSRect(x: 0, y: 0, width: 120, height: 120), canKey: false)
            p.ignoresMouseEvents = true
            p.contentView = NSHostingView(rootView: ChatHeadTrash())
            return p
        }()
        trashPanel = panel
        let vf = Self.visibleFrame(of: head)
        panel.setFrameOrigin(NSPoint(x: vf.midX - 60, y: vf.minY + 24))
        panel.orderFrontRegardless()
    }

    // MARK: Chat window

    private func makeChatPanel() -> FloatingPanel {
        let p = FloatingPanel(contentRect: NSRect(origin: .zero, size: Self.chatSize), canKey: true)
        p.contentView = NSHostingView(rootView: ChatHeadWindow().environment(AppModel.shared))
        p.contentView?.wantsLayer = true
        p.contentView?.layer?.cornerRadius = 16
        p.contentView?.layer?.masksToBounds = true
        p.hasShadow = true
        observers.append(NotificationCenter.default.addObserver(forName: NSWindow.didBecomeKeyNotification, object: p, queue: .main) { _ in
            MainActor.assumeIsolated {
                if let r = ChatHeads.shared.expanded { AppModel.shared.session(r.platform).markRead(r.chatId) }
            }
        })
        return p
    }

    /// Beside the head it was opened from, level with its top (or its bottom, for a head low on the screen).
    private func positionChatPanel() {
        guard let chat = chatPanel, let ref = expanded, let head = headPanels[ref] else { return }
        let vf = Self.visibleFrame(of: head)
        let size = Self.chatSize
        let right = head.frame.midX > vf.midX
        var x = right ? head.frame.minX - size.width + 6 : head.frame.maxX - 6
        x = min(max(x, vf.minX + 4), vf.maxX - size.width - 4)
        var y = head.frame.midY < vf.midY ? head.frame.minY + 6 : head.frame.maxY - size.height - 6
        y = min(max(y, vf.minY + 4), vf.maxY - size.height)
        chat.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
    }
}

/// Borderless panel that floats over full-screen apps on every Space.
final class FloatingPanel: NSPanel {
    private let canKey: Bool

    init(contentRect: NSRect, canKey: Bool) {
        self.canKey = canKey
        super.init(contentRect: contentRect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = false
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        becomesKeyOnlyIfNeeded = !canKey
    }

    override var canBecomeKey: Bool { canKey }
    override var canBecomeMain: Bool { false }
}

// MARK: SwiftUI content

/// One head's window content; it shrinks while held over the ✕.
struct ChatHeadSlot: View {
    let ref: ChatRef
    @State private var appeared = false

    var body: some View {
        let heads = ChatHeads.shared
        let trashing = heads.overTrash && heads.dragged == ref
        ChatHeadBubble(ref: ref)
            .padding(ChatHeads.inset)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .scaleEffect(trashing ? 0.85 : appeared ? 1 : 0.3)
            .opacity(trashing ? 0.7 : 1)
            .animation(.spring(duration: 0.25), value: trashing)
            .onAppear { withAnimation(.spring(response: 0.38, dampingFraction: 0.6)) { appeared = true } }
    }
}

struct ChatHeadBubble: View {
    @Environment(AppModel.self) private var model
    let ref: ChatRef
    @State private var hovered = false

    var body: some View {
        let chat = model.chat(ref)
        let size = ChatHeads.headSize
        let open = ChatHeads.shared.expanded == ref
        AvatarView(source: chat?.avatar, size: size) {
            Circle().fill(LinearGradient(colors: [Color(hex: 0x72D5FD), Color(hex: 0x2A9EF1)], startPoint: .top, endPoint: .bottom))
                .overlay(Text(String((chat?.title ?? "?").prefix(1)).uppercased()).font(.system(size: 22, weight: .semibold)).foregroundStyle(.white))
        }
        .clipShape(Circle())
        .overlay(Circle().stroke(Color.white.opacity(open ? 1 : 0), lineWidth: 3))
        .shadow(color: .black.opacity(0.35), radius: 6, y: 3)
        .overlay(alignment: .bottomTrailing) {
            PlatformIcon(platform: ref.platform, size: 18)
                .background(Circle().fill(.white).padding(-2))
                .offset(x: 2, y: 2)
        }
        .overlay(alignment: .topTrailing) {
            if let c = chat, c.hasUnread {
                RailBadge(count: max(c.unread, 1)).offset(x: 4, y: -4)
            }
        }
        .scaleEffect(hovered ? 1.06 : 1)
        .animation(.easeOut(duration: 0.12), value: hovered)
        .keyframeAnimator(initialValue: Bounce(), trigger: ChatHeads.shared.bounces[ref] ?? 0) { content, b in
            content.scaleEffect(b.scale).offset(y: b.y)
        } keyframes: { _ in
            KeyframeTrack(\.scale) {
                SpringKeyframe(1.16, duration: 0.14)
                SpringKeyframe(0.94, duration: 0.14)
                SpringKeyframe(1.05, duration: 0.14)
                SpringKeyframe(1, duration: 0.2)
            }
            KeyframeTrack(\.y) {
                CubicKeyframe(-9, duration: 0.14)
                CubicKeyframe(0, duration: 0.16)
                CubicKeyframe(-4, duration: 0.12)
                CubicKeyframe(0, duration: 0.2)
            }
        }
        .frame(width: size, height: size)
        .contentShape(Circle())
        // Chats whose list row never scrolled into view haven't had their avatar resolved yet.
        .task(id: chat?.avatar == nil) {
            if let chat, chat.avatar == nil { model.session(ref.platform).requestAvatar(for: chat) }
        }
        .onHover { hovered = $0 }
        .help(chat.map { "\($0.title) — \(ref.platform.displayName)" } ?? ref.platform.displayName)
        .gesture(
            DragGesture(minimumDistance: 0, coordinateSpace: .global)
                .onChanged { _ in ChatHeads.shared.dragChanged(ref) }
                .onEnded { _ in
                    if !ChatHeads.shared.dragEnded(ref) { ChatHeads.shared.toggle(ref) }
                }
        )
        .contextMenu {
            Button("Mở trong ChatSpace") { ChatHeads.shared.openInMainWindow(ref) }
            if let c = chat, c.hasUnread { Button("Đánh dấu đã đọc") { model.session(ref.platform).markRead(ref.chatId) } }
            Divider()
            Button("Đóng bong bóng này") { ChatHeads.shared.dismiss(ref) }
            Button("Đóng tất cả bong bóng") { ChatHeads.shared.dismissAll() }
        }
    }
}

private struct Bounce {
    var scale: CGFloat = 1
    var y: CGFloat = 0
}

/// The newest message, shown for a few seconds beside the bubble that just popped; clicking it opens the chat.
struct ChatHeadPreview: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let heads = ChatHeads.shared
        if let p = heads.preview {
            let onRight = heads.previewOnRight
            Button { heads.open(p.ref) } label: {
                HStack(alignment: .top, spacing: 8) {
                    PlatformIcon(platform: p.ref.platform, size: 16).padding(.top, 1)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(p.title).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                        Text(p.text).font(.system(size: 13)).lineLimit(2).foregroundStyle(Color(hex: 0x3C3C43))
                    }
                }
                .foregroundStyle(.black)
                .padding(.horizontal, 12).padding(.vertical, 9)
                .frame(maxWidth: 270, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(.white)
                    .shadow(color: .black.opacity(0.22), radius: 8, y: 3))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Mở đoạn chat")
            .padding(12)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: onRight ? .leading : .trailing)
            .environment(\.colorScheme, .light)
            .id(p.id)
            .transition(.scale(scale: 0.85, anchor: onRight ? .leading : .trailing).combined(with: .opacity))
        } else {
            Color.clear
        }
    }
}

struct ChatHeadTrash: View {
    var body: some View {
        let over = ChatHeads.shared.overTrash
        Circle()
            .fill(Color.black.opacity(over ? 0.75 : 0.5))
            .overlay(Image(systemName: "xmark").font(.system(size: over ? 26 : 22, weight: .semibold)).foregroundStyle(.white))
            .frame(width: over ? 76 : 62, height: over ? 76 : 62)
            .shadow(radius: 8)
            .frame(width: 120, height: 120)
            .animation(.spring(duration: 0.2), value: over)
    }
}

/// The expanded bubble: a compact title bar plus the platform's own conversation view.
struct ChatHeadWindow: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let ref = ChatHeads.shared.expanded, let chat = model.chat(ref) {
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    PlatformIcon(platform: ref.platform, size: 16)
                    Text(ref.platform.displayName).font(.system(size: 12, weight: .semibold))
                    Spacer()
                    Button { ChatHeads.shared.openInMainWindow(ref) } label: {
                        Image(systemName: "arrow.up.left.and.arrow.down.right").frame(width: 24, height: 22).contentShape(Rectangle())
                    }
                    .help("Mở trong cửa sổ ChatSpace")
                    Button { ChatHeads.shared.collapse() } label: {
                        Image(systemName: "minus").frame(width: 24, height: 22).contentShape(Rectangle())
                    }
                    .help("Thu nhỏ về bong bóng")
                    Button { ChatHeads.shared.dismiss(ref) } label: {
                        Image(systemName: "xmark").frame(width: 24, height: 22).contentShape(Rectangle())
                    }
                    .help("Đóng bong bóng")
                }
                .buttonStyle(.plain)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 10).frame(height: 30)
                .background(.regularMaterial)
                PlatformConversation(ref: ref, chat: chat).id(ref.id)
                    .environment(\.compactChat, true)
            }
            .background(Color.white)
            .environment(\.colorScheme, .light)
        } else {
            Color.clear
        }
    }
}
