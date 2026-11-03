import SwiftUI

// MARK: Mute

/// One entry of an official app's "Mute notifications" menu.
struct MuteOption: Identifiable {
    enum Length { case seconds(Double), until8AM, forever }
    var label: String
    var length: Length
    var id: String { label }

    /// The `until` of chats.mute: ms epoch, or -1 for forever.
    var until: Double {
        switch length {
        case .forever: return -1
        case .seconds(let s): return (Date().timeIntervalSince1970 + s) * 1000
        case .until8AM:
            let cal = Calendar.current
            let today8 = cal.date(bySettingHour: 8, minute: 0, second: 0, of: Date())!
            let next = today8 > Date() ? today8 : cal.date(byAdding: .day, value: 1, to: today8)!
            return next.timeIntervalSince1970 * 1000
        }
    }

    /// The durations each app offers, in its own order.
    static func options(_ platform: Platform) -> [MuteOption] {
        let h = 3600.0
        switch platform {
        case .zalo:
            return [.init(label: "Trong 1 giờ", length: .seconds(h)), .init(label: "Trong 4 giờ", length: .seconds(4 * h)),
                    .init(label: "Cho đến 8:00 AM", length: .until8AM), .init(label: "Cho đến khi được mở lại", length: .forever)]
        case .messenger:
            return [.init(label: "Trong 15 phút", length: .seconds(h / 4)), .init(label: "Trong 1 giờ", length: .seconds(h)),
                    .init(label: "Trong 8 giờ", length: .seconds(8 * h)), .init(label: "Trong 24 giờ", length: .seconds(24 * h)),
                    .init(label: "Cho đến khi tôi bật lại", length: .forever)]
        case .whatsapp:
            return [.init(label: "8 giờ", length: .seconds(8 * h)), .init(label: "1 tuần", length: .seconds(7 * 24 * h)),
                    .init(label: "Luôn luôn", length: .forever)]
        case .telegram:
            return [.init(label: "Trong 1 giờ", length: .seconds(h)), .init(label: "Trong 8 giờ", length: .seconds(8 * h)),
                    .init(label: "Trong 2 ngày", length: .seconds(48 * h)), .init(label: "Vĩnh viễn", length: .forever)]
        }
    }
}

extension Chat {
    /// "Đã tắt" / "Tắt đến 14:30" / "Tắt đến 14:30 05/11"; nil when notifications are on.
    var muteDescription: String? {
        guard isMuted else { return nil }
        guard let until = mutedUntil, until > 0 else { return "Đã tắt" }
        let d = Date(timeIntervalSince1970: until / 1000)
        let f = DateFormatter()
        f.dateFormat = Calendar.current.isDateInToday(d) ? "HH:mm" : "HH:mm dd/MM"
        return "Tắt đến " + f.string(from: d)
    }
}

/// "Tắt thông báo ▸ durations" or "Bật thông báo", for menus.
struct MuteMenuItems: View {
    let session: PlatformSession
    let chat: Chat

    var body: some View {
        if session.can("mute") {
            if chat.isMuted {
                Button("Bật thông báo") { session.setMute(chat.id, until: 0) }
            } else {
                Menu("Tắt thông báo") {
                    ForEach(MuteOption.options(session.platform)) { o in
                        Button(o.label) { session.setMute(chat.id, until: o.until) }
                    }
                }
            }
        }
    }
}

// MARK: Pinned messages

/// The bar under a conversation header showing the chat's pinned messages, styled per platform.
/// Clicking jumps to the pin shown and moves on to the next older one (Telegram's behaviour);
/// with several pins, the button on the right lists them all.
struct PinnedBar: View {
    let session: PlatformSession
    let chat: Chat
    @Environment(\.compactChat) private var compact
    @State private var index = 0
    @State private var showList = false
    @State private var notFound = false

    var body: some View {
        let pins = session.pinned(chat.id)
        if !pins.isEmpty {
            let i = min(index, pins.count - 1)
            VStack(spacing: 0) {
                HStack(spacing: 10) {
                    Button { jump(to: pins[i], advance: pins.count) } label: {
                        HStack(spacing: 10) {
                            marker(i, pins.count)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(title(i, pins.count)).font(font(13, .semibold)).foregroundStyle(style.accent)
                                Group {
                                    if notFound { Text(style.notFound) } else { preview(pins[i]) }
                                }
                                .font(font(13)).foregroundStyle(style.text).lineLimit(1)
                            }
                            Spacer(minLength: 0)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help(style.jumpHelp)
                    if pins.count > 1 {
                        Button { showList = true } label: { listLabel(pins.count) }
                            .buttonStyle(.plain)
                            .help(style.listHelp)
                            .popover(isPresented: $showList, arrowEdge: .bottom) { list(pins).preferredColorScheme(.light) }
                    }
                }
                .padding(.horizontal, compact ? 10 : 16)
                .frame(height: compact ? 40 : 46)
                .background(style.background)
                Rectangle().fill(style.separator).frame(height: 1)
            }
            .onChange(of: pins.map(\.id)) { _, _ in index = 0 }
            .id(chat.id)
        }
    }

    private func jump(to m: Message, advance count: Int) {
        if count > 1 { index = (min(index, count - 1) + 1) % count }
        reveal(m)
    }

    private func reveal(_ m: Message) {
        // Paging back on Messenger asks Meta for each page: stay within what scrolling up a little would load.
        let pages = session.platform == .messenger ? 3 : 60
        Task {
            guard await session.reveal(m, maxPages: pages) == false else { return }
            notFound = true
            try? await Task.sleep(nanoseconds: 2_500_000_000)
            notFound = false
        }
    }

    // MARK: Pieces

    @ViewBuilder
    private func marker(_ i: Int, _ count: Int) -> some View {
        switch session.platform {
        case .telegram:
            // Telegram's vertical line, one segment per pin (up to 4), the shown one solid.
            let shown = min(count, 4)
            let current = shown - 1 - (i % shown)
            VStack(spacing: 2) {
                ForEach(0..<shown, id: \.self) { k in
                    Capsule().fill(style.accent.opacity(k == current ? 1 : 0.3))
                }
            }
            .frame(width: 2.5, height: 34)
        case .zalo:
            ZaloGlyph(code: ZaloGlyphs.pin, fallback: "pin", size: 18).foregroundStyle(style.accent)
        case .whatsapp:
            WAIcon(name: "ChatCellPinned", shared: true, fallback: "pin.fill", size: 17).foregroundStyle(style.secondary)
        case .messenger:
            Image(systemName: "pin.fill").font(.system(size: 14)).foregroundStyle(style.accent)
        }
    }

    @ViewBuilder
    private func listLabel(_ count: Int) -> some View {
        switch session.platform {
        case .telegram:
            TGIcon(name: "Icon_ChatPinnedMessages", fallback: "list.bullet", size: 22).foregroundStyle(style.secondary)
        case .zalo:
            Text("+\(count - 1) ghim").font(font(12, .medium)).foregroundStyle(style.text)
                .padding(.horizontal, 8).padding(.vertical, 3)
                .overlay(RoundedRectangle(cornerRadius: 4).stroke(style.separator))
        default:
            HStack(spacing: 3) {
                Text("\(count)").font(font(12, .semibold))
                Image(systemName: "chevron.down").font(.system(size: 10, weight: .semibold))
            }
            .foregroundStyle(style.secondary)
        }
    }

    private func list(_ pins: [Message]) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(style.listTitle(pins.count)).font(.system(size: 13, weight: .semibold))
                .padding(.horizontal, 14).padding(.vertical, 10)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(pins) { m in
                        Button {
                            showList = false
                            reveal(m)
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                HStack {
                                    Text(sender(m) ?? chat.title).font(.system(size: 12, weight: .semibold)).foregroundStyle(style.accent)
                                    Spacer()
                                    if m.timestamp > 0 { Text(Fmt.time(m.date)).font(.system(size: 11)).foregroundStyle(.secondary) }
                                }
                                Text(body(m)).font(.system(size: 13)).lineLimit(3).foregroundStyle(.primary)
                            }
                            .padding(.horizontal, 14).padding(.vertical, 8)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        Divider().padding(.leading, 14)
                    }
                }
            }
            .frame(maxHeight: 360)
        }
        .frame(width: 320)
    }

    private func title(_ i: Int, _ count: Int) -> String {
        switch session.platform {
        case .telegram: return count > 1 ? "Pinned Message #\(count - i)" : "Pinned Message"
        case .zalo: return "Tin nhắn"
        case .whatsapp, .messenger: return count > 1 ? "Tin nhắn đã ghim (\(i + 1)/\(count))" : "Tin nhắn đã ghim"
        }
    }

    private func preview(_ m: Message) -> Text {
        guard let who = sender(m), chat.isGroup || session.platform == .zalo else { return Text(body(m)) }
        return Text(who + ": ").foregroundColor(style.secondary) + Text(body(m))
    }

    private func sender(_ m: Message) -> String? {
        if m.fromMe { return session.platform == .telegram ? "You" : "Bạn" }
        return m.senderName.flatMap { $0.isEmpty ? nil : $0 }
    }

    private func body(_ m: Message) -> String {
        let english = session.platform == .telegram
        let t = english ? m.preview()
            : m.preview(photo: "Hình ảnh", video: "Video", voice: "Tin nhắn thoại", sticker: "Sticker", file: "Tệp",
                        deleted: "Tin nhắn đã thu hồi")
        return t.isEmpty ? (english ? "Message" : "Tin nhắn") : t
    }

    private func font(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        session.platform == .zalo ? .zalo(size, weight) : .system(size: size, weight: weight)
    }

    private struct Style {
        var accent: Color, text: Color, secondary: Color, background: Color, separator: Color
        var notFound: String, jumpHelp: String, listHelp: String
        var listTitle: (Int) -> String
    }

    private var style: Style {
        switch session.platform {
        case .telegram:
            return Style(accent: TG.accent, text: .black, secondary: TG.secondary, background: .white, separator: TG.separator,
                         notFound: "Message not found", jumpHelp: "Go to message", listHelp: "All pinned messages",
                         listTitle: { "\($0) Pinned Messages" })
        case .zalo:
            return Style(accent: ZL.blue, text: ZL.text, secondary: ZL.secondary, background: .white, separator: ZL.divider,
                         notFound: "Không tìm thấy tin nhắn gốc", jumpHelp: "Xem tin nhắn", listHelp: "Danh sách ghim",
                         listTitle: { "Danh sách ghim (\($0))" })
        case .whatsapp:
            return Style(accent: WA.teal, text: WA.text, secondary: WA.secondary, background: WA.panel, separator: WA.separator,
                         notFound: "Không tìm thấy tin nhắn gốc", jumpHelp: "Đi tới tin nhắn", listHelp: "Tất cả tin nhắn đã ghim",
                         listTitle: { "\($0) tin nhắn đã ghim" })
        case .messenger:
            return Style(accent: MS.blue, text: MS.text, secondary: MS.secondary, background: .white, separator: Color(hex: 0xE4E6EB),
                         notFound: "Không tìm thấy tin nhắn gốc", jumpHelp: "Xem trong đoạn chat", listHelp: "Tin nhắn đã ghim",
                         listTitle: { "Tin nhắn đã ghim (\($0))" })
        }
    }
}
