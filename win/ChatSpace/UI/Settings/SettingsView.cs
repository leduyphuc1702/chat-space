using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

// Port of App/SettingsView.swift: Settings laid out like Telegram for macOS — a list of coloured-icon rows on
// the left, grouped cards on a grey background on the right.

public enum SettingsSection { Accounts, Notifications, Storage, Groups, Ignored, Bubbles, Translation, Composer, Telegram, Assets }

public static class SettingsSections
{
    public static readonly SettingsSection[][] SidebarGroups =
    [
        [SettingsSection.Notifications, SettingsSection.Storage],
        [SettingsSection.Groups, SettingsSection.Ignored, SettingsSection.Bubbles, SettingsSection.Translation, SettingsSection.Composer],
        [SettingsSection.Telegram, SettingsSection.Assets],
    ];

    /// `rawValue` of the Swift enum (the case name), which is what `settings.section` stores.
    public static string Raw(this SettingsSection s) => s.ToString().ToLowerInvariant();

    public static SettingsSection? Parse(string? raw) =>
        Enum.GetValues<SettingsSection>().Cast<SettingsSection?>().FirstOrDefault(s => s!.Value.Raw() == raw);

    public static string Title(this SettingsSection s) => s switch
    {
        SettingsSection.Accounts => "Tài khoản",
        SettingsSection.Notifications => "Thông báo và âm thanh",
        SettingsSection.Storage => "Dữ liệu và bộ nhớ",
        SettingsSection.Groups => "Nhóm tuỳ chỉnh",
        SettingsSection.Ignored => "Đã bỏ qua",
        SettingsSection.Bubbles => "Bong bóng chat",
        SettingsSection.Translation => "Dịch tin nhắn",
        SettingsSection.Composer => "Soạn tin nhắn",
        SettingsSection.Telegram => "Telegram API",
        _ => "Tài nguyên gốc",
    };

    public static string Icon(this SettingsSection s) => s switch
    {
        SettingsSection.Accounts => "person.crop.circle.fill",
        SettingsSection.Notifications => "bell.badge.fill",
        SettingsSection.Storage => "cylinder.split.1x2.fill",
        SettingsSection.Groups => "folder.fill",
        SettingsSection.Ignored => "eye.slash.fill",
        SettingsSection.Bubbles => "bubble.left.and.bubble.right.fill",
        SettingsSection.Translation => "translate",
        SettingsSection.Composer => "function",
        SettingsSection.Telegram => "paperplane.fill",
        _ => "square.stack.3d.up.fill",
    };

    public static SColor Color(this SettingsSection s) => s switch
    {
        SettingsSection.Accounts => SColor.Hex(0x3390EC),
        SettingsSection.Notifications => SColor.Hex(0xFF3B30),
        SettingsSection.Storage => SColor.Hex(0x34C759),
        SettingsSection.Groups => SColor.Hex(0x5E5CE6),
        SettingsSection.Ignored => SColor.Hex(0xFF9500),
        SettingsSection.Bubbles => SColor.Hex(0x32ADE6),
        SettingsSection.Translation => SColor.Hex(0x30B0C7),
        SettingsSection.Composer => SColor.Hex(0xAF52DE),
        SettingsSection.Telegram => SColor.Hex(0x2AABEE),
        _ => SColor.Hex(0x8E8E93),
    };
}

/// `@AppStorage("settings.section")`: the pane Settings opens on. Demo mode keeps it in memory only.
public sealed class SettingsModel : Observable
{
    public static readonly SettingsModel Shared = new();

    private SettingsSection _section;

    private SettingsModel()
    {
        _section = (DemoData.Enabled ? null : SettingsSections.Parse(Prefs.GetString("settings.section"))) ?? SettingsSection.Storage;
    }

    public SettingsSection Section
    {
        get => Get(ref _section);
        set
        {
            Set(ref _section, value);
            if (!DemoData.Enabled) Prefs.Set("settings.section", value.Raw());
        }
    }
}

/// Telegram settings palette.
public static class TGS
{
    public static readonly SColor Accent = SColor.Hex(0x3390EC);
    public static readonly SColor Background = SColor.Hex(0xEFEFF4);
    public static readonly SColor Secondary = SColor.Hex(0x8E8E93);
    public static readonly SColor Separator = SColor.Hex(0xE3E3E6);
    /// SwiftUI's `.link` button colour on macOS and `.red`, as measured from the captures.
    public static readonly SColor Link = SColor.Hex(0x0068DA);
    public static readonly SColor Red = SColor.Hex(0xFF383C);
}

public static class SettingsWindow
{
    /// Section named by `CHATSPACE_DEMO_SETTINGS`; applied the next time the window is shown.
    public static string? InitialSection;

    private static MacWindow? _window;

    /// The shared Settings window (null until first shown).
    public static MacWindow? Window => _window;

    public static void ShowShared()
    {
        if (InitialSection != null)
        {
            if (SettingsSections.Parse(InitialSection) is { } s) SettingsModel.Shared.Section = s;
            InitialSection = null;
        }
        _window ??= Create();
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// `openSettings()` after choosing a pane.
    public static void Show(SettingsSection section)
    {
        SettingsModel.Shared.Section = section;
        ShowShared();
    }

    /// `App.ScreenshotTargets["settings"]`.
    public static TopLevel? ScreenshotTarget()
    {
        ShowShared();
        return _window;
    }

    private static MacWindow Create()
    {
        // The captures are of a dark-mode Mac: dark title bar, light content (`.environment(\.colorScheme, .light)`).
        var win = new MacWindow("ChatSpace Settings", Chrome("ChatSpace Settings", new SettingsRoot()), true, Scheme.Dark)
        {
            Width = 860,
            Height = MacWindow.TitleBarHeight + 640,
            CanResize = false,
            ShowZoom = false,
            HideOnClose = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        // A Settings window has only a close button, 10 pt from the edge (AppKit's inset for a titled window; the Kit's
        // strip sits at 20 pt, as in a hidden-title-bar window). This strip paints over the Kit's.
        if (win.Content is Panel root)
        {
            root.Children.Add(new SettingsTrafficLights(win));
            root.Children.Add(_sheetLayer);
        }
        return win;
    }

    /// The title bar of a dark-mode Mac window (0x262626 in the captures, the Kit's `MacWindow` draws 0x222222) above the content.
    private static Control Chrome(string title, Control content) =>
        VStack(0,
            ZStack(
                    ColorView(SColor.Hex(0x262626)),
                    Text(title).Font(13, Weight.Bold).Foreground(SColor.White.Opacity(0.55)).LineLimit(1).Offset(0, -1.5))
                .Frame(height: MacWindow.TitleBarHeight),
            content.Fill());

    private static readonly Panel _sheetLayer = new();

    /// `.sheet` on the Settings view (the group editor), in the light appearance the content uses.
    public static void PresentSheet(Func<Action, Control> content)
    {
        ShowShared();
        Sheets.Present(_sheetLayer, content, Scheme.Light);
    }
}

/// Close (live) + minimise and zoom (disabled) lights at AppKit's titled-window inset. Covers `MacWindow`'s own strip.
internal sealed class SettingsTrafficLights : Control
{
    private const double D = 12, Gap = 8, Left = 10, Top = 8;
    private readonly Window _window;
    private bool _hover, _pressed;

    public SettingsTrafficLights(Window window)
    {
        _window = window;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Width = 20 + 3 * D + 2 * Gap + 8;
        Height = MacWindow.TitleBarHeight;
        PointerEntered += (_, _) => { _hover = true; InvalidateVisual(); };
        PointerExited += (_, _) => { _hover = false; InvalidateVisual(); };
    }

    private static bool OverClose(Point p) => Math.Abs(p.X - (Left + D / 2)) <= D / 2 + 2 && Math.Abs(p.Y - (Top + D / 2)) <= D / 2 + 2;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!OverClose(e.GetPosition(this))) return;
        _pressed = true;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var hit = _pressed && OverClose(e.GetPosition(this));
        _pressed = false;
        InvalidateVisual();
        if (!hit) return;
        e.Handled = true;
        _window.Close();
    }

    public override void Render(DrawingContext ctx)
    {
        // The title bar colour under the Kit's strip (dark scheme), then the three lights.
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)), null, new Rect(0, 0, Bounds.Width, Bounds.Height));
        var grey = Color.FromRgb(0x4B, 0x4B, 0x4D);
        for (var i = 1; i < 3; i++)
            ctx.DrawEllipse(new ImmutableSolidColorBrush(grey), null, new Rect(Left + i * (D + Gap), Top, D, D));
        var red = Color.FromRgb(0xFF, 0x5F, 0x57);
        if (_pressed) red = Color.FromRgb((byte)(red.R * 0.8), (byte)(red.G * 0.8), (byte)(red.B * 0.8));
        var close = new Rect(Left, Top, D, D);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(red), new Pen(new ImmutableSolidColorBrush(Color.FromRgb(0xF2, 0x5A, 0x52)), 0.5), close.Deflate(0.25));
        if (!_hover) return;
        var pen = new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), 1.1, lineCap: PenLineCap.Round);
        var c = close.Center;
        ctx.DrawLine(pen, c + new Vector(-2.6, -2.6), c + new Vector(2.6, 2.6));
        ctx.DrawLine(pen, c + new Vector(-2.6, 2.6), c + new Vector(2.6, -2.6));
    }
}

/// The app icon in a `size` square the way AppKit shows it: the artwork's rounded tile (206 of the PNG's 256 px) fills the
/// square and its shadow spills out of it. (`ChatSpaceIcon` squeezes the PNG into the square, which shrinks the tile.)
internal sealed class AppIconTile : Control
{
    private readonly double _size;

    public AppIconTile(double size)
    {
        _size = size;
        Width = size;
        Height = size;
    }

    public override void Render(DrawingContext ctx)
    {
        if (NativeAssets.Shared.Image("icons/chatspace-256.png") is not { } img) return;
        var full = _size * 1.22; // ChatSpaceIcon's `.frame(size * 1.22).frame(size)`, which SwiftUI lets overflow
        var o = (_size - full) / 2;
        ctx.DrawImage(img, new Rect(o, o, full, full));
    }
}

/// AppKit draws the title bar's separator over the top of the content (28 pt bar, 640 pt content): a 1 px black line
/// and a 1 px soft edge below it. The window builds its own bar (`SettingsWindow.Chrome`), so the content starts right
/// under it and this paints the two pixel rows on top of the content.
internal sealed class UnderTitleSeparator : Decorator, IFlexible
{
    public UnderTitleSeparator(Control body) =>
        Child = ZStack(Alignment.TopLeading, body, new TitleEdge());

    public bool IsFlexible(Orientation axis) => true;

    /// Drawn in `Render`: layout rounding would turn a 0.5 pt frame into a whole point.
    private sealed class TitleEdge : Control
    {
        private static readonly IBrush Line = new ImmutableSolidColorBrush(Colors.Black);
        private static readonly IBrush Soft = new ImmutableSolidColorBrush(Colors.Black, 0.22);

        protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 0 : available.Width, 1);

        public TitleEdge()
        {
            Height = 1;
            VerticalAlignment = VerticalAlignment.Top;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext ctx)
        {
            ctx.FillRectangle(Line, new Rect(0, 0, Bounds.Width, 0.5));
            ctx.FillRectangle(Soft, new Rect(0, 0.5, Bounds.Width, 0.5));
        }
    }
}

/// A vertical scroll view with AppKit's legacy (always shown) scroller: a 15 pt strip that takes its width from the content
/// only while the content overflows, with a rounded knob. The captures were taken with "Show scroll bars: Always".
internal sealed class LegacyScrollHost : Decorator, IFlexible
{
    private const double Bar = 15, Inset = 3, KnobWidth = 8, MinKnob = 20;
    private static IBrush Brush(uint rgb) => new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    // Light and dark appearance, measured from the captures: track, left edge, right edge, knob.
    private static readonly IBrush[] Light = [Brush(0xFAFAFA), Brush(0xE7E7E7), Brush(0xEDEDED), Brush(0xC8C8C8)];
    private static readonly IBrush[] Dark = [Brush(0x2D2D2D), Brush(0x434343), Brush(0x303030), Brush(0x747474)];

    private readonly ScrollViewer _sv;
    private Control _content;
    private bool _bar;
    private bool _drag;
    private double _dragStartY, _dragStartOffset;

    public LegacyScrollHost(Control content)
    {
        _content = content;
        _sv = ScrollView(content, indicators: false);
        Child = _sv;
        _sv.ScrollChanged += (_, _) => InvalidateVisual();
    }

    /// Swaps the content while keeping the scroll position (SwiftUI keeps it across body updates).
    public void SetContent(Control content)
    {
        if (ReferenceEquals(content, _content)) return;
        content.VerticalAlignment = VerticalAlignment.Top;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        _content = content;
        _sv.Content = content;
        InvalidateMeasure();
    }

    public bool IsFlexible(Orientation axis) => true;

    protected override Size MeasureOverride(Size available)
    {
        _sv.Measure(available);
        return new Size(double.IsInfinity(available.Width) ? _sv.DesiredSize.Width : available.Width,
            double.IsInfinity(available.Height) ? _sv.DesiredSize.Height : available.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _content.Measure(new Size(finalSize.Width, double.PositiveInfinity));
        var bar = _content.DesiredSize.Height > finalSize.Height + 0.5;
        if (bar != _bar) { _bar = bar; InvalidateVisual(); }
        var w = Math.Max(0, finalSize.Width - (_bar ? Bar : 0));
        _sv.Measure(new Size(w, finalSize.Height));
        _sv.Arrange(new Rect(0, 0, w, finalSize.Height));
        return finalSize;
    }

    // Knob geometry within the track: `Inset` above and below, proportional to the visible part of the content.
    private (Rect knob, double slot, double length)? Knob()
    {
        var extent = _sv.Extent.Height;
        var view = _sv.Viewport.Height;
        if (!_bar || extent <= view || view <= 0) return null;
        var slot = Bounds.Height - 2 * Inset;
        var length = Math.Max(MinKnob, slot * view / extent);
        var range = extent - view;
        var top = Inset + (slot - length) * Math.Clamp(_sv.Offset.Y / range, 0, 1);
        return (new Rect(Bounds.Width - Bar + 4, top, KnobWidth, length), slot, length);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        if (!_bar) return;
        var x = Bounds.Width - Bar;
        var p = GetValue(Env.SchemeProperty) == Scheme.Dark ? Dark : Light;
        ctx.DrawRectangle(p[0], null, new Rect(x, 0, Bar, Bounds.Height));
        ctx.DrawRectangle(p[1], null, new Rect(x, 0, 1, Bounds.Height));
        ctx.DrawRectangle(p[2], null, new Rect(Bounds.Width - 1, 0, 1, Bounds.Height));
        if (Knob() is { } k) ctx.DrawRectangle(p[3], null, new RoundedRect(k.knob, KnobWidth / 2));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (!_bar || p.X < Bounds.Width - Bar || Knob() is not { } k) return;
        e.Handled = true;
        if (k.knob.Contains(p))
        {
            _drag = true;
            _dragStartY = p.Y;
            _dragStartOffset = _sv.Offset.Y;
            e.Pointer.Capture(this);
        }
        else
        {
            var page = _sv.Viewport.Height * 0.9;
            _sv.Offset = new Vector(0, Math.Clamp(_sv.Offset.Y + (p.Y < k.knob.Top ? -page : page), 0, _sv.Extent.Height - _sv.Viewport.Height));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_drag || Knob() is not { } k) return;
        var travel = k.slot - k.length;
        if (travel <= 0) return;
        var dy = e.GetPosition(this).Y - _dragStartY;
        var range = _sv.Extent.Height - _sv.Viewport.Height;
        _sv.Offset = new Vector(0, Math.Clamp(_dragStartOffset + dy * range / travel, 0, range));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        e.Pointer.Capture(null);
    }
}

/// `SettingsView`.
public sealed class SettingsRoot : Decorator, IFlexible
{
    private readonly State<string> _search = new("");

    public SettingsRoot()
    {
        var model = SettingsModel.Shared;
        Control body = HStack(0,
                View(() => Sidebar(model)).Frame(width: 250),
                Rectangle().Fill(TGS.Separator).Frame(width: 1),
                Detail(model))
            .Scheme(Scheme.Light);
        Child = new UnderTitleSeparator(body);
    }

    public bool IsFlexible(Orientation axis) => true;

    // MARK: Sidebar

    private Control Sidebar(SettingsModel model)
    {
        var search = _search;
        var rows = new List<Control?> { Profile(model) };
        foreach (var group in SettingsSections.SidebarGroups)
        {
            var filtered = group.Where(s => search.Value.Length == 0 ||
                s.Title().Contains(search.Value, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (filtered.Count == 0) continue;
            rows.Add(Rectangle().Fill(TGS.Separator).Frame(height: 1).PaddingV(6).PaddingLeading(56));
            foreach (var s in filtered) rows.Add(SidebarRow(model, s));
        }

        var field = Keep("settings.search", () =>
        {
            var f = new Field("Tìm kiếm", _search, FieldStyle.Plain, font: SFont.System(13));
            f.Box.CaretBrush = new ImmutableSolidColorBrush(SColor.Accent.Resolve(Scheme.Light));
            f.AttachedToVisualTree += (_, _) => f.FocusField();
            return f;
        });

        return VStack(0,
                HStack(6, Image("magnifyingglass").Font(11).Foreground(TGS.Secondary), field)
                    .PaddingH(10).Frame(height: 28)
                    .Background(RoundedRectangle(14).Fill(SColor.Hex(0xF1F1F4)))
                    .Padding(10),
                ScrollView(VStack(0, rows.ToArray())))
            .Background(SColor.White);
    }

    /// An explicit line box for a `Text`, where the Kit's font metrics differ from AppKit's.
    private static SText Line(SText t, double height) { t.LineHeight = height; return t; }

    private static Control Profile(SettingsModel model)
    {
        var on = model.Section == SettingsSection.Accounts;
        var n = Platforms.All.Count(p => AppModel.Shared.Session(p).AuthState == AuthState.LoggedIn);
        return HStack(12,
                new AppIconTile(50),
                // SwiftUI offers the text column half of what is left beside the icon (the Spacer takes the rest), so the
                // caption wraps after "đã"; the Kit's HStack gives it all of it. Capped to reproduce the capture.
                VStack(HAlign.Leading, 2,
                        // AppKit's line boxes (ceil(ascent) + ceil(descent)): 19 pt at 15 pt, 15 pt at 12 pt; the Kit's are a point shorter.
                        Line(Text("ChatSpace").Font(15, Weight.Semibold), 19),
                        Line(Text($"{n}/{Platforms.All.Length} nền tảng đã đăng nhập").Font(12).Foreground(TGS.Secondary), 15))
                    .Frame(maxWidth: 96, alignment: Alignment.Leading),
                Spacer())
            .PaddingH(14).PaddingV(8)
            .Background(on ? TGS.Accent : SColor.Clear)
            .Foreground(on ? SColor.White : SColor.Primary)
            .OnTap(() => model.Section = SettingsSection.Accounts);
    }

    private static Control SidebarRow(SettingsModel model, SettingsSection s)
    {
        var on = model.Section == s;
        var badge = Badge(s);
        return HStack(12,
                RoundedRectangle(7, true).Fill(s.Color())
                    .Frame(28, 28)
                    .Overlay(Image(s.Icon()).Font(14, Weight.Medium).Foreground(SColor.White)),
                Text(s.Title()).Font(13.5),
                Spacer(),
                badge != null
                    ? Text(badge).Font(11, Weight.Semibold).Foreground(on ? TGS.Accent : SColor.White)
                        .PaddingH(6).Frame(minHeight: 18)
                        .Background(Capsule().Fill(on ? SColor.White : TGS.Accent))
                    : null)
            .PaddingH(14).Frame(height: 40)
            .Background(on ? TGS.Accent : SColor.Clear)
            .Foreground(on ? SColor.White : SColor.Primary)
            .OnTap(() => model.Section = s);
    }

    private static string? Badge(SettingsSection s) => s switch
    {
        SettingsSection.Ignored => Workspace.Shared.Ignored.IsEmpty ? null : Workspace.Shared.Ignored.Count.ToString(),
        SettingsSection.Groups => Workspace.Shared.Groups.IsEmpty ? null : Workspace.Shared.Groups.Count.ToString(),
        _ => null,
    };

    // MARK: Detail

    private static Control Detail(SettingsModel model) =>
        new LegacyScrollHost(View(() =>
            {
                var section = model.Section;
                return VStack(HAlign.Leading, 0,
                        Text(section.Title()).Font(20, Weight.Semibold).PaddingH(22).PaddingTop(18).PaddingBottom(4),
                        Pane(section))
                    .FillWidth(Alignment.Leading)
                    .PaddingBottom(24);
            }))
            .Background(TGS.Background);

    private static Control Pane(SettingsSection s) => s switch
    {
        SettingsSection.Accounts => AccountsSettings.Make(),
        SettingsSection.Notifications => new NotificationSettings(),
        SettingsSection.Storage => new StorageSettingsPane(),
        SettingsSection.Groups => GroupsSettings.Make(),
        SettingsSection.Ignored => IgnoredSettings.Make(),
        SettingsSection.Bubbles => BubbleSettingsPane.Make(),
        SettingsSection.Translation => TranslationSettingsPane.Make(),
        SettingsSection.Composer => ComposerSettingsPane.Make(),
        SettingsSection.Telegram => new TelegramAPISettings(),
        _ => AssetsSettings.Make(),
    };
}

// MARK: Building blocks

public static class SettingsKit
{
    /// A white rounded card with Telegram's uppercase grey header and footer.
    public static Control TGSection(string? header, string? footer, params Control?[] content) =>
        VStack(HAlign.Leading, 6,
                header != null ? Text(header.ToUpperInvariant()).Font(11.5).Foreground(TGS.Secondary).PaddingH(14) : null,
                VStack(0, content).Background(RoundedRectangle(10, true).Fill(SColor.White)),
                footer != null ? FootText(footer).PaddingH(14).FixedSize(false, true) : null)
            .PaddingH(22).PaddingTop(16);

    /// Section footnote: 11.5pt grey. AppKit's line box for this size (13.75pt) is tighter than the Kit's default.
    public static SText FootText(string text)
    {
        var t = Text(text).Font(11.5).Foreground(TGS.Secondary);
        t.LineHeight = 13.75;
        return t;
    }

    /// One row inside a TGSection; rows after the first get a hairline separator.
    public static Control TGRow(string title, Control? trailing = null, string? icon = null, SColor? iconColor = null,
        string? subtitle = null, bool first = false, SColor? titleColor = null) =>
        HStack(12,
                icon != null
                    ? RoundedRectangle(6, true).Fill(iconColor ?? TGS.Accent).Frame(24, 24)
                        .Overlay(Image(icon).Font(12, Weight.Medium).Foreground(SColor.White))
                    : null,
                VStack(HAlign.Leading, 1,
                    Text(title).Font(13.5).Foreground(titleColor ?? SColor.Primary),
                    subtitle != null ? Text(subtitle).Font(11.5).Foreground(TGS.Secondary).LineLimit(2) : null),
                Spacer(8),
                trailing)
            .PaddingH(14).Frame(minHeight: 42)
            .Overlay(Alignment.Top, !first ? Rectangle().Fill(TGS.Separator).Frame(height: 1).PaddingLeading(icon == null ? 14 : 50) : null);

    /// A row built by hand (icon + two lines + trailing buttons) with the hairline that starts at `inset`.
    public static Control HairlineRow(Control row, int index, double inset) =>
        row.Overlay(Alignment.Top, index > 0 ? Rectangle().Fill(TGS.Separator).Frame(height: 1).PaddingLeading(inset) : null);

    /// `Button(…).buttonStyle(.link)`: blue text, optionally recoloured (`.foregroundStyle(.red)`). A disabled
    /// button keeps an explicit colour on macOS, so it is only made inert.
    public static Control LinkButton(string title, Action action, SColor? color = null, bool enabled = true)
    {
        var b = Button(action, Text(title).Font(13).Foreground(color ?? TGS.Link).LineLimit(1));
        if (!enabled) b.AllowsHitTesting(false);
        else b.Cursor(StandardCursorType.Hand);
        return b;
    }

    public static Control Switch(Func<bool> get, Action<bool> set) => new ToggleView(new Binding<bool>(get, set), true, ControlSize.Small);

    /// Telegram's stepped slider: a blue track with dots and the labels of each stop.
    public static Control TickSlider<V>(IReadOnlyList<(V value, string label)> stops, Binding<V> value)
    {
        int IndexOf() => Math.Max(0, stops.ToList().FindIndex(s => EqualityComparer<V>.Default.Equals(s.value, value.Value)));
        var labels = View(() =>
        {
            var index = IndexOf();
            var items = new List<Control?>();
            for (var i = 0; i < stops.Count; i++)
            {
                items.Add(Text(stops[i].label).Font(11.5).Foreground(i == index ? TGS.Accent : TGS.Secondary));
                if (i < stops.Count - 1) items.Add(Spacer());
            }
            return HStack(0, items.ToArray());
        });
        var track = new TickTrack(stops.Count, IndexOf, i => value.Value = stops[Math.Clamp(i, 0, stops.Count - 1)].value);
        return VStack(6, labels, track).PaddingH(14).PaddingV(10);
    }

    /// `ByteCountFormatter(.file)`: decimal units, KB without decimals, MB up to one, larger up to two.
    public static string FormatBytes(double bytes)
    {
        var n = (long)bytes;
        if (n == 0) return "Zero KB";
        if (n == 1) return "1 byte";
        if (n < 1000) return $"{n} bytes";
        string[] units = ["KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
        var u = 0;
        var v = n / 1000.0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        int Digits(int unit) => unit == 0 ? 0 : unit == 1 ? 1 : 2;
        var rounded = Math.Round(v, Digits(u), MidpointRounding.ToEven);
        if (rounded >= 1000 && u < units.Length - 1)
        {
            u++;
            v /= 1000;
            rounded = Math.Round(v, Digits(u), MidpointRounding.ToEven);
        }
        return rounded.ToString("0." + new string('#', Digits(u)), CultureInfo.InvariantCulture) + " " + units[u];
    }

    /// `Date.formatted(.relative(presentation: .named).locale(vi_VN))`.
    public static string ViRelative(DateTime date)
    {
        var seconds = (DateTime.Now - date).TotalSeconds;
        if (seconds < 0.5) return "bây giờ";
        if (seconds < 60) return $"{(int)Math.Round(seconds)} giây trước";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{(int)Math.Round(minutes)} phút trước";
        var hours = minutes / 60;
        if (hours < 24) return $"{(int)Math.Round(hours)} giờ trước";
        var days = hours / 24;
        var d = (int)Math.Round(days);
        if (days < 7 || d < 7)
            return d switch { <= 1 => "Hôm qua", 2 => "Hôm kia", _ => $"{d} ngày trước" };
        if (days < 31)
        {
            var w = (int)Math.Floor(days / 7);
            return w <= 1 ? "tuần trước" : $"{w} tuần trước";
        }
        var months = (int)Math.Floor(days / 30.44);
        if (months < 12) return months <= 1 ? "tháng trước" : $"{months} tháng trước";
        var years = (int)Math.Floor(days / 365.25);
        return years <= 1 ? "năm ngoái" : $"{years} năm trước";
    }

    /// `chevron.up.chevron.down` (missing from the exported symbols): an up and a down chevron.
    internal static Control UpDownChevrons(double size) =>
        VStack(0.5,
            Image("chevron.down").Font(size, Weight.Bold).RotationEffect(180),
            Image("chevron.down").Font(size, Weight.Bold));

    /// A `Picker` in the default pop-up style, `.labelsHidden().frame(width:)`.
    public static Control PopUp<T>(Binding<T> selection, IReadOnlyList<(T value, string title)> options, double width) =>
        View(() =>
        {
            var cur = selection.Value;
            var title = options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.value, cur)).title ?? "";
            Control label = HStack(6,
                    Text(title).Font(13).LineLimit(1).PaddingLeading(8),
                    Spacer(0),
                    RoundedRectangle(4).Fill(SColor.Accent)
                        .Overlay(UpDownChevrons(9).Foreground(SColor.White))
                        .Frame(16, 16).PaddingTrailing(3))
                .Frame(height: 22)
                .Background(ZStack(
                    RoundedRectangle(5).Fill(SColor.Black.Opacity(0.18)).Offset(0, 0.5),
                    RoundedRectangle(5).Fill(SColor.White).Overlay(RoundedRectangle(5).StrokeBorder(SColor.Black.Opacity(0.12), 0.5))));
            Control? anchor = null;
            anchor = Button(() => Menus.Popup(anchor!, options.Select(o =>
            {
                var v = o.value;
                return MenuEntry.Item(o.title, () => selection.Value = v,
                    EqualityComparer<T>.Default.Equals(v, Tracker.Untracked(() => selection.Value)));
            })), label);
            return anchor;
        }).Frame(width: width);

    /// `Stepper("\(n)", value:in:)`.
    public static Control Stepper(string label, Binding<int> value, int min, int max) =>
        HStack(6,
            Text(label),
            VStack(0,
                    Button(() => value.Value = Math.Min(max, Tracker.Untracked(() => value.Value) + 1),
                        Image("chevron.down").Font(7, Weight.Bold).RotationEffect(180).Frame(13, 10)),
                    Rectangle().Fill(SColor.Black.Opacity(0.1)).Frame(13, 0.5),
                    Button(() => value.Value = Math.Max(min, Tracker.Untracked(() => value.Value) - 1),
                        Image("chevron.down").Font(7, Weight.Bold).Frame(13, 10)))
                .Background(RoundedRectangle(4).Fill(SColor.White).Overlay(RoundedRectangle(4).StrokeBorder(SColor.Black.Opacity(0.15), 0.5))));
}

/// The track of a `TickSlider`: drag anywhere to snap to the nearest stop.
internal sealed class TickTrack : Control, IFlexible
{
    private readonly int _count;
    private readonly Func<int> _index;
    private readonly Action<int> _set;
    private readonly Watch _watch;
    private bool _drag;

    public TickTrack(int count, Func<int> index, Action<int> set)
    {
        _count = count;
        _index = index;
        _set = set;
        _watch = Watch.Effect(() => { _ = _index(); InvalidateVisual(); });
    }

    public bool IsFlexible(Orientation axis) => axis == Orientation.Horizontal;

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 0 : available.Width, 22);

    private void Choose(Point p)
    {
        var step = _count > 1 ? Bounds.Width / (_count - 1) : 0;
        if (step <= 0) return;
        var i = (int)Math.Round(p.X / step, MidpointRounding.AwayFromZero);
        _set(Math.Clamp(i, 0, _count - 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = true;
        e.Pointer.Capture(this);
        Choose(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag) Choose(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = false;
        e.Pointer.Capture(null);
    }

    public override void Render(DrawingContext ctx)
    {
        var index = Tracker.Untracked(_index);
        var w = Bounds.Width;
        var cy = Bounds.Height / 2;
        var step = _count > 1 ? w / (_count - 1) : 0;
        var grey = new ImmutableSolidColorBrush(TGS.Separator.Resolve(Scheme.Light));
        var blue = new ImmutableSolidColorBrush(TGS.Accent.Resolve(Scheme.Light));
        ctx.DrawRectangle(grey, null, new Rect(0, cy - 1.5, w, 3), 1.5, 1.5);
        if (step * index > 0) ctx.DrawRectangle(blue, null, new Rect(0, cy - 1.5, step * index, 3), 1.5, 1.5);
        for (var i = 0; i < _count; i++)
            ctx.DrawEllipse(i <= index ? blue : grey, null, new Point(step * i, cy), 3.5, 3.5);
        var knob = new Rect(step * index - 10, cy - 10, 20, 20);
        ctx.DrawRectangle(Brushes.White, null, knob, 10, 10,
            new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 1, Blur = 4, Color = Color.FromArgb(0x40, 0, 0, 0) }));
    }
}

/// Telegram's storage ring.
internal sealed class StorageDonut : Control, IFlexible
{
    private readonly (SColor color, double value)[] _parts;

    public StorageDonut((SColor, double)[] parts) { _parts = parts; }

    public bool IsFlexible(Orientation axis) => true;

    protected override Size MeasureOverride(Size a) =>
        new(double.IsInfinity(a.Width) ? 0 : a.Width, double.IsInfinity(a.Height) ? 0 : a.Height);

    public override void Render(DrawingContext ctx)
    {
        // `.padding(7)` around a circle stroked 14 pt wide (the stroke is centred on the circle's edge).
        const double lineWidth = 14, pad = 7;
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - pad;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        ctx.DrawEllipse(null, new Pen(new ImmutableSolidColorBrush(TGS.Separator.Resolve(Scheme.Light)), lineWidth), c, radius, radius);
        var total = Math.Max(_parts.Sum(p => p.value), 1);
        double acc = 0;
        foreach (var (color, value) in _parts)
        {
            var start = acc / total;
            var end = start + value / total;
            acc += value;
            if (end <= start) continue;
            var from = start + 0.004;
            var to = Math.Max(from, end - 0.004);
            if (to <= from) continue;
            Point At(double f)
            {
                var a = f * 2 * Math.PI;
                return new Point(c.X + radius * Math.Sin(a), c.Y - radius * Math.Cos(a));
            }
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(At(from), false);
                s.ArcTo(At(to), new Size(radius, radius), 0, (to - from) > 0.5, SweepDirection.Clockwise);
                s.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(color.Resolve(Scheme.Light)), lineWidth, lineCap: PenLineCap.Round), g);
        }
    }
}

// MARK: Panes

internal static class AccountsSettings
{
    public static Control Make() => View(() =>
    {
        var model = AppModel.Shared;
        var rows = Platforms.All.Select((p, i) =>
        {
            var s = model.Session(p);
            return SettingsKit.HairlineRow(
                HStack(12,
                        new PlatformGlyph(p, 30),
                        VStack(HAlign.Leading, 1,
                            Text(p.DisplayName()).Font(13.5),
                            Text(s.SelfInfo?.Name ?? StateText(s.AuthState)).Font(11.5).Foreground(TGS.Secondary)),
                        Spacer(),
                        s.AuthState == AuthState.LoggedIn ? SettingsKit.LinkButton("Đăng xuất", s.Logout, TGS.Red) : null,
                        SettingsKit.LinkButton("Khởi động lại", s.Restart),
                        SettingsKit.LinkButton("Log", () => KitShell.Open(Path.Combine(p.DataDirectory(), "bridge.log"))))
                    .PaddingH(14).Frame(minHeight: 50),
                i, 56);
        }).Cast<Control?>().ToArray();
        return SettingsKit.TGSection("Nền tảng", "Phiên đăng nhập và dữ liệu chỉ lưu cục bộ trong %APPDATA%\\ChatSpace.", rows);
    });

    private static string StateText(AuthState s) => s switch
    {
        AuthState.LoggedIn => "Đã đăng nhập",
        AuthState.Connecting => "Đang kết nối…",
        AuthState.AwaitingQr => "Chờ quét mã QR",
        AuthState.AwaitingCode or AuthState.AwaitingPassword or AuthState.AwaitingInput => "Đang đăng nhập",
        AuthState.Error => "Lỗi — xem log",
        _ => "Chưa đăng nhập",
    };
}

internal sealed class NotificationSettings : Decorator
{
    private readonly State<string> _status = new("…");

    public NotificationSettings()
    {
        Child = View(() => SettingsKit.TGSection("Thông báo hệ thống",
            "Âm báo, nhóm theo cuộc trò chuyện, trả lời nhanh và tự gỡ khi đã đọc — giống app gốc. Chat đã tắt thông báo không hiện banner và không tính vào huy hiệu.",
            SettingsKit.TGRow("Trạng thái", icon: "bell.fill", iconColor: SColor.Hex(0xFF3B30), subtitle: _status.Value, first: true,
                trailing: SettingsKit.LinkButton("Mở Cài đặt hệ thống", () =>
                    KitShell.OpenUrl(OperatingSystem.IsWindows()
                        ? "ms-settings:notifications"
                        : "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=vn.chatspace.app")))));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _status.Value = Notifier.Shared.StatusDescription();
    }
}

internal sealed class StorageSettingsPane : Decorator
{
    private readonly State<ImmutableDictionary<Platform, PlatformSession.StorageUsage>> _usage = new(ImmutableDictionary<Platform, PlatformSession.StorageUsage>.Empty);
    private readonly State<Platform?> _clearing = new(null);

    public StorageSettingsPane()
    {
        Child = View(Body);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = Reload();
    }

    private static SColor ColorFor(Platform p) => p switch
    {
        Platform.Zalo => SColor.Hex(0x0068FF),
        Platform.Messenger => SColor.Hex(0xA033FF),
        Platform.Whatsapp => SColor.Hex(0x25D366),
        _ => SColor.Hex(0x2AABEE),
    };

    private async Task Reload()
    {
        foreach (var p in Platforms.All)
            if (await AppModel.Shared.Session(p).GetStorageUsage() is { } u)
                _usage.Value = _usage.Value.SetItem(p, u);
    }

    private async void Clear(Platform p)
    {
        _clearing.Value = p;
        await AppModel.Shared.Session(p).ClearStorage();
        await Reload();
        _clearing.Value = null;
    }

    private Control Body()
    {
        var ws = Workspace.Shared;
        var usage = _usage.Value;
        var clearing = _clearing.Value;
        var total = usage.Values.Sum(u => u.MediaBytes + u.DatabaseBytes);

        var parts = Platforms.All.Select(p =>
            (ColorFor(p), usage.TryGetValue(p, out var u) ? u.MediaBytes + u.DatabaseBytes : 0.0)).ToArray();
        var legend = VStack(HAlign.Leading, 8, Platforms.All.Select(p => (Control?)HStack(8,
            Circle().Fill(ColorFor(p)).Frame(9, 9),
            Text(p.DisplayName()).Font(12.5),
            Spacer(),
            usage.TryGetValue(p, out var u)
                ? Text($"{SettingsKit.FormatBytes(u.MediaBytes)} media · {SettingsKit.FormatBytes(u.DatabaseBytes)} dữ liệu").Font(11.5).Foreground(TGS.Secondary)
                : Text("—").Foreground(TGS.Secondary))).ToArray());

        var cacheRows = Platforms.All.Where(p => p != Platform.Messenger).Select(p =>
        {
            usage.TryGetValue(p, out var u);
            return (Control?)SettingsKit.TGRow($"Xoá bộ nhớ đệm {p.DisplayName()}", icon: "trash.fill", iconColor: ColorFor(p),
                subtitle: u != null ? $"{u.MediaFiles} tệp · {SettingsKit.FormatBytes(u.MediaBytes)}" : null,
                trailing: clearing == p
                    ? new ProgressView { Size = ControlSize.Small }
                    : SettingsKit.LinkButton("Xoá", () => Clear(p), TGS.Red, enabled: (u?.MediaBytes ?? 0) != 0));
        }).ToList();

        var usedCard = new List<Control?>
        {
            HStack(20,
                    new StorageDonut(parts).Frame(120, 120)
                        .Overlay(VStack(0,
                            Text(SettingsKit.FormatBytes(total)).Font(15, Weight.Semibold),
                            Text("tổng").Font(11).Foreground(TGS.Secondary))),
                    legend)
                .Padding(14),
        };
        usedCard.AddRange(cacheRows);

        return VStack(HAlign.Leading, 0,
            SettingsKit.TGSection("Bộ nhớ đã dùng",
                "Ảnh, video và tệp luôn được tải từ cloud khi xem; chỉ khi nền tảng bắt buộc tải về (WhatsApp mã hoá đầu cuối, Telegram) thì mới lưu vào bộ nhớ đệm, sau khi nén.",
                usedCard.ToArray()),
            SettingsKit.TGSection("Giữ media",
                "Ảnh, video và tệp không mở lại trong khoảng thời gian này sẽ bị xoá khỏi máy; chúng vẫn còn trên cloud và được tải lại khi cần.",
                SettingsKit.TickSlider([(3, "3 ngày"), (7, "1 tuần"), (30, "1 tháng"), (0, "Mãi mãi")],
                    new Binding<int>(() => ws.Storage.KeepMediaDays, v => ws.Storage = ws.Storage with { KeepMediaDays = v }))),
            SettingsKit.TGSection("Dung lượng bộ nhớ đệm tối đa", "Khi vượt mức, các tệp lâu không dùng nhất bị xoá trước.",
                SettingsKit.TickSlider([(1024, "1 GB"), (2048, "2 GB"), (5120, "5 GB"), (20480, "20 GB"), (0, "Không giới hạn")],
                    new Binding<int>(() => ws.Storage.MaxCacheMB, v => ws.Storage = ws.Storage with { MaxCacheMB = v }))),
            SettingsKit.TGSection("Tin nhắn lưu cục bộ",
                "Số tin nhắn mỗi đoạn chat được giữ trên máy (nhóm Zalo, WhatsApp). Tin cũ hơn được tải lại khi cuộn lên. Chat 1-1 của Zalo luôn giữ đủ vì Zalo không cho tải lại lịch sử cũ.",
                SettingsKit.TickSlider([(200, "200"), (1000, "1.000"), (5000, "5.000"), (0, "Không giới hạn")],
                    new Binding<int>(() => ws.Storage.MaxStoredMessagesPerChat, v => ws.Storage = ws.Storage with { MaxStoredMessagesPerChat = v }))),
            SettingsKit.TGSection("Siêu nén",
                "Ảnh tải về được chuyển sang HEIC chất lượng 60%, cạnh dài tối đa 1600 px — thường nhỏ hơn 5–10 lần mà vẫn nét trên màn hình.",
                SettingsKit.TGRow("Nén ảnh khi tải về", icon: "arrow.down.right.and.arrow.up.left", iconColor: SColor.Hex(0x34C759), first: true,
                    trailing: SettingsKit.Switch(() => ws.Storage.CompressImages, v => ws.Storage = ws.Storage with { CompressImages = v }))),
            SettingsKit.TGSection("Tự động tải media", "Media không tự tải sẽ hiện ảnh xem trước; bấm vào để tải.",
                SettingsKit.TGRow("Ảnh", icon: "photo.fill", iconColor: SColor.Hex(0x3390EC), first: true,
                    trailing: SettingsKit.Switch(() => ws.Storage.AutoDownloadPhotos, v => ws.Storage = ws.Storage with { AutoDownloadPhotos = v })),
                SettingsKit.TGRow("Video", icon: "video.fill", iconColor: SColor.Hex(0xFF9500),
                    trailing: SettingsKit.Switch(() => ws.Storage.AutoDownloadVideos, v => ws.Storage = ws.Storage with { AutoDownloadVideos = v })),
                SettingsKit.TGRow("Tệp", icon: "doc.fill", iconColor: SColor.Hex(0x5E5CE6),
                    trailing: SettingsKit.Switch(() => ws.Storage.AutoDownloadFiles, v => ws.Storage = ws.Storage with { AutoDownloadFiles = v })),
                SettingsKit.TickSlider([(1.0, "1 MB"), (5.0, "5 MB"), (10.0, "10 MB"), (50.0, "50 MB"), (100.0, "100 MB")],
                    new Binding<double>(() => ws.Storage.AutoDownloadMaxMB, v => ws.Storage = ws.Storage with { AutoDownloadMaxMB = v }))));
    }
}

internal static class GroupsSettings
{
    public static Control Make() => View(() =>
    {
        var ws = Workspace.Shared;
        var model = AppModel.Shared;
        var rows = new List<Control?>();
        var groups = ws.Groups;
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            rows.Add(SettingsKit.HairlineRow(
                HStack(12,
                        new GroupIcon(g, 28),
                        VStack(HAlign.Leading, 1,
                            Text(g.Name).Font(13.5),
                            Text($"{string.Join(", ", g.Keywords)} · {model.GroupRefs(g).Count} đoạn chat")
                                .Font(11.5).Foreground(TGS.Secondary)),
                        Spacer(),
                        SettingsKit.LinkButton("Sửa", () => GroupEditor.Present(g, SettingsWindow.Window)),
                        SettingsKit.LinkButton("Xoá", () => ws.Delete(g.Id), TGS.Red))
                    .PaddingH(14).Frame(minHeight: 46),
                i, 54));
        }
        rows.Add(Button(() => GroupEditor.Present(new CustomGroup { Name = "", Keywords = [] }, SettingsWindow.Window),
            SettingsKit.TGRow("Tạo nhóm mới", icon: "plus", iconColor: TGS.Accent, first: groups.IsEmpty, titleColor: TGS.Accent).ContentShape()));

        return VStack(HAlign.Leading, 0,
            SettingsKit.TGSection("ChatSpace", "Mục “Chưa đọc” trên thanh bên gom tin chưa đọc của mọi nền tảng.",
                SettingsKit.TGRow("Hiện mục Chưa đọc", icon: "tray.full.fill", iconColor: SColor.Hex(0x5E5CE6), first: true,
                    trailing: SettingsKit.Switch(() => ws.ShowInbox, v => ws.ShowInbox = v))),
            SettingsKit.TGSection("Nhóm tuỳ chỉnh",
                "Mỗi nhóm là một mục trên thanh bên, gom mọi đoạn chat có tên chứa từ khoá (VD “VF” gom “VF 1”, “VF-2”, “VF3”…).",
                rows.ToArray()));
    });
}

internal static class IgnoredSettings
{
    public static Control Make() => View(() =>
    {
        var ws = Workspace.Shared;
        var rows = new List<Control?>();
        if (ws.Ignored.IsEmpty)
            rows.Add(SettingsKit.TGRow("Chưa bỏ qua đoạn chat nào", subtitle: "Chuột phải vào một đoạn chat → “Bỏ qua cho lần sau”.", first: true));
        var items = ws.Ignored.OrderByDescending(i => i.IgnoredAt).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            rows.Add(SettingsKit.HairlineRow(
                HStack(12,
                        new PlatformGlyph(item.Ref.Platform, 26),
                        VStack(HAlign.Leading, 1,
                            Text(item.Title).Font(13.5).LineLimit(1),
                            Text($"{item.Ref.Platform.DisplayName()} · bỏ qua {SettingsKit.ViRelative(item.IgnoredAt)}")
                                .Font(11.5).Foreground(TGS.Secondary)),
                        Spacer(),
                        SettingsKit.LinkButton("Khôi phục", () => ws.Restore(item.Ref)))
                    .PaddingH(14).Frame(minHeight: 46),
                i, 52));
        }
        if (ws.Ignored.Count > 1)
            rows.Add(Button(() => ws.Ignored = ImmutableList<IgnoredChat>.Empty,
                SettingsKit.TGRow("Khôi phục tất cả", icon: "arrow.uturn.backward", iconColor: SColor.Hex(0xFF9500), titleColor: TGS.Accent).ContentShape()));
        return SettingsKit.TGSection("Đoạn chat đã bỏ qua",
            "Các đoạn chat này không hiện trong mục Chưa đọc của ChatSpace và không tính vào huy hiệu của nó. Chúng vẫn hiện bình thường trong giao diện của từng nền tảng.",
            rows.ToArray());
    });
}

internal static class BubbleSettingsPane
{
    public static Control Make() => View(() =>
    {
        var ws = Workspace.Shared;
        return VStack(HAlign.Leading, 0,
            SettingsKit.TGSection("Bong bóng chat",
                "Giống Telegram trên Android: mỗi tin nhắn mới làm bong bóng của đoạn chat bật lên ở mép màn hình, trên mọi ứng dụng, kèm nội dung tin nhắn. Bấm để mở khung chat nhỏ, kéo để di chuyển, kéo xuống dấu ✕ để đóng.",
                SettingsKit.TGRow("Hiện bong bóng", icon: "bubble.left.and.bubble.right.fill", iconColor: SColor.Hex(0x32ADE6), first: true,
                    trailing: SettingsKit.PopUp(
                        new Binding<BubbleSettings.TriggerKind>(() => ws.Bubbles.Trigger, v => ws.Bubbles = ws.Bubbles with { Trigger = v }),
                        [(BubbleSettings.TriggerKind.NewMessages, "Khi có tin nhắn mới"),
                         (BubbleSettings.TriggerKind.Manual, "Chỉ khi tôi mở"),
                         (BubbleSettings.TriggerKind.Off, "Tắt")], 180)),
                SettingsKit.TGRow("Hiện nội dung tin nhắn cạnh bong bóng", icon: "text.bubble.fill", iconColor: SColor.Hex(0xFF9500),
                    trailing: SettingsKit.Switch(() => ws.Bubbles.ShowPreview, v => ws.Bubbles = ws.Bubbles with { ShowPreview = v })),
                SettingsKit.TGRow("Chỉ khi ChatSpace ở chế độ nền", icon: "macwindow", iconColor: SColor.Hex(0x8E8E93),
                    trailing: SettingsKit.Switch(() => ws.Bubbles.OnlyInBackground, v => ws.Bubbles = ws.Bubbles with { OnlyInBackground = v })),
                SettingsKit.TGRow("Cả tin nhắn nhóm", icon: "person.3.fill", iconColor: SColor.Hex(0x34C759),
                    trailing: SettingsKit.Switch(() => ws.Bubbles.IncludeGroups, v => ws.Bubbles = ws.Bubbles with { IncludeGroups = v })),
                SettingsKit.TGRow("Số bong bóng tối đa", icon: "square.stack.fill", iconColor: SColor.Hex(0x5E5CE6),
                    trailing: SettingsKit.Stepper(ws.Bubbles.MaxBubbles.ToString(),
                        new Binding<int>(() => ws.Bubbles.MaxBubbles, v => ws.Bubbles = ws.Bubbles with { MaxBubbles = v }), 1, 8))),
            SettingsKit.TGSection(null, "Chuột phải vào bất kỳ đoạn chat nào → “Mở bong bóng chat” để mở thủ công.",
                Button(TryBubble,
                    SettingsKit.TGRow("Thử bong bóng với đoạn chat gần nhất", icon: "sparkles", iconColor: SColor.Hex(0xFF9500), first: true, titleColor: TGS.Accent).ContentShape())));
    });

    private static void TryBubble()
    {
        var m = AppModel.Shared;
        ChatRef? r = m.InboxRefs.Count > 0 ? m.InboxRefs[0] : null;
        r ??= Platforms.All.Select(p => m.Session(p).Chats.FirstOrDefault() is { } c ? new ChatRef(p, c.Id) : (ChatRef?)null).FirstOrDefault(x => x != null);
        if (r is { } chat) ChatHeads.Shared.Show(chat, false);
    }
}

internal static class TranslationSettingsPane
{
    public static Control Make() => View(() =>
    {
        var ws = Workspace.Shared;
        var model = AppModel.Shared;
        var perChat = ws.Translation.ChatLanguages.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

        var chatRows = new List<Control?>();
        if (perChat.Count == 0) chatRows.Add(SettingsKit.TGRow("Mọi đoạn chat gửi nguyên văn", first: true));
        for (var i = 0; i < perChat.Count; i++)
        {
            var item = perChat[i];
            var chatRef = ParseRef(item.Key);
            chatRows.Add(SettingsKit.HairlineRow(
                HStack(12,
                        chatRef is { } cr ? new PlatformGlyph(cr.Platform, 26) : null,
                        VStack(HAlign.Leading, 1,
                            Text(Title(model, chatRef)).Font(13.5).LineLimit(1),
                            Text($"Gửi bằng {TranslationLanguages.Name(item.Value)}").Font(11.5).Foreground(TGS.Secondary)),
                        Spacer(),
                        SettingsKit.LinkButton("Gửi nguyên văn", () =>
                            ws.Translation = ws.Translation with { ChatLanguages = ws.Translation.ChatLanguages.Remove(item.Key) }))
                    .PaddingH(14).Frame(minHeight: 46),
                i, 52));
        }

        return VStack(HAlign.Leading, 0,
            SettingsKit.TGSection("Ngôn ngữ",
                "Nút dịch nằm cạnh nút bày tỏ cảm xúc khi rê chuột vào tin nhắn; bản dịch hiện in nghiêng ngay trong tin nhắn. Tin bạn gửi giữ nguyên ngôn ngữ này, trừ khi chọn ngôn ngữ khác ở nút 文A trong khung soạn tin.",
                SettingsKit.TGRow("Ngôn ngữ của bạn", icon: "translate", iconColor: SColor.Hex(0x30B0C7), first: true,
                    trailing: SettingsKit.PopUp(
                        new Binding<string>(() => ws.Translation.Language, v => ws.Translation = ws.Translation with { Language = v }),
                        TranslationLanguages.All.Select(l => (l.Code, l.Name)).ToList(), 210)),
                SettingsKit.TGRow("Trả lời bằng ngôn ngữ của tin được trả lời", icon: "arrowshape.turn.up.left.fill", iconColor: SColor.Hex(0xFF9500),
                    subtitle: "VD trả lời một tin tiếng Trung thì tự dịch sang tiếng Trung rồi mới gửi",
                    trailing: SettingsKit.Switch(() => ws.Translation.MatchReplyLanguage, v => ws.Translation = ws.Translation with { MatchReplyLanguage = v }))),
            SettingsKit.TGSection("Ngôn ngữ gửi theo đoạn chat", "Chọn ở nút 文A trong khung soạn tin của từng đoạn chat.", chatRows.ToArray()),
            SettingsKit.TGSection("Dịch vụ",
                "Dùng Google Dịch qua địa chỉ công khai translate.googleapis.com, không cần API key. Nội dung tin được dịch sẽ được gửi tới Google. Bản dịch lưu trên máy để không phải dịch lại.",
                SettingsKit.TGRow("Bản dịch đã lưu", icon: "internaldrive.fill", iconColor: SColor.Hex(0x8E8E93),
                    subtitle: $"{Translator.Shared.Entries.Count} tin nhắn", first: true,
                    trailing: SettingsKit.LinkButton("Xoá", Translator.Shared.ClearCache, TGS.Red, enabled: !Translator.Shared.Entries.IsEmpty))));
    });

    private static ChatRef? ParseRef(string id)
    {
        var parts = id.Split('|', 2);
        return parts.Length == 2 && Platforms.Parse(parts[0]) is { } p ? new ChatRef(p, parts[1]) : null;
    }

    private static string Title(AppModel model, ChatRef? r) =>
        r is not { } cr ? "Đoạn chat" : model.Session(cr.Platform).Chats.FirstOrDefault(c => c.Id == cr.ChatId)?.Title ?? cr.ChatId;
}

internal static class ComposerSettingsPane
{
    public static Control Make() => View(() =>
    {
        var ws = Workspace.Shared;
        return SettingsKit.TGSection("Phép tính",
            "VD gõ \"Số tiền: 23500*410\" rồi Enter: trên khung soạn tin hiện bản \"23500*410 = 9.635.000\" để chọn gửi bản có kết quả (Enter lần nữa) hoặc bản gốc. Ngày tháng, số điện thoại, khoảng \"8h - 17h\" và phép tính đã có dấu = không bị nhận nhầm.",
            SettingsKit.TGRow("Nhận diện phép tính trước khi gửi", icon: "function", iconColor: SColor.Hex(0xAF52DE),
                subtitle: "Cộng, trừ, nhân (* x ×), chia, luỹ thừa ^, phần trăm %", first: true,
                trailing: SettingsKit.Switch(() => ws.MathPreview, v => ws.MathPreview = v)));
    });
}

internal sealed class TelegramAPISettings : Decorator
{
    private readonly State<string> _apiId = new(DemoData.Enabled ? "" : AppSettings.TelegramApiId);
    private readonly State<string> _apiHash = new(DemoData.Enabled ? "" : AppSettings.TelegramApiHash);

    public TelegramAPISettings()
    {
        Child = View(() =>
        {
            var idField = Keep("telegram.apiId", () => Entry(_apiId, secure: false));
            var hashField = Keep("telegram.apiHash", () => Entry(_apiHash, secure: true));
            return SettingsKit.TGSection("Ứng dụng của bạn", "Lấy tại my.telegram.org → API development tools. Lưu cục bộ trên máy này.",
                SettingsKit.TGRow("API ID", first: true, trailing: idField.Frame(width: 260)),
                SettingsKit.TGRow("API Hash", trailing: hashField.Frame(width: 260)),
                Button(Save, SettingsKit.TGRow("Lưu & khởi động lại Telegram", titleColor: TGS.Accent).ContentShape()));
        });
    }

    private static Control Entry(State<string> text, bool secure)
    {
        var f = new Field("", text, FieldStyle.Plain, secure);
        f.Box.TextAlignment = TextAlignment.Right;
        return f;
    }

    private void Save()
    {
        if (DemoData.Enabled) return;
        AppSettings.TelegramApiId = _apiId.Value.Trim();
        AppSettings.TelegramApiHash = _apiHash.Value.Trim();
        AppModel.Shared.Session(Platform.Telegram).Restart();
    }
}

internal static class AssetsSettings
{
    public static Control Make() => View(() =>
    {
        var rows = Platforms.All.Select((p, i) => SettingsKit.HairlineRow(
            HStack(12,
                    new PlatformGlyph(p, 26),
                    VStack(HAlign.Leading, 1,
                        Text(p.DisplayName()).Font(13.5),
                        Text(AppName(p) is { } name ? $"Dùng icon, âm thanh, hình ảnh từ {name}" : "Chưa cài app gốc — dùng bản vẽ lại")
                            .Font(11.5).Foreground(TGS.Secondary)),
                    Spacer())
                .PaddingH(14).Frame(minHeight: 46),
            i, 52)).Cast<Control?>().ToArray();
        return SettingsKit.TGSection("App gốc trên máy",
            "ChatSpace đọc tài nguyên trực tiếp từ các app chính thức đã cài trên máy này, không sao chép hay phân phối lại.", rows);
    });

    /// `appURL(p).lastPathComponent`: the official app's file name, or its name when it starts through a protocol.
    private static string? AppName(Platform p)
    {
        if (NativeAssets.Shared.AppLaunch(p) is not { } launch) return null;
        return Path.IsPathRooted(launch) ? Path.GetFileName(launch.TrimEnd('/', '\\')) : p.DisplayName();
    }
}
