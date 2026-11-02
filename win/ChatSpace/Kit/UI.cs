using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ChatSpace.Kit;

/// SwiftUI-shaped builders. `using static ChatSpace.Kit.UI;` lets ported views read like the Swift originals.
public static class UI
{
    private static Control[] Kids(Control?[] children) => children.Where(c => c != null).Cast<Control>().ToArray();

    public static Stack VStack(params Control?[] children) => VStack(HAlign.Center, null, children);
    public static Stack VStack(double? spacing, params Control?[] children) => VStack(HAlign.Center, spacing, children);
    public static Stack VStack(HAlign alignment, double? spacing, params Control?[] children)
    {
        var s = new Stack { Axis = Orientation.Vertical, Spacing = spacing, HAlignment = alignment };
        s.Children.AddRange(Kids(children));
        return s;
    }

    public static Stack HStack(params Control?[] children) => HStack(VAlign.Center, null, children);
    public static Stack HStack(double? spacing, params Control?[] children) => HStack(VAlign.Center, spacing, children);
    public static Stack HStack(VAlign alignment, double? spacing, params Control?[] children)
    {
        var s = new Stack { Axis = Orientation.Horizontal, Spacing = spacing, VAlignment = alignment };
        s.Children.AddRange(Kids(children));
        return s;
    }

    public static ZStackPanel ZStack(params Control?[] children) => ZStack(Alignment.Center, children);
    public static ZStackPanel ZStack(Alignment alignment, params Control?[] children)
    {
        var z = new ZStackPanel { Alignment = alignment };
        z.Children.AddRange(Kids(children));
        return z;
    }

    public static Control Group(params Control?[] children) => children.Length == 1 && children[0] != null ? children[0]! : ZStack(children);

    public static Spacer Spacer(double? minLength = null) => new() { MinLength = minLength };
    public static DividerView Divider() => new();

    public static SText Text(string text) => new(text);
    public static SText Text(string text, SFont font) => new(text) { OwnFont = font };

    /// `Image(systemName:)`.
    public static Symbol Image(string systemName) => new(systemName);
    public static Symbol ResizableSymbol(string systemName) => new(systemName) { Resizable = true };
    /// `Image(nsImage:).resizable()` with `.scaledToFill()` (default) or `.scaledToFit()`.
    public static BitmapView Bitmap(Avalonia.Media.Imaging.Bitmap? source, ContentMode mode = ContentMode.Fill) => new(source) { Mode = mode };

    public static ShapeView Rectangle() => new() { Kind = ShapeKind.Rectangle, FillWithForeground = true };
    public static ShapeView RoundedRectangle(double radius, bool continuous = false) =>
        new() { Kind = ShapeKind.RoundedRectangle, CornerRadius = radius, Continuous = continuous, FillWithForeground = true };
    public static ShapeView Circle() => new() { Kind = ShapeKind.Circle, FillWithForeground = true };
    public static ShapeView Capsule(bool continuous = true) => new() { Kind = ShapeKind.Capsule, Continuous = continuous, FillWithForeground = true };
    public static ShapeView Ellipse() => new() { Kind = ShapeKind.Ellipse, FillWithForeground = true };
    public static ShapeView PathShape(Func<Rect, Geometry> path) => new() { Kind = ShapeKind.Custom, Path = path, FillWithForeground = true };

    /// A SwiftUI `Color` used as a view: a flexible filled rectangle.
    public static ShapeView ColorView(Paint p) => new() { Kind = ShapeKind.Rectangle, Fill = p };
    public static ShapeView Clear => ColorView(SColor.Clear);

    /// A view body that rebuilds when observed state it reads changes.
    public static Reactive View(Func<Control?> body) => new(body);

    public static ForEachStack<T> ForEach<T>(Func<IReadOnlyList<T>> items, Func<T, object> key, Func<T, Control> row,
        Orientation axis = Orientation.Vertical, double? spacing = 0, HAlign alignment = HAlign.Center, VAlign valign = VAlign.Center) where T : notnull =>
        new(items, key, row, axis, spacing, alignment, valign);

    /// `ScrollView`: content gets the viewport's width (vertical) and its own height.
    public static ScrollViewer ScrollView(Control content, bool vertical = true, bool horizontal = false, bool indicators = true)
    {
        var sv = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = vertical ? (indicators ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden) : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = horizontal ? (indicators ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden) : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        content.VerticalAlignment = VerticalAlignment.Top;
        content.HorizontalAlignment = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        return sv;
    }

    /// `@State`: a box that survives rebuilds of the enclosing `View`/`Reactive` (keyed; unkeyed calls
    /// are matched by call order, so keep them out of conditionals or give them a key).
    public static State<T> Remember<T>(T initial, object? key = null) => Reactive.Memo(key, () => new State<T>(initial));
    public static T Memo<T>(object key, Func<T> make) where T : class => Reactive.Memo(key, make);

    /// A control (text field, editor) that must survive rebuilds so it keeps focus, caret and undo.
    public static Control Keep(object key, Func<Control> make) => new KeepHost(Reactive.Memo(key, make));

    /// `TimelineView(.periodic(by:))`: rebuilt every `period` while on screen.
    public static Control Timeline(TimeSpan period, Func<DateTime, Control?> body) => new TimelineReactive(period, body);

    /// `Button(action:label:)` with `.buttonStyle(.plain)`.
    public static Pressable Button(Action action, Control label) => new(label, action);
    public static Pressable Button(string title, Action action) => new(Text(title), action);
}

public static class Mod
{
    // MARK: Layout

    public static Control Padding(this Control c, double all = 16) => new PaddingBox { Child = c, Insets = new Thickness(all) };
    public static Control Padding(this Control c, double h, double v) => new PaddingBox { Child = c, Insets = new Thickness(h, v) };
    public static Control Padding(this Control c, double top, double leading, double bottom, double trailing) =>
        new PaddingBox { Child = c, Insets = new Thickness(leading, top, trailing, bottom) };
    public static Control PaddingH(this Control c, double v = 16) => new PaddingBox { Child = c, Insets = new Thickness(v, 0) };
    public static Control PaddingV(this Control c, double v = 16) => new PaddingBox { Child = c, Insets = new Thickness(0, v) };
    public static Control PaddingTop(this Control c, double v = 16) => new PaddingBox { Child = c, Insets = new Thickness(0, v, 0, 0) };
    public static Control PaddingBottom(this Control c, double v = 16) => new PaddingBox { Child = c, Insets = new Thickness(0, 0, 0, v) };
    public static Control PaddingLeading(this Control c, double v = 16) => new PaddingBox { Child = c, Insets = new Thickness(v, 0, 0, 0) };
    public static Control PaddingTrailing(this Control c, double v = 16) => new PaddingBox { Child = c, Insets = new Thickness(0, 0, v, 0) };

    /// `.frame(width:height:alignment:)` and `.frame(minWidth:idealWidth:maxWidth:minHeight:idealHeight:maxHeight:alignment:)`.
    public static Control Frame(this Control c, double? width = null, double? height = null, Alignment? alignment = null,
        double? minWidth = null, double? idealWidth = null, double? maxWidth = null,
        double? minHeight = null, double? idealHeight = null, double? maxHeight = null) =>
        new FrameBox
        {
            Child = c, FixedW = width, FixedH = height, MinW = minWidth, IdealW = idealWidth, MaxW = maxWidth,
            MinH = minHeight, IdealH = idealHeight, MaxH = maxHeight, Alignment = alignment ?? Alignment.Center,
        };

    public const double Inf = double.PositiveInfinity;

    /// `.frame(maxWidth: .infinity, alignment:)`.
    public static Control FillWidth(this Control c, Alignment? alignment = null) => c.Frame(maxWidth: Inf, alignment: alignment);
    public static Control FillHeight(this Control c, Alignment? alignment = null) => c.Frame(maxHeight: Inf, alignment: alignment);
    public static Control Fill(this Control c, Alignment? alignment = null) => c.Frame(maxWidth: Inf, maxHeight: Inf, alignment: alignment);

    public static Control FixedSize(this Control c, bool horizontal = true, bool vertical = true) =>
        new FixedSizeBox { Child = c, Horizontal = horizontal, Vertical = vertical };

    public static Control AspectRatio(this Control c, double? ratio, bool fill = false) => new AspectBox { Child = c, Ratio = ratio, Fill = fill };

    public static Control Background(this Control c, Control background, Alignment? alignment = null) =>
        new DecorationBox(c, background, true, alignment ?? Alignment.Center);
    public static Control Background(this Control c, Paint paint) => c.Background(UI.ColorView(paint));
    public static Control Background(this Control c, SColor color) => c.Background(UI.ColorView(color));

    public static Control Overlay(this Control c, Control overlay, Alignment? alignment = null) =>
        new DecorationBox(c, overlay, false, alignment ?? Alignment.Center);
    public static Control Overlay(this Control c, Alignment alignment, Control? overlay) =>
        overlay == null ? c : new DecorationBox(c, overlay, false, alignment);

    public static Control Offset(this Control c, double x = 0, double y = 0)
    {
        if (x == 0 && y == 0) return c;
        var t = new TranslateTransform(x, y);
        c.RenderTransform = Compose(c, t);
        return c;
    }

    public static Control ScaleEffect(this Control c, double s, RelativePoint? anchor = null)
    {
        c.RenderTransformOrigin = anchor ?? RelativePoint.Center;
        var t = new ScaleTransform(s, s);
        c.RenderTransform = Compose(c, t);
        return c;
    }

    public static Control RotationEffect(this Control c, double degrees)
    {
        c.RenderTransformOrigin = RelativePoint.Center;
        var t = new RotateTransform(degrees);
        c.RenderTransform = Compose(c, t);
        return c;
    }

    private static ITransform Compose(Control c, Transform t)
    {
        if (c.RenderTransform is not Transform existing) return t;
        if (existing is TransformGroup g) { g.Children.Add(t); return g; }
        return new TransformGroup { Children = { existing, t } };
    }

    public static Control ZIndex(this Control c, int z) { c.ZIndex = z; return c; }

    // MARK: Shapes (on ShapeView)

    public static ShapeView Fill(this ShapeView s, Paint p) { s.Fill = p; s.FillWithForeground = false; return s; }
    public static ShapeView Stroke(this ShapeView s, Paint p, double lineWidth = 1, double[]? dash = null)
    {
        s.Stroke = p; s.LineWidth = lineWidth; s.Dash = dash; s.Inset = false;
        if (s.Fill == null) s.FillWithForeground = false;
        return s;
    }
    public static ShapeView StrokeBorder(this ShapeView s, Paint p, double lineWidth = 1, double[]? dash = null)
    {
        s.Stroke = p; s.LineWidth = lineWidth; s.Dash = dash; s.Inset = true;
        if (s.Fill == null) s.FillWithForeground = false;
        return s;
    }

    // MARK: Clipping

    public static Control ClipShape(this Control c, ShapeKind kind, double radius = 0, bool continuous = false) =>
        new ClipBox { Child = c, Kind = kind, CornerRadius = radius, Continuous = continuous };
    public static Control ClipRounded(this Control c, double radius, bool continuous = false) => c.ClipShape(ShapeKind.RoundedRectangle, radius, continuous);
    public static Control ClipCircle(this Control c) => c.ClipShape(ShapeKind.Circle);
    public static Control ClipCapsule(this Control c) => c.ClipShape(ShapeKind.Capsule, 0, true);
    public static Control Clipped(this Control c) => c.ClipShape(ShapeKind.Rectangle);
    /// `.contentShape(...)`: the whole frame takes clicks, transparent areas included.
    public static Control ContentShape(this Control c)
    {
        return c.Background(SColor.Clear);
    }

    // MARK: Environment

    public static T Font<T>(this T c, SFont f) where T : Control
    {
        if (c is SText t) t.OwnFont = f;
        c.SetValue(Env.FontProperty, f);
        return c;
    }

    public static T Font<T>(this T c, double size, Weight weight = Weight.Regular, Design design = Design.Default) where T : Control =>
        c.Font(SFont.System(size, weight, design));

    /// `.foregroundStyle(...)`: a color, or `.secondary`/`.tertiary`/`.quaternary` of the current style.
    public static T Foreground<T>(this T c, SColor color) where T : Control
    {
        switch (color.K)
        {
            case SColor.Kind.Secondary: c.SetValue(Env.LevelProperty, 1); break;
            case SColor.Kind.Tertiary: c.SetValue(Env.LevelProperty, 2); break;
            case SColor.Kind.Quaternary: c.SetValue(Env.LevelProperty, 3); break;
            default:
                c.SetValue(Env.ForegroundProperty, color);
                c.SetValue(Env.LevelProperty, 0);
                c.SetValue(Env.ForegroundPaintProperty, null);
                break;
        }
        return c;
    }

    public static T Foreground<T>(this T c, Paint paint) where T : Control
    {
        if (paint is ColorPaint cp) return c.Foreground(cp.Color);
        c.SetValue(Env.ForegroundPaintProperty, paint);
        c.SetValue(Env.LevelProperty, 0);
        return c;
    }

    public static T Scheme<T>(this T c, Scheme s) where T : Control { c.SetValue(Env.SchemeProperty, s); return c; }
    public static T LineLimit<T>(this T c, int? n) where T : Control { c.SetValue(Env.LineLimitProperty, n); return c; }
    public static T MultilineAlignment<T>(this T c, TextAlignment a) where T : Control { c.SetValue(Env.MultilineAlignmentProperty, a); return c; }
    public static T Tint<T>(this T c, SColor color) where T : Control { c.SetValue(Env.TintProperty, color); return c; }

    public static T Opacity<T>(this T c, double o) where T : Control { c.Opacity = o; return c; }
    public static T Hidden<T>(this T c, bool hidden = true) where T : Control { c.IsVisible = !hidden; return c; }
    /// `.opacity(0)` that keeps the layout slot (SwiftUI `.hidden()`).
    public static T Invisible<T>(this T c, bool invisible = true) where T : Control { if (invisible) c.Opacity = 0; c.IsHitTestVisible = !invisible; return c; }
    public static T Disabled<T>(this T c, bool disabled = true) where T : Control
    {
        c.IsEnabled = !disabled;
        c.SetValue(Env.EnabledProperty, !disabled);
        return c;
    }
    public static T AllowsHitTesting<T>(this T c, bool allow) where T : Control { c.IsHitTestVisible = allow; return c; }

    public static T Shadow<T>(this T c, SColor color, double radius, double x = 0, double y = 0) where T : Control
    {
        var col = color.Resolve(Kit.Scheme.Light);
        c.Effect = new DropShadowEffect { Color = Color.FromRgb(col.R, col.G, col.B), Opacity = col.A / 255.0, BlurRadius = radius * 2, OffsetX = x, OffsetY = y };
        return c;
    }

    public static T Blur<T>(this T c, double radius) where T : Control { c.Effect = new BlurEffect { Radius = radius * 2 }; return c; }

    // MARK: Interaction

    public static T Help<T>(this T c, string? text) where T : Control
    {
        if (!string.IsNullOrEmpty(text)) ToolTip.SetTip(c, text);
        return c;
    }

    public static Control OnTap(this Control c, Action action, int count = 1)
    {
        var target = c.ContentShape();
        target.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(target).Properties.IsLeftButtonPressed || e.ClickCount != count) return;
            action();
            e.Handled = true;
        };
        return target;
    }

    public static Control OnHover(this Control c, Action<bool> hovering)
    {
        c.PointerEntered += (_, _) => hovering(true);
        c.PointerExited += (_, _) => hovering(false);
        return c;
    }

    public static T OnAppear<T>(this T c, Action action) where T : Control
    {
        c.AttachedToVisualTree += (_, _) => action();
        return c;
    }

    public static T OnDisappear<T>(this T c, Action action) where T : Control
    {
        c.DetachedFromVisualTree += (_, _) => action();
        return c;
    }

    public static T Cursor<T>(this T c, StandardCursorType type) where T : Control { c.Cursor = new Cursor(type); return c; }
}

/// `Button` with `.buttonStyle(.plain)`: the label itself, dimmed while pressed.
public sealed class Pressable : Decorator, IFlexible
{
    private readonly Action _action;
    private bool _pressed;

    public Pressable(Control label, Action action)
    {
        _action = action;
        Child = new DecorationBox(label, UI.ColorView(SColor.Clear), true, Alignment.Center);
        Focusable = false;
    }

    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsEffectivelyEnabled) return;
        _pressed = true;
        Opacity = 0.75;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        Opacity = 1;
        e.Pointer.Capture(null);
        var p = e.GetPosition(this);
        if (new Rect(Bounds.Size).Contains(p)) _action();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
        Opacity = 1;
    }
}
