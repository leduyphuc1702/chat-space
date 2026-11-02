using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ChatSpace.Kit;

/// SwiftUI `Text`: system font from the environment (with macOS tracking), hierarchical foreground,
/// wraps by default and truncates the tail when out of room.
public class SText : TextBlock
{
    public SFont? OwnFont { get; set; }
    public bool Truncate { get; set; } = true;

    public SText() { }
    public SText(string text) { Text = text; }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Apply();
    }

    private void EnvChanged(AvaloniaProperty p)
    {
        if (p == Env.FontProperty || p == Env.ForegroundProperty || p == Env.LevelProperty || p == Env.SchemeProperty ||
            p == Env.ForegroundPaintProperty || p == Env.LineLimitProperty || p == Env.EnabledProperty || p == Env.MultilineAlignmentProperty)
            Apply();
    }

    private List<Run>? _emojiRuns;
    private bool _splitting;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty && !_splitting && _emojiRuns != null)
        {
            _emojiRuns = null;
            Inlines = null;
            if (this.IsAttachedToVisualTree()) Apply();
        }
        else EnvChanged(change.Property);
    }

    /// Emoji get their own runs so they can be drawn at the size CoreText draws them (see `Emoji.Scale`).
    private void SplitEmoji(double size)
    {
        if (_emojiRuns == null && this is not RichText && Text is { } t && EmojiText.Has(t))
        {
            _splitting = true;
            _emojiRuns = new();
            var inlines = new InlineCollection();
            foreach (var (seg, isEmoji) in EmojiText.Segments(t))
            {
                var r = new Run(seg);
                if (isEmoji)
                {
                    // Skia multiplies colour glyphs by the text colour's alpha; CoreText draws emoji opaque.
                    r.Foreground = Brushes.Black;
                    _emojiRuns.Add(r);
                }
                inlines.Add(r);
            }
            Inlines = inlines;
            _splitting = false;
        }
        if (_emojiRuns != null) foreach (var r in _emojiRuns) r.FontSize = size * EmojiText.Scale(size);
    }

    internal void Apply()
    {
        var f = OwnFont ?? GetValue(Env.FontProperty);
        FontFamily = f.AvaloniaFamily;
        FontSize = f.Size;
        SplitEmoji(f.Size);
        FontWeight = f.AvaloniaWeight;
        FontStyle = f.Italic ? FontStyle.Italic : FontStyle.Normal;
        LetterSpacing = f.LetterSpacing;
        Foreground = Env.ResolveForeground(this, Bounds);
        var limit = GetValue(Env.LineLimitProperty);
        MaxLines = limit ?? 0;
        TextWrapping = limit == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        TextTrimming = Truncate ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        TextAlignment = GetValue(Env.MultilineAlignmentProperty);
    }
}

/// SF Symbol drawn from the exported alpha masks, sized like `Image(systemName:)` under the current font.
public sealed class Symbol : Control
{
    private static readonly Dictionary<string, Dictionary<string, (double w, double h)>> Manifest = LoadManifest();
    private static readonly Dictionary<string, Bitmap?> Bitmaps = new();
    private const double ManifestPoint = 64;

    public string Name { get; }
    /// `.resizable()` + frame: fill the given size keeping the aspect ratio.
    public bool Resizable { get; init; }
    /// Palette rendering: (primary layer, secondary layer).
    public (Paint, Paint)? Palette { get; init; }
    public SFont? OwnFont { get; init; }

    public Symbol(string name) { Name = name; }

    public static bool Exists(string name) => Manifest.ContainsKey(name);

    private static Dictionary<string, Dictionary<string, (double, double)>> LoadManifest()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://ChatSpace/Assets/Native/symbols/symbols.json"));
            var doc = JsonDocument.Parse(s);
            var result = new Dictionary<string, Dictionary<string, (double, double)>>();
            foreach (var sym in doc.RootElement.GetProperty("symbols").EnumerateObject())
            {
                var weights = new Dictionary<string, (double, double)>();
                foreach (var w in sym.Value.EnumerateObject())
                    weights[w.Name] = (w.Value.GetProperty("w").GetDouble(), w.Value.GetProperty("h").GetDouble());
                result[sym.Name] = weights;
            }
            return result;
        }
        catch { return new(); }
    }

    private static string WeightKey(Weight w) => w switch
    {
        Weight.UltraLight => "ultraLight", Weight.Thin => "thin", Weight.Light => "light", Weight.Regular => "regular",
        Weight.Medium => "medium", Weight.Semibold => "semibold", Weight.Bold => "bold", Weight.Heavy => "heavy", _ => "black",
    };

    internal static Bitmap? Mask(string name, string weight, string suffix = "")
    {
        var key = $"{name}.{weight}{suffix}";
        lock (Bitmaps)
        {
            if (Bitmaps.TryGetValue(key, out var b)) return b;
            try { b = new Bitmap(AssetLoader.Open(new Uri($"avares://ChatSpace/Assets/Native/symbols/{key}.png"))); }
            catch { b = null; }
            Bitmaps[key] = b;
            return b;
        }
    }

    private SFont Font => OwnFont ?? GetValue(Env.FontProperty);

    private (double w, double h) NaturalSize
    {
        get
        {
            if (!Manifest.TryGetValue(Name, out var m)) return (Font.Size, Font.Size);
            var k = WeightKey(Font.Weight);
            var (w, h) = m.TryGetValue(k, out var v) ? v : m.Values.First();
            var s = Font.Size / ManifestPoint;
            return (w * s, h * s);
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var (w, h) = NaturalSize;
        if (!Resizable) return new Size(w, h);
        if (double.IsInfinity(available.Width) && double.IsInfinity(available.Height)) return new Size(w, h);
        var scale = Math.Min(double.IsInfinity(available.Width) ? double.MaxValue : available.Width / w,
                             double.IsInfinity(available.Height) ? double.MaxValue : available.Height / h);
        return new Size(w * scale, h * scale);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == Env.FontProperty) InvalidateMeasure();
        if (p == Env.FontProperty || p == Env.ForegroundProperty || p == Env.LevelProperty || p == Env.SchemeProperty ||
            p == Env.ForegroundPaintProperty || p == Env.EnabledProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var weight = Manifest.TryGetValue(Name, out var m) && m.ContainsKey(WeightKey(Font.Weight)) ? WeightKey(Font.Weight) : "regular";
        var (w, h) = NaturalSize;
        var scale = Math.Min(Bounds.Width / w, Bounds.Height / h);
        if (!Resizable) scale = 1;
        var rect = new Rect((Bounds.Width - w * scale) / 2, (Bounds.Height - h * scale) / 2, w * scale, h * scale);
        var scheme = GetValue(Env.SchemeProperty);
        if (Palette is { } pal)
        {
            Draw(ctx, Mask(Name, weight, ".p0"), rect, pal.Item1.Brush(scheme, rect));
            Draw(ctx, Mask(Name, weight, ".p1"), rect, pal.Item2.Brush(scheme, rect));
            return;
        }
        Draw(ctx, Mask(Name, weight), rect, Env.ResolveForeground(this, rect));
    }

    internal static void Draw(DrawingContext ctx, Bitmap? mask, Rect rect, IBrush brush)
    {
        if (mask == null) return;
        using (ctx.PushOpacityMask(new ImageBrush(mask) { Stretch = Stretch.Fill }, rect))
            ctx.FillRectangle(brush, rect);
    }
}

/// An image from an official app's asset catalog (WhatsApp, Telegram), drawn as a template in the
/// foreground color or in its own colors.
public sealed class NativeImage : Control
{
    private static readonly Dictionary<string, Dictionary<string, (double w, double h, bool template)>> Catalogs = Load();
    private static readonly Dictionary<string, Bitmap?> Cache = new();

    public string Catalog { get; }
    public string Name { get; }
    public double? Size { get; init; }
    public bool Template { get; init; } = true;

    public NativeImage(string catalog, string name) { Catalog = catalog; Name = name; }

    private static Dictionary<string, Dictionary<string, (double, double, bool)>> Load()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://ChatSpace/Assets/Native/catalogs.json"));
            var doc = JsonDocument.Parse(s);
            return doc.RootElement.EnumerateObject().ToDictionary(c => c.Name, c => c.Value.EnumerateObject().ToDictionary(
                e => e.Name, e => (e.Value.GetProperty("w").GetDouble(), e.Value.GetProperty("h").GetDouble(), e.Value.GetProperty("template").GetDouble() > 0)));
        }
        catch { return new(); }
    }

    public static bool Exists(string catalog, string name) => Catalogs.TryGetValue(catalog, out var c) && c.ContainsKey(name);

    public static (double w, double h)? NaturalSize(string catalog, string name) =>
        Catalogs.TryGetValue(catalog, out var c) && c.TryGetValue(name, out var e) ? (e.w, e.h) : null;

    public static Bitmap? Bitmap(string catalog, string name)
    {
        var key = catalog + "/" + name;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var b)) return b;
            try { b = new Bitmap(AssetLoader.Open(new Uri($"avares://ChatSpace/Assets/Native/{catalog}/{name}.png"))); }
            catch { b = null; }
            Cache[key] = b;
            return b;
        }
    }

    private (double w, double h) Natural => NaturalSize(Catalog, Name) ?? (16, 16);

    protected override Size MeasureOverride(Size available)
    {
        var (w, h) = Natural;
        if (Size is { } s) return new Size(s, s * h / Math.Max(w, 1));
        return new Size(w, h);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == Env.ForegroundProperty || change.Property == Env.LevelProperty || change.Property == Env.SchemeProperty ||
            change.Property == Env.ForegroundPaintProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var bmp = Bitmap(Catalog, Name);
        if (bmp == null) return;
        var (w, h) = Natural;
        var scale = Math.Min(Bounds.Width / w, Bounds.Height / h);
        var rect = new Rect((Bounds.Width - w * scale) / 2, (Bounds.Height - h * scale) / 2, w * scale, h * scale);
        if (Template) Symbol.Draw(ctx, bmp, rect, Env.ResolveForeground(this, rect));
        else ctx.DrawImage(bmp, rect);
    }
}

public enum ShapeKind { Rectangle, RoundedRectangle, Circle, Capsule, Ellipse, Custom }

/// SwiftUI shapes (`Rectangle`, `RoundedRectangle(style: .continuous)`, `Circle`, `Capsule`, `Ellipse`)
/// with fill, stroke and strokeBorder. Like SwiftUI shapes they take all the space offered.
public sealed class ShapeView : Control, IFlexible
{
    public ShapeKind Kind { get; init; }
    public double CornerRadius { get; init; }
    public bool Continuous { get; init; }
    public Func<Rect, Geometry>? Path { get; init; }
    public Paint? Fill { get; set; }
    public Paint? Stroke { get; set; }
    public double LineWidth { get; set; } = 1;
    /// strokeBorder: the stroke sits inside the shape.
    public bool Inset { get; set; }
    public double[]? Dash { get; set; }
    /// Fill with the environment foreground (a shape with no explicit fill).
    public bool FillWithForeground { get; set; }

    public bool IsFlexible(Orientation axis) => true;

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == Env.SchemeProperty || (FillWithForeground && (change.Property == Env.ForegroundProperty ||
            change.Property == Env.LevelProperty || change.Property == Env.ForegroundPaintProperty)))
            InvalidateVisual();
    }

    public Geometry Geometry(Rect r) => Shapes.Geometry(Kind, r, CornerRadius, Continuous, Path);

    public override void Render(DrawingContext ctx)
    {
        var scheme = GetValue(Env.SchemeProperty);
        var bounds = new Rect(Bounds.Size);
        if (Fill != null || FillWithForeground)
        {
            var brush = Fill?.Brush(scheme, bounds) ?? Env.ResolveForeground(this, bounds);
            ctx.DrawGeometry(brush, null, Geometry(bounds));
        }
        if (Stroke != null)
        {
            var r = Inset ? bounds.Deflate(LineWidth / 2) : bounds;
            var pen = new Pen(Stroke.Brush(scheme, bounds), LineWidth) { DashStyle = Dash != null ? new DashStyle(Dash.Select(d => d / LineWidth), 0) : null };
            var radiusAdj = Inset ? Math.Max(0, CornerRadius - LineWidth / 2) : CornerRadius;
            ctx.DrawGeometry(null, pen, Shapes.Geometry(Kind, r, radiusAdj, Continuous, Path));
        }
    }
}

public static class Shapes
{
    public static Geometry Geometry(ShapeKind kind, Rect r, double radius, bool continuous, Func<Rect, Geometry>? path = null)
    {
        switch (kind)
        {
            case ShapeKind.Rectangle: return new RectangleGeometry(r);
            case ShapeKind.Circle:
                var d = Math.Min(r.Width, r.Height);
                return new EllipseGeometry(new Rect(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d));
            case ShapeKind.Ellipse: return new EllipseGeometry(r);
            case ShapeKind.Capsule:
                return continuous ? Continuous(r, Math.Min(r.Width, r.Height) / 2) : new RectangleGeometry(r, Math.Min(r.Width, r.Height) / 2, Math.Min(r.Width, r.Height) / 2);
            case ShapeKind.Custom: return path!(r);
            default:
                return continuous ? Continuous(r, radius) : new RectangleGeometry(r, radius, radius);
        }
    }

    /// Apple's continuous ("squircle") corners, the curve UIKit/SwiftUI use for `.continuous` rounded rectangles.
    public static Geometry Continuous(Rect r, double radius)
    {
        var limit = Math.Min(r.Width, r.Height) / 2;
        if (radius <= 0) return new RectangleGeometry(r);
        // The continuous curve spans 1.528 r along each edge; when that does not fit fall back to circular arcs.
        if (radius * 1.52866483 > limit) return new RectangleGeometry(r, Math.Min(radius, limit), Math.Min(radius, limit));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double x0 = r.Left, y0 = r.Top, x1 = r.Right, y1 = r.Bottom, k = radius;
            Point P(double x, double y) => new(x, y);
            c.BeginFigure(P(x0 + 1.52866483 * k, y0), true);
            // top edge → top-right
            c.LineTo(P(x1 - 1.52866483 * k, y0));
            c.CubicBezierTo(P(x1 - 1.08849323 * k, y0), P(x1 - 0.86840689 * k, y0), P(x1 - 0.63149399 * k, y0 + 0.07491100 * k));
            c.CubicBezierTo(P(x1 - 0.37282300 * k, y0 + 0.16906619 * k), P(x1 - 0.16906619 * k, y0 + 0.37282300 * k), P(x1 - 0.07491100 * k, y0 + 0.63149399 * k));
            c.CubicBezierTo(P(x1, y0 + 0.86840689 * k), P(x1, y0 + 1.08849323 * k), P(x1, y0 + 1.52866483 * k));
            // right edge → bottom-right
            c.LineTo(P(x1, y1 - 1.52866483 * k));
            c.CubicBezierTo(P(x1, y1 - 1.08849323 * k), P(x1, y1 - 0.86840689 * k), P(x1 - 0.07491100 * k, y1 - 0.63149399 * k));
            c.CubicBezierTo(P(x1 - 0.16906619 * k, y1 - 0.37282300 * k), P(x1 - 0.37282300 * k, y1 - 0.16906619 * k), P(x1 - 0.63149399 * k, y1 - 0.07491100 * k));
            c.CubicBezierTo(P(x1 - 0.86840689 * k, y1), P(x1 - 1.08849323 * k, y1), P(x1 - 1.52866483 * k, y1));
            // bottom edge → bottom-left
            c.LineTo(P(x0 + 1.52866483 * k, y1));
            c.CubicBezierTo(P(x0 + 1.08849323 * k, y1), P(x0 + 0.86840689 * k, y1), P(x0 + 0.63149399 * k, y1 - 0.07491100 * k));
            c.CubicBezierTo(P(x0 + 0.37282300 * k, y1 - 0.16906619 * k), P(x0 + 0.16906619 * k, y1 - 0.37282300 * k), P(x0 + 0.07491100 * k, y1 - 0.63149399 * k));
            c.CubicBezierTo(P(x0, y1 - 0.86840689 * k), P(x0, y1 - 1.08849323 * k), P(x0, y1 - 1.52866483 * k));
            // left edge → top-left
            c.LineTo(P(x0, y0 + 1.52866483 * k));
            c.CubicBezierTo(P(x0, y0 + 1.08849323 * k), P(x0, y0 + 0.86840689 * k), P(x0 + 0.07491100 * k, y0 + 0.63149399 * k));
            c.CubicBezierTo(P(x0 + 0.16906619 * k, y0 + 0.37282300 * k), P(x0 + 0.37282300 * k, y0 + 0.16906619 * k), P(x0 + 0.63149399 * k, y0 + 0.07491100 * k));
            c.CubicBezierTo(P(x0 + 0.86840689 * k, y0), P(x0 + 1.08849323 * k, y0), P(x0 + 1.52866483 * k, y0));
            c.EndFigure(true);
        }
        return g;
    }
}

/// `.clipShape(...)` / `.clipped()` / `.cornerRadius(...)`.
public sealed class ClipBox : Decorator, IFlexible
{
    public ShapeKind Kind { get; init; } = ShapeKind.Rectangle;
    public double CornerRadius { get; init; }
    public bool Continuous { get; init; }
    public Func<Rect, Geometry>? Path { get; init; }

    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = base.ArrangeOverride(finalSize);
        Clip = Shapes.Geometry(Kind, new Rect(s), CornerRadius, Continuous, Path);
        return s;
    }
}

/// SwiftUI `Divider`: a hairline across the parent stack's cross axis.
public sealed class DividerView : Control, IFlexible
{
    public SColor Color { get; init; } = SColor.Separator;

    private bool Vertical => Parent is Stack { Axis: Orientation.Horizontal };

    public bool IsFlexible(Orientation axis) => Vertical ? axis == Orientation.Vertical : axis == Orientation.Horizontal;

    protected override Size MeasureOverride(Size available) => Vertical
        ? new Size(1, double.IsInfinity(available.Height) ? 0 : available.Height)
        : new Size(double.IsInfinity(available.Width) ? 0 : available.Width, 1);

    public override void Render(DrawingContext ctx) =>
        ctx.FillRectangle(new ImmutableSolidColorBrush(Color.Resolve(GetValue(Env.SchemeProperty))), new Rect(Bounds.Size));
}

/// A SwiftUI view body: rebuilt whenever an observed value it read while building changes.
public class Reactive : Decorator, IObserver, IFlexible
{
    private readonly Func<Control?> _build;
    private HashSet<(Observable, string)> _deps = new();
    private bool _dirty = true;
    private bool _queued;
    private bool _attached;

    public Reactive(Func<Control?> build) { _build = build; }

    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (_dirty) Rebuild();
        else foreach (var (o, n) in _deps) o.Subscribe(n, this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        foreach (var (o, n) in _deps) o.Unsubscribe(n, this);
    }

    public void Invalidate()
    {
        _dirty = true;
        if (!_attached || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (_attached && _dirty) Rebuild();
        }, DispatcherPriority.Render);
    }

    // SwiftUI keeps @State and platform views (text fields) alive across body re-evaluations; a rebuild
    // here makes fresh controls, so those are remembered by key from one build to the next.
    [ThreadStatic] private static Reactive? _building;
    private Dictionary<object, object> _memo = new();
    private Dictionary<object, object>? _nextMemo;
    private int _autoKey;

    internal static T Memo<T>(object? key, Func<T> make) where T : class
    {
        var r = _building;
        if (r == null) return make();
        key ??= "auto#" + r._autoKey++;
        if (r._nextMemo!.TryGetValue(key, out var dup) && dup is T d) return d;
        var v = r._memo.TryGetValue(key, out var old) && old is T t ? t : make();
        r._nextMemo[key] = v;
        return v;
    }

    public void Rebuild()
    {
        _dirty = false;
        Control? child = null;
        var top = TopLevel.GetTopLevel(this);
        var focused = top?.FocusManager?.GetFocusedElement() as Control;
        if (focused != null && !this.IsVisualAncestorOf(focused)) focused = null;
        var saved = _building;
        _building = this;
        _nextMemo = new();
        _autoKey = 0;
        HashSet<(Observable, string)> deps;
        try { deps = Tracker.Capture(() => child = _build()); }
        finally { _building = saved; }
        _memo = _nextMemo;
        _nextMemo = null;
        foreach (var (o, n) in _deps) if (!deps.Contains((o, n))) o.Unsubscribe(n, this);
        foreach (var (o, n) in deps) o.Subscribe(n, this);
        _deps = deps;
        Child = child;
        if (focused != null && focused.IsAttachedToVisualTree() && focused.Focusable) focused.Focus();
    }
}

/// Hosts a control kept across rebuilds (`UI.Keep`): the control moves into each new host.
public sealed class KeepHost : Decorator, IFlexible
{
    public KeepHost(Control c)
    {
        if (c.Parent is KeepHost old) old.Child = null;
        Child = c;
    }

    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);
}

/// `ForEach` inside a (Lazy)VStack/HStack: rows are kept by key and only rebuilt when their item changes.
public sealed class ForEachStack<T> : Decorator, IObserver, IFlexible where T : notnull
{
    private readonly Func<IReadOnlyList<T>> _items;
    private readonly Func<T, object> _key;
    private readonly Func<T, Control> _build;
    private readonly Stack _stack;
    private readonly Dictionary<object, (T item, Control view)> _rows = new();
    private HashSet<(Observable, string)> _deps = new();
    private bool _queued, _attached, _dirty = true;

    public ForEachStack(Func<IReadOnlyList<T>> items, Func<T, object> key, Func<T, Control> build,
                        Orientation axis = Orientation.Vertical, double? spacing = 0, HAlign h = HAlign.Center, VAlign v = VAlign.Center)
    {
        _items = items;
        _key = key;
        _build = build;
        _stack = new Stack { Axis = axis, Spacing = spacing, HAlignment = h, VAlignment = v };
        Child = _stack;
    }

    public bool IsFlexible(Orientation axis) => Flex.Of(_stack, axis);

    public Control? ViewFor(object key) => _rows.TryGetValue(key, out var r) ? r.view : null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (_dirty) Sync();
        else foreach (var (o, n) in _deps) o.Subscribe(n, this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        foreach (var (o, n) in _deps) o.Unsubscribe(n, this);
    }

    public void Invalidate()
    {
        _dirty = true;
        if (!_attached || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; if (_attached && _dirty) Sync(); }, DispatcherPriority.Render);
    }

    public void Sync()
    {
        _dirty = false;
        IReadOnlyList<T> items = [];
        var deps = Tracker.Capture(() => items = _items());
        foreach (var (o, n) in _deps) if (!deps.Contains((o, n))) o.Unsubscribe(n, this);
        foreach (var (o, n) in deps) o.Subscribe(n, this);
        _deps = deps;

        var wanted = new List<Control>(items.Count);
        var seen = new HashSet<object>();
        foreach (var item in items)
        {
            var k = _key(item);
            if (!seen.Add(k)) continue;
            if (_rows.TryGetValue(k, out var row) && Equals(row.item, item)) { wanted.Add(row.view); continue; }
            var it = item;
            Control view = new Reactive(() => _build(it));
            _rows[k] = (item, view);
            wanted.Add(view);
        }
        foreach (var k in _rows.Keys.Where(k => !seen.Contains(k)).ToList()) _rows.Remove(k);

        var children = _stack.Children;
        var wantedSet = new HashSet<Control>(wanted);
        for (var i = children.Count - 1; i >= 0; i--) if (!wantedSet.Contains(children[i])) children.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < children.Count && ReferenceEquals(children[i], wanted[i])) continue;
            var at = children.IndexOf(wanted[i]);
            if (at >= 0) children.Move(at, i);
            else children.Insert(i, wanted[i]);
        }
    }
}

public static class EmojiText
{
    /// CoreText draws Apple Color Emoji about 1.3× larger than Skia (or Segoe UI Emoji) at text sizes,
    /// converging at large sizes (a 44 pt jumbo emoji matches).
    public static double Scale(double size) => size <= 17 ? 1.3 : size >= 40 ? 1.0 : 1.3 - (size - 17) / 23 * 0.3;

    public static bool Has(string s)
    {
        foreach (var r in s.EnumerateRunes()) if (IsEmojiRune(r.Value)) return true;
        return false;
    }

    /// Characters drawn as colour emoji by default (Unicode Emoji_Presentation), or any character followed by VS16 (U+FE0F).
    /// Symbols such as ⌘ © ™ ★ stay text.
    private static bool IsEmojiRune(int c) =>
        c >= 0x1F000 ? c is < 0x1F100 or >= 0x1F1E6 : c == 0xFE0F || BmpPresentation.Contains(c);

    private static readonly HashSet<int> BmpPresentation = new(new[]
    {
        0x231A, 0x231B, 0x23E9, 0x23EA, 0x23EB, 0x23EC, 0x23F0, 0x23F3, 0x25FD, 0x25FE, 0x2614, 0x2615,
        0x2648, 0x2649, 0x264A, 0x264B, 0x264C, 0x264D, 0x264E, 0x264F, 0x2650, 0x2651, 0x2652, 0x2653,
        0x267F, 0x2693, 0x26A1, 0x26AA, 0x26AB, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26CE, 0x26D4, 0x26EA, 0x26F2,
        0x26F3, 0x26F5, 0x26FA, 0x26FD, 0x2705, 0x270A, 0x270B, 0x2728, 0x274C, 0x274E, 0x2753, 0x2754, 0x2755,
        0x2757, 0x2795, 0x2796, 0x2797, 0x27B0, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B55,
    });

    /// Splits text into runs of plain text and runs of emoji grapheme clusters.
    public static IEnumerable<(string text, bool emoji)> Segments(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool? current = null;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            var isEmoji = false;
            foreach (var r in el.EnumerateRunes()) if (IsEmojiRune(r.Value)) { isEmoji = true; break; }
            if (current != null && current != isEmoji)
            {
                yield return (sb.ToString(), current.Value);
                sb.Clear();
            }
            current = isEmoji;
            sb.Append(el);
        }
        if (current != null && sb.Length > 0) yield return (sb.ToString(), current.Value);
    }
}

/// Text built from styled runs (links, bold spans, mentions).
public sealed class RichText : SText
{
    public RichText(IEnumerable<Inline> runs)
    {
        Inlines = new InlineCollection();
        foreach (var r in runs) Inlines.Add(r);
    }
}

public enum ContentMode { Fit, Fill, Stretch }

/// `Image(nsImage:).resizable()`: a bitmap that takes the space offered and draws scaled to fit or fill it
/// (fill is clipped to the bounds, like `.scaledToFill()` + `.clipped()`).
public sealed class BitmapView : Control, IFlexible
{
    public Bitmap? Source { get; set; }
    public ContentMode Mode { get; init; } = ContentMode.Fill;
    /// Not resizable: its natural size in points (pixels / scale).
    public bool Resizable { get; init; } = true;
    public double PixelScale { get; init; } = 2;
    /// `.interpolation(.none)` when false (QR codes).
    public bool Smooth { get; init; } = true;

    public BitmapView(Bitmap? source) { Source = source; ClipToBounds = true; }

    public bool IsFlexible(Orientation axis) => Resizable;

    protected override Size MeasureOverride(Size available)
    {
        if (Source == null) return new Size(0, 0);
        var natural = new Size(Source.PixelSize.Width / PixelScale, Source.PixelSize.Height / PixelScale);
        if (!Resizable) return natural;
        double w = available.Width, h = available.Height;
        if (double.IsInfinity(w) && double.IsInfinity(h)) return natural;
        var aspect = natural.Width / Math.Max(1, natural.Height);
        if (double.IsInfinity(w)) return new Size(Mode == ContentMode.Fit ? h * aspect : h * aspect, h);
        if (double.IsInfinity(h)) return new Size(w, w / aspect);
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        if (Source == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var src = new Rect(0, 0, Source.PixelSize.Width, Source.PixelSize.Height);
        var b = new Rect(Bounds.Size);
        Rect dst;
        if (Mode == ContentMode.Stretch) dst = b;
        else
        {
            var s = Mode == ContentMode.Fit ? Math.Min(b.Width / src.Width, b.Height / src.Height) : Math.Max(b.Width / src.Width, b.Height / src.Height);
            var w = src.Width * s; var h = src.Height * s;
            dst = new Rect((b.Width - w) / 2, (b.Height - h) / 2, w, h);
        }
        RenderOptions.SetBitmapInterpolationMode(this, Smooth ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None);
        using (ctx.PushClip(b)) ctx.DrawImage(Source, src, dst);
    }
}
