import AppKit
import Foundation
import Observation
import UniformTypeIdentifiers
import UserNotifications

/// Observable state for one platform, fed by its bridge process.
@MainActor
@Observable
final class PlatformSession {
    let platform: Platform

    var authState: AuthState = .connecting
    var selfInfo: SelfInfo?
    var authError: String?
    var qrPayload: String?
    var qrImage: NSImage?
    /// When the current QR stops being scannable (ms epoch), if the bridge said.
    var qrExpiresAt: Double?
    var qrRefreshing = false
    var authStep: AuthStep?
    var authBusy = false
    var connection: String = "connecting"
    var connectionDetail: String?
    var capabilities: Set<String> = []
    var bridgeError: String?

    var chats: [Chat] = []
    var folders: [ChatFolder] = []
    /// Telegram folder shown in the chat list; nil = All Chats.
    var selectedFolderId: String?
    var messages: [String: [Message]] = [:]
    var selectedChatId: String?
    var typing: [String: [TypingInfo]] = [:]
    var reachedStart: Set<String> = []
    var loadingHistory: Set<String> = []
    var drafts: [String: String] = [:]
    /// Images/files pasted or dropped into a composer, sent with the draft as caption.
    var attachments: [String: [URL]] = [:]
    var replyingTo: [String: Message] = [:]
    /// Message whose text the composer is editing, per chat; the draft holds the new text meanwhile.
    var editing: [String: Message] = [:]
    /// Why the last edit in a chat was refused, shown in the edit bar until the next attempt.
    var editError: [String: String] = [:]
    @ObservationIgnored private var editStash: [String: ComposerStash] = [:]
    /// Group members for the "@" list, loaded on first use.
    var members: [String: [ChatMember]] = [:]
    var mentionPicks: [String: [MentionPick]] = [:]
    var mathPreviews: [String: MathPreview] = [:]
    @ObservationIgnored private var loadingMembers: Set<String> = []
    /// Platform id of each message first shown with a "pending-" id, once the send went through.
    @ObservationIgnored private var sentIds: [String: String] = [:]
    /// Messenger's PIN-protected backup of end-to-end encrypted chats (history.status).
    var historyBackup: HistoryBackup = .unknown
    /// Encrypted Messenger chats whose pre-encryption history is loading / fully loaded (history.loadOpen).
    var loadingOpenHistory: Set<String> = []
    var openHistoryDone: Set<String> = []
    /// Zalo history transfer from the phone (history.phoneSync); nil until the bridge reports it.
    var phoneSync: PhoneSyncProgress?
    /// In-chat search jumps here (message id per chat).
    var scrollTarget: [String: String] = [:]
    /// Pinned messages per chat (chats.pins), most recently pinned first; absent until the chat is opened.
    var pins: [String: [Message]] = [:]
    @ObservationIgnored private var loadingPins: Set<String> = []
    /// Bot keyboards put away after a one-time press, by the id of the message that installed them.
    var dismissedKeyboards: Set<String> = []
    /// Chats whose bot keyboard is folded behind the composer's keyboard button.
    var collapsedKeyboards: Set<String> = []
    /// A bot's short answer to a pressed button, shown over the chat for a few seconds.
    var botToast: [String: String] = [:]
    /// Buttons waiting for the bot's answer ("chatId|messageId|row|column").
    var pressingButtons: Set<String> = []
    @ObservationIgnored private var toastTasks: [String: Task<Void, Never>] = [:]

    @ObservationIgnored private var bridge: BridgeProcess?
    @ObservationIgnored private var avatarRequested: Set<String> = []
    /// The member page shown over a conversation (opened from a sender's avatar or name).
    var memberPage: MemberPage?
    @ObservationIgnored private var downloadRequested: Set<String> = []
    @ObservationIgnored private var previews: [String: String] = [:]
    @ObservationIgnored private var restartAttempts = 0
    @ObservationIgnored private var typingExpiry: [String: Task<Void, Never>] = [:]
    /// The user is on a QR screen and expects a scannable code at all times.
    @ObservationIgnored private var qrWanted = false
    @ObservationIgnored private var qrWatchdog: Task<Void, Never>?
    @ObservationIgnored private var sendQueue: Task<Void, Never>?

    init(platform: Platform) {
        self.platform = platform
    }

    var selectedChat: Chat? { chats.first { $0.id == selectedChatId } }
    /// The number each official app puts on its icon: Telegram counts unread messages, the others
    /// count unread conversations. Muted chats are excluded everywhere (the apps' default).
    var unreadTotal: Int {
        let active = chats.filter { $0.hasUnread && !$0.isMuted }
        switch platform {
        case .telegram: return active.reduce(0) { $0 + max($1.unread, 1) }
        default: return active.count
        }
    }

    /// The user is looking at this chat right now: app frontmost, this platform shown, chat open.
    func isViewing(_ chatId: String) -> Bool {
        AppModel.shared.isDisplaying(ChatRef(platform: platform, chatId: chatId))
    }

    /// Chats in the selected Telegram folder (all chats when none is selected).
    var visibleChats: [Chat] { chats(inFolder: selectedFolderId) }

    /// Chats of one Telegram folder in Telegram's order (all chats for nil or an unknown folder).
    func chats(inFolder id: String?) -> [Chat] {
        guard let id, let f = folders.first(where: { $0.id == id }) else { return chats }
        let pinned = f.pinnedChatIds
        let list = chats.filter { f.contains($0) }
        guard !pinned.isEmpty else { return list }
        // Inside a folder Telegram orders by the folder's own pins, not the global ones.
        let top = pinned.compactMap { id in list.first { $0.id == id } }
        let rest = list.filter { !pinned.contains($0.id) }.sorted { $0.timestamp > $1.timestamp }
        return top.map { var c = $0; c.pinned = true; return c } + rest.map { var c = $0; c.pinned = false; return c }
    }

    /// Badge on a folder tab: Telegram counts unread chats, greyed when all of them are muted.
    func folderBadge(_ f: ChatFolder?) -> (count: Int, muted: Bool) {
        let list = (f.map { f in chats.filter { f.contains($0) } } ?? chats).filter(\.hasUnread)
        return (list.count, !list.isEmpty && list.allSatisfy(\.isMuted))
    }
    func can(_ capability: String) -> Bool { capabilities.contains(capability) }

    // MARK: Lifecycle

    func start() {
        guard bridge == nil else { return }
        if DemoData.enabled {
            DemoData.load(into: self)
            for list in messages.values { ImageTextIndexer.shared.consider(list, on: platform) }
            return
        }
        let b = BridgeProcess(platform: platform)
        b.onEvent = { [weak self] name, data in self?.handleEvent(name, data) }
        b.onExit = { [weak self] status in self?.bridgeExited(status) }
        do {
            try b.start()
            bridge = b
            bridgeError = nil
            Task { await handshake() }
        } catch {
            bridgeError = error.localizedDescription
            authState = .error
        }
    }

    func stop() {
        bridge?.onExit = nil
        bridge?.stop()
        bridge = nil
    }

    private func handshake() async {
        do {
            let hello = try await call("bridge.hello", ["config": .object(AppSettings.bridgeConfig(for: platform))])
            if case .array(let caps)? = hello["capabilities"] {
                capabilities = Set(caps.compactMap(\.stringValue))
            }
            configureStorage()
            let status = try await call("auth.status")
            applyAuth(status)
            if authState == .loggedIn { await refreshChats() }
        } catch {
            bridgeError = error.localizedDescription
        }
    }

    private func bridgeExited(_ status: Int32) {
        bridge = nil
        connection = "offline"
        guard restartAttempts < 5 else {
            bridgeError = "Bridge \(platform.displayName) dừng (mã \(status)). Xem bridge.log."
            authState = .error
            return
        }
        restartAttempts += 1
        let delay = UInt64(restartAttempts * restartAttempts) * 1_000_000_000
        Task {
            try? await Task.sleep(nanoseconds: delay)
            start()
        }
    }

    func restart() {
        stop()
        restartAttempts = 0
        start()
    }

    @discardableResult
    func call(_ method: String, _ params: [String: JSONValue] = [:]) async throws -> JSONValue {
        guard let bridge else { throw BridgeError.exited }
        return try await bridge.call(method, params)
    }

    /// The Mac woke up or changed networks: the bridge checks its connection now and fetches what it missed
    /// (`connection.resume`), instead of finding out at its next keep-alive. Bridges without it answer `unsupported`.
    func resume() {
        guard bridge != nil, authState == .loggedIn, !DemoData.enabled else { return }
        Task { _ = try? await call("connection.resume") }
    }

    // MARK: Auth

    func login(method: String = "qr", cookies: String? = nil, phone: String? = nil,
               username: String? = nil, password: String? = nil) {
        authError = nil
        authStep = nil
        qrWanted = method == "qr"
        if qrWanted { qrRefreshing = qrImage != nil }
        var p: [String: JSONValue] = ["method": .string(method)]
        if let cookies { p["cookies"] = .string(cookies) }
        if let phone { p["phone"] = .string(phone) }
        if let username { p["username"] = .string(username) }
        if let password { p["password"] = .string(password) }
        authBusy = true
        Task {
            defer { authBusy = false }
            do { try await call("auth.login", p) } catch { authError = error.localizedDescription }
        }
    }

    /// Answers the current `auth.step` of a multi-step native login.
    func submitStep(_ inputs: [String: String]) {
        authError = nil
        authBusy = true
        Task {
            defer { authBusy = false }
            do {
                try await call("auth.submit", ["inputs": .object(inputs.mapValues { .string($0) })])
            } catch { authError = error.localizedDescription }
        }
    }

    /// Leaves a native login flow locally; the bridge discards it on the next `auth.login`.
    func abandonStep() {
        authStep = nil
        authError = nil
        if authState == .awaitingInput { authState = .loggedOut }
    }

    /// Called by QR screens: start a QR flow unless a live code is already showing.
    func ensureQR() {
        qrWanted = true
        if qrImage == nil || isQRExpired || authState == .loggedOut { refreshQR() }
    }

    /// QR screen went away (other login method, view closed).
    func stopWantingQR() {
        qrWanted = false
        qrWatchdog?.cancel()
    }

    var isQRExpired: Bool {
        guard let e = qrExpiresAt else { return false }
        return e < Date().timeIntervalSince1970 * 1000
    }

    @ObservationIgnored private var autoRefreshes: [Date] = []

    /// `auto`: at most 10 unattended refreshes per 10 minutes, 3 s apart, so a failing bridge can't spin.
    func refreshQR(auto: Bool = false) {
        guard !authBusy, authState != .loggedIn else { return }
        if auto {
            let now = Date()
            autoRefreshes.removeAll { now.timeIntervalSince($0) > 600 }
            if autoRefreshes.count >= 10 || (autoRefreshes.last.map { now.timeIntervalSince($0) < 3 } ?? false) {
                qrRefreshing = false
                return
            }
            autoRefreshes.append(now)
        }
        qrRefreshing = true
        login(method: "qr")
    }

    /// If a code expires and the bridge doesn't replace it within a few seconds, request a new flow.
    private func armQRWatchdog() {
        qrWatchdog?.cancel()
        guard let exp = qrExpiresAt else { return }
        let wait = max(0, exp / 1000 - Date().timeIntervalSince1970) + 6
        qrWatchdog = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000))
            guard !Task.isCancelled, let self, self.qrWanted, self.authState != .loggedIn, self.isQRExpired else { return }
            self.refreshQR(auto: true)
        }
    }

    func cancelStep() {
        Task {
            do { try await call("auth.cancelStep") } catch { authError = error.localizedDescription }
        }
    }

    func submit(password: String? = nil, code: String? = nil) {
        authError = nil
        var p: [String: JSONValue] = [:]
        if let password { p["password"] = .string(password) }
        if let code { p["code"] = .string(code) }
        Task {
            do { try await call("auth.submit", p) } catch { authError = error.localizedDescription }
        }
    }

    func logout() {
        Task {
            _ = try? await call("auth.logout")
            chats = []
            messages = [:]
            selectedChatId = nil
            authState = .loggedOut
            Notifier.shared.reconcile(platform: platform, unread: [])
        }
    }

    private func applyAuth(_ v: JSONValue) {
        if let s = v["state"]?.stringValue, let st = AuthState(rawValue: s) {
            let wasLoggedIn = authState == .loggedIn
            authState = st
            if st != .awaitingInput { authStep = nil }
            else if let step = v["step"], let decoded = try? step.decode(AuthStep.self) { authStep = decoded }
            if st == .loggedIn {
                qrImage = nil
                qrPayload = nil
                qrExpiresAt = nil
                qrWanted = false
                qrWatchdog?.cancel()
                restartAttempts = 0
                if !wasLoggedIn { Task { await refreshChats() } }
            } else if st == .loggedOut {
                historyBackup = .unknown
            }
        }
        if let selfV = v["self"], selfV != .null { selfInfo = try? selfV.decode(SelfInfo.self) }
        let e = v["error"]?.stringValue
        if let e, e != "qr_expired" { authError = e }
        // The platform stopped issuing codes: start a fresh flow instead of leaving a dead QR on screen.
        if qrWanted && authState == .loggedOut && (e == "qr_expired" || qrImage != nil) {
            qrImage = nil
            refreshQR(auto: true)
        }
    }

    // MARK: Data

    func refreshChats() async {
        do {
            let r = try await call("chats.list", ["limit": 200])
            chats = sorted(try r["chats"]?.decode([Chat].self) ?? [])
            Notifier.shared.reconcile(platform: platform, unread: Set(chats.filter(\.hasUnread).map(\.id)))
            // Pins may have changed while the bridge was away; other chats reload them when reopened.
            pins = pins.filter { $0.key == selectedChatId }
            if let id = selectedChatId { loadPins(id, force: true) }
        } catch {
            bridgeError = error.localizedDescription
        }
        if can("folders"), let r = try? await call("chats.folders"),
           let f = try? r["folders"]?.decode([ChatFolder].self) {
            applyFolders(f)
        }
        ImageTextIndexer.shared.crawl(self)
    }

    func applyFolders(_ f: [ChatFolder]) {
        folders = f
        if let id = selectedFolderId, !f.contains(where: { $0.id == id }) { selectedFolderId = nil }
    }

    // MARK: Storage

    /// Pushes Settings → Dữ liệu và bộ nhớ to the bridge (unknown methods are fine: Messenger has no cache).
    func configureStorage() {
        guard bridge != nil else { return }
        let params = Workspace.shared.storage.bridgeParams
        Task { _ = try? await call("storage.configure", params) }
    }

    struct StorageUsage: Equatable { var mediaBytes: Double; var mediaFiles: Int; var databaseBytes: Double }

    func storageUsage() async -> StorageUsage? {
        if DemoData.enabled {
            let seed = Double(platform.rawValue.count)
            return StorageUsage(mediaBytes: seed * 61_000_000, mediaFiles: Int(seed) * 140, databaseBytes: seed * 4_200_000)
        }
        guard let r = try? await call("storage.usage") else { return nil }
        func n(_ k: String) -> Double { if case .number(let v)? = r[k] { return v }; return 0 }
        return StorageUsage(mediaBytes: n("mediaBytes"), mediaFiles: Int(n("mediaFiles")), databaseBytes: n("databaseBytes"))
    }

    func clearStorage(olderThanDays: Int? = nil) async -> Double {
        var p: [String: JSONValue] = [:]
        if let d = olderThanDays { p["olderThanDays"] = .number(Double(d)) }
        guard let r = try? await call("storage.clear", p), case .number(let freed)? = r["freedBytes"] else { return 0 }
        for k in messages.keys {
            for i in messages[k]!.indices where messages[k]![i].media?.path != nil
                && !FileManager.default.fileExists(atPath: messages[k]![i].media!.path!) {
                messages[k]![i].media?.path = nil
            }
        }
        downloadRequested.removeAll()
        return freed
    }

    func select(_ chatId: String) {
        if memberPage?.chatId != chatId { memberPage = nil }
        selectedChatId = chatId
        prepare(chatId)
    }

    /// Opens a chat outside this platform's own list (ChatSpace inbox, custom group, chat bubble).
    func prepare(_ chatId: String) {
        trimMessages(except: chatId)
        if messages[chatId] == nil { Task { await loadMessages(chatId) } }
        loadPins(chatId)
        if let c = chats.first(where: { $0.id == chatId }), c.unread > 0 { openedUnread[chatId] = c.unread }
        markRead(chatId)
    }

    /// Unread count a chat had when it was opened (it is marked read at once), for the scroll view to land
    /// on the first unread message. Read once.
    @ObservationIgnored private var openedUnread: [String: Int] = [:]

    func takeOpenedUnread(_ chatId: String) -> Int {
        openedUnread.removeValue(forKey: chatId) ?? 0
    }

    /// The oldest of the `count` newest incoming messages, paging back a little when they aren't all loaded
    /// (never on Messenger, where each page is a request to Meta: it lands on the oldest loaded one instead).
    func firstUnread(_ chatId: String, count: Int) async -> String? {
        func find() -> (id: String?, enough: Bool) {
            guard let list = messages[chatId] else { return (nil, false) }
            var seen = 0
            for m in list.reversed() where !m.fromMe && m.kind != .system {
                seen += 1
                if seen == count { return (m.id, true) }
            }
            return (list.first { !$0.fromMe && $0.kind != .system }?.id, false)
        }
        var pages = 0
        while case let r = find(), !r.enough, platform != .messenger, pages < 4, !reachedStart.contains(chatId) {
            while loadingHistory.contains(chatId) { try? await Task.sleep(nanoseconds: 40_000_000) }
            guard await fetchOlder(chatId, limit: 100) else { break }
            pages += 1
        }
        return find().id
    }

    // MARK: Bot buttons

    /// The bot keyboard a chat shows under its composer: the newest incoming message that installed one, unless a
    /// later message removed it (ReplyKeyboardHide) or the user put it away after a one-time press.
    func botKeyboard(_ chatId: String) -> (message: Message, keyboard: ReplyKeyboard)? {
        guard can("bot_buttons"), let list = messages[chatId] else { return nil }
        for m in list.reversed() {
            if m.hideKeyboard == true { return nil }
            if let kb = m.replyKeyboard, !m.fromMe, !kb.rows.isEmpty {
                return dismissedKeyboards.contains(m.id) ? nil : (m, kb)
            }
        }
        return nil
    }

    /// A button of an inline keyboard (`keyboard` nil) or of the chat's bot keyboard was pressed.
    func pressButton(_ button: BotButton, row: Int, column: Int, in message: Message, keyboard: ReplyKeyboard? = nil) {
        let chatId = message.chatId
        switch button.kind {
        case .text:
            // A custom-keyboard button simply sends its label; one-time keyboards fold away after that.
            if keyboard?.oneTime == true { dismissedKeyboards.insert(message.id) }
            Task {
                do { _ = try await post(button.text, in: chatId) } catch { bridgeError = error.localizedDescription }
            }
        case .url, .login, .userProfile:
            if let u = button.url, let url = URL(string: u) { NSWorkspace.shared.open(url) }
        case .copy:
            guard let t = button.copyText else { return }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(t, forType: .string)
            toast("Text copied to clipboard", in: chatId)
        case .switchInline:
            guard let insert = button.insert else { toast("This bot's inline mode is not available in ChatSpace", in: chatId); return }
            drafts[chatId] = insert
            DispatchQueue.main.async { Composer.focus() }
        case .unsupported:
            toast("This button needs the Telegram app", in: chatId)
        case .callback, .game, .webApp:
            if DemoData.enabled { toast("\(message.senderName ?? "Bot"): \(button.text) ✓", in: chatId); return }
            let key = "\(chatId)|\(message.id)|\(row)|\(column)"
            guard !pressingButtons.contains(key) else { return }
            pressingButtons.insert(key)
            Task {
                defer { pressingButtons.remove(key) }
                do {
                    let r = try await call("messages.clickButton", [
                        "chatId": .string(chatId), "messageId": .string(message.id),
                        "row": .number(Double(row)), "column": .number(Double(column)),
                    ])
                    if let u = r["url"]?.stringValue, let url = URL(string: u) { NSWorkspace.shared.open(url) }
                    if let text = r["message"]?.stringValue, !text.isEmpty {
                        if r["alert"]?.boolValue == true {
                            let alert = NSAlert()
                            alert.messageText = message.senderName ?? "Telegram"
                            alert.informativeText = text
                            alert.runModal()
                        } else {
                            toast(text, in: chatId)
                        }
                    }
                } catch {
                    toast(error.localizedDescription, in: chatId)
                }
            }
        }
    }

    /// Shows a bot's answer (or a short notice) over the chat for a few seconds.
    func toast(_ text: String, in chatId: String) {
        botToast[chatId] = text
        toastTasks[chatId]?.cancel()
        toastTasks[chatId] = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            guard !Task.isCancelled else { return }
            self?.botToast[chatId] = nil
        }
    }

    func toggleBotKeyboard(_ chatId: String) {
        if collapsedKeyboards.contains(chatId) { collapsedKeyboards.remove(chatId) } else { collapsedKeyboards.insert(chatId) }
    }

    // MARK: Pins & mute

    /// Asks the bridge once per chat; `pins.update` refreshes chats already loaded.
    func loadPins(_ chatId: String, force: Bool = false) {
        guard can("pins"), force || pins[chatId] == nil, !loadingPins.contains(chatId) else { return }
        if DemoData.enabled {
            pins[chatId] = DemoData.pins(chatId, in: self)
            return
        }
        loadingPins.insert(chatId)
        Task {
            defer { loadingPins.remove(chatId) }
            guard let r = try? await call("chats.pins", ["chatId": .string(chatId)]),
                  let list = try? r["messages"]?.decode([Message].self) else { return }
            pins[chatId] = list
        }
    }

    /// A pin as currently loaded in the chat (edits and recalls show), else as the bridge reported it.
    func pinned(_ chatId: String) -> [Message] {
        (pins[chatId] ?? []).map { p in messages[chatId]?.first { $0.id == p.id } ?? p }
    }

    /// Mutes the chat on the platform itself until `until` (ms epoch), forever (-1), or unmutes it (0).
    /// Shown at once; put back with an alert if the platform refuses.
    func setMute(_ chatId: String, until: Double) {
        guard let i = chats.firstIndex(where: { $0.id == chatId }) else { return }
        let before = chats[i]
        chats[i].muted = until != 0
        chats[i].mutedUntil = until == 0 ? nil : until
        if until != 0 { Notifier.shared.clear(platform: platform, chatId: chatId) }
        if DemoData.enabled { return }
        Task {
            do {
                let r = try await call("chats.mute", ["chatId": .string(chatId), "until": .number(until)])
                if let c = try? r["chat"]?.decode(Chat.self) { upsertChat(c) }
            } catch {
                if let j = chats.firstIndex(where: { $0.id == chatId }) {
                    chats[j].muted = before.muted
                    chats[j].mutedUntil = before.mutedUntil
                }
                let alert = NSAlert()
                alert.messageText = until == 0 ? "Không bật được thông báo" : "Không tắt được thông báo"
                alert.informativeText = "\(before.title) (\(platform.displayName)): \(error.localizedDescription)"
                alert.runModal()
            }
        }
    }

    private static let keptPerChat = 50

    /// A chat scrolled far back holds hundreds of messages; once left, keep its newest page only
    /// (scrolling up reloads the rest from the bridge).
    private func trimMessages(except chatId: String) {
        let bubble = ChatHeads.shared.expanded
        for (id, list) in messages where id != chatId && list.count > Self.keptPerChat + 10 {
            if bubble == ChatRef(platform: platform, chatId: id) { continue }
            messages[id] = Array(list.suffix(Self.keptPerChat))
            reachedStart.remove(id)
        }
    }

    func loadMessages(_ chatId: String) async {
        guard !loadingHistory.contains(chatId) else { return }
        loadingHistory.insert(chatId)
        defer { loadingHistory.remove(chatId) }
        do {
            let r = try await call("messages.list", ["chatId": .string(chatId), "limit": 50])
            let list = try r["messages"]?.decode([Message].self) ?? []
            messages[chatId] = merge(messages[chatId] ?? [], list)
            if list.count < 50 { reachedStart.insert(chatId) }
            ImageTextIndexer.shared.consider(list, on: platform)
        } catch {
            bridgeError = error.localizedDescription
        }
    }

    func loadOlder(_ chatId: String) {
        Task { await fetchOlder(chatId) }
    }

    /// One page of older history; false when nothing more could be loaded.
    @discardableResult
    private func fetchOlder(_ chatId: String, limit: Int = 50) async -> Bool {
        guard !reachedStart.contains(chatId), !loadingHistory.contains(chatId),
              let first = messages[chatId]?.first else { return false }
        loadingHistory.insert(chatId)
        defer { loadingHistory.remove(chatId) }
        do {
            let r = try await call("messages.list", ["chatId": .string(chatId), "limit": .number(Double(limit)), "before": .string(first.id)])
            let list = try r["messages"]?.decode([Message].self) ?? []
            if list.isEmpty { reachedStart.insert(chatId) }
            messages[chatId] = merge(messages[chatId] ?? [], list)
            ImageTextIndexer.shared.consider(list, on: platform)
            return !list.isEmpty
        } catch {
            reachedStart.insert(chatId)
            return false
        }
    }

    // MARK: Private chat with a member

    /// The one-to-one chat with `userId`, when it is in the chat list.
    func directChat(with userId: String) -> Chat? {
        // Zalo prefixes 1:1 chat ids with "u:".
        let ids: Set<String> = platform == .zalo ? [userId, "u:" + userId] : [userId]
        return chats.first { ($0.kind == .direct || $0.kind == .bot) && ids.contains($0.id) }
    }

    /// Opens the private chat with a group member: the existing one, else the one the bridge finds or starts
    /// (`chats.direct`). False when the platform can't open it from here.
    func openDirect(userId: String, name: String, avatar: String?) async -> Bool {
        var chat = directChat(with: userId)
        if chat == nil, DemoData.enabled {
            let c = Chat(id: userId, title: name, avatar: avatar, kind: .direct, unread: 0, timestamp: Date().timeIntervalSince1970 * 1000)
            chats.insert(c, at: 0)
            messages[userId] = messages[userId] ?? []
            reachedStart.insert(userId)
            chat = c
        }
        if chat == nil, can("direct_chat"),
           let r = try? await call("chats.direct", ["userId": .string(userId)]),
           var c = try? r["chat"]?.decode(Chat.self) {
            if c.avatar == nil { c.avatar = avatar }
            upsertChat(c)
            chat = c
        }
        guard let chat else { return false }
        AppModel.shared.rail = .platform(platform)
        select(chat.id)
        return true
    }

    // MARK: Search

    /// Messages matching `query` (every word, accent-insensitive), newest first: the bridge searches all it has
    /// (`messages.search`), otherwise only what is loaded here. `before` (ms) asks for the next page.
    /// `senderId` keeps one person's messages (with no query or kinds: all of them).
    func search(_ query: String, chatId: String? = nil, kinds: [MessageKind]? = nil, senderId: String? = nil,
                before: Double? = nil, limit: Int = 50) async throws -> [Message] {
        try await searchPage(query, chatId: chatId, kinds: kinds, senderId: senderId, before: before, limit: limit).messages
    }

    /// `search` plus where the next page starts (`before` for the next call), nil at the end of the history.
    /// A bridge that filters after reading (sender, kinds) can return a short page with more to come, so it
    /// says `next` itself; for one that does not, a full page means there may be more.
    func searchPage(_ query: String, chatId: String? = nil, kinds: [MessageKind]? = nil, senderId: String? = nil,
                    before: Double? = nil, limit: Int = 50) async throws -> (messages: [Message], next: Double?) {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty || !(kinds ?? []).isEmpty || senderId != nil else { return ([], nil) }
        let list: [Message]
        if can("search"), bridge != nil {
            var p: [String: JSONValue] = ["query": .string(q), "limit": .number(Double(limit))]
            if let chatId { p["chatId"] = .string(chatId) }
            if let senderId { p["senderId"] = .string(senderId) }
            if let kinds, !kinds.isEmpty { p["kinds"] = .array(kinds.map { .string($0.rawValue) }) }
            if let before { p["before"] = .number(before) }
            let r = try await call("messages.search", p)
            list = try r["messages"]?.decode([Message].self) ?? []
            switch r["next"] {
            case .number(let n)?: return (list, n)
            case .null?: return (list, nil)
            default: break
            }
        } else {
            list = SearchText.localSearch(messages, query: q, chatId: chatId, kinds: kinds, senderId: senderId, before: before, limit: limit)
        }
        return (list, list.count >= limit ? list.map(\.timestamp).min() : nil)
    }

    /// Message briefly highlighted after a search jumped to it, per chat.
    var highlighted: [String: String] = [:]
    /// Chat whose history is being paged back to reach a search result.
    var revealing: String?

    /// Opens the history of `m`'s chat far enough back to contain it, then scrolls there and highlights it.
    /// Returns false when the message could not be reached (deleted, or too far back).
    @discardableResult
    func reveal(_ m: Message, maxPages: Int = 60) async -> Bool {
        let chatId = m.chatId
        revealing = chatId
        defer { if revealing == chatId { revealing = nil } }
        if messages[chatId] == nil { await loadMessages(chatId) }
        // A WhatsApp reply can point at an album's hidden parent, whose id is the items' groupId.
        func loaded() -> Bool { messages[chatId]?.contains { $0.id == m.id || $0.groupId == m.id } ?? false }
        var pages = 0
        while !loaded(), pages < maxPages {
            while loadingHistory.contains(chatId) { try? await Task.sleep(nanoseconds: 50_000_000) }
            if let first = messages[chatId]?.first, first.timestamp < m.timestamp - 1 { break }
            guard await fetchOlder(chatId, limit: 100) else { break }
            pages += 1
        }
        guard loaded() else { return false }
        scrollTarget[chatId] = nil
        highlighted[chatId] = m.id
        try? await Task.sleep(nanoseconds: 30_000_000)
        scrollTarget[chatId] = m.id
        Task {
            try? await Task.sleep(nanoseconds: 2_600_000_000)
            if highlighted[chatId] == m.id { highlighted[chatId] = nil }
        }
        return true
    }

    /// Without an explicit file, the chat's staged attachments go out too, the text as the first one's caption.
    /// Text with arithmetic waits in `mathPreviews` unless `checkMath` is off; sending it again picks the computed version.
    func send(_ text: String, in chatId: String, filePath: String? = nil, checkMath: Bool = true) {
        if filePath == nil, let target = editing[chatId] {
            commitEdit(target, text: text)
            return
        }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let files = filePath.map { [$0] } ?? (attachments[chatId] ?? []).map(\.path)
        guard !trimmed.isEmpty || !files.isEmpty else { return }
        if checkMath, Workspace.shared.mathPreview, let pieces = MessageMath.annotate(trimmed) {
            let preview = MathPreview(original: trimmed, pieces: pieces, filePath: filePath)
            if mathPreviews[chatId] == preview { resolveMath(in: chatId, computed: true) } else { mathPreviews[chatId] = preview }
            return
        }
        mathPreviews[chatId] = nil
        let staged = filePath == nil ? attachments[chatId] : nil
        if filePath == nil { attachments[chatId] = nil }
        let reply = replyingTo.removeValue(forKey: chatId)
        let picks = mentionPicks.removeValue(forKey: chatId) ?? []
        drafts[chatId] = ""
        let ref = ChatRef(platform: platform, chatId: chatId)
        let translator = Translator.shared
        translator.sendError[ref.id] = nil
        let previous = sendQueue
        sendQueue = Task {
            // Translation takes a round trip; queueing keeps quick successive sends in order.
            await previous?.value
            var outgoing = trimmed
            let language = await translator.resolveOutgoing(ref, reply: reply)
            translator.replyAutoOff.remove(ref.id)
            if !TranslationLanguages.same(language, translator.settings.language) && Translator.hasWords(trimmed) {
                do {
                    outgoing = try await Translator.translate(trimmed, keeping: picks.map { "@" + $0.name }, to: language)
                } catch {
                    // The untranslated text would reach someone reading another language: hand it back instead.
                    if (drafts[chatId] ?? "").isEmpty {
                        drafts[chatId] = text
                        if let reply, replyingTo[chatId] == nil { replyingTo[chatId] = reply }
                        if !picks.isEmpty { mentionPicks[chatId] = picks }
                        if let staged, attachments[chatId] == nil { attachments[chatId] = staged }
                    }
                    translator.sendError[ref.id] = "Không dịch được sang \(TranslationLanguages.name(language)) (\(error.localizedDescription)). Tin nhắn chưa được gửi."
                    return
                }
            }
            let mentions = Mentions.resolve(picks, in: outgoing)
            // One after another, so several pasted images arrive in order.
            for (i, f) in (files.isEmpty ? [nil] : files.map { Optional($0) }).enumerated() {
                do {
                    let kind: MessageKind? = f.flatMap { UTType(filenameExtension: ($0 as NSString).pathExtension)?.conforms(to: .image) == true ? .image : nil }
                    let sent = try await post(i == 0 ? outgoing : "", in: chatId, filePath: f, kind: kind,
                                              reply: i == 0 ? reply : nil, mentions: i == 0 ? mentions : [])
                    if i == 0, outgoing != trimmed, let sent { translator.rememberSent(sent, typed: trimmed, language: language) }
                } catch {
                    bridgeError = error.localizedDescription
                }
            }
        }
    }

    func resolveMath(in chatId: String, computed: Bool) {
        guard let p = mathPreviews.removeValue(forKey: chatId) else { return }
        send(computed ? p.computed : p.original, in: chatId, filePath: p.filePath, checkMath: false)
    }

    func stage(_ files: [URL], in chatId: String) {
        let have = attachments[chatId] ?? []
        attachments[chatId] = have + files.filter { !have.contains($0) }
    }

    func unstage(_ file: URL, in chatId: String) {
        attachments[chatId]?.removeAll { $0 == file }
        if attachments[chatId]?.isEmpty == true { attachments[chatId] = nil }
    }

    /// Sends without touching the chat's composer (draft, reply, mentions), showing it at once as pending
    /// and marking it failed if the bridge refuses. `kind` only shapes that pending bubble.
    @discardableResult
    func post(_ text: String, in chatId: String, filePath: String? = nil, kind: MessageKind? = nil,
              reply: Message? = nil, mentions: [[String: JSONValue]] = []) async throws -> Message? {
        let tempId = "pending-\(UUID().uuidString)"
        var optimistic = Message(id: tempId, chatId: chatId, senderId: selfInfo?.id ?? "me", senderName: selfInfo?.name,
                                 fromMe: true, timestamp: Date().timeIntervalSince1970 * 1000,
                                 kind: kind ?? (filePath == nil ? .text : .file), text: text.isEmpty ? nil : text,
                                 status: .pending)
        if let filePath { optimistic.media = Media(path: filePath, fileName: (filePath as NSString).lastPathComponent) }
        if let reply { optimistic.replyTo = ReplyRef(id: reply.id, text: reply.text, senderName: reply.senderName, kind: reply.kind) }
        upsert(optimistic)

        var reply = reply
        if let pending = reply {
            do {
                reply = try await settled(pending)
            } catch {
                if let i = messages[chatId]?.firstIndex(where: { $0.id == tempId }) { messages[chatId]?[i].status = .failed }
                throw error
            }
        }
        var p: [String: JSONValue] = ["chatId": .string(chatId)]
        if !text.isEmpty { p["text"] = .string(text) }
        if let filePath { p["filePath"] = .string(filePath) }
        if let reply { p["replyTo"] = .string(reply.id) }
        if !mentions.isEmpty && filePath == nil { p["mentions"] = .array(mentions.map { .object($0) }) }
        if DemoData.enabled {
            var sent = optimistic
            sent.id = "demo-\(UUID().uuidString)"
            sent.status = .sent
            sentIds[tempId] = sent.id
            messages[chatId]?.removeAll { $0.id == tempId }
            upsert(sent)
            return sent
        }
        do {
            let r = try await call("messages.send", p)
            let m = try r["message"]?.decode(Message.self)
            if let m {
                sentIds[tempId] = m.id
                messages[chatId]?.removeAll { $0.id == tempId }
                upsert(m)
            }
            if platform == .whatsapp || platform == .telegram { NativeAssets.shared.play(platform, .sent) }
            return m
        } catch {
            if let i = messages[chatId]?.firstIndex(where: { $0.id == tempId }) {
                messages[chatId]?[i].status = .failed
            }
            throw error
        }
    }

    /// `m` under its platform id. A message still showing as pending (an image uploading) is waited for,
    /// since replying to its local "pending-" id is refused by every bridge.
    func settled(_ m: Message) async throws -> Message {
        guard m.id.hasPrefix("pending-") else { return m }
        for _ in 0..<240 {
            if let id = sentIds[m.id] {
                if let real = messages[m.chatId]?.first(where: { $0.id == id }) { return real }
                var real = m
                real.id = id
                real.status = .sent
                return real
            }
            let current = messages[m.chatId]?.first { $0.id == m.id }
            if current == nil || current?.status == .failed { break }
            try await Task.sleep(nanoseconds: 500_000_000)
        }
        throw BridgeError.remote(code: "pending", message: "Tin được trả lời chưa gửi xong, thử lại sau khi nó gửi xong")
    }

    /// The platform's own forward (capability `forward`): same message, "Forwarded" label, no re-upload.
    func forwardNatively(_ message: Message, to chatId: String) async throws {
        if DemoData.enabled {
            var m = message
            m.id = "demo-\(UUID().uuidString)"
            m.chatId = chatId
            m.timestamp = Date().timeIntervalSince1970 * 1000
            m.forwarded = !message.fromMe
            m.fromMe = true
            m.senderId = selfInfo?.id ?? "me"
            m.reactions = nil
            m.replyTo = nil
            m.status = .sent
            upsert(m)
            return
        }
        let r = try await call("messages.forward", ["fromChatId": .string(message.chatId), "messageId": .string(message.id),
                                                    "toChatId": .string(chatId)])
        if let m = try r["message"]?.decode(Message.self) { upsert(m) }
    }

    func loadMembers(_ chatId: String) {
        guard can("mentions"), !loadingMembers.contains(chatId) else { return }
        if DemoData.enabled {
            members[chatId] = DemoData.members(chatId)
            return
        }
        loadingMembers.insert(chatId)
        Task {
            defer { loadingMembers.remove(chatId) }
            do {
                let r = try await call("chats.members", ["chatId": .string(chatId)])
                members[chatId] = try r["members"]?.decode([ChatMember].self) ?? []
            } catch {
                members[chatId] = []
                try? await Task.sleep(nanoseconds: 20_000_000_000)
                if members[chatId]?.isEmpty == true { members[chatId] = nil }
            }
        }
    }

    func checkHistoryBackup() async {
        guard can("history_pin"), historyBackup == .unknown, !DemoData.enabled else { return }
        for _ in 0..<6 {
            if let r = try? await call("history.status") {
                historyBackup = r["restored"]?.boolValue == true ? .restored
                    : r["available"]?.boolValue == true ? .locked : .none
                return
            }
            try? await Task.sleep(nanoseconds: 10_000_000_000)
        }
    }

    /// Unlocks the encrypted backup; on success every chat reloads with its restored history.
    func restoreHistory(pin: String) async -> HistoryRestoreResult {
        do {
            let r = try await call("history.restore", ["pin": .string(pin)])
            guard r["restored"]?.boolValue == true else {
                if r["noBackup"]?.boolValue == true { return .noBackup }
                if case .number(let n)? = r["attemptsRemaining"] { return .wrongPin(attemptsLeft: Int(n)) }
                return .wrongPin(attemptsLeft: nil)
            }
            historyBackup = .restored
            messages = messages.filter { !$0.value.isEmpty }
            reachedStart.removeAll()
            if let id = selectedChatId, messages[id] == nil { await loadMessages(id) }
            await refreshChats()
            return .restored
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    /// Loads the next older page of an encrypted chat's pre-encryption history. Contacts Meta, so it
    /// runs only when the user asks for it.
    func loadOpenHistory(_ chatId: String) {
        guard can("open_history"), !loadingOpenHistory.contains(chatId), !openHistoryDone.contains(chatId) else { return }
        loadingOpenHistory.insert(chatId)
        Task {
            defer { loadingOpenHistory.remove(chatId) }
            do {
                let r = try await call("history.loadOpen", ["chatId": .string(chatId)])
                if r["done"]?.boolValue == true { openHistoryDone.insert(chatId) }
                guard case .number(let added)? = r["added"], added > 0 else { return }
                let limit = Double(messages[chatId]?.count ?? 0) + added
                let list = try await call("messages.list", ["chatId": .string(chatId), "limit": .number(limit)])
                messages[chatId] = merge(messages[chatId] ?? [], try list["messages"]?.decode([Message].self) ?? [])
            } catch {
                bridgeError = error.localizedDescription
            }
        }
    }

    /// Asks the phone for the account's history (the user approves it in the Zalo app on the phone).
    func startPhoneSync() {
        guard can("phone_sync") else { return }
        phoneSync = PhoneSyncProgress(state: .preparing)
        Task {
            do {
                let r = try await call("history.phoneSync", ["action": "start"])
                if let p = try? r.decode(PhoneSyncProgress.self), phoneSync?.state == .preparing { phoneSync = p }
            } catch {
                phoneSync = PhoneSyncProgress(state: .failed, error: error.localizedDescription)
            }
        }
    }

    func cancelPhoneSync() {
        Task { _ = try? await call("history.phoneSync", ["action": "cancel"]) }
    }

    func loadPhoneSyncStatus() async {
        guard can("phone_sync"), phoneSync == nil, !DemoData.enabled,
              let r = try? await call("history.phoneSync", ["action": "status"]) else { return }
        phoneSync = try? r.decode(PhoneSyncProgress.self)
    }

    private func applyPhoneSync(_ p: PhoneSyncProgress) {
        let ended = phoneSync?.running == true && !p.running
        phoneSync = p
        guard ended else { return }
        if p.state == .done { UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: "zaloPhoneSyncedAt") }
        // Every chat may have gained older messages: drop the cached pages so they reload from the bridge.
        messages = [:]
        reachedStart.removeAll()
        Task {
            await refreshChats()
            if let id = selectedChatId { await loadMessages(id) }
            if let ref = ChatHeads.shared.expanded, ref.platform == platform { await loadMessages(ref.chatId) }
        }
    }

    func markRead(_ chatId: String) {
        Notifier.shared.clear(platform: platform, chatId: chatId)
        guard let i = chats.firstIndex(where: { $0.id == chatId }), chats[i].hasUnread else { return }
        chats[i].unread = 0
        chats[i].markedUnread = false
        Task { try? await call("messages.markRead", ["chatId": .string(chatId)]) }
    }

    func markAllRead() {
        for c in visibleChats where c.hasUnread { markRead(c.id) }
    }

    func react(_ message: Message, emoji: String) {
        guard can("reactions") else { return }
        let mine = message.reactions?.first { $0.fromMe }?.emoji
        let target = mine == emoji ? "" : emoji
        if DemoData.enabled {
            var m = message
            var rs = (m.reactions ?? []).compactMap { r -> Reaction? in
                guard r.fromMe else { return r }
                return r.count > 1 ? Reaction(emoji: r.emoji, count: r.count - 1, fromMe: false) : nil
            }
            if !target.isEmpty {
                if let i = rs.firstIndex(where: { $0.emoji == target }) { rs[i].count += 1; rs[i].fromMe = true }
                else { rs.append(Reaction(emoji: target, count: 1, fromMe: true)) }
            }
            m.reactions = rs.isEmpty ? nil : rs
            if let i = messages[m.chatId]?.firstIndex(where: { $0.id == m.id }) { messages[m.chatId]?[i] = m }
            return
        }
        Task {
            try? await call("messages.react", ["chatId": .string(message.chatId), "messageId": .string(message.id), "emoji": .string(target)])
        }
    }

    func delete(_ message: Message, forEveryone: Bool) {
        Task {
            do {
                try await call("messages.delete", ["chatId": .string(message.chatId), "messageId": .string(message.id), "forEveryone": .bool(forEveryone)])
                if !forEveryone { messages[message.chatId]?.removeAll { $0.id == message.id } }
            } catch {
                bridgeError = error.localizedDescription
            }
        }
    }

    // MARK: Editing

    /// Each official app's rule: your own messages only, with text (Telegram: also a media caption),
    /// within its time limit — Telegram 48 hours (none in Saved Messages and channels), WhatsApp and
    /// Messenger 15 minutes. Zalo has no editing.
    func canEdit(_ m: Message) -> Bool {
        guard can("edit"), m.fromMe, !m.isDeleted, m.forwarded != true, !m.id.hasPrefix("pending-"),
              m.status != .pending, m.status != .failed else { return false }
        let hasText = !(m.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let age = Date().timeIntervalSince(m.date)
        switch platform {
        case .telegram:
            let captioned: Set<MessageKind> = [.text, .link, .image, .video, .gif, .file, .audio, .voice]
            guard captioned.contains(m.kind), m.kind != .text || hasText else { return false }
            let kind = chats.first { $0.id == m.chatId }?.kind
            return kind == .self || kind == .channel || age < 48 * 3600
        case .whatsapp, .messenger:
            return (m.kind == .text || m.kind == .link) && hasText && age < 15 * 60
        case .zalo:
            return false
        }
    }

    /// Puts the message's text in the composer; whatever was being written comes back afterwards.
    func beginEdit(_ m: Message, force: Bool = false) {
        guard force || canEdit(m) else { return }
        let id = m.chatId
        if editing[id] == nil {
            editStash[id] = ComposerStash(draft: drafts[id] ?? "", reply: replyingTo[id], picks: mentionPicks[id], files: attachments[id])
        }
        editing[id] = m
        editError[id] = nil
        replyingTo[id] = nil
        attachments[id] = nil
        mentionPicks[id] = nil
        mathPreviews[id] = nil
        drafts[id] = m.text ?? ""
        DispatchQueue.main.async { Composer.focus() }
    }

    func cancelEdit(_ chatId: String) {
        guard editing.removeValue(forKey: chatId) != nil else { return }
        editError[chatId] = nil
        let s = editStash.removeValue(forKey: chatId)
        drafts[chatId] = s?.draft ?? ""
        if replyingTo[chatId] == nil { replyingTo[chatId] = s?.reply }
        mentionPicks[chatId] = s?.picks
        attachments[chatId] = s?.files
    }

    /// Replying ends an edit in progress, as in the official apps.
    func startReply(_ m: Message) {
        cancelEdit(m.chatId)
        replyingTo[m.chatId] = m
    }

    /// Shows the new text at once; if the platform refuses, the old text returns and the editor reopens
    /// with what was typed and the reason.
    private func commitEdit(_ target: Message, text: String) {
        let chatId = target.chatId
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard canEdit(target) else {
            editError[chatId] = "Đã quá thời gian cho phép sửa tin nhắn này."
            return
        }
        let picks = mentionPicks[chatId] ?? []
        cancelEdit(chatId)
        guard trimmed != (target.text ?? "").trimmingCharacters(in: .whitespacesAndNewlines) else { return }
        let before = messages[chatId]?.first { $0.id == target.id } ?? target
        var optimistic = before
        optimistic.text = trimmed
        optimistic.edited = true
        replaceLocally(optimistic)
        if DemoData.enabled { return }
        var p: [String: JSONValue] = ["chatId": .string(chatId), "messageId": .string(target.id), "text": .string(trimmed)]
        let mentions = Mentions.resolve(picks, in: trimmed)
        if !mentions.isEmpty { p["mentions"] = .array(mentions.map { .object($0) }) }
        Task {
            do {
                let r = try await call("messages.edit", p)
                if let m = try r["message"]?.decode(Message.self) { replaceLocally(m) }
            } catch {
                if messages[chatId]?.first(where: { $0.id == target.id })?.text == trimmed { replaceLocally(before) }
                guard editing[chatId] == nil else { return }
                beginEdit(before, force: true)
                drafts[chatId] = trimmed
                if !picks.isEmpty { mentionPicks[chatId] = picks }
                editError[chatId] = "Không sửa được tin nhắn: \(error.localizedDescription)"
            }
        }
    }

    /// Composer keys of the official apps: ↑ in an empty composer edits your last message, Esc stops editing.
    func composerCommand(_ selector: Selector, in chatId: String) -> Bool {
        switch selector {
        case #selector(NSResponder.cancelOperation(_:)) where editing[chatId] != nil:
            cancelEdit(chatId)
            return true
        case #selector(NSResponder.moveUp(_:)) where editing[chatId] == nil && (drafts[chatId] ?? "").isEmpty && attachments[chatId] == nil:
            guard let m = messages[chatId]?.last(where: { canEdit($0) }) else { return false }
            beginEdit(m)
            return true
        default:
            return false
        }
    }

    private func replaceLocally(_ m: Message) {
        if let i = messages[m.chatId]?.firstIndex(where: { $0.id == m.id }) {
            var merged = m
            if merged.media?.path == nil { merged.media?.path = messages[m.chatId]?[i].media?.path }
            messages[m.chatId]?[i] = merged
        }
        if let i = chats.firstIndex(where: { $0.id == m.chatId }), chats[i].lastMessage?.id == m.id {
            chats[i].lastMessage = m
        }
    }

    func setTyping(_ chatId: String, _ typing: Bool) {
        guard can("typing") else { return }
        Task { try? await call("typing.send", ["chatId": .string(chatId), "typing": .bool(typing)]) }
    }

    /// Lazily resolves avatars the bridge didn't include in chats.list.
    func requestAvatar(for chat: Chat) {
        guard chat.avatar == nil, !avatarRequested.contains(chat.id) else { return }
        avatarRequested.insert(chat.id)
        Task {
            guard let r = try? await call("chats.avatar", ["chatId": .string(chat.id)]),
                  let a = r["avatar"]?.stringValue,
                  let i = chats.firstIndex(where: { $0.id == chat.id }) else { return }
            chats[i].avatar = a
        }
    }

    /// Downloads media that has no directly fetchable URL (encrypted WhatsApp/Telegram media).
    func ensureMedia(_ message: Message) {
        guard message.media?.path == nil, message.media?.url == nil else { return }
        let key = message.chatId + "|" + message.id
        guard !downloadRequested.contains(key) else { return }
        downloadRequested.insert(key)
        Task {
            guard let r = try? await call("media.download", ["chatId": .string(message.chatId), "messageId": .string(message.id)]),
                  let path = r["path"]?.stringValue,
                  let i = messages[message.chatId]?.firstIndex(where: { $0.id == message.id }) else { return }
            if messages[message.chatId]?[i].media == nil { messages[message.chatId]?[i].media = Media() }
            messages[message.chatId]?[i].media?.path = path
        }
    }

    /// Photos and videos open in ChatSpace's own viewer; other files in their default app.
    func openMedia(_ message: Message) {
        if MediaViewer.canShow(message) {
            MediaViewer.shared.show(session: self, message: message)
        } else {
            openExternally(message)
        }
    }

    /// Local path of the full-size media, downloading it through the bridge when the platform needs that.
    /// Falls back to the http URL for platforms that serve media straight from their CDN.
    func mediaSource(_ message: Message) async -> String? {
        if let p = message.media?.path, FileManager.default.fileExists(atPath: p) { return p }
        if let u = message.media?.url, u.hasPrefix("http"), !can("media_download") { return u }
        if let r = try? await call("media.download", ["chatId": .string(message.chatId), "messageId": .string(message.id)]),
           let path = r["path"]?.stringValue { return path }
        return message.media?.url
    }

    /// A sharp picture for a grid tile or filmstrip: the downloaded file when there is one, else a mid-size rendition
    /// from the bridge (Telegram's inline thumbnails are a blurry few pixels), else what the message carries.
    func previewSource(_ message: Message) async -> String? {
        let media = message.media
        let photo = message.kind == .image
        if photo, let p = media?.path, FileManager.default.fileExists(atPath: p) { return p }
        let key = message.chatId + "|" + message.id
        if let p = previews[key], FileManager.default.fileExists(atPath: p) { return p }
        if can("media_preview") {
            guard let r = try? await call("media.preview", ["chatId": .string(message.chatId), "messageId": .string(message.id)]),
                  let path = r["path"]?.stringValue else { return nil }
            previews[key] = path
            return path
        }
        if photo, let u = media?.url, u.hasPrefix("http") { return u }
        // WhatsApp keeps photos encrypted: tiles fetch them like the chat does, when auto-download allows it.
        if photo, platform == .whatsapp, media?.url == nil, Workspace.shared.storage.autoDownloads(message),
           let r = try? await call("media.download", ["chatId": .string(message.chatId), "messageId": .string(message.id)]),
           let path = r["path"]?.stringValue {
            previews[key] = path
            return path
        }
        return nil
    }

    func openExternally(_ message: Message) {
        if let p = message.media?.path {
            NSWorkspace.shared.open(URL(fileURLWithPath: p))
        } else if let u = message.media?.url, let url = URL(string: u), url.scheme?.hasPrefix("http") == true,
                  !can("media_download") {
            NSWorkspace.shared.open(url)
        } else {
            Task {
                guard let r = try? await call("media.download", ["chatId": .string(message.chatId), "messageId": .string(message.id)]),
                      let path = r["path"]?.stringValue else { return }
                NSWorkspace.shared.open(URL(fileURLWithPath: path))
            }
        }
    }

    // MARK: Events

    /// Feeds a synthetic bridge event (demo mode only).
    func injectDemoEvent(_ name: String, _ data: JSONValue) {
        guard DemoData.enabled else { return }
        handleEvent(name, data)
    }

    private func handleEvent(_ name: String, _ data: JSONValue) {
        switch name {
        case "auth.state":
            applyAuth(data)
        case "auth.step":
            authStep = try? data.decode(AuthStep.self)
            if let e = authStep?.error, !e.isEmpty { authError = e }
            authState = .awaitingInput
        case "auth.qr":
            qrPayload = data["qr"]?.stringValue
            if let img = data["image"]?.stringValue, let image = ImageLoader.decodeDataURL(img) {
                qrImage = image
            } else if let payload = qrPayload {
                qrImage = QRCode.image(for: payload)
            }
            if case .number(let e)? = data["expiresAt"] { qrExpiresAt = e } else { qrExpiresAt = nil }
            qrRefreshing = false
            qrWanted = true
            authState = .awaitingQR
            armQRWatchdog()
        case "folders.update":
            if let f = try? data["folders"]?.decode([ChatFolder].self) { applyFolders(f) }
        case "connection":
            connection = data["state"]?.stringValue ?? connection
            connectionDetail = data["detail"]?.stringValue
        case "chat.upsert":
            if let c = try? data["chat"]?.decode(Chat.self) { upsertChat(c) }
        case "message.new":
            if let m = try? data["message"]?.decode(Message.self) {
                upsert(m)
                clearTyping(chatId: m.chatId, userId: m.senderId)
                if !m.fromMe {
                    // A new bot keyboard unfolds; a bot asking for a reply gets the composer set to it (as in Telegram).
                    if m.replyKeyboard != nil { collapsedKeyboards.remove(m.chatId) }
                    if m.forceReply != nil, data["live"]?.boolValue == true, isViewing(m.chatId), replyingTo[m.chatId] == nil {
                        replyingTo[m.chatId] = m
                    }
                }
                guard !m.fromMe, data["live"]?.boolValue == true, firstSighting(m) else { return }
                if isViewing(m.chatId) {
                    markRead(m.chatId)
                } else if let chat = chats.first(where: { $0.id == m.chatId }), !chat.isMuted {
                    Notifier.shared.notify(platform: platform, message: m, chat: chat)
                    ChatHeads.shared.incoming(platform: platform, chat: chat, message: m)
                } else if !chats.contains(where: { $0.id == m.chatId }) {
                    Notifier.shared.notify(platform: platform, message: m, chat: nil)
                }
            }
        case "message.update":
            if let m = try? data["message"]?.decode(Message.self) { upsert(m, isUpdate: true) }
        case "message.delete":
            if let c = data["chatId"]?.stringValue, let id = data["messageId"]?.stringValue,
               let i = messages[c]?.firstIndex(where: { $0.id == id }) {
                messages[c]?[i].deleted = true
            }
        case "typing":
            guard let c = data["chatId"]?.stringValue, let u = data["userId"]?.stringValue else { return }
            if data["typing"]?.boolValue == true {
                var list = typing[c] ?? []
                if !list.contains(where: { $0.userId == u }) { list.append(TypingInfo(userId: u, name: data["name"]?.stringValue)) }
                typing[c] = list
                typingExpiry[c + u]?.cancel()
                typingExpiry[c + u] = Task { [weak self] in
                    try? await Task.sleep(nanoseconds: 6_000_000_000)
                    if !Task.isCancelled { self?.clearTyping(chatId: c, userId: u) }
                }
            } else {
                clearTyping(chatId: c, userId: u)
            }
        case "history.phoneSync":
            if let p = try? data.decode(PhoneSyncProgress.self) { applyPhoneSync(p) }
        case "pins.update":
            if let c = data["chatId"]?.stringValue, pins[c] != nil { loadPins(c, force: true) }
        case "presence":
            if let c = data["chatId"]?.stringValue, let i = chats.firstIndex(where: { $0.id == c }) {
                chats[i].online = data["online"]?.boolValue
                if case .number(let ls)? = data["lastSeen"] { chats[i].lastSeen = ls }
            }
        default:
            break
        }
    }

    private func clearTyping(chatId: String, userId: String) {
        typing[chatId]?.removeAll { $0.userId == userId }
    }

    private func upsertChat(_ c: Chat) {
        var c = c
        if let i = chats.firstIndex(where: { $0.id == c.id }) {
            if c.avatar == nil { c.avatar = chats[i].avatar }
            if c.lastMessage == nil { c.lastMessage = chats[i].lastMessage }
            // Read receipts and metadata updates must not move a chat down the list.
            c.timestamp = max(c.timestamp, chats[i].timestamp)
            if c.hasUnread && isViewing(c.id) {
                chats[i] = c
                markRead(c.id)
                chats = sorted(chats)
                return
            }
            if chats[i].hasUnread && !c.hasUnread { Notifier.shared.clear(platform: platform, chatId: c.id) }
            chats[i] = c
        } else {
            chats.append(c)
        }
        chats = sorted(chats)
    }

    private var seenLive: [String] = []

    /// Bridges may repeat `message.new` for the same id; only the first live sighting alerts.
    private func firstSighting(_ m: Message) -> Bool {
        let key = m.chatId + "|" + m.id
        if seenLive.contains(key) { return false }
        seenLive.append(key)
        if seenLive.count > 400 { seenLive.removeFirst(100) }
        return true
    }

    private func upsert(_ m: Message, isUpdate: Bool = false) {
        var list = messages[m.chatId] ?? []
        if let i = list.firstIndex(where: { $0.id == m.id }) {
            var merged = m
            if merged.media?.path == nil { merged.media?.path = list[i].media?.path }
            let moved = merged.timestamp != list[i].timestamp
            list[i] = merged
            // Bridges correct an album's order by re-timing its items.
            if moved { list.sort { $0.timestamp < $1.timestamp } }
        } else if !isUpdate || messages[m.chatId] != nil {
            if m.fromMe, let i = list.firstIndex(where: { $0.status == .pending && $0.text == m.text }) {
                list.remove(at: i)
            }
            list.append(m)
            list.sort { $0.timestamp < $1.timestamp }
        }
        if messages[m.chatId] != nil || m.chatId == selectedChatId { messages[m.chatId] = list }
        ImageTextIndexer.shared.consider([m], on: platform)

        if !isUpdate, let i = chats.firstIndex(where: { $0.id == m.chatId }) {
            if (chats[i].lastMessage?.timestamp ?? 0) <= m.timestamp {
                chats[i].lastMessage = m
                chats[i].timestamp = max(chats[i].timestamp, m.timestamp)
            }
            chats = sorted(chats)
        } else if !isUpdate, !chats.contains(where: { $0.id == m.chatId }) {
            Task { await refreshChats() }
        }
    }

    private func merge(_ a: [Message], _ b: [Message]) -> [Message] {
        var byId: [String: Message] = [:]
        for m in a { byId[m.id] = m }
        for m in b {
            var m = m
            if m.media?.path == nil { m.media?.path = byId[m.id]?.media?.path }
            byId[m.id] = m
        }
        return byId.values.sorted { $0.timestamp < $1.timestamp }
    }

    private func sorted(_ list: [Chat]) -> [Chat] {
        list.sorted {
            if $0.isPinned != $1.isPinned { return $0.isPinned }
            return $0.timestamp > $1.timestamp
        }
    }
}

/// What the composer held when an edit began, put back when it ends.
struct ComposerStash {
    var draft: String
    var reply: Message?
    var picks: [MentionPick]?
    var files: [URL]?
}

enum HistoryBackup: Equatable {
    case unknown
    /// No encrypted chats, or the account has no PIN-protected backup.
    case none
    case locked
    case restored
}

/// Progress of a Zalo phone sync, as reported by the bridge (docs/PROTOCOL.md, history.phoneSync).
struct PhoneSyncProgress: Decodable, Equatable {
    enum State: String, Decodable {
        case idle, preparing, waiting, paused, chats, messages, done, failed, cancelled
    }
    var state: State
    var chats: Int?
    var chatsDone: Int?
    var messages: Int?
    var skipped: Int?
    var code: String?
    var error: String?

    var running: Bool { [.preparing, .waiting, .paused, .chats, .messages].contains(state) }
}

enum HistoryRestoreResult: Equatable {
    case restored
    case wrongPin(attemptsLeft: Int?)
    /// The account never set up Messenger's secure storage.
    case noBackup
    case failed(String)
}
