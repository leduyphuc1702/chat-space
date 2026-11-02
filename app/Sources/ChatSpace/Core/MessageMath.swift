import Foundation

/// Arithmetic typed into a message ("Số tiền: 23500*410"), so the composer can offer sending it with the result.
enum MessageMath {
    struct Piece: Equatable {
        var text: String
        var isResult = false
    }

    /// The message cut into pieces with " = result" after each expression, or nil when there is nothing to compute.
    static func annotate(_ text: String) -> [Piece]? {
        let all = Array(text)
        var chars = all
        // Links keep their digits ("…/item/12+3"): blank them out so no expression starts inside one.
        for r in linkRanges(text) { for i in r where i < chars.count { chars[i] = "a" } }
        var pieces: [Piece] = []
        var copied = 0
        var i = 0
        while i < chars.count {
            guard canStart(chars, at: i), let hit = expression(chars, at: i) else { i += 1; continue }
            pieces.append(Piece(text: String(all[copied..<hit.end])))
            pieces.append(Piece(text: " = " + hit.result, isResult: true))
            copied = hit.end
            i = hit.end
        }
        guard !pieces.isEmpty else { return nil }
        if copied < all.count { pieces.append(Piece(text: String(all[copied...]))) }
        return pieces
    }

    static func joined(_ pieces: [Piece]) -> String { pieces.map(\.text).joined() }

    private static func canStart(_ c: [Character], at i: Int) -> Bool {
        guard c[i] == "(" || c[i].isDigit else { return false }
        guard i > 0 else { return true }
        let p = c[i - 1]
        // "-5*3", "+84…", "1/2*3" or "a1+2": the expression would really begin before this point.
        return !(p.isLetter || p.isDigit || "_.,+-−*/×÷^%=(".contains(p))
    }

    private static func expression(_ c: [Character], at start: Int) -> (end: Int, result: String)? {
        var p = Parser(c: c, i: start)
        guard let value = p.expr(), !p.ops.isEmpty, value.isFinite, abs(value) < 1e15 else { return nil }
        // Dashes and slashes alone are mostly ranges, dates and phone numbers ("8h - 17h", "30/10/2026");
        // they count only beside ×, + or ^, or as a spaced division ("1500000 / 3").
        let strong = p.ops.contains { !"-−/".contains($0.op) }
        let spacedDivision = p.ops.allSatisfy { $0.op == "/" && $0.spaced }
        guard strong || spacedDivision else { return nil }
        var j = p.i
        while j < c.count, c[j].isInlineSpace { j += 1 }
        // Already worked out by the sender ("2+2 = 4").
        if j < c.count, c[j] == "=" { return nil }
        return (p.i, format(value, usStyle: p.usStyle && !p.vnStyle))
    }

    private static func format(_ v: Double, usStyle: Bool) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        f.usesGroupingSeparator = true
        f.groupingSeparator = usStyle ? "," : "."
        f.decimalSeparator = usStyle ? "." : ","
        f.minimumFractionDigits = 0
        f.maximumFractionDigits = abs(v) < 1 ? 6 : 2
        f.roundingMode = .halfUp
        return f.string(from: NSNumber(value: v == 0 ? 0 : v)) ?? String(v)
    }

    private static func linkRanges(_ text: String) -> [Range<Int>] {
        guard let d = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) else { return [] }
        return d.matches(in: text, range: NSRange(text.startIndex..., in: text)).compactMap { m in
            guard let r = Range(m.range, in: text) else { return nil }
            let lo = text.distance(from: text.startIndex, to: r.lowerBound)
            return lo..<(lo + text[r].count)
        }
    }

    /// Recursive descent over one line: + − below × ÷ below ^, postfix %, parentheses.
    private struct Parser {
        let c: [Character]
        var i: Int
        var ops: [(op: Character, spaced: Bool)] = []
        /// Number spelling seen so far: "1.500" / "2,5" is Vietnamese, "1,500" / "2.5" English.
        var vnStyle = false
        var usStyle = false

        mutating func expr() -> Double? {
            guard var v = term() else { return nil }
            while case let (op, r)? = binary("+-−", operand: { $0.term() }) {
                v = op == "+" ? v + r : v - r
            }
            return v
        }

        mutating func term() -> Double? {
            guard var v = unary() else { return nil }
            while case let (op, r)? = binary("*×xX·/÷", operand: { $0.unary() }) {
                v = "/÷".contains(op) ? v / r : v * r
            }
            return v
        }

        mutating func power() -> Double? {
            guard let base = postfix() else { return nil }
            if case let (_, e)? = binary("^", operand: { $0.unary() }) { return pow(base, e) }
            return base
        }

        mutating func unary() -> Double? {
            let save = i
            skipSpaces()
            if i < c.count, "-−+".contains(c[i]) {
                let sign: Double = c[i] == "+" ? 1 : -1
                i += 1
                if let v = unary() { return sign * v }
                i = save
                return nil
            }
            return power()
        }

        mutating func postfix() -> Double? {
            guard var v = primary() else { return nil }
            while i < c.count, c[i] == "%" { v /= 100; i += 1 }
            return v
        }

        mutating func primary() -> Double? {
            let save = i
            skipSpaces()
            if i < c.count, c[i] == "(" {
                i += 1
                if let v = expr() {
                    skipSpaces()
                    if i < c.count, c[i] == ")" { i += 1; return v }
                }
                i = save
                return nil
            }
            if let v = number() { return v }
            i = save
            return nil
        }

        /// One operator from `set` and its right operand; on failure nothing is consumed, so the expression ends before it.
        mutating func binary(_ set: String, operand: (inout Parser) -> Double?) -> (Character, Double)? {
            let save = i, savedOps = ops.count, savedVN = vnStyle, savedUS = usStyle
            let spaceBefore = i < c.count && c[i].isInlineSpace
            skipSpaces()
            guard i < c.count, set.contains(c[i]) else { i = save; return nil }
            let op = c[i]
            i += 1
            let spaceAfter = i < c.count && c[i].isInlineSpace
            // "x" multiplies only between numbers: "23500x410", "2 x 3", not "3xl" or "x2".
            if "xX".contains(op), !nextIsOperand() { i = save; return nil }
            guard let r = operand(&self) else {
                i = save; ops.removeLast(ops.count - savedOps); vnStyle = savedVN; usStyle = savedUS
                return nil
            }
            ops.append((op, spaceBefore && spaceAfter))
            return (op, r)
        }

        private func nextIsOperand() -> Bool {
            var j = i
            while j < c.count, c[j].isInlineSpace { j += 1 }
            return j < c.count && (c[j].isDigit || c[j] == "(")
        }

        /// Digits with "." / "," as thousands or decimal separator, read the Vietnamese way when ambiguous ("1.500" = 1500).
        mutating func number() -> Double? {
            guard i < c.count, c[i].isDigit else { return nil }
            var groups: [String] = []
            var seps: [Character] = []
            var cur = ""
            while i < c.count {
                if c[i].isDigit { cur.append(c[i]); i += 1; continue }
                if (c[i] == "." || c[i] == ","), i + 1 < c.count, c[i + 1].isDigit {
                    groups.append(cur); cur = ""; seps.append(c[i]); i += 1; continue
                }
                break
            }
            groups.append(cur)
            guard !seps.isEmpty else { return Double(cur) }
            let last = seps.last!
            var decimalAt: Int? = nil
            if Set(seps).count == 2 {
                // "1.234,5" / "1,234.5": the last separator is the decimal one and appears once.
                guard seps.filter({ $0 == last }).count == 1 else { return nil }
                decimalAt = seps.count - 1
            } else if seps.count == 1, groups[1].count != 3 {
                decimalAt = 0
            }
            let groupEnd = decimalAt ?? seps.count
            // Thousand groups are three digits; anything else ("30.10.2026", "192.168.1.1") is not a number.
            guard groups.dropFirst().prefix(groupEnd).allSatisfy({ $0.count == 3 }) else { return nil }
            if let d = decimalAt {
                if seps[d] == "," { vnStyle = true } else { usStyle = true }
            }
            if groupEnd > 0 {
                if seps[0] == "." { vnStyle = true } else { usStyle = true }
            }
            let whole = groups[0...groupEnd].joined()
            let frac = decimalAt.map { groups[$0 + 1] } ?? ""
            return Double(frac.isEmpty ? whole : whole + "." + frac)
        }

        private mutating func skipSpaces() {
            while i < c.count, c[i].isInlineSpace { i += 1 }
        }
    }
}

/// A message held back because it contains arithmetic, until the user picks the computed or the original text.
struct MathPreview: Equatable {
    let original: String
    let pieces: [MessageMath.Piece]
    /// The file it was being sent with (attach button), so either choice still sends it.
    let filePath: String?
    var computed: String { MessageMath.joined(pieces) }
}

private extension Character {
    var isDigit: Bool { ("0"..."9").contains(self) }
    var isInlineSpace: Bool { self == " " || self == "\t" || self == "\u{00A0}" }
}
