using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace ChatSpace.Kit;

public enum Scheme { Light, Dark }

public enum Weight { UltraLight, Thin, Light, Regular, Medium, Semibold, Bold, Heavy, Black }

public enum Design { Default, Monospaced, Rounded }

/// SwiftUI `Font`: the system font (SF Pro, optical size and tracking chosen from the point size) or a named family.
public sealed record SFont(double Size, Weight Weight = Weight.Regular, Design Design = Design.Default, string? Family = null,
    bool Italic = false)
{
    /// macOS `.body`.
    public static readonly SFont Body = new(13);
    public static SFont System(double size, Weight weight = Weight.Regular, Design design = Design.Default) => new(size, weight, design);
    public static SFont Custom(string family, double size, Weight weight = Weight.Regular) => new(size, weight, Design.Default, family);
    public static readonly SFont Title = new(22);
    public static readonly SFont Title2 = new(17);
    public static readonly SFont Title3 = new(15);
    public static readonly SFont Headline = new(13, Weight.Bold);
    public static readonly SFont Subheadline = new(11);
    public static readonly SFont Callout = new(12);
    public static readonly SFont Caption = new(10);
    public static readonly SFont Caption2 = new(10);
    public static readonly SFont Footnote = new(10);
    public static readonly SFont LargeTitle = new(26);

    public SFont WithWeight(Weight w) => this with { Weight = w };
    public SFont Bold() => this with { Weight = Weight.Bold };
    public SFont Monospaced() => this with { Design = Design.Monospaced };

    public FontWeight AvaloniaWeight => Weight switch
    {
        Weight.UltraLight => (FontWeight)100, Weight.Thin => (FontWeight)200, Weight.Light => FontWeight.Light,
        Weight.Regular => FontWeight.Normal, Weight.Medium => FontWeight.Medium, Weight.Semibold => FontWeight.SemiBold,
        Weight.Bold => FontWeight.Bold, Weight.Heavy => FontWeight.ExtraBold, _ => FontWeight.Black,
    };

    private const string Fonts = "avares://ChatSpace/Assets/Fonts";
    private static readonly Dictionary<string, FontFamily> Families = new();

    public FontFamily AvaloniaFamily
    {
        get
        {
            string key;
            if (Family != null) key = Family;
            else if (Design == Design.Monospaced) key = Fonts + "#SF Mono";
            else
            {
                var opsz = Size < 18.5 ? 17 : Size < 22 ? 20 : Size < 26 ? 24 : 28;
                key = $"{Fonts}#SF Pro {opsz}";
            }
            lock (Families)
            {
                if (!Families.TryGetValue(key, out var f)) Families[key] = f = new FontFamily(Family != null ? key : key + ", Segoe UI Emoji, Segoe UI Symbol, Apple Color Emoji");
                return f;
            }
        }
    }

    /// The tracking macOS applies to the system font at this size (`trak` table of SFNS.ttf).
    public double LetterSpacing => Family == null && Design == Design.Default ? Tracking.At(Size) : 0;
}

internal static class Tracking
{
    private static readonly (double size, double value)[] Table = Load();

    private static (double, double)[] Load()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://ChatSpace/Assets/Fonts/sf-tracking.json"));
            var doc = JsonDocument.Parse(s);
            return doc.RootElement.GetProperty("track").EnumerateObject()
                .Select(p => (double.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p.Value.GetDouble()))
                .OrderBy(t => t.Item1).ToArray();
        }
        catch { return [(12, 0)]; }
    }

    public static double At(double size)
    {
        var t = Table;
        double v;
        if (size <= t[0].size) v = t[0].value;
        else if (size >= t[^1].size) v = t[^1].value;
        else
        {
            var i = 1;
            while (t[i].size < size) i++;
            var (s0, v0) = t[i - 1];
            var (s1, v1) = t[i];
            v = v0 + (v1 - v0) * (size - s0) / (s1 - s0);
        }
        return v * size / 2048.0;
    }
}

/// SwiftUI colors, including the semantic ones that depend on the color scheme.
public readonly record struct SColor
{
    public enum Kind { Fixed, Primary, Secondary, Tertiary, Quaternary, Accent, WindowBackground, ControlBackground, Separator, TextBackground, UnderPage }

    public Kind K { get; init; }
    public Color C { get; init; }
    public double Alpha { get; init; }

    public SColor(Color c) { K = Kind.Fixed; C = c; Alpha = 1; }
    private SColor(Kind k, double alpha = 1) { K = k; C = default; Alpha = alpha; }

    public static SColor Hex(uint hex, double alpha = 1) =>
        new(Color.FromArgb((byte)Math.Round(alpha * 255), (byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF)));
    public static SColor Rgb(double r, double g, double b, double a = 1) =>
        new(Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255)));

    /// `Color(hue:saturation:brightness:)`, components 0…1.
    public static SColor Hsb(double h, double s, double b, double a = 1)
    {
        h = (h % 1 + 1) % 1 * 6;
        var i = (int)Math.Floor(h);
        var f = h - i;
        double p = b * (1 - s), q = b * (1 - s * f), t = b * (1 - s * (1 - f));
        var (r, g, bl) = i switch { 0 => (b, t, p), 1 => (q, b, p), 2 => (p, b, t), 3 => (p, q, b), 4 => (t, p, b), _ => (b, p, q) };
        return Rgb(r, g, bl, a);
    }
    public static readonly SColor Clear = new(Colors.Transparent);
    public static readonly SColor White = new(Colors.White);
    public static readonly SColor Black = new(Colors.Black);
    // macOS system colors (light appearance).
    public static readonly SColor Red = Hex(0xFF3B30);
    public static readonly SColor Orange = Hex(0xFF9500);
    public static readonly SColor Yellow = Hex(0xFFCC00);
    public static readonly SColor Green = Hex(0x28CD41);
    public static readonly SColor Mint = Hex(0x00C7BE);
    public static readonly SColor Teal = Hex(0x59ADC4);
    public static readonly SColor Cyan = Hex(0x55BEF0);
    public static readonly SColor Blue = Hex(0x007AFF);
    public static readonly SColor Indigo = Hex(0x5856D6);
    public static readonly SColor Purple = Hex(0xAF52DE);
    public static readonly SColor Pink = Hex(0xFF2D55);
    public static readonly SColor Brown = Hex(0xA2845E);
    public static readonly SColor Gray = Hex(0x8E8E93);

    public static readonly SColor Primary = new(Kind.Primary);
    public static readonly SColor Secondary = new(Kind.Secondary);
    public static readonly SColor Tertiary = new(Kind.Tertiary);
    public static readonly SColor Quaternary = new(Kind.Quaternary);
    public static readonly SColor Accent = new(Kind.Accent);
    public static readonly SColor WindowBackground = new(Kind.WindowBackground);
    public static readonly SColor ControlBackground = new(Kind.ControlBackground);
    public static readonly SColor TextBackground = new(Kind.TextBackground);
    public static readonly SColor Separator = new(Kind.Separator);
    public static readonly SColor UnderPage = new(Kind.UnderPage);

    public SColor Opacity(double a) => this with { Alpha = Alpha * a, C = K == Kind.Fixed ? WithAlpha(C, C.A / 255.0 * a) : C };

    public bool IsSemantic => K != Kind.Fixed;

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    public Color Resolve(Scheme s)
    {
        var dark = s == Scheme.Dark;
        Color c = K switch
        {
            Kind.Fixed => C,
            Kind.Primary => dark ? Color.FromArgb(217, 255, 255, 255) : Color.FromArgb(217, 0, 0, 0),
            Kind.Secondary => dark ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(128, 0, 0, 0),
            Kind.Tertiary => dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(66, 0, 0, 0),
            Kind.Quaternary => dark ? Color.FromArgb(25, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0),
            Kind.Accent => dark ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x00, 0x7A, 0xFF),
            Kind.WindowBackground => dark ? Color.FromRgb(0x32, 0x32, 0x32) : Color.FromRgb(0xEC, 0xEC, 0xEC),
            Kind.ControlBackground => dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White,
            Kind.TextBackground => dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White,
            Kind.Separator => dark ? Color.FromArgb(25, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0),
            Kind.UnderPage => dark ? Color.FromRgb(0x28, 0x28, 0x28) : Color.FromRgb(0xE3, 0xE3, 0xE3),
            _ => C,
        };
        return K == Kind.Fixed ? c : WithAlpha(c, c.A / 255.0 * Alpha);
    }

    public static implicit operator SColor(Color c) => new(c);
}

/// Any SwiftUI ShapeStyle used for fills and foregrounds: a color, a hierarchical level of the current
/// foreground, or a gradient.
public abstract record Paint
{
    public static implicit operator Paint(SColor c) => new ColorPaint(c);
    public abstract IBrush Brush(Scheme s, Rect bounds);
}

public sealed record ColorPaint(SColor Color) : Paint
{
    public override IBrush Brush(Scheme s, Rect bounds) => new ImmutableSolidColorBrush(Color.Resolve(s));
}

public sealed record LinearGradient(SColor[] Colors, Point Start, Point End, double[]? Locations = null) : Paint
{
    public static readonly Point Top = new(0.5, 0), Bottom = new(0.5, 1), Leading = new(0, 0.5), Trailing = new(1, 0.5),
        TopLeading = new(0, 0), TopTrailing = new(1, 0), BottomLeading = new(0, 1), BottomTrailing = new(1, 1), Center = new(0.5, 0.5);

    public override IBrush Brush(Scheme s, Rect bounds)
    {
        var stops = new GradientStops();
        for (var i = 0; i < Colors.Length; i++)
        {
            var off = Locations?[i] ?? (Colors.Length == 1 ? 0 : (double)i / (Colors.Length - 1));
            stops.Add(new GradientStop(Colors[i].Resolve(s), off));
        }
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(Start, RelativeUnit.Relative),
            EndPoint = new RelativePoint(End, RelativeUnit.Relative),
            GradientStops = stops,
        };
    }
}

public sealed record RadialGradient(SColor[] Colors, Point Center, double StartRadius, double EndRadius) : Paint
{
    public override IBrush Brush(Scheme s, Rect bounds)
    {
        var stops = new GradientStops();
        var span = EndRadius <= 0 ? 1 : EndRadius;
        for (var i = 0; i < Colors.Length; i++)
        {
            var r = StartRadius + (EndRadius - StartRadius) * (Colors.Length == 1 ? 0 : (double)i / (Colors.Length - 1));
            stops.Add(new GradientStop(Colors[i].Resolve(s), r / span));
        }
        return new RadialGradientBrush
        {
            Center = new RelativePoint(Center.X * bounds.Width, Center.Y * bounds.Height, RelativeUnit.Absolute),
            GradientOrigin = new RelativePoint(Center.X * bounds.Width, Center.Y * bounds.Height, RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(EndRadius, RelativeUnit.Absolute),
            RadiusY = new RelativeScalar(EndRadius, RelativeUnit.Absolute),
            GradientStops = stops,
        };
    }
}

/// The SwiftUI environment values ChatSpace relies on, as inherited Avalonia properties.
public static class Env
{
    public static readonly AttachedProperty<SFont> FontProperty =
        AvaloniaProperty.RegisterAttached<Control, SFont>("Font", typeof(Env), SFont.Body, inherits: true);

    /// Base foreground (`.foregroundStyle(color)`); null = the scheme's label color.
    public static readonly AttachedProperty<SColor?> ForegroundProperty =
        AvaloniaProperty.RegisterAttached<Control, SColor?>("Foreground", typeof(Env), null, inherits: true);

    /// `.foregroundStyle(.secondary)` etc. on top of the base foreground: 0 primary … 3 quaternary.
    public static readonly AttachedProperty<int> LevelProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("Level", typeof(Env), 0, inherits: true);

    public static readonly AttachedProperty<Paint?> ForegroundPaintProperty =
        AvaloniaProperty.RegisterAttached<Control, Paint?>("ForegroundPaint", typeof(Env), null, inherits: true);

    public static readonly AttachedProperty<Scheme> SchemeProperty =
        AvaloniaProperty.RegisterAttached<Control, Scheme>("Scheme", typeof(Env), Scheme.Light, inherits: true);

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enabled", typeof(Env), true, inherits: true);

    public static readonly AttachedProperty<int?> LineLimitProperty =
        AvaloniaProperty.RegisterAttached<Control, int?>("LineLimit", typeof(Env), null, inherits: true);

    public static readonly AttachedProperty<TextAlignment> MultilineAlignmentProperty =
        AvaloniaProperty.RegisterAttached<Control, TextAlignment>("MultilineAlignment", typeof(Env), TextAlignment.Left, inherits: true);

    public static readonly AttachedProperty<SColor?> TintProperty =
        AvaloniaProperty.RegisterAttached<Control, SColor?>("Tint", typeof(Env), null, inherits: true);

    public static IBrush ResolveForeground(Control c, Rect bounds)
    {
        var scheme = c.GetValue(SchemeProperty);
        var level = c.GetValue(LevelProperty);
        var paint = c.GetValue(ForegroundPaintProperty);
        if (paint != null && level == 0) return paint.Brush(scheme, bounds);
        return new ImmutableSolidColorBrush(ResolveColor(c));
    }

    public static Color ResolveColor(Control c)
    {
        var scheme = c.GetValue(SchemeProperty);
        var level = c.GetValue(LevelProperty);
        var fg = c.GetValue(ForegroundProperty);
        Color color;
        if (fg == null || fg.Value.K == SColor.Kind.Primary)
        {
            color = (level switch { 0 => SColor.Primary, 1 => SColor.Secondary, 2 => SColor.Tertiary, _ => SColor.Quaternary }).Resolve(scheme);
        }
        else
        {
            var baseC = fg.Value.Resolve(scheme);
            var f = level switch { 0 => 1.0, 1 => 0.6, 2 => 0.32, _ => 0.14 };
            color = Color.FromArgb((byte)Math.Round(baseC.A * f), baseC.R, baseC.G, baseC.B);
        }
        if (!c.GetValue(EnabledProperty)) color = Color.FromArgb((byte)(color.A * 0.45), color.R, color.G, color.B);
        return color;
    }
}
