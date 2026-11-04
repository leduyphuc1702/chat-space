import Network
import AppKit
import SwiftUI
import UserNotifications

@main
struct ChatSpaceApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @State private var model = AppModel.shared

    var body: some Scene {
        WindowGroup("ChatSpace") {
            RootView()
                .environment(model)
                .frame(minWidth: 980, minHeight: 620)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 1280, height: 820)
        .commands {
            CommandGroup(after: .sidebar) {
                Button("ChatSpace — Chưa đọc") { model.rail = .inbox }
                    .keyboardShortcut("0", modifiers: .command)
                ForEach(Array(Platform.allCases.enumerated()), id: \.element) { i, p in
                    Button(p.displayName) { model.rail = .platform(p) }
                        .keyboardShortcut(KeyEquivalent(Character("\(i + 1)")), modifiers: .command)
                }
            }
            CommandGroup(after: .textEditing) {
                Button("Tìm kiếm trên mọi nền tảng…") { model.searchOpen.toggle() }
                    .keyboardShortcut("k", modifiers: .command)
                Button("Tìm tin nhắn") { model.searchOpen = true }
                    .keyboardShortcut("f", modifiers: [.command, .shift])
            }
        }

        Settings {
            SettingsView().environment(model)
        }
    }
}

@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()

    /// What the main window shows.
    var rail: RailItem {
        didSet {
            UserDefaults.standard.set(rail.key, forKey: "currentRail")
            if case .platform(let p) = rail { current = p }
            markVisibleRead()
        }
    }
    /// Last platform whose own UI was shown.
    private(set) var current: Platform
    /// The chat open in the ChatSpace inbox and in each custom group.
    var aggregateSelection: [RailItem: ChatRef] = [:] {
        didSet { markVisibleRead() }
    }
    let sessions: [Platform: PlatformSession]
    /// The ⌘K search over every platform is shown.
    var searchOpen = false

    private init() {
        let env = ProcessInfo.processInfo.environment["CHATSPACE_PLATFORM"]
        let saved = env.map { Platform(rawValue: $0) != nil ? "platform:" + $0 : $0 } ?? UserDefaults.standard.string(forKey: "currentRail")
        let r = saved.flatMap(RailItem.init(key:)) ?? .platform(.zalo)
        rail = r
        if case .platform(let p) = r { current = p } else { current = .zalo }
        sessions = Dictionary(uniqueKeysWithValues: Platform.allCases.map { ($0, PlatformSession(platform: $0)) })
    }

    /// The chat open in the main window right now (nil when the inbox/group has nothing selected).
    var mainWindowChat: ChatRef? {
        switch rail {
        case .platform(let p): session(p).selectedChatId.map { ChatRef(platform: p, chatId: $0) }
        default: aggregateSelection[rail]
        }
    }

    /// The user is looking at this chat: in the frontmost main window, or in a focused chat bubble.
    func isDisplaying(_ ref: ChatRef) -> Bool {
        if ChatHeads.shared.isFocused(ref) { return true }
        guard mainWindowChat == ref, NSApp.isActive, let w = NSApp.keyWindow, w.isVisible, !w.isMiniaturized,
              !(w is NSPanel) else { return false }
        return true
    }

    func markVisibleRead() {
        guard let ref = mainWindowChat, isDisplaying(ref) else { return }
        session(ref.platform).markRead(ref.chatId)
    }

    func session(_ p: Platform) -> PlatformSession { sessions[p]! }

    func startAll() {
        for p in Platform.allCases { sessions[p]!.start() }
        observeDockBadge()
        observeResumeTriggers()
    }

    @ObservationIgnored private var pathMonitor: NWPathMonitor?
    @ObservationIgnored private var pathSeen = false
    @ObservationIgnored private var lastResume = Date.distantPast

    /// Waking from sleep and network changes: every bridge checks its socket and fetches what it missed at once
    /// (`connection.resume`), instead of finding out at its next keep-alive, so new messages show up as quickly as
    /// in the official apps when the lid opens.
    private func observeResumeTriggers() {
        _ = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { _ in
            Task { @MainActor in AppModel.shared.resumeAll("wake") }
        }
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { path in
            let satisfied = path.status == .satisfied
            Task { @MainActor in
                let model = AppModel.shared
                // The first report is the state at launch, not a change.
                let first = !model.pathSeen
                model.pathSeen = true
                if satisfied && !first { model.resumeAll("network") }
            }
        }
        monitor.start(queue: DispatchQueue.global(qos: .utility))
        pathMonitor = monitor
    }

    /// A wake and the network coming back usually arrive together: one round per few seconds is enough.
    func resumeAll(_ reason: String) {
        let now = Date()
        guard now.timeIntervalSince(lastResume) > 3 else { return }
        lastResume = now
        for s in sessions.values { s.resume() }
    }

    /// Sum of each platform's own icon-badge number.
    var dockBadge: Int { Platform.allCases.reduce(0) { $0 + session($1).unreadTotal } }

    private func observeDockBadge() {
        let n = withObservationTracking { dockBadge } onChange: {
            Task { @MainActor in AppModel.shared.observeDockBadge() }
        }
        DockBadge.show(n)
    }

    func stopAll() {
        for s in sessions.values { s.stop() }
    }

    func open(platform: Platform, chatId: String) {
        rail = .platform(platform)
        session(platform).select(chatId)
    }

    /// Chats with something unread across every platform, newest first (ChatSpace inbox).
    var inboxRefs: [ChatRef] {
        let ws = Workspace.shared
        return Platform.allCases.flatMap { p in
            session(p).chats.filter { $0.hasUnread && !ws.isIgnored(ChatRef(platform: p, chatId: $0.id)) }
                .map { (ChatRef(platform: p, chatId: $0.id), $0.timestamp) }
        }
        .sorted { $0.1 > $1.1 }.map(\.0)
    }

    /// Badge on the ChatSpace rail item: unread, unmuted, not ignored chats.
    var inboxBadge: Int {
        let ws = Workspace.shared
        return Platform.allCases.reduce(0) { n, p in
            n + session(p).chats.filter { $0.hasUnread && !$0.isMuted && !ws.isIgnored(ChatRef(platform: p, chatId: $0.id)) }.count
        }
    }

    func groupRefs(_ g: CustomGroup) -> [ChatRef] {
        Platform.allCases.flatMap { p in
            session(p).chats.filter { g.matches($0, on: p) }.map { (ChatRef(platform: p, chatId: $0.id), $0) }
        }
        .sorted { a, b in
            if a.1.hasUnread != b.1.hasUnread { return a.1.hasUnread }
            return a.1.timestamp > b.1.timestamp
        }
        .map(\.0)
    }

    func groupBadge(_ g: CustomGroup) -> Int {
        Platform.allCases.reduce(0) { n, p in n + session(p).chats.filter { g.matches($0, on: p) && $0.hasUnread && !$0.isMuted }.count }
    }

    func chat(_ ref: ChatRef) -> Chat? { session(ref.platform).chats.first { $0.id == ref.chatId } }
}

final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        // Needed when launched as a bare SwiftPM executable (no Info.plist).
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        if Bundle.main.bundleIdentifier != nil {
            UNUserNotificationCenter.current().delegate = self
        }
        MainActor.assumeIsolated {
            NativeAssets.shared.registerZaloFonts()
            Notifier.shared.requestAuthorization()
            AppModel.shared.startAll()
            Updater.shared.start()
            if DemoData.enabled { Self.runDemoScript() }
        }
    }

    /// Screenshot helpers: CHATSPACE_DEMO_BUBBLE=<platform>:<chatId>[:open], CHATSPACE_DEMO_SETTINGS=<section>,
    /// CHATSPACE_DEMO_SELECT=<platform>:<chatId> (chat opened in the inbox / group),
    /// CHATSPACE_DEMO_FORWARD=<platform>:<chatId>:<messageId> (forward picker for that message),
    /// CHATSPACE_DEMO_COST=<platform>:<chatId>:<messageId> (image viewer with the VietFlow cost form).
    @MainActor private static func runDemoScript() {
        let env = ProcessInfo.processInfo.environment
        if let b = env["CHATSPACE_DEMO_BUBBLE"] {
            let parts = b.split(separator: ":").map(String.init)
            if parts.count >= 2, let p = Platform(rawValue: parts[0]) {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                    ChatHeads.shared.show(ChatRef(platform: .zalo, chatId: "z1"), expand: false)
                    ChatHeads.shared.show(ChatRef(platform: .messenger, chatId: "m2"), expand: false)
                    ChatHeads.shared.show(ChatRef(platform: p, chatId: parts[1]), expand: parts.count > 2)
                }
            }
        }
        if let sec = env["CHATSPACE_DEMO_SETTINGS"] {
            UserDefaults.standard.set(sec, forKey: "settings.section")
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                NSApp.sendAction(Selector(("showSettingsWindow:")), to: nil, from: nil)
            }
        }
        if let sel = env["CHATSPACE_DEMO_SELECT"] {
            let parts = sel.split(separator: ":").map(String.init)
            if parts.count == 2, let p = Platform(rawValue: parts[0]) {
                let ref = ChatRef(platform: p, chatId: parts[1])
                AppModel.shared.aggregateSelection[AppModel.shared.rail] = ref
                AppModel.shared.session(p).prepare(ref.chatId)
            }
        }
        if let fw = env["CHATSPACE_DEMO_FORWARD"] {
            let parts = fw.split(separator: ":").map(String.init)
            if parts.count == 3, let p = Platform(rawValue: parts[0]) {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                    let s = AppModel.shared.session(p)
                    guard let m = s.messages[parts[1]]?.first(where: { $0.id == parts[2] }) else { return }
                    Forwarder.shared.show(m, from: s)
                }
            }
        }
        if let cost = env["CHATSPACE_DEMO_COST"] {
            let parts = cost.split(separator: ":").map(String.init)
            if parts.count == 3, let p = Platform(rawValue: parts[0]) {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                    let s = AppModel.shared.session(p)
                    guard let m = s.messages[parts[1]]?.first(where: { $0.id == parts[2] }) else { return }
                    MediaViewer.shared.show(session: s, message: m, cost: true)
                }
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        MainActor.assumeIsolated {
            AppModel.shared.stopAll()
            Updater.shared.installOnQuit()
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationDidBecomeActive(_ notification: Notification) {
        MainActor.assumeIsolated {
            AppModel.shared.markVisibleRead()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { true }

    /// ChatSpace never posts banners while frontmost (it plays the in-app tone instead).
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.list])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        let info = response.notification.request.content.userInfo
        let action = response.actionIdentifier
        let text = (response as? UNTextInputNotificationResponse)?.userText
        guard let p = (info["platform"] as? String).flatMap(Platform.init(rawValue:)), let c = info["chatId"] as? String else {
            completionHandler(); return
        }
        Task { @MainActor in
            let s = AppModel.shared.session(p)
            switch action {
            case "reply":
                if let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { s.send(text, in: c, checkMath: false) }
                s.markRead(c)
            case "read":
                s.markRead(c)
            case UNNotificationDismissActionIdentifier:
                break
            default:
                NSApp.activate(ignoringOtherApps: true)
                NSApp.windows.first { $0.canBecomeMain }?.makeKeyAndOrderFront(nil)
                AppModel.shared.open(platform: p, chatId: c)
            }
            completionHandler()
        }
    }
}
