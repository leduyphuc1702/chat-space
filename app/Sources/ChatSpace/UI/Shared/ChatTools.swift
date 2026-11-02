import AppKit
import AVFoundation
import Observation
import SwiftUI

// MARK: Clickable toolbar icons

extension View {
    /// Turns a decorative icon into a real button: generous hit area, hover highlight, tooltip.
    func tool(_ help: String, size: CGFloat = 32, action: @escaping () -> Void) -> some View {
        Button(action: action) { ToolHover(size: size) { self } }
            .buttonStyle(.plain)
            .focusEffectDisabled()
            .help(help)
    }

    /// Same, but the click opens a popover anchored on the icon.
    func toolPopover<P: View>(_ help: String, size: CGFloat = 32, isPresented: Binding<Bool>,
                              @ViewBuilder content: @escaping () -> P) -> some View {
        tool(help, size: size) { isPresented.wrappedValue.toggle() }
            .popover(isPresented: isPresented, arrowEdge: .bottom, content: content)
    }

    /// Clicking empty space around the composer focuses it.
    func composerTapArea() -> some View {
        contentShape(Rectangle()).onTapGesture { Composer.focus() }
    }
}

private struct ToolHover<Content: View>: View {
    var size: CGFloat
    @ViewBuilder var content: () -> Content
    @State private var hovered = false

    var body: some View {
        content()
            .frame(minWidth: size, minHeight: size)
            .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(hovered ? 0.08 : 0)))
            .contentShape(Rectangle())
            .onHover { hovered = $0 }
    }
}

// MARK: Actions

@MainActor
enum ChatTools {
    /// Calls, stickers, contact cards… need the official client; offer to open it.
    static func nativeOnly(_ platform: Platform, _ feature: String) {
        let alert = NSAlert()
        alert.messageText = "\(feature) chưa có trong ChatSpace"
        let app = NativeAssets.shared.appURL(platform)
        alert.informativeText = app != nil
            ? "Tính năng này chỉ dùng được trong app \(platform.displayName) chính thức. Mở \(platform.displayName) ngay?"
            : "Tính năng này chỉ dùng được trong app \(platform.displayName) chính thức (chưa cài trên máy này)."
        if app != nil { alert.addButton(withTitle: "Mở \(platform.displayName)") }
        alert.addButton(withTitle: app != nil ? "Huỷ" : "OK")
        if alert.runModal() == .alertFirstButtonReturn, let app {
            NSWorkspace.shared.openApplication(at: app, configuration: NSWorkspace.OpenConfiguration())
        }
    }

    static func call(_ platform: Platform, video: Bool) {
        nativeOnly(platform, video ? "Gọi video" : "Gọi thoại")
    }

    /// Zalo's "Chụp màn hình": interactive capture, then send the image to the chat.
    static func screenshot(session: PlatformSession, chatId: String) {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("chatspace-\(UUID().uuidString).png").path
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        p.arguments = ["-i", "-x", path]
        p.terminationHandler = { _ in
            Task { @MainActor in
                guard FileManager.default.fileExists(atPath: path) else { return }
                session.send(session.drafts[chatId] ?? "", in: chatId, filePath: path)
            }
        }
        try? p.run()
    }

    static func attach(session: PlatformSession, chatId: String) {
        if let f = pickFile() { session.send(session.drafts[chatId] ?? "", in: chatId, filePath: f) }
    }

    /// Pasted or dropped images/files wait above the composer, so a caption and emoji can be
    /// typed before Return sends them together.
    static func sendPasted(_ files: [URL], session: PlatformSession, chatId: String) {
        guard !files.isEmpty else { return }
        session.stage(files, in: chatId)
        NSApp.activate(ignoringOtherApps: true)
        Composer.focus()
    }
}

// MARK: Staged attachments

/// Thumbnails of the chat's pasted images/files above the composer; × removes one.
struct ComposerAttachments: View {
    let session: PlatformSession
    let chatId: String
    var accent: Color = .accentColor

    var body: some View {
        if let files = session.attachments[chatId], !files.isEmpty {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    ForEach(files, id: \.self) { f in
                        AttachmentThumb(url: f, accent: accent) { session.unstage(f, in: chatId) }
                    }
                }
                .padding(.horizontal, 16).padding(.top, 10).padding(.bottom, 2)
            }
        }
    }
}

private struct AttachmentThumb: View {
    let url: URL
    let accent: Color
    let remove: () -> Void
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().scaledToFill()
            } else {
                VStack(spacing: 4) {
                    Image(systemName: "doc.fill").font(.system(size: 22)).foregroundStyle(accent)
                    Text(url.lastPathComponent).font(.system(size: 10)).lineLimit(2).multilineTextAlignment(.center)
                        .foregroundStyle(.secondary)
                }
                .padding(4)
            }
        }
        .frame(width: 72, height: 72)
        .background(Color.primary.opacity(0.06))
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.primary.opacity(0.1)))
        .overlay(alignment: .topTrailing) {
            Button(action: remove) {
                Image(systemName: "xmark.circle.fill").font(.system(size: 16))
                    .symbolRenderingMode(.palette).foregroundStyle(.white, Color.black.opacity(0.6))
            }
            .buttonStyle(.plain).padding(3).help("Bỏ tệp này")
        }
        .help(url.lastPathComponent)
        .task(id: url) { image = NSImage(contentsOf: url) }
    }
}

// MARK: Voice messages

/// Records an AAC voice note; the second click sends it as a file.
@MainActor
@Observable
final class VoiceRecorder {
    static let shared = VoiceRecorder()

    private(set) var recordingChat: ChatRef?
    private(set) var startedAt: Date?
    @ObservationIgnored private var recorder: AVAudioRecorder?
    @ObservationIgnored private var url: URL?

    func isRecording(_ ref: ChatRef) -> Bool { recordingChat == ref }

    func toggle(_ ref: ChatRef) {
        if recordingChat == ref { stop(send: true) } else { start(ref) }
    }

    func start(_ ref: ChatRef) {
        stop(send: false)
        AVCaptureDevice.requestAccess(for: .audio) { ok in
            Task { @MainActor in
                guard ok else {
                    let a = NSAlert()
                    a.messageText = "ChatSpace chưa có quyền dùng micro"
                    a.informativeText = "Bật trong Cài đặt hệ thống → Quyền riêng tư & Bảo mật → Micro."
                    a.runModal()
                    return
                }
                let url = FileManager.default.temporaryDirectory.appendingPathComponent("voice-\(Int(Date().timeIntervalSince1970)).m4a")
                let settings: [String: Any] = [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 24_000,
                                               AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 32_000]
                guard let r = try? AVAudioRecorder(url: url, settings: settings), r.record() else { return }
                self.recorder = r
                self.url = url
                self.recordingChat = ref
                self.startedAt = Date()
            }
        }
    }

    func stop(send: Bool) {
        guard let r = recorder, let ref = recordingChat, let url else { return }
        let long = r.currentTime >= 0.7
        r.stop()
        recorder = nil
        recordingChat = nil
        startedAt = nil
        if send && long {
            AppModel.shared.session(ref.platform).send("", in: ref.chatId, filePath: url.path)
        } else {
            try? FileManager.default.removeItem(at: url)
        }
    }
}

/// Mic button content: the platform's mic icon, or a red timer while recording.
struct VoiceButtonLabel<Icon: View>: View {
    let ref: ChatRef
    @ViewBuilder var icon: () -> Icon

    var body: some View {
        let rec = VoiceRecorder.shared
        if rec.isRecording(ref), let start = rec.startedAt {
            TimelineView(.periodic(from: start, by: 1)) { ctx in
                let s = Int(ctx.date.timeIntervalSince(start))
                HStack(spacing: 5) {
                    Circle().fill(.red).frame(width: 8, height: 8)
                    Text(String(format: "%d:%02d", s / 60, s % 60)).font(.system(size: 12, weight: .semibold).monospacedDigit())
                    Image(systemName: "arrow.up.circle.fill").font(.system(size: 18)).foregroundStyle(.red)
                }
                .foregroundStyle(.red)
            }
        } else {
            icon()
        }
    }
}

extension View {
    /// Click: start/stop-and-send a voice note. Right-click while recording: cancel.
    func voiceTool(session: PlatformSession, chatId: String) -> some View {
        let ref = ChatRef(platform: session.platform, chatId: chatId)
        return VoiceButtonLabel(ref: ref) { self }
            .tool(VoiceRecorder.shared.isRecording(ref) ? "Bấm để gửi tin nhắn thoại" : "Ghi âm tin nhắn thoại") {
                VoiceRecorder.shared.toggle(ref)
            }
            .contextMenu {
                if VoiceRecorder.shared.isRecording(ref) { Button("Huỷ ghi âm") { VoiceRecorder.shared.stop(send: false) } }
            }
    }
}

// MARK: Popovers

/// Search the loaded history of one chat; picking a result scrolls the conversation to it.
struct InChatSearch: View {
    let session: PlatformSession
    let chatId: String
    var placeholder = "Tìm tin nhắn"
    @State private var q = ""
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        let needle = CustomGroup.normalize(q)
        let hits = needle.isEmpty ? [] : (session.messages[chatId] ?? []).reversed().filter {
            CustomGroup.normalize($0.text ?? $0.media?.fileName ?? "").contains(needle)
        }
        VStack(alignment: .leading, spacing: 8) {
            TextField(placeholder, text: $q).textFieldStyle(.roundedBorder)
            if !needle.isEmpty {
                Text(hits.isEmpty ? "Không tìm thấy trong \(session.messages[chatId]?.count ?? 0) tin đã tải" : "\(hits.count) kết quả")
                    .font(.caption).foregroundStyle(.secondary)
            }
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 2) {
                    ForEach(hits.prefix(60)) { m in
                        Button {
                            session.scrollTarget[chatId] = m.id
                            dismiss()
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                HStack {
                                    Text(m.fromMe ? "Bạn" : (m.senderName ?? "")).font(.system(size: 11, weight: .semibold))
                                    Spacer()
                                    Text(m.date, format: .dateTime.day().month().hour().minute()).font(.system(size: 10)).foregroundStyle(.secondary)
                                }
                                Text(m.text ?? m.media?.fileName ?? "").font(.system(size: 12)).lineLimit(2)
                            }
                            .padding(6).frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .frame(height: needle.isEmpty ? 0 : 260)
            if !session.reachedStart.contains(chatId) && !needle.isEmpty {
                Button("Tải thêm tin cũ để tìm") { session.loadOlder(chatId) }.font(.caption)
            }
        }
        .padding(12)
        .frame(width: 320)
    }
}

/// Conversation info: who/what this chat is, plus ChatSpace actions.
struct ChatInfoPanel: View {
    let session: PlatformSession
    let chat: Chat

    var body: some View {
        let ref = ChatRef(platform: session.platform, chatId: chat.id)
        VStack(spacing: 10) {
            AvatarView(source: chat.avatar, size: 72) {
                Circle().fill(Color.gray.opacity(0.3))
                    .overlay(Text(String(chat.title.prefix(1))).font(.system(size: 28, weight: .semibold)).foregroundStyle(.white))
            }
            Text(chat.title).font(.system(size: 16, weight: .semibold)).multilineTextAlignment(.center)
            HStack(spacing: 6) {
                PlatformIcon(platform: session.platform, size: 14)
                Text(kindText).font(.caption).foregroundStyle(.secondary)
            }
            Divider()
            VStack(alignment: .leading, spacing: 6) {
                info("Thông báo", chat.isMuted ? "Đã tắt" : "Đang bật")
                if chat.hasUnread { info("Chưa đọc", chat.unread > 0 ? "\(chat.unread) tin" : "Đánh dấu chưa đọc") }
                info("Tin đã tải", "\(session.messages[chat.id]?.count ?? 0)")
                let groups = Workspace.shared.groups.filter { $0.matches(chat, on: session.platform) }
                if !groups.isEmpty { info("Nhóm tuỳ chỉnh", groups.map(\.name).joined(separator: ", ")) }
            }
            Divider()
            VStack(spacing: 6) {
                Button("Mở bong bóng chat") { ChatHeads.shared.show(ref, expand: true) }
                if Workspace.shared.isIgnored(ref) {
                    Button("Hiện lại trong Chưa đọc của ChatSpace") { Workspace.shared.restore(ref) }
                } else {
                    Button("Bỏ qua trong Chưa đọc của ChatSpace") { Workspace.shared.ignore(ref, title: chat.title) }
                }
            }
            .buttonStyle(.link)
        }
        .padding(16)
        .frame(width: 260)
    }

    private var kindText: String {
        switch chat.kind {
        case .direct: return "Trò chuyện cá nhân"
        case .group: return chat.memberCount.map { "Nhóm · \($0) thành viên" } ?? "Nhóm"
        case .channel: return "Kênh"
        case .bot: return "Bot"
        case .self: return "Tin nhắn đã lưu"
        }
    }

    private func info(_ k: String, _ v: String) -> some View {
        HStack(alignment: .top) {
            Text(k).foregroundStyle(.secondary)
            Spacer()
            Text(v).multilineTextAlignment(.trailing)
        }
        .font(.system(size: 12))
    }
}
