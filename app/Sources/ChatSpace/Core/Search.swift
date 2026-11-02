import Foundation
import SwiftUI

/// Matching rules shared with the bridges' `messages.search` (docs/PROTOCOL.md, Search): every word must
/// appear, ignoring case, Vietnamese accents and đ/d.
enum SearchText {
    private static let vi = Locale(identifier: "vi_VN")

    static func fold(_ s: String) -> String {
        s.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: vi)
            .replacingOccurrences(of: "đ", with: "d")
    }

    static func terms(_ query: String) -> [String] {
        fold(query).split(whereSeparator: \.isWhitespace).map(String.init)
    }

    static func contains(_ text: String, all terms: [String]) -> Bool {
        let f = fold(text)
        return terms.allSatisfy { f.contains($0) }
    }

    /// What a message is found by: its text or caption, and a file's name.
    static func haystack(_ m: Message) -> String {
        [m.text, m.media?.fileName].compactMap { $0 }.joined(separator: "\n")
    }

    static func kindMatches(_ m: Message, _ kinds: [MessageKind]?) -> Bool {
        guard let kinds, !kinds.isEmpty else { return ![.system, .call, .unsupported].contains(m.kind) }
        if kinds.contains(m.kind) { return true }
        return kinds.contains(.link) && (m.text.map(hasLink) ?? false)
    }

    static func hasLink(_ s: String) -> Bool {
        s.range(of: #"https?://|www\."#, options: [.regularExpression, .caseInsensitive]) != nil
    }

    /// Search over messages already in memory (demo mode and bridges without `search`).
    static func localSearch(_ byChat: [String: [Message]], query: String, chatId: String?, kinds: [MessageKind]?,
                            before: Double?, limit: Int) -> [Message] {
        let words = terms(query)
        let lists = chatId.map { [byChat[$0] ?? []] } ?? Array(byChat.values)
        let hits = lists.joined().filter { m in
            !m.isDeleted && kindMatches(m, kinds) && (before.map { m.timestamp < $0 } ?? true)
                && contains(haystack(m), all: words)
        }
        return Array(hits.sorted { $0.timestamp > $1.timestamp }.prefix(limit))
    }

    /// Ranges of every occurrence of each term in `s`, matched the same accent-insensitive way.
    static func ranges(of terms: [String], in s: String) -> [Range<String.Index>] {
        guard !terms.isEmpty else { return [] }
        var folded: [Character] = []
        var origin: [String.Index] = []
        var i = s.startIndex
        while i < s.endIndex {
            for c in fold(String(s[i])) {
                folded.append(c)
                origin.append(i)
            }
            i = s.index(after: i)
        }
        var out: [Range<String.Index>] = []
        for t in terms {
            let needle = Array(t)
            guard !needle.isEmpty, needle.count <= folded.count else { continue }
            var start = 0
            while start + needle.count <= folded.count {
                if folded[start] == needle[0], Array(folded[start..<start + needle.count]) == needle {
                    let lastOrigin = origin[start + needle.count - 1]
                    out.append(origin[start]..<s.index(after: lastOrigin))
                    start += needle.count
                } else {
                    start += 1
                }
            }
        }
        return out
    }

    /// `s` with the matched words emphasised.
    static func highlighted(_ s: String, terms: [String], color: Color) -> AttributedString {
        var a = AttributedString(s)
        for r in ranges(of: terms, in: s) {
            guard let ar = Range(r, in: a) else { continue }
            a[ar].foregroundColor = color
            a[ar].inlinePresentationIntent = .stronglyEmphasized
        }
        return a
    }

    /// One line around the first match, so a hit deep in a long text (or OCR output) is visible.
    static func snippet(_ s: String, terms: [String], radius: Int = 48) -> String {
        let flat = s.split(whereSeparator: \.isNewline).joined(separator: " · ")
        guard let first = ranges(of: terms, in: flat).min(by: { $0.lowerBound < $1.lowerBound }) else {
            return String(flat.prefix(radius * 2))
        }
        let before = flat.distance(from: flat.startIndex, to: first.lowerBound)
        guard before > radius else { return flat }
        let start = flat.index(first.lowerBound, offsetBy: -radius)
        return "…" + flat[start...]
    }
}
