using System.Globalization;
using System.Text;

namespace ChatSpace.Core;

/// Arithmetic typed into a message ("Số tiền: 23500*410"), so the composer can offer sending it with the result.
public static class MessageMath
{
    public sealed record Piece(string Text, bool IsResult = false);

    /// The message cut into pieces with " = result" after each expression, or null when there is nothing to compute.
    public static IReadOnlyList<Piece>? Annotate(string text)
    {
        var chars = text.ToCharArray();
        // Links keep their digits ("…/item/12+3"): blank them out so no expression starts inside one.
        var at = 0;
        foreach (var seg in Linkify.Segments(text))
        {
            if (seg.Url != null) for (var k = at; k < at + seg.Text.Length && k < chars.Length; k++) chars[k] = 'a';
            at += seg.Text.Length;
        }
        var pieces = new List<Piece>();
        var copied = 0;
        var i = 0;
        while (i < chars.Length)
        {
            if (!CanStart(chars, i) || Expression(chars, i) is not { } hit) { i++; continue; }
            pieces.Add(new Piece(text[copied..hit.end]));
            pieces.Add(new Piece(" = " + hit.result, true));
            copied = hit.end;
            i = hit.end;
        }
        if (pieces.Count == 0) return null;
        if (copied < text.Length) pieces.Add(new Piece(text[copied..]));
        return pieces;
    }

    public static string Joined(IEnumerable<Piece> pieces) => string.Concat(pieces.Select(p => p.Text));

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsInlineSpace(char c) => c is ' ' or '\t' or '\u00A0';

    private static bool CanStart(char[] c, int i)
    {
        if (c[i] != '(' && !IsDigit(c[i])) return false;
        if (i == 0) return true;
        var p = c[i - 1];
        // "-5*3", "+84…", "1/2*3" or "a1+2": the expression would really begin before this point.
        return !(char.IsLetter(p) || IsDigit(p) || "_.,+-−*/×÷^%=(".Contains(p));
    }

    private static (int end, string result)? Expression(char[] c, int start)
    {
        var p = new Parser(c, start);
        if (p.Expr() is not { } value || p.Ops.Count == 0 || !double.IsFinite(value) || Math.Abs(value) >= 1e15) return null;
        // Dashes and slashes alone are mostly ranges, dates and phone numbers ("8h - 17h", "30/10/2026");
        // they count only beside ×, + or ^, or as a spaced division ("1500000 / 3").
        var strong = p.Ops.Any(o => !"-−/".Contains(o.op));
        var spacedDivision = p.Ops.All(o => o.op == '/' && o.spaced);
        if (!strong && !spacedDivision) return null;
        var j = p.I;
        while (j < c.Length && IsInlineSpace(c[j])) j++;
        // Already worked out by the sender ("2+2 = 4").
        if (j < c.Length && c[j] == '=') return null;
        return (p.I, Format(value, p.UsStyle && !p.VnStyle));
    }

    private static string Format(double v, bool usStyle)
    {
        if (v == 0) v = 0;
        var digits = Math.Abs(v) < 1 ? 6 : 2;
        var rounded = Math.Round(v, digits, MidpointRounding.AwayFromZero);
        var s = rounded.ToString("#,##0." + new string('#', digits), CultureInfo.InvariantCulture);
        if (usStyle) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch == ',' ? '.' : ch == '.' ? ',' : ch);
        return sb.ToString();
    }

    /// Recursive descent over one line: + − below × ÷ below ^, postfix %, parentheses.
    private sealed class Parser
    {
        private readonly char[] _c;
        public int I;
        public readonly List<(char op, bool spaced)> Ops = new();
        /// Number spelling seen so far: "1.500" / "2,5" is Vietnamese, "1,500" / "2.5" English.
        public bool VnStyle, UsStyle;

        public Parser(char[] c, int i) { _c = c; I = i; }

        public double? Expr()
        {
            if (Term() is not { } v) return null;
            while (Binary("+-−", p => p.Term()) is { } b) v = b.op == '+' ? v + b.r : v - b.r;
            return v;
        }

        private double? Term()
        {
            if (Unary() is not { } v) return null;
            while (Binary("*×xX·/÷", p => p.Unary()) is { } b) v = "/÷".Contains(b.op) ? v / b.r : v * b.r;
            return v;
        }

        private double? Power()
        {
            if (Postfix() is not { } b) return null;
            if (Binary("^", p => p.Unary()) is { } e) return Math.Pow(b, e.r);
            return b;
        }

        private double? Unary()
        {
            var save = I;
            SkipSpaces();
            if (I < _c.Length && "-−+".Contains(_c[I]))
            {
                double sign = _c[I] == '+' ? 1 : -1;
                I++;
                if (Unary() is { } v) return sign * v;
                I = save;
                return null;
            }
            return Power();
        }

        private double? Postfix()
        {
            if (Primary() is not { } v) return null;
            while (I < _c.Length && _c[I] == '%') { v /= 100; I++; }
            return v;
        }

        private double? Primary()
        {
            var save = I;
            SkipSpaces();
            if (I < _c.Length && _c[I] == '(')
            {
                I++;
                if (Expr() is { } v)
                {
                    SkipSpaces();
                    if (I < _c.Length && _c[I] == ')') { I++; return v; }
                }
                I = save;
                return null;
            }
            if (Number() is { } n) return n;
            I = save;
            return null;
        }

        /// One operator from `set` and its right operand; on failure nothing is consumed, so the expression ends before it.
        private (char op, double r)? Binary(string set, Func<Parser, double?> operand)
        {
            int save = I, savedOps = Ops.Count;
            bool savedVN = VnStyle, savedUS = UsStyle;
            var spaceBefore = I < _c.Length && IsInlineSpace(_c[I]);
            SkipSpaces();
            if (I >= _c.Length || !set.Contains(_c[I])) { I = save; return null; }
            var op = _c[I];
            I++;
            var spaceAfter = I < _c.Length && IsInlineSpace(_c[I]);
            // "x" multiplies only between numbers: "23500x410", "2 x 3", not "3xl" or "x2".
            if ((op == 'x' || op == 'X') && !NextIsOperand()) { I = save; return null; }
            if (operand(this) is not { } r)
            {
                I = save;
                Ops.RemoveRange(savedOps, Ops.Count - savedOps);
                VnStyle = savedVN;
                UsStyle = savedUS;
                return null;
            }
            Ops.Add((op, spaceBefore && spaceAfter));
            return (op, r);
        }

        private bool NextIsOperand()
        {
            var j = I;
            while (j < _c.Length && IsInlineSpace(_c[j])) j++;
            return j < _c.Length && (IsDigit(_c[j]) || _c[j] == '(');
        }

        /// Digits with "." / "," as thousands or decimal separator, read the Vietnamese way when ambiguous ("1.500" = 1500).
        private double? Number()
        {
            if (I >= _c.Length || !IsDigit(_c[I])) return null;
            var groups = new List<string>();
            var seps = new List<char>();
            var cur = new StringBuilder();
            while (I < _c.Length)
            {
                if (IsDigit(_c[I])) { cur.Append(_c[I]); I++; continue; }
                if ((_c[I] == '.' || _c[I] == ',') && I + 1 < _c.Length && IsDigit(_c[I + 1]))
                {
                    groups.Add(cur.ToString()); cur.Clear(); seps.Add(_c[I]); I++; continue;
                }
                break;
            }
            groups.Add(cur.ToString());
            if (seps.Count == 0) return double.Parse(groups[0], CultureInfo.InvariantCulture);
            var last = seps[^1];
            int? decimalAt = null;
            if (seps.Distinct().Count() == 2)
            {
                // "1.234,5" / "1,234.5": the last separator is the decimal one and appears once.
                if (seps.Count(s => s == last) != 1) return null;
                decimalAt = seps.Count - 1;
            }
            else if (seps.Count == 1 && groups[1].Length != 3)
            {
                decimalAt = 0;
            }
            var groupEnd = decimalAt ?? seps.Count;
            // Thousand groups are three digits; anything else ("30.10.2026", "192.168.1.1") is not a number.
            if (!groups.Skip(1).Take(groupEnd).All(g => g.Length == 3)) return null;
            if (decimalAt is { } d)
            {
                if (seps[d] == ',') VnStyle = true; else UsStyle = true;
            }
            if (groupEnd > 0)
            {
                if (seps[0] == '.') VnStyle = true; else UsStyle = true;
            }
            var whole = string.Concat(groups.Take(groupEnd + 1));
            var frac = decimalAt is { } dd ? groups[dd + 1] : "";
            return double.Parse(frac.Length == 0 ? whole : whole + "." + frac, CultureInfo.InvariantCulture);
        }

        private void SkipSpaces()
        {
            while (I < _c.Length && IsInlineSpace(_c[I])) I++;
        }
    }
}

/// A message held back because it contains arithmetic, until the user picks the computed or the original text.
public sealed record MathPreview(string Original, IReadOnlyList<MessageMath.Piece> Pieces, string? FilePath)
{
    public string Computed => MessageMath.Joined(Pieces);

    public bool Equals(MathPreview? o) => o is not null && Original == o.Original && FilePath == o.FilePath && Pieces.SequenceEqual(o.Pieces);
    public override int GetHashCode() => HashCode.Combine(Original, FilePath);
}
