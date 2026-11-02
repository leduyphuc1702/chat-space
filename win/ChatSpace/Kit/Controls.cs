using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using static ChatSpace.Kit.UI;

namespace ChatSpace.Kit;

public enum ControlSize { Mini, Small, Regular, Large }

/// `TimelineView(.periodic)`.
public sealed class TimelineReactive : Reactive
{
    private readonly DispatcherTimer _timer;

    public TimelineReactive(TimeSpan period, Func<DateTime, Control?> body) : base(() => body(DateTime.Now))
    {
        _timer = new DispatcherTimer { Interval = period };
        _timer.Tick += (_, _) => Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }
}

/// A stable hover target whose content is rebuilt with the hover state (`@State var hovered` + `.onHover`).
public sealed class HoverBox : Decorator, IFlexible
{
    private readonly State<bool> _hovered = new(false);

    public HoverBox(Func<bool, Control?> body)
    {
        Child = new Reactive(() => body(_hovered.Value));
        Background = Brushes.Transparent;
        PointerEntered += (_, _) => _hovered.Value = true;
        PointerExited += (_, _) => _hovered.Value = false;
        DetachedFromVisualTree += (_, _) => _hovered.Value = false;
    }

    public IBrush? Background { get; set; }

    public override void Render(DrawingContext context)
    {
        if (Background != null) context.FillRectangle(Background, new Rect(Bounds.Size));
    }

    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);
}

/// macOS indeterminate spinner (`ProgressView()`): twelve fading spokes, or a determinate bar.
public sealed class ProgressView : Control
{
    private readonly DispatcherTimer _timer;
    private int _phase;
    public ControlSize Size { get; init; } = ControlSize.Regular;
    /// `ProgressView(value:)` with `.progressViewStyle(.linear)`; null = spinner.
    public double? Value { get; init; }

    public ProgressView()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 12) };
        _timer.Tick += (_, _) => { _phase = (_phase + 1) % 12; InvalidateVisual(); };
    }

    private double Diameter => Size switch { ControlSize.Mini => 10, ControlSize.Small => 16, ControlSize.Large => 32, _ => 20 };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); if (Value == null) _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

    protected override Size MeasureOverride(Size available) =>
        Value != null ? new Size(double.IsInfinity(available.Width) ? 160 : available.Width, 6) : new Size(Diameter, Diameter);

    public override void Render(DrawingContext ctx)
    {
        if (Value is { } v)
        {
            var r = new Rect(0, 0, Bounds.Width, 6);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)), null, r, 3, 3);
            var tint = GetValue(Env.TintProperty) ?? SColor.Accent;
            ctx.DrawRectangle(new ImmutableSolidColorBrush(tint.Resolve(Scheme.Light)), null, r.WithWidth(Bounds.Width * Math.Clamp(v, 0, 1)), 3, 3);
            return;
        }
        var d = Diameter;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var dark = GetValue(Env.SchemeProperty) == Scheme.Dark;
        var w = Math.Max(1.2, d * 0.09);
        for (var i = 0; i < 12; i++)
        {
            var age = (_phase - i + 12) % 12;
            var alpha = Math.Max(0.18, 1 - age / 12.0 * 0.9);
            var a = i * Math.PI / 6 - Math.PI / 2;
            var p0 = c + new Vector(Math.Cos(a), Math.Sin(a)) * d * 0.25;
            var p1 = c + new Vector(Math.Cos(a), Math.Sin(a)) * d * 0.47;
            var col = dark ? Color.FromArgb((byte)(alpha * 230), 255, 255, 255) : Color.FromArgb((byte)(alpha * 150), 0, 0, 0);
            ctx.DrawLine(new Pen(new ImmutableSolidColorBrush(col), w, lineCap: PenLineCap.Round), p0, p1);
        }
    }
}

/// Image from any source string; shows `placeholder` until loaded or on failure.
public sealed class RemoteImage : Decorator, IFlexible
{
    private readonly string? _source;
    private readonly ContentMode _mode;
    private readonly double _maxPixel;
    private readonly Func<Control> _placeholder;
    private bool _loaded;

    public RemoteImage(string? source, Func<Control> placeholder, ContentMode mode = ContentMode.Fill, double maxPixel = Core.ImageLoader.DefaultMaxPixel)
    {
        _source = source;
        _mode = mode;
        _maxPixel = maxPixel;
        _placeholder = placeholder;
        if (!string.IsNullOrEmpty(source) && Core.ImageLoader.Shared.Cached(source, maxPixel) is { } hit) Show(hit);
        else Child = placeholder();
    }

    public bool IsFlexible(Orientation axis) => _loaded || Flex.Of(Child, axis);

    private void Show(Avalonia.Media.Imaging.Bitmap bmp)
    {
        _loaded = true;
        Child = new BitmapView(bmp) { Mode = _mode };
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_loaded || string.IsNullOrEmpty(_source)) return;
        var img = await Core.ImageLoader.Shared.Load(_source, _maxPixel);
        if (img != null && this.IsAttachedToVisualTree()) Show(img);
    }
}

/// Shared text styling for SwiftUI-like text controls.
internal static class TextStyle
{
    public static void Apply(TextBlock t, SFont? own, bool truncate)
    {
        var f = own ?? t.GetValue(Env.FontProperty);
        t.FontFamily = f.AvaloniaFamily;
        t.FontSize = f.Size;
        t.FontWeight = f.AvaloniaWeight;
        t.FontStyle = f.Italic ? FontStyle.Italic : FontStyle.Normal;
        t.LetterSpacing = f.LetterSpacing;
        t.Foreground = Env.ResolveForeground(t, t.Bounds);
        var limit = t.GetValue(Env.LineLimitProperty);
        t.MaxLines = limit ?? 0;
        t.TextWrapping = limit == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        t.TextTrimming = truncate ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        t.TextAlignment = t.GetValue(Env.MultilineAlignmentProperty);
    }

    public static bool Affects(AvaloniaProperty p) =>
        p == Env.FontProperty || p == Env.ForegroundProperty || p == Env.LevelProperty || p == Env.SchemeProperty ||
        p == Env.ForegroundPaintProperty || p == Env.LineLimitProperty || p == Env.EnabledProperty || p == Env.MultilineAlignmentProperty;
}

/// A styled span of message text.
public sealed record Span(string Text, SColor? Color = null, Weight? Weight = null, bool Underline = false, bool Italic = false,
    string? Link = null, bool Strike = false, double? Size = null, bool Mono = false, SFont? Font = null);

/// Message text: selectable (`.textSelection(.enabled)`) with clickable links, like `Text(AttributedString)`.
public sealed class LinkText : SelectableTextBlock
{
    private readonly List<(int start, int end, string url)> _links = new();
    private readonly List<(Run run, double? size)> _emoji = new();
    public SFont? OwnFont { get; set; }
    public bool Selectable { get; init; } = true;

    public LinkText(IEnumerable<Span> spans)
    {
        Inlines = new InlineCollection();
        var at = 0;
        foreach (var whole in spans)
        foreach (var (segment, isEmoji) in EmojiText.Has(whole.Text) ? EmojiText.Segments(whole.Text) : [(whole.Text, false)])
        {
            var s = whole with { Text = segment };
            var run = new Run(s.Text);
            if (isEmoji) _emoji.Add((run, s.Font?.Size ?? s.Size));
            if (s.Color is { } c) run.Foreground = new ImmutableSolidColorBrush(c.Resolve(Scheme.Light));
            // Skia multiplies colour glyphs by the text colour's alpha; CoreText draws emoji opaque.
            if (isEmoji) run.Foreground = Brushes.Black;
            if (s.Weight is { } w) run.FontWeight = new SFont(13, w).AvaloniaWeight;
            if (s.Italic) run.FontStyle = FontStyle.Italic;
            if (s.Size is { } sz) run.FontSize = sz;
            if (s.Mono) run.FontFamily = SFont.System(13, Weight.Regular, Design.Monospaced).AvaloniaFamily;
            if (s.Font is { } sf)
            {
                run.FontFamily = sf.AvaloniaFamily;
                run.FontSize = sf.Size;
                run.FontWeight = sf.AvaloniaWeight;
            }
            var deco = new TextDecorationCollection();
            if (s.Underline) deco.AddRange(Avalonia.Media.TextDecorations.Underline);
            if (s.Strike) deco.AddRange(Avalonia.Media.TextDecorations.Strikethrough);
            if (deco.Count > 0) run.TextDecorations = deco;
            Inlines!.Add(run);
            if (s.Link != null) _links.Add((at, at + s.Text.Length, s.Link));
            at += s.Text.Length;
        }
        SelectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0x00, 0x7A, 0xFF));
        ContextFlyout = null;
    }

    private double? _setLineHeight;

    private void ScaleEmoji()
    {
        var tallest = 0.0;
        foreach (var (run, size) in _emoji)
        {
            var s = size ?? FontSize;
            run.FontSize = s * EmojiText.Scale(s);
            tallest = Math.Max(tallest, run.FontSize);
        }
        // AppKit lays the line out for the unscaled emoji font, so it grows a little; the enlarged glyph overhangs.
        // Avalonia would size it for the enlarged emoji font's ascent and descent, which is several points taller.
        _setLineHeight ??= LineHeight;
        if (tallest <= 0) return;
        var line = _setLineHeight.Value;
        if (double.IsNaN(line))
            line = FontManager.Current.TryGetGlyphTypeface(new Typeface(FontFamily, FontStyle, FontWeight), out var gt)
                ? (-gt.Metrics.Ascent + gt.Metrics.Descent + gt.Metrics.LineGap) / (double)gt.Metrics.DesignEmHeight * FontSize
                : FontSize * 1.2;
        LineHeight = Math.Max(line, Math.Ceiling(tallest * (double.IsNaN(_setLineHeight.Value) ? 1.04 : 1.17)));
    }

    /// `Text("… **bold** …")`: markdown bold runs, not selectable. `bold` overrides the font of bold runs.
    public static LinkText Markdown(string text, SFont? bold = null)
    {
        var parts = text.Split("**");
        var spans = parts.Select((p, i) => i % 2 == 1
            ? new Span(p, Weight: bold == null ? Kit.Weight.Bold : null, Font: bold)
            : new Span(p)).Where(s => s.Text.Length > 0);
        return new LinkText(spans) { Selectable = false };
    }

    /// Plain text with URLs turned into links (`linkified`).
    public static LinkText Linkified(string text, SColor? linkColor = null, bool underline = false) =>
        new(LinkSpans.Spans(text, linkColor, underline));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TextStyle.Apply(this, OwnFont, false);
        ScaleEmoji();
        if (!Selectable) IsHitTestVisible = _links.Count > 0;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (TextStyle.Affects(change.Property)) { TextStyle.Apply(this, OwnFont, false); ScaleEmoji(); }
    }

    private string? LinkAt(Point p)
    {
        if (_links.Count == 0) return null;
        var hit = TextLayout.HitTestPoint(p - new Point(Padding.Left, Padding.Top));
        if (!hit.IsInside) return null;
        var i = hit.TextPosition;
        foreach (var (s, e, url) in _links) if (i >= s && i < e) return url;
        return null;
    }

    private Point _down;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) != null ? new Cursor(StandardCursorType.Hand) : (Selectable ? new Cursor(StandardCursorType.Ibeam) : Cursor.Default);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _down = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!Selectable) { if (LinkAt(_down) != null) e.Handled = true; return; }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (Selectable) base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (e.InitialPressMouseButton != MouseButton.Left || Math.Abs(p.X - _down.X) > 3 || Math.Abs(p.Y - _down.Y) > 3) return;
        if (LinkAt(p) is { } url)
        {
            KitShell.OpenUrl(url);
            e.Handled = true;
        }
    }
}

public static class LinkSpans
{
    private static readonly System.Text.RegularExpressions.Regex Url = new(
        @"\b(?:https?://|www\.)[^\s<>""]+[^\s<>"".,;:!?)\]}'’”]|\b[a-z0-9.-]+\.(?:com|net|org|vn|io|me|co|app|dev|ai)(?:/[^\s<>""]*[^\s<>"".,;:!?)\]}'’”])?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static IReadOnlyList<Span> Spans(string text, SColor? linkColor = null, bool underline = false)
    {
        var spans = new List<Span>();
        var last = 0;
        foreach (System.Text.RegularExpressions.Match m in Url.Matches(text))
        {
            if (m.Index > last) spans.Add(new Span(text[last..m.Index]));
            var url = m.Value.Contains("://") ? m.Value : "http://" + m.Value;
            spans.Add(new Span(m.Value, linkColor ?? SColor.Hex(0x0068DA), Underline: underline, Link: url));
            last = m.Index + m.Length;
        }
        if (last < text.Length) spans.Add(new Span(text[last..]));
        return spans;
    }

    public static bool HasLink(string text) => Url.IsMatch(text);
}

public static class KitShell
{
    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static void Open(string path) => OpenUrl(path);

    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else System.Diagnostics.Process.Start("open", ["-R", path]);
        }
        catch { }
    }
}

public enum FieldStyle { Plain, RoundedBorder }

/// `TextField` / `SecureField`: an Avalonia TextBox kept across rebuilds, with its own placeholder,
/// two-way bound to `text`.
public sealed class Field : Panel, IFlexible
{
    public readonly TextBox Box;
    private readonly SText _placeholder;
    private Binding<string> _text;
    private bool _pushing;
    public Action? OnSubmit { get; set; }
    public Action? OnCancel { get; set; }
    public FieldStyle Style { get; }

    public Field(string placeholder, Binding<string> text, FieldStyle style = FieldStyle.Plain, bool secure = false, bool multiline = false,
        SFont? font = null, ControlSize size = ControlSize.Regular)
    {
        _text = text;
        Style = style;
        var f = font ?? SFont.System(size == ControlSize.Small ? 11 : size == ControlSize.Large ? 15 : 13);
        Box = new TextBox
        {
            Text = Tracker.Untracked(() => text.Value),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = f.AvaloniaFamily, FontSize = f.Size, FontWeight = f.AvaloniaWeight, LetterSpacing = f.LetterSpacing,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = new ImmutableSolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0)),
        };
        if (secure) Box.PasswordChar = '•';
        _placeholder = new SText(placeholder) { OwnFont = f, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _placeholder.SetValue(Env.LineLimitProperty, 1);
        _placeholder.SetValue(Env.ForegroundProperty, SColor.Black.Opacity(0.25));
        if (style == FieldStyle.RoundedBorder)
        {
            var pad = size == ControlSize.Small ? new Thickness(5, 2) : size == ControlSize.Large ? new Thickness(8, 5) : new Thickness(6, 3);
            Box.Padding = pad;
            _placeholder.Margin = new Thickness(pad.Left, pad.Top, pad.Right, pad.Bottom);
            MinHeight = size == ControlSize.Small ? 19 : size == ControlSize.Large ? 28 : 22;
        }
        _chrome = new Chrome(this);
        Children.Add(_chrome);
        Children.Add(Box);
        Children.Add(_placeholder);
        _placeholder.IsVisible = string.IsNullOrEmpty(Box.Text);
        Box.TextChanged += (_, _) =>
        {
            _placeholder.IsVisible = string.IsNullOrEmpty(Box.Text);
            if (_pushing) return;
            _pushing = true;
            try { _text.Value = Box.Text ?? ""; } finally { _pushing = false; }
        };
        Box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !multiline) { OnSubmit?.Invoke(); e.Handled = OnSubmit != null; }
            else if (e.Key == Key.Escape && OnCancel != null) { OnCancel(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        _watch = Watch.Effect(() =>
        {
            var v = _text.Value;
            if (_pushing || v == (Box.Text ?? "")) return;
            Box.Text = v;
            Box.CaretIndex = v.Length;
        });
    }

    private readonly Watch _watch;

    public void Rebind(Binding<string> text) => _text = text;

    public bool IsFlexible(Orientation axis) => axis == Orientation.Horizontal;

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = base.MeasureOverride(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? Math.Max(s.Width, 120) : availableSize.Width, Math.Max(s.Height, MinHeight));
    }

    private sealed class Chrome(Field f) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            if (f.Style != FieldStyle.RoundedBorder) return;
            var dark = f.GetValue(Env.SchemeProperty) == Scheme.Dark;
            var r = new Rect(Bounds.Size).Deflate(0.5);
            ctx.DrawRectangle(dark ? new ImmutableSolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)) : Brushes.White,
                new Pen(new ImmutableSolidColorBrush(dark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x40, 0, 0, 0)), 0.5), r, 5, 5);
            if (f.Box.IsFocused)
                ctx.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0x00, 0x7A, 0xFF)), 3), new Rect(Bounds.Size).Inflate(1), 6, 6);
        }
    }

    private Chrome? _chrome;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Box.GotFocus += Invalidate;
        Box.LostFocus += Invalidate;
    }

    private void Invalidate(object? s, RoutedEventArgs e) => _chrome?.InvalidateVisual();

    public void FocusField() => Dispatcher.UIThread.Post(() => Box.Focus());
}

/// `Toggle` as `.switch` or `.checkbox`.
public sealed class ToggleView : Control
{
    private readonly Binding<bool> _on;
    private readonly bool _switch;
    private readonly ControlSize _size;
    private readonly Watch _watch;

    public ToggleView(Binding<bool> on, bool switchStyle, ControlSize size = ControlSize.Regular)
    {
        _on = on;
        _switch = switchStyle;
        _size = size;
        Cursor = Cursor.Default;
        _watch = Watch.Effect(() => { _ = _on.Value; InvalidateVisual(); });
    }

    private Size Natural => _switch
        ? _size switch { ControlSize.Mini => new Size(26, 15), ControlSize.Small => new Size(32, 18), _ => new Size(38, 22) }
        : new Size(14, 14);

    protected override Size MeasureOverride(Size available) => Natural;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsEffectivelyEnabled || e.InitialPressMouseButton != MouseButton.Left) return;
        _on.Value = !Tracker.Untracked(() => _on.Value);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); e.Handled = true; }

    public override void Render(DrawingContext ctx)
    {
        var on = Tracker.Untracked(() => _on.Value);
        var accent = SColor.Accent.Resolve(Scheme.Light);
        var dim = IsEffectivelyEnabled ? 1.0 : 0.45;
        var n = Natural;
        var r = new Rect((Bounds.Width - n.Width) / 2, (Bounds.Height - n.Height) / 2, n.Width, n.Height);
        using var _ = ctx.PushOpacity(dim);
        if (_switch)
        {
            var track = on ? accent : Color.FromArgb(0x24, 0, 0, 0);
            ctx.DrawRectangle(new ImmutableSolidColorBrush(track), on ? null : new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)), 0.5), r, r.Height / 2, r.Height / 2);
            var k = r.Height - 2;
            var kx = on ? r.Right - 1 - k : r.Left + 1;
            var knob = new Rect(kx, r.Top + 1, k, k);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, knob.Translate(new Vector(0, 0.5)).Inflate(0.5));
            ctx.DrawEllipse(Brushes.White, null, knob);
            return;
        }
        if (on)
        {
            ctx.DrawRectangle(new ImmutableSolidColorBrush(accent), null, r, 3.5, 3.5);
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(r.X + 3.3, r.Y + 7.2), false);
                s.LineTo(new Point(r.X + 5.9, r.Y + 9.9));
                s.LineTo(new Point(r.X + 10.8, r.Y + 4.3));
                s.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(Brushes.White, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
        }
        else
        {
            ctx.DrawRectangle(Brushes.White, new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 0.5), r.Deflate(0.25), 3.5, 3.5);
        }
    }
}

public enum ButtonKind { Push, Prominent, Link, Borderless, Destructive }

/// Native-looking macOS buttons: push (`.automatic`), `.borderedProminent`, `.link`, `.borderless`.
public sealed class MacButton : Decorator
{
    private readonly Action _action;
    private readonly ButtonKind _kind;
    private readonly ControlSize _size;
    private bool _pressed, _hover;

    public MacButton(Control label, Action action, ButtonKind kind = ButtonKind.Push, ControlSize size = ControlSize.Regular)
    {
        _action = action;
        _kind = kind;
        _size = size;
        var fs = size switch { ControlSize.Mini => 9, ControlSize.Small => 11, ControlSize.Large => 13, _ => 13 };
        label.SetValue(Env.FontProperty, SFont.System(fs));
        if (kind == ButtonKind.Prominent) label.Foreground(SColor.White);
        else if (kind == ButtonKind.Link) label.Foreground(SColor.Accent);
        else if (kind == ButtonKind.Destructive) label.Foreground(SColor.Red);
        var pad = kind is ButtonKind.Link or ButtonKind.Borderless ? new Thickness(0) :
            size switch { ControlSize.Mini => new Thickness(6, 1), ControlSize.Small => new Thickness(8, 1), ControlSize.Large => new Thickness(14, 6), _ => new Thickness(10, 2) };
        Padding = pad;
        Child = label;
        MinHeight = kind is ButtonKind.Link or ButtonKind.Borderless ? 0 : size switch { ControlSize.Mini => 15, ControlSize.Small => 19, ControlSize.Large => 28, _ => 22 };
        MinWidth = kind is ButtonKind.Link or ButtonKind.Borderless ? 0 : MinHeight;
        PointerEntered += (_, _) => { _hover = true; InvalidateVisual(); };
        PointerExited += (_, _) => { _hover = false; InvalidateVisual(); };
        if (kind == ButtonKind.Link) Cursor = new Cursor(StandardCursorType.Hand);
    }

    public static MacButton Titled(string title, Action action, ButtonKind kind = ButtonKind.Push, ControlSize size = ControlSize.Regular) =>
        new(new SText(title) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.LineLimit(1), action, kind, size);

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = base.MeasureOverride(availableSize);
        return new Size(Math.Max(s.Width, MinWidth), Math.Max(s.Height, MinHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var c = Child;
        if (c == null) return finalSize;
        var w = c.DesiredSize.Width;
        var h = c.DesiredSize.Height;
        c.Arrange(new Rect((finalSize.Width - w) / 2, (finalSize.Height - h) / 2, w, h));
        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsEffectivelyEnabled) return;
        _pressed = true;
        InvalidateVisual();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        InvalidateVisual();
        e.Pointer.Capture(null);
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) _action();
        e.Handled = true;
    }

    public void Press() => _action();

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        var enabled = IsEffectivelyEnabled;
        switch (_kind)
        {
            case ButtonKind.Prominent:
                var c = SColor.Accent.Resolve(Scheme.Light);
                if (_pressed) c = Color.FromRgb((byte)(c.R * 0.85), (byte)(c.G * 0.85), (byte)(c.B * 0.85));
                if (!enabled) c = Color.FromArgb(0x70, c.R, c.G, c.B);
                var rad = _size == ControlSize.Large ? 6 : 5;
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x25, 0, 0, 0)), null, r.Translate(new Vector(0, 0.5)), rad, rad);
                ctx.DrawRectangle(new ImmutableSolidColorBrush(c), null, r, rad, rad);
                break;
            case ButtonKind.Push:
            case ButtonKind.Destructive:
                var rr = _size == ControlSize.Large ? 6 : 5;
                ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, r.Translate(new Vector(0, 0.5)), rr, rr);
                ctx.DrawRectangle(new ImmutableSolidColorBrush(_pressed ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Colors.White),
                    new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)), 0.5), r.Deflate(0.25), rr, rr);
                break;
            case ButtonKind.Borderless:
                if (_pressed) ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)), null, r.Inflate(new Thickness(4, 2)), 5, 5);
                break;
        }
        if (_kind == ButtonKind.Link && _pressed) Opacity = 0.6; else if (Opacity != 1 && _kind == ButtonKind.Link) Opacity = 1;
    }
}

/// A menu entry for context menus, `Menu` buttons and pop-up pickers.
public abstract record MenuEntry
{
    public static MenuEntry Item(string title, Action action, bool check = false, bool enabled = true, string? symbol = null) =>
        new MenuAction(title, action, check, enabled, symbol);
    public static readonly MenuEntry Separator = new MenuSeparator();
    public static MenuEntry Sub(string title, IEnumerable<MenuEntry> items, string? symbol = null) => new MenuSub(title, items.ToList(), symbol);
    public static MenuEntry Header(string title) => new MenuHeader(title);
}

public sealed record MenuAction(string Title, Action Action, bool Check, bool Enabled, string? Symbol) : MenuEntry;
public sealed record MenuSeparator : MenuEntry;
public sealed record MenuSub(string Title, IReadOnlyList<MenuEntry> Items, string? Symbol) : MenuEntry;
public sealed record MenuHeader(string Title) : MenuEntry;

public static class Menus
{
    public static IEnumerable<Control> Build(IEnumerable<MenuEntry> entries)
    {
        foreach (var e in entries)
        {
            switch (e)
            {
                case MenuSeparator: yield return new Separator(); break;
                case MenuHeader h:
                    yield return new MenuItem { Header = h.Title, IsEnabled = false, FontSize = 11, FontWeight = FontWeight.SemiBold };
                    break;
                case MenuAction a:
                    var mi = new MenuItem { Header = a.Title, IsEnabled = a.Enabled };
                    if (a.Check) mi.Icon = new SText("✓") { FontSize = 12 };
                    else if (a.Symbol != null && Symbol.Exists(a.Symbol)) mi.Icon = new Symbol(a.Symbol).Font(13);
                    var act = a.Action;
                    mi.Click += (_, _) => Dispatcher.UIThread.Post(act);
                    yield return mi;
                    break;
                case MenuSub s:
                    var sub = new MenuItem { Header = s.Title };
                    if (s.Symbol != null && Symbol.Exists(s.Symbol)) sub.Icon = new Symbol(s.Symbol).Font(13);
                    foreach (var c in Build(s.Items)) sub.Items.Add(c);
                    yield return sub;
                    break;
            }
        }
    }

    public static ContextMenu Context(Func<IEnumerable<MenuEntry>> items)
    {
        var m = new ContextMenu();
        m.Opening += (_, e) =>
        {
            m.Items.Clear();
            var list = items().ToList();
            if (list.Count == 0) { e.Cancel = true; return; }
            foreach (var c in Build(list)) m.Items.Add(c);
        };
        m.Items.Add(new MenuItem { Header = "" });
        return m;
    }

    /// Opens a menu below `anchor` (SwiftUI `Menu` / pop-up `Picker`).
    public static void Popup(Control anchor, IEnumerable<MenuEntry> items, PlacementMode placement = PlacementMode.BottomEdgeAlignedLeft)
    {
        var f = new MenuFlyout { Placement = placement };
        foreach (var c in Build(items)) f.Items.Add(c);
        f.ShowAt(anchor);
    }
}

/// macOS popover chrome: white rounded panel with an arrow pointing at the anchor.
public sealed class PopoverFlyout : PopupFlyoutBase
{
    private readonly Func<Action, Control> _content;
    public PopoverFlyout(Func<Action, Control> content, bool arrowUp = true)
    {
        _content = content;
        Placement = arrowUp ? PlacementMode.Bottom : PlacementMode.Top;
        _arrowUp = arrowUp;
    }

    private readonly bool _arrowUp;

    protected override Control CreatePresenter()
    {
        var body = _content(Hide);
        body.SetValue(Env.FontProperty, SFont.Body);
        var chrome = new PopoverChrome(body, _arrowUp);
        return chrome;
    }
}

public sealed class PopoverChrome : Decorator
{
    public const double Arrow = 9;
    private readonly bool _up;

    public PopoverChrome(Control content, bool arrowUp)
    {
        _up = arrowUp;
        Child = content;
        Padding = new Thickness(0, arrowUp ? Arrow : 0, 0, arrowUp ? 0 : Arrow);
        Margin = new Thickness(14, 0, 14, 14);
        Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.22, BlurRadius = 18, OffsetX = 0, OffsetY = 4 };
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var top = _up ? Arrow : 0;
        var bottom = _up ? h : h - Arrow;
        var body = new Rect(0, top, w, bottom - top);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            const double r = 10;
            var mid = w / 2;
            c.BeginFigure(new Point(r, top), true);
            if (_up) { c.LineTo(new Point(mid - Arrow, top)); c.LineTo(new Point(mid, 0)); c.LineTo(new Point(mid + Arrow, top)); }
            c.LineTo(new Point(w - r, top));
            c.ArcTo(new Point(w, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(w, bottom - r));
            c.ArcTo(new Point(w - r, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise);
            if (!_up) { c.LineTo(new Point(mid + Arrow, bottom)); c.LineTo(new Point(mid, h)); c.LineTo(new Point(mid - Arrow, bottom)); }
            c.LineTo(new Point(r, bottom));
            c.ArcTo(new Point(0, bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            c.LineTo(new Point(0, top + r));
            c.ArcTo(new Point(r, top), new Size(r, r), 0, false, SweepDirection.Clockwise);
            c.EndFigure(true);
        }
        ctx.DrawGeometry(new ImmutableSolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6)), new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)), 0.5), g);
        _ = body;
    }
}

public static class ControlMods
{
    /// `.contextMenu { ... }`, built when it opens.
    public static T ContextMenu<T>(this T c, Func<IEnumerable<MenuEntry>> items) where T : Control
    {
        c.ContextMenu = Menus.Context(items);
        return c;
    }

    /// `.popover(isPresented:)`: shows `content` anchored on this control while `isPresented` is true.
    public static Control Popover(this Control c, State<bool> isPresented, Func<Action, Control> content, bool arrowUp = true)
    {
        var host = new KeepHostless(c);
        PopoverFlyout? open = null;
        void Sync()
        {
            var want = isPresented.Value;
            if (want && open == null && host.IsAttachedToVisualTree())
            {
                open = new PopoverFlyout(content, arrowUp);
                open.Closed += (_, _) => { open = null; isPresented.Value = false; };
                open.ShowAt(host);
            }
            else if (!want && open != null) { var o = open; open = null; o.Hide(); }
        }
        Watch? w = null;
        host.AttachedToVisualTree += (_, _) => { w ??= Watch.Effect(Sync); Dispatcher.UIThread.Post(Sync); };
        host.DetachedFromVisualTree += (_, _) => { w?.Dispose(); w = null; open?.Hide(); open = null; };
        return host;
    }

    /// `.help(...)` + clickable icon with hover highlight (`.tool(_:size:action:)`).
    public static Control Tool(this Control icon, string help, Action action, double size = 32)
    {
        var bg = RoundedRectangle(6).Fill(SColor.Clear);
        var body = icon.Frame(minWidth: size, minHeight: size).Background(bg);
        body.PointerEntered += (_, _) => { bg.Fill = SColor.Primary.Opacity(0.08); bg.InvalidateVisual(); };
        body.PointerExited += (_, _) => { bg.Fill = SColor.Clear; bg.InvalidateVisual(); };
        return Button(action, body).Help(help);
    }

    public static Control ToolPopover(this Control icon, string help, State<bool> isPresented, Func<Action, Control> content, double size = 32) =>
        icon.Tool(help, () => isPresented.Value = !Tracker.Untracked(() => isPresented.Value), size).Popover(isPresented, content);

    /// Shown only while `condition` holds (re-evaluated when observed state changes), without rebuilding.
    public static T VisibleWhen<T>(this T c, Func<bool> condition) where T : Control
    {
        Watch? w = null;
        c.IsVisible = Tracker.Untracked(condition);
        c.AttachedToVisualTree += (_, _) => w ??= Watch.Effect(() => c.IsVisible = condition());
        c.DetachedFromVisualTree += (_, _) => { w?.Dispose(); w = null; };
        return c;
    }

    /// Background that changes while the pointer is over the control (`.onHover` + conditional fill).
    public static Control HoverBackground(this Control c, SColor normal, SColor hover, double radius = 0)
    {
        var bg = (radius > 0 ? RoundedRectangle(radius) : Rectangle()).Fill(normal);
        var body = c.Background(bg);
        body.PointerEntered += (_, _) => { bg.Fill = hover; bg.InvalidateVisual(); };
        body.PointerExited += (_, _) => { bg.Fill = normal; bg.InvalidateVisual(); };
        return body;
    }

    /// Tracks the pointer over the control in `state`.
    public static T TrackHover<T>(this T c, State<bool> state) where T : Control
    {
        c.PointerEntered += (_, _) => state.Value = true;
        c.PointerExited += (_, _) => state.Value = false;
        return c;
    }

    public static T ControlSize<T>(this T c, ControlSize s) where T : Control { c.SetValue(ControlSizeEnv.Property, s); return c; }
}

public static class ControlSizeEnv
{
    public static readonly AttachedProperty<ControlSize> Property =
        AvaloniaProperty.RegisterAttached<Control, ControlSize>("ControlSize", typeof(ControlSizeEnv), Kit.ControlSize.Regular, inherits: true);
}

/// A plain wrapper (used as a popover anchor) that stays flexible like its child.
public sealed class KeepHostless : Decorator, IFlexible
{
    public KeepHostless(Control c) { Child = c; }
    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);
}

/// SwiftUI `Picker` with the default (pop-up menu) or `.segmented` style.
public static class Pickers
{
    public static Control Menu<T>(Binding<T> selection, IReadOnlyList<(T value, string title)> options, ControlSize size = ControlSize.Regular,
        bool borderless = false)
    {
        return View(() =>
        {
            var cur = selection.Value;
            var title = options.FirstOrDefault(o => Equals(o.value, cur)).title ?? "";
            var fs = size == ControlSize.Small ? 11 : size == ControlSize.Mini ? 9 : 13;
            Control label;
            if (borderless)
            {
                label = HStack(3, Text(title).Font(fs).LineLimit(1), Image("chevron.down").Font(fs - 4, Weight.Semibold).Foreground(SColor.Secondary));
            }
            else
            {
                var h = size == ControlSize.Small ? 19.0 : size == ControlSize.Mini ? 15 : 22;
                label = HStack(6,
                        Text(title).Font(fs).LineLimit(1).PaddingLeading(8),
                        Spacer(0),
                        RoundedRectangle(4).Fill(SColor.Accent)
                            .Overlay(Image("chevron.up.chevron.down").Font(fs - 4, Weight.Bold).Foreground(SColor.White))
                            .Frame(h - 6, h - 6).PaddingTrailing(3))
                    .Frame(height: h)
                    .Background(ZStack(
                        RoundedRectangle(5).Fill(SColor.Black.Opacity(0.18)).Offset(0, 0.5),
                        RoundedRectangle(5).Fill(SColor.White).Overlay(RoundedRectangle(5).StrokeBorder(SColor.Black.Opacity(0.12), 0.5))));
            }
            Control? anchor = null;
            anchor = Button(() => Menus.Popup(anchor!, options.Select(o =>
            {
                var v = o.value;
                return MenuEntry.Item(o.title, () => selection.Value = v, Equals(v, Tracker.Untracked(() => selection.Value)));
            })), label);
            return anchor;
        });
    }

    public static Control Segmented<T>(Binding<T> selection, IReadOnlyList<(T value, string title)> options)
    {
        return View(() =>
        {
            var cur = selection.Value;
            var items = new List<Control?>();
            for (var i = 0; i < options.Count; i++)
            {
                var (v, t) = options[i];
                var on = Equals(v, cur);
                if (i > 0) items.Add(Rectangle().Fill(SColor.Black.Opacity(on || Equals(options[i - 1].value, cur) ? 0 : 0.12)).Frame(1, 12));
                items.Add(Button(() => selection.Value = v,
                    Text(t).Font(13, on ? Weight.Medium : Weight.Regular).LineLimit(1).PaddingH(10).Frame(height: 20).FillWidth()
                        .Background(on ? ZStack(RoundedRectangle(5).Fill(SColor.Black.Opacity(0.12)).Offset(0, 0.5), RoundedRectangle(5).Fill(SColor.White)) : Clear)));
            }
            return HStack(0, items.ToArray()).Padding(2).Background(RoundedRectangle(7).Fill(SColor.Black.Opacity(0.06)));
        });
    }
}

/// `Slider(value:in:)`.
public sealed class SliderView : Control
{
    private readonly Binding<double> _value;
    private readonly double _min, _max;
    private readonly double? _step;
    private readonly Watch _watch;
    private bool _drag;

    public SliderView(Binding<double> value, double min, double max, double? step = null)
    {
        _value = value; _min = min; _max = max; _step = step;
        _watch = Watch.Effect(() => { _ = _value.Value; InvalidateVisual(); });
    }

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 160 : available.Width, 20);

    private void SetFrom(Point p)
    {
        var t = Math.Clamp((p.X - 10) / Math.Max(1, Bounds.Width - 20), 0, 1);
        var v = _min + t * (_max - _min);
        if (_step is { } s && s > 0) v = Math.Round((v - _min) / s) * s + _min;
        _value.Value = Math.Clamp(v, _min, _max);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { _drag = true; e.Pointer.Capture(this); SetFrom(e.GetPosition(this)); e.Handled = true; }
    protected override void OnPointerMoved(PointerEventArgs e) { if (_drag) SetFrom(e.GetPosition(this)); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _drag = false; e.Pointer.Capture(null); }

    public override void Render(DrawingContext ctx)
    {
        var v = Tracker.Untracked(() => _value.Value);
        var t = (_max - _min) <= 0 ? 0 : (v - _min) / (_max - _min);
        var y = Bounds.Height / 2;
        var track = new Rect(10, y - 2, Bounds.Width - 20, 4);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0)), null, track, 2, 2);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(SColor.Accent.Resolve(Scheme.Light)), null, track.WithWidth(track.Width * t), 2, 2);
        var kx = 10 + track.Width * t;
        var knob = new Rect(kx - 9, y - 9, 18, 18);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(0x35, 0, 0, 0)), null, knob.Translate(new Vector(0, 0.5)).Inflate(0.5));
        ctx.DrawEllipse(Brushes.White, null, knob);
    }
}

/// `Stepper(value:in:)`: label + tiny up/down arrows.
public static class Steppers
{
    public static Control Make(string label, Binding<int> value, int min, int max, int step = 1) =>
        HStack(6,
            View(() => Text(label)),
            VStack(0,
                Button(() => value.Value = Math.Min(max, Tracker.Untracked(() => value.Value) + step),
                    Image("chevron.up").Font(7, Weight.Bold).Frame(13, 10)),
                Rectangle().Fill(SColor.Black.Opacity(0.1)).Frame(13, 0.5),
                Button(() => value.Value = Math.Max(min, Tracker.Untracked(() => value.Value) - step),
                    Image("chevron.down").Font(7, Weight.Bold).Frame(13, 10)))
                .Background(RoundedRectangle(4).Fill(SColor.White).Overlay(RoundedRectangle(4).StrokeBorder(SColor.Black.Opacity(0.15), 0.5))));
}
