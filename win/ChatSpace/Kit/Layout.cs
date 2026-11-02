using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ChatSpace.Kit;

public enum HAlign { Leading, Center, Trailing }
public enum VAlign { Top, Center, Bottom, FirstBaseline, LastBaseline }

/// SwiftUI `Alignment` for frames, overlays and ZStacks.
public readonly record struct Alignment(HAlign H, VAlign V)
{
    public static readonly Alignment Center = new(HAlign.Center, VAlign.Center);
    public static readonly Alignment Leading = new(HAlign.Leading, VAlign.Center);
    public static readonly Alignment Trailing = new(HAlign.Trailing, VAlign.Center);
    public static readonly Alignment Top = new(HAlign.Center, VAlign.Top);
    public static readonly Alignment Bottom = new(HAlign.Center, VAlign.Bottom);
    public static readonly Alignment TopLeading = new(HAlign.Leading, VAlign.Top);
    public static readonly Alignment TopTrailing = new(HAlign.Trailing, VAlign.Top);
    public static readonly Alignment BottomLeading = new(HAlign.Leading, VAlign.Bottom);
    public static readonly Alignment BottomTrailing = new(HAlign.Trailing, VAlign.Bottom);

    public static double Place(HAlign a, double space, double size) => a switch
    {
        HAlign.Leading => 0, HAlign.Trailing => space - size, _ => (space - size) / 2,
    };

    public static double Place(VAlign a, double space, double size) => a switch
    {
        VAlign.Top => 0, VAlign.Bottom => space - size, _ => (space - size) / 2,
    };
}

/// Controls that, like SwiftUI shapes, spacers, scroll views and infinite frames, take whatever space they are offered.
public interface IFlexible
{
    bool IsFlexible(Orientation axis);
}

public static class Flex
{
    public static readonly AttachedProperty<bool?> HorizontalProperty =
        AvaloniaProperty.RegisterAttached<Control, bool?>("Horizontal", typeof(Flex));
    public static readonly AttachedProperty<bool?> VerticalProperty =
        AvaloniaProperty.RegisterAttached<Control, bool?>("Vertical", typeof(Flex));

    /// SwiftUI lays out nothing for an empty view (a `View` that built no content), not even spacing.
    public static bool IsEmpty(Control c)
    {
        while (c is Reactive or KeepHost or PaddingBox)
        {
            if (((Decorator)c).Child is not { } inner) return true;
            c = inner;
        }
        return false;
    }

    public static bool Of(Control? c, Orientation axis)
    {
        if (c == null || !c.IsVisible) return false;
        var o = c.GetValue(axis == Orientation.Horizontal ? HorizontalProperty : VerticalProperty);
        if (o.HasValue) return o.Value;
        return c switch
        {
            IFlexible f => f.IsFlexible(axis),
            ScrollViewer => true,
            TextBox => axis == Orientation.Horizontal,
            Decorator d => Of(d.Child, axis),
            ContentControl cc => Of(cc.Content as Control, axis),
            _ => false,
        };
    }
}

/// SwiftUI `Spacer`.
public sealed class Spacer : Control, IFlexible
{
    public double? MinLength { get; init; }
    public bool IsFlexible(Orientation axis) => true;
    protected override Size MeasureOverride(Size availableSize) => default;
}

/// SwiftUI `HStack` / `VStack`: children at their own size, flexible children and spacers share what is left,
/// and when space runs short the least flexible children are sized first (so text truncates, icons do not).
public sealed class Stack : Panel, IFlexible
{
    public Orientation Axis { get; init; }
    /// nil = system spacing (8, none next to a Spacer).
    public double? Spacing { get; init; }
    public HAlign HAlignment { get; init; } = HAlign.Center;
    public VAlign VAlignment { get; init; } = VAlign.Center;

    private double[] _main = [];
    private double[] _cross = [];

    public bool IsFlexible(Orientation axis)
    {
        foreach (var c in Children) if (c is not Spacer && Flex.Of(c, axis)) return true;
        if (axis == Axis) foreach (var c in Children) if (c is Spacer && c.IsVisible) return true;
        return false;
    }

    private bool H => Axis == Orientation.Horizontal;
    private static double M(Size s, bool h) => h ? s.Width : s.Height;
    private static double X(Size s, bool h) => h ? s.Height : s.Width;
    private static Size Sz(double main, double cross, bool h) => h ? new Size(main, cross) : new Size(cross, main);

    private double Gap(Control a, Control b)
    {
        if (Spacing is { } s) return s;
        return a is Spacer || b is Spacer ? 0 : 8;
    }

    protected override Size MeasureOverride(Size available)
    {
        var h = H;
        // Empty views are still measured so Avalonia re-lays them out once they build content.
        foreach (var c in Children) if (c.IsVisible && Flex.IsEmpty(c)) c.Measure(available);
        var kids = Children.Where(c => c.IsVisible && !Flex.IsEmpty(c)).ToList();
        var n = kids.Count;
        _main = new double[Children.Count];
        _cross = new double[Children.Count];
        if (n == 0) return default;
        var availMain = M(available, h);
        var availCross = X(available, h);

        double gaps = 0;
        for (var i = 1; i < n; i++) gaps += Gap(kids[i - 1], kids[i]);

        var flex = new bool[n];
        var ideal = new double[n];
        double fixedSum = 0;
        for (var i = 0; i < n; i++)
        {
            var c = kids[i];
            flex[i] = c is Spacer || Flex.Of(c, Axis);
            if (c is Spacer sp) { ideal[i] = sp.MinLength ?? 8; fixedSum += 0; continue; }
            c.Measure(Sz(double.PositiveInfinity, availCross, h));
            ideal[i] = M(c.DesiredSize, h);
            if (!flex[i]) fixedSum += ideal[i];
        }

        var size = new double[n];
        if (double.IsInfinity(availMain))
        {
            for (var i = 0; i < n; i++) size[i] = ideal[i];
        }
        else
        {
            var flexCount = flex.Count(f => f);
            var minFlex = 0.0;
            for (var i = 0; i < n; i++) if (flex[i] && kids[i] is Spacer) minFlex += ideal[i];
            var remaining = availMain - gaps - minFlex;
            if (fixedSum <= remaining)
            {
                for (var i = 0; i < n; i++) if (!flex[i]) size[i] = ideal[i];
                remaining -= fixedSum;
            }
            else
            {
                // Short of space: least flexible first, each offered an equal share of what is left.
                var order = Enumerable.Range(0, n).Where(i => !flex[i]).Select(i =>
                {
                    kids[i].Measure(Sz(0, availCross, h));
                    return (i, flexibility: ideal[i] - M(kids[i].DesiredSize, h));
                }).OrderBy(t => t.flexibility).Select(t => t.i).ToList();
                var left = order.Count;
                foreach (var i in order)
                {
                    var share = Math.Max(0, remaining / left);
                    kids[i].Measure(Sz(share, availCross, h));
                    size[i] = Math.Min(M(kids[i].DesiredSize, h), Math.Max(share, M(kids[i].DesiredSize, h)));
                    remaining -= size[i];
                    left--;
                }
                remaining = Math.Max(0, remaining);
            }
            if (flexCount > 0)
            {
                // Flexible children (views, then spacers on top of their minimum) split what is left equally.
                var order = Enumerable.Range(0, n).Where(i => flex[i]).OrderBy(i => kids[i] is Spacer ? 1 : 0).ToList();
                var pool = Math.Max(0, remaining);
                var left = order.Count;
                foreach (var i in order)
                {
                    var share = Math.Max(0, pool / left);
                    if (kids[i] is Spacer)
                    {
                        size[i] = ideal[i] + share;
                    }
                    else
                    {
                        kids[i].Measure(Sz(share, availCross, h));
                        size[i] = Math.Max(M(kids[i].DesiredSize, h), share);
                    }
                    pool -= kids[i] is Spacer ? share : size[i];
                    left--;
                }
            }
        }

        // Final measure at the decided main size so cross sizes (wrapped text) are right.
        double crossMax = 0, mainSum = gaps;
        for (var i = 0; i < n; i++)
        {
            var c = kids[i];
            if (c is not Spacer)
            {
                // A fixed child that got all it asked for stays unbounded along the axis: Avalonia re-measures a
                // grown descendant with its last constraint, and a bound here would clamp the growth unseen.
                var unbounded = !flex[i] && size[i] >= ideal[i];
                c.Measure(Sz(unbounded ? double.PositiveInfinity : size[i], availCross, h));
                if (!flex[i] || double.IsInfinity(availMain)) size[i] = M(c.DesiredSize, h);
                crossMax = Math.Max(crossMax, X(c.DesiredSize, h));
            }
            mainSum += size[i];
            var idx = Children.IndexOf(c);
            _main[idx] = size[i];
            _cross[idx] = c is Spacer ? 0 : X(c.DesiredSize, h);
        }
        return Sz(mainSum, crossMax, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var h = H;
        foreach (var c in Children) if (c.IsVisible && Flex.IsEmpty(c)) c.Arrange(default);
        var kids = Children.Where(c => c.IsVisible && !Flex.IsEmpty(c)).ToList();
        var cross = X(finalSize, h);
        // Leftover main-axis space (parent gave more than desired) goes to spacers, else to flexible children.
        var used = 0.0;
        for (var i = 0; i < kids.Count; i++)
        {
            used += _main[Children.IndexOf(kids[i])];
            if (i > 0) used += Gap(kids[i - 1], kids[i]);
        }
        var extra = Math.Max(0, M(finalSize, h) - used);
        var spacers = kids.Count(c => c is Spacer);
        var flexViews = spacers == 0 ? kids.Count(c => Flex.Of(c, Axis)) : 0;
        double pos = 0;
        for (var i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (i > 0) pos += Gap(kids[i - 1], c);
            var idx = Children.IndexOf(c);
            var main = _main[idx];
            if (c is Spacer && spacers > 0) main += extra / spacers;
            else if (flexViews > 0 && Flex.Of(c, Axis)) main += extra / flexViews;
            var cs = Flex.Of(c, h ? Orientation.Vertical : Orientation.Horizontal) ? cross : Math.Min(_cross[idx], cross);
            var off = h ? Alignment.Place(VAlignment, cross, cs) : Alignment.Place(HAlignment, cross, cs);
            c.Arrange(h ? new Rect(pos, off, main, cs) : new Rect(off, pos, cs, main));
            pos += main;
        }
        return finalSize;
    }
}

/// SwiftUI `ZStack`: children on top of each other, sized to the largest, aligned by `Alignment`.
public sealed class ZStackPanel : Panel, IFlexible
{
    public Alignment Alignment { get; init; } = Alignment.Center;

    public bool IsFlexible(Orientation axis) => Children.Any(c => Flex.Of(c, axis));

    protected override Size MeasureOverride(Size available)
    {
        double w = 0, h = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(available);
            w = Math.Max(w, c.DesiredSize.Width);
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            var cw = Flex.Of(c, Orientation.Horizontal) ? finalSize.Width : Math.Min(c.DesiredSize.Width, finalSize.Width);
            var ch = Flex.Of(c, Orientation.Vertical) ? finalSize.Height : Math.Min(c.DesiredSize.Height, finalSize.Height);
            c.Arrange(new Rect(Alignment.Place(Alignment.H, finalSize.Width, cw), Alignment.Place(Alignment.V, finalSize.Height, ch), cw, ch));
        }
        return finalSize;
    }
}

/// SwiftUI `.frame(...)`: fixed or flexible size around a child placed by `Alignment`.
public sealed class FrameBox : Decorator, IFlexible
{
    public double? FixedW { get; set; }
    public double? FixedH { get; set; }
    public double? MinW { get; set; }
    public double? MaxW { get; set; }
    public double? MinH { get; set; }
    public double? MaxH { get; set; }
    public double? IdealW { get; set; }
    public double? IdealH { get; set; }
    public Alignment Alignment { get; set; } = Alignment.Center;

    public bool IsFlexible(Orientation axis)
    {
        if (axis == Orientation.Horizontal)
            return FixedW == null && (MaxW is { } mw ? double.IsPositiveInfinity(mw) || (MinW == null && Flex.Of(Child, axis)) : Flex.Of(Child, axis));
        return FixedH == null && (MaxH is { } mh ? double.IsPositiveInfinity(mh) || (MinH == null && Flex.Of(Child, axis)) : Flex.Of(Child, axis));
    }

    private static double Propose(double p, double? fixedV, double? min, double? max, double? ideal)
    {
        if (fixedV is { } f) return f;
        if (double.IsInfinity(p) && ideal is { } i) p = i;
        if (min is { } mn && p < mn) p = mn;
        if (max is { } mx && p > mx) p = mx;
        return p;
    }

    private static double Result(double p, double child, double? fixedV, double? min, double? max, double? ideal)
    {
        if (fixedV is { } f) return f;
        if (min == null && max == null) return child;
        if (double.IsInfinity(p)) p = ideal ?? child;
        var lo = min ?? child;
        var hi = max ?? child;
        if (hi < lo) hi = lo;
        return Math.Clamp(p, lo, hi);
    }

    protected override Size MeasureOverride(Size available)
    {
        var pw = Propose(available.Width, FixedW, MinW, MaxW, IdealW);
        var ph = Propose(available.Height, FixedH, MinH, MaxH, IdealH);
        var cs = default(Size);
        if (Child != null)
        {
            Child.Measure(new Size(pw, ph));
            cs = Child.DesiredSize;
        }
        return new Size(Result(available.Width, cs.Width, FixedW, MinW, MaxW, IdealW),
                        Result(available.Height, cs.Height, FixedH, MinH, MaxH, IdealH));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child == null) return finalSize;
        var cw = Flex.Of(Child, Orientation.Horizontal) ? finalSize.Width : Math.Min(Child.DesiredSize.Width, Math.Max(finalSize.Width, Child.DesiredSize.Width));
        var ch = Flex.Of(Child, Orientation.Vertical) ? finalSize.Height : Math.Min(Child.DesiredSize.Height, Math.Max(finalSize.Height, Child.DesiredSize.Height));
        if (FixedW != null || MaxW != null) cw = Flex.Of(Child, Orientation.Horizontal) ? finalSize.Width : Child.DesiredSize.Width;
        if (FixedH != null || MaxH != null) ch = Flex.Of(Child, Orientation.Vertical) ? finalSize.Height : Child.DesiredSize.Height;
        Child.Arrange(new Rect(Alignment.Place(Alignment.H, finalSize.Width, cw), Alignment.Place(Alignment.V, finalSize.Height, ch), cw, ch));
        return finalSize;
    }
}

/// SwiftUI `.padding(...)`.
public sealed class PaddingBox : Decorator, IFlexible
{
    public Thickness Insets { get; set; }
    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);

    protected override Size MeasureOverride(Size available)
    {
        var inner = new Size(Math.Max(0, available.Width - Insets.Left - Insets.Right), Math.Max(0, available.Height - Insets.Top - Insets.Bottom));
        if (Child == null) return new Size(Insets.Left + Insets.Right, Insets.Top + Insets.Bottom);
        Child.Measure(inner);
        return new Size(Child.DesiredSize.Width + Insets.Left + Insets.Right, Child.DesiredSize.Height + Insets.Top + Insets.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(Insets.Left, Insets.Top, Math.Max(0, finalSize.Width - Insets.Left - Insets.Right),
            Math.Max(0, finalSize.Height - Insets.Top - Insets.Bottom)));
        return finalSize;
    }
}

/// SwiftUI `.background(view)` / `.overlay(view)`: the decoration gets exactly the content's size.
public sealed class DecorationBox : Panel, IFlexible
{
    public Control Content { get; }
    public Control Decoration { get; }
    public bool Behind { get; }
    public Alignment Alignment { get; }

    public DecorationBox(Control content, Control decoration, bool behind, Alignment alignment)
    {
        Content = content;
        Decoration = decoration;
        Behind = behind;
        Alignment = alignment;
        if (behind) { Children.Add(decoration); Children.Add(content); }
        else { Children.Add(content); Children.Add(decoration); }
    }

    public bool IsFlexible(Orientation axis) => Flex.Of(Content, axis);

    protected override Size MeasureOverride(Size available)
    {
        Content.Measure(available);
        Decoration.Measure(Content.DesiredSize);
        return Content.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Content.Arrange(new Rect(finalSize));
        // Re-measure against the arranged size: flexible content may have grown past its desired size.
        if (Decoration.DesiredSize != finalSize) Decoration.Measure(finalSize);
        var dw = Flex.Of(Decoration, Orientation.Horizontal) ? finalSize.Width : Decoration.DesiredSize.Width;
        var dh = Flex.Of(Decoration, Orientation.Vertical) ? finalSize.Height : Decoration.DesiredSize.Height;
        Decoration.Arrange(new Rect(Alignment.Place(Alignment.H, finalSize.Width, dw), Alignment.Place(Alignment.V, finalSize.Height, dh), dw, dh));
        return finalSize;
    }
}

/// `.fixedSize()`: the child gets its ideal size regardless of the proposal.
public sealed class FixedSizeBox : Decorator, IFlexible
{
    public bool Horizontal { get; set; } = true;
    public bool Vertical { get; set; } = true;

    public bool IsFlexible(Orientation axis) => !(axis == Orientation.Horizontal ? Horizontal : Vertical) && Flex.Of(Child, axis);

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null) return default;
        Child.Measure(new Size(Horizontal ? double.PositiveInfinity : available.Width, Vertical ? double.PositiveInfinity : available.Height));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(new Size(Horizontal ? Child.DesiredSize.Width : finalSize.Width, Vertical ? Child.DesiredSize.Height : finalSize.Height)));
        return finalSize;
    }
}

/// `.aspectRatio(r, contentMode:)` around a flexible child.
public sealed class AspectBox : Decorator, IFlexible
{
    public double? Ratio { get; set; }
    public bool Fill { get; set; }
    public bool IsFlexible(Orientation axis) => true;

    protected override Size MeasureOverride(Size available)
    {
        var r = Ratio ?? 1;
        double w = available.Width, h = available.Height;
        if (double.IsInfinity(w) && double.IsInfinity(h)) { w = 100 * r; h = 100; }
        else if (double.IsInfinity(w)) w = h * r;
        else if (double.IsInfinity(h)) h = w / r;
        else if (Fill ? w / h < r : w / h > r) w = h * r; else h = w / r;
        Child?.Measure(new Size(w, h));
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
