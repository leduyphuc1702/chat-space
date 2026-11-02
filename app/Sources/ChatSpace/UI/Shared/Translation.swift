import SwiftUI

/// The hover button beside each app's reaction button; `label` draws it in that app's style.
struct TranslateToggle<Label: View>: View {
    let message: Message
    @ViewBuilder var label: (_ on: Bool) -> Label

    var body: some View {
        let t = Translator.shared
        let on = t.isShown(message)
        Button { t.toggle(message) } label: { label(on) }
            .buttonStyle(.plain)
            .focusEffectDisabled()
            .help(on ? "Ẩn bản dịch" : "Dịch sang \(TranslationLanguages.name(t.settings.language))")
    }
}

/// The translation inside a bubble, as an italic quote under the message text.
struct TranslationQuote: View {
    let message: Message
    var bar: Color
    var color: Color
    var size: CGFloat = 14

    var body: some View {
        let t = Translator.shared
        Group {
            if let e = t.shownEntry(message) {
                Text(TranslationLanguages.same(e.source, e.target) && e.text == e.original
                     ? "Tin nhắn đã là \(TranslationLanguages.name(e.target))" : e.text)
                    .textSelection(.enabled)
            } else if t.isLoading(message) {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.mini)
                    Text("Đang dịch…")
                }
            } else if let error = t.failure(message) {
                HStack(spacing: 6) {
                    Text("Không dịch được")
                    Button("Thử lại") { t.translate(message) }.buttonStyle(.link).font(.system(size: size - 1))
                }
                .help(error)
            }
        }
        .font(.system(size: size).italic())
        .foregroundStyle(color)
        .fixedSize(horizontal: false, vertical: true)
        .padding(.leading, 9)
        .overlay(alignment: .leading) { RoundedRectangle(cornerRadius: 1).fill(bar).frame(width: 2) }
        .padding(.vertical, 1)
    }
}

/// Composer button choosing the language a message goes out in (`文A` plus the language when it isn't the user's own).
struct OutgoingLanguageButton: View {
    let session: PlatformSession
    let chatId: String
    var tint: Color
    var idle: Color
    var iconSize: CGFloat = 17
    var toolSize: CGFloat = 32
    /// Hide while it would only mean "send as typed" (the compact composer collapses while typing).
    var hideWhenIdle = false
    @State private var open = false

    var body: some View {
        let t = Translator.shared
        let ref = ChatRef(platform: session.platform, chatId: chatId)
        let reply = session.replyingTo[chatId]
        let target = t.outgoingLanguage(ref, reply: reply)
        let active = target.map { !TranslationLanguages.same($0, t.settings.language) } ?? false
        if !(hideWhenIdle && target != nil && !active && t.sendError[ref.id] == nil && !open) {
            button(t: t, ref: ref, reply: reply, target: target, active: active)
        }
    }

    private func button(t: Translator, ref: ChatRef, reply: Message?, target: String?, active: Bool) -> some View {
        HStack(spacing: 2) {
            Image(systemName: "translate").font(.system(size: iconSize))
            if let target, active {
                Text(TranslationLanguages.short(target)).font(.system(size: 10, weight: .bold))
            } else if target == nil {
                ProgressView().controlSize(.mini)
            }
        }
        .foregroundStyle(t.sendError[ref.id] != nil ? .red : (active ? tint : idle))
        .padding(.horizontal, active ? 3 : 0)
        .toolPopover(help(target, reply: reply), size: toolSize, isPresented: $open) {
            OutgoingLanguagePicker(session: session, ref: ref, reply: reply) { open = false }
        }
        .task(id: reply.map(t.key)) {
            if let reply, t.settings.matchReplyLanguage { await t.detectLanguage(of: reply) }
        }
    }

    private func help(_ target: String?, reply: Message?) -> String {
        guard let target else { return "Đang nhận diện ngôn ngữ tin nhắn được trả lời…" }
        let own = Translator.shared.settings.language
        if TranslationLanguages.same(target, own) { return "Gửi nguyên văn (\(TranslationLanguages.name(own))) — bấm để chọn ngôn ngữ gửi" }
        return "Tự dịch sang \(TranslationLanguages.name(target)) trước khi gửi"
    }
}

private struct OutgoingLanguagePicker: View {
    let session: PlatformSession
    let ref: ChatRef
    let reply: Message?
    var done: () -> Void

    var body: some View {
        let t = Translator.shared
        let own = t.settings.language
        let replyAuto = reply != nil && t.settings.matchReplyLanguage && Translator.hasWords(reply?.text)
        let followingReply = replyAuto && !t.replyAutoOff.contains(ref.id)
        let chosen = t.chatLanguage(ref)
        VStack(alignment: .leading, spacing: 2) {
            Text("Gửi tin nhắn bằng").font(.system(size: 11.5, weight: .semibold)).foregroundStyle(.secondary)
                .padding(.horizontal, 10).padding(.top, 4).padding(.bottom, 2)
            if replyAuto {
                let detected = reply.flatMap(t.knownLanguage)
                row(title: "Theo tin nhắn đang trả lời" + (detected.map { " · " + TranslationLanguages.name($0) } ?? "…"),
                    icon: "arrowshape.turn.up.left", selected: followingReply) {
                    t.replyAutoOff.remove(ref.id)
                }
                Divider().padding(.vertical, 3)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(TranslationLanguages.all, id: \.code) { l in
                        let isOwn = TranslationLanguages.same(l.code, own)
                        row(title: l.name + (isOwn ? " (không dịch)" : ""), icon: nil,
                            selected: !followingReply && TranslationLanguages.same(l.code, chosen)) {
                            t.setChatLanguage(l.code, for: ref)
                            if replyAuto { t.replyAutoOff.insert(ref.id) }
                        }
                    }
                }
            }
            .frame(maxHeight: 300)
            if let e = t.sendError[ref.id] {
                Divider().padding(.vertical, 3)
                Text(e).font(.system(size: 11.5)).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 10).padding(.bottom, 4)
            }
        }
        .padding(6)
        .frame(width: 290)
        .preferredColorScheme(.light)
    }

    private func row(title: String, icon: String?, selected: Bool, action: @escaping () -> Void) -> some View {
        Button { action(); done() } label: {
            HStack(spacing: 8) {
                Image(systemName: "checkmark").font(.system(size: 11, weight: .semibold)).opacity(selected ? 1 : 0)
                if let icon { Image(systemName: icon).font(.system(size: 12)).foregroundStyle(.secondary) }
                Text(title).font(.system(size: 13))
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10).frame(height: 28).contentShape(Rectangle())
        }
        .buttonStyle(PickerRowStyle())
    }
}

private struct PickerRowStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { Row(configuration: configuration) }

    private struct Row: View {
        let configuration: Configuration
        @State private var hovered = false
        var body: some View {
            configuration.label
                .background(RoundedRectangle(cornerRadius: 5).fill(Color.primary.opacity(configuration.isPressed ? 0.12 : (hovered ? 0.06 : 0))))
                .onHover { hovered = $0 }
        }
    }
}

/// Shown above a composer when a message was held back because it could not be translated.
struct TranslationNotice: View {
    let session: PlatformSession
    let chatId: String

    var body: some View {
        let id = ChatRef(platform: session.platform, chatId: chatId).id
        if let e = Translator.shared.sendError[id] {
            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                Text(e).font(.system(size: 12)).foregroundStyle(Color(hex: 0x333333)).fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 4)
                Button { Translator.shared.sendError[id] = nil } label: { Image(systemName: "xmark").foregroundStyle(.secondary) }
                    .buttonStyle(.plain)
            }
            .padding(8)
            .background(RoundedRectangle(cornerRadius: 6).fill(Color(hex: 0xFFF4E5)))
            .padding(.horizontal, 12).padding(.top, 8)
        }
    }
}
