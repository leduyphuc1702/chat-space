import AppKit
import SwiftUI

struct ChatMember: Codable, Identifiable, Hashable, Sendable {
    var id: String
    var name: String
    var avatar: String?
}

/// A member picked from the "@" list; its visible text is "@" + name.
struct MentionPick: Hashable {
    var userId: String
    var name: String
}

/// The "@name" being typed in a group composer and the list offered for it.
@MainActor @Observable
final class Mentions {
    static let shared = Mentions()

    struct Query: Equatable {
        var ref: ChatRef
        var text: String
        /// "@" plus the typed part, in the composer's UTF-16 string.
        var range: NSRange
    }

    private(set) var query: Query?
    var index = 0
    @ObservationIgnored private var editors: [ChatRef: WeakTextView] = [:]

    private final class WeakTextView {
        weak var view: NSTextView?
        init(_ view: NSTextView) { self.view = view }
    }

    func register(_ tv: NSTextView, for ref: ChatRef) { editors[ref] = WeakTextView(tv) }

    func candidates(_ session: PlatformSession, _ chatId: String) -> [ChatMember] {
        candidates(session, chatId, for: query)
    }

    private func candidates(_ session: PlatformSession, _ chatId: String, for query: Query?) -> [ChatMember] {
        guard let q = query, q.ref.platform == session.platform, q.ref.chatId == chatId else { return [] }
        let all = (session.members[chatId] ?? []).filter { $0.id != session.selfInfo?.id }
        let needle = CustomGroup.normalize(q.text)
        guard !needle.isEmpty else { return Array(all.prefix(50)) }
        let words = { (s: String) in CustomGroup.normalize(s).split(separator: " ").map(String.init) }
        return all.filter { m in
            let name = CustomGroup.normalize(m.name)
            return name.hasPrefix(needle) || words(m.name).contains { $0.hasPrefix(needle) } || name.contains(needle)
        }
        .prefix(50).map { $0 }
    }

    /// Recomputes the "@query" before the caret; called on every edit and caret move.
    func update(_ tv: NSTextView, session: PlatformSession, chatId: String) {
        let ref = ChatRef(platform: session.platform, chatId: chatId)
        let sel = tv.selectedRange()
        let s = tv.string as NSString
        guard sel.length == 0, sel.location <= s.length else { return clear(ref) }
        var i = sel.location
        let floor = max(0, sel.location - 40)
        while i > floor {
            i -= 1
            let c = s.character(at: i)
            if c == 0x0A || c == 0x0D { return clear(ref) }
            guard c == 0x40 else { continue }
            let before = i == 0 ? nil : s.character(at: i - 1)
            if let b = before, !(CharacterSet.whitespacesAndNewlines.contains(Unicode.Scalar(b) ?? " ")) { return clear(ref) }
            let typed = s.substring(with: NSRange(location: i + 1, length: sel.location - i - 1))
            if typed.hasPrefix(" ") || typed.contains("  ") || typed.split(separator: " ").count > 3 { return clear(ref) }
            // A finished pick: the caret has moved past "@Name".
            if (session.mentionPicks[chatId] ?? []).contains(where: { typed.hasPrefix($0.name) && typed.count > $0.name.count }) {
                return clear(ref)
            }
            let q = Query(ref: ref, text: typed, range: NSRange(location: i, length: sel.location - i))
            // Words typed after an "@" that matches nobody are ordinary text, not a name.
            if typed.contains(" "), session.members[chatId] != nil, candidates(session, chatId, for: q).isEmpty {
                return clear(ref)
            }
            if query != q {
                if query?.ref != ref || query?.text != q.text { index = 0 }
                query = q
            }
            if session.members[chatId] == nil { session.loadMembers(chatId) }
            return
        }
        clear(ref)
    }

    func clear(_ ref: ChatRef) {
        if query?.ref == ref { query = nil }
    }

    /// Arrow/Return/Tab/Esc while the list is open. Returns true when handled.
    func handle(_ command: Selector, session: PlatformSession, chatId: String) -> Bool {
        let list = candidates(session, chatId)
        guard let q = query, !list.isEmpty else { return false }
        switch command {
        case #selector(NSResponder.moveUp(_:)): index = (index - 1 + list.count) % list.count
        case #selector(NSResponder.moveDown(_:)): index = (index + 1) % list.count
        case #selector(NSResponder.insertTab(_:)), #selector(NSResponder.insertNewline(_:)):
            pick(list[min(index, list.count - 1)], session: session, chatId: chatId)
        case #selector(NSResponder.cancelOperation(_:)): clear(q.ref)
        default: return false
        }
        return true
    }

    func pick(_ m: ChatMember, session: PlatformSession, chatId: String) {
        let ref = ChatRef(platform: session.platform, chatId: chatId)
        guard let q = query, q.ref == ref, let tv = editors[ref]?.view else { return }
        let name = m.name.trimmingCharacters(in: .whitespacesAndNewlines)
        tv.window?.makeFirstResponder(tv)
        tv.insertText("@\(name) ", replacementRange: q.range)
        session.mentionPicks[chatId, default: []].append(MentionPick(userId: m.id, name: name))
        query = nil
    }

    /// The picks still present in `text`, as protocol mentions (UTF-16 offsets, span includes "@").
    static func resolve(_ picks: [MentionPick], in text: String) -> [[String: JSONValue]] {
        let s = text as NSString
        var from = 0
        var out: [[String: JSONValue]] = []
        var used = Set<Int>()
        for p in picks {
            let token = "@" + p.name
            var r = s.range(of: token, range: NSRange(location: from, length: s.length - from))
            if r.location == NSNotFound { r = s.range(of: token) }
            guard r.location != NSNotFound, !used.contains(r.location) else { continue }
            used.insert(r.location)
            from = r.location + r.length
            out.append(["userId": .string(p.userId), "offset": .number(Double(r.location)), "length": .number(Double(r.length))])
        }
        func offset(_ m: [String: JSONValue]) -> Double {
            if case .number(let n)? = m["offset"] { return n }
            return 0
        }
        return out.sorted { offset($0) < offset($1) }
    }
}

/// The member list that floats above a group composer while "@name" is being typed.
struct MentionSuggestions: View {
    let session: PlatformSession
    let chatId: String
    var accent: Color = .accentColor

    var body: some View {
        let center = Mentions.shared
        let list = center.candidates(session, chatId)
        if center.query != nil, center.query?.ref.chatId == chatId {
            VStack(alignment: .leading, spacing: 0) {
                if list.isEmpty {
                    HStack(spacing: 8) {
                        if session.members[chatId] == nil { ProgressView().controlSize(.small) }
                        Text(session.members[chatId] == nil ? "Đang tải thành viên…" : "Không tìm thấy thành viên")
                            .font(.system(size: 12.5)).foregroundStyle(.secondary)
                    }
                    .padding(10)
                } else {
                    ScrollViewReader { proxy in
                        ScrollView {
                            LazyVStack(spacing: 0) {
                                ForEach(Array(list.enumerated()), id: \.element.id) { i, m in
                                    row(m, selected: i == center.index).id(m.id)
                                }
                            }
                        }
                        .frame(height: min(CGFloat(list.count), 5) * 40)
                        .onChange(of: center.index) { _, i in
                            if i < list.count { proxy.scrollTo(list[i].id) }
                        }
                    }
                }
            }
            .frame(width: 280)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.white).shadow(color: .black.opacity(0.18), radius: 8, y: 2))
            .environment(\.colorScheme, .light)
            .padding(.horizontal, 12).padding(.bottom, 6)
        }
    }

    private func row(_ m: ChatMember, selected: Bool) -> some View {
        Button { Mentions.shared.pick(m, session: session, chatId: chatId) } label: {
            HStack(spacing: 10) {
                AvatarView(source: m.avatar, size: 28) {
                    Circle().fill(Color(hue: Double(m.id.stableHash % 360) / 360, saturation: 0.45, brightness: 0.85))
                        .overlay(Text(m.name.initials).font(.system(size: 11, weight: .semibold)).foregroundStyle(.white))
                }
                Text(m.name).font(.system(size: 13.5)).foregroundStyle(Color.black.opacity(0.85)).lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10).frame(height: 40)
            .background(selected ? accent.opacity(0.12) : .clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

extension View {
    /// Floats the "@" member list above this composer for group chats.
    func mentionSuggestions(session: PlatformSession, chat: Chat, accent: Color = .accentColor) -> some View {
        overlay(alignment: .topLeading) {
            // Hung from the composer's top edge so the list grows upward over the messages.
            Color.clear.frame(height: 0).overlay(alignment: .bottomLeading) {
                if chat.isGroup && session.can("mentions") {
                    MentionSuggestions(session: session, chatId: chat.id, accent: accent)
                        .fixedSize()
                }
            }
        }
    }
}
