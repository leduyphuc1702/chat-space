using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using static ChatSpace.Kit.UI;

namespace ChatSpace.Kit;

/// An AppKit-looking window: no system caption, our own traffic lights, and either a standard title bar
/// (28 pt, centred title) or a hidden one with content running to the top edge.
public class MacWindow : Window
{
    public const double TitleBarHeight = 28;
    private readonly Panel _root = new();
    private readonly Panel _sheets = new();
    private readonly TrafficLightsView _lights;
    public bool HiddenTitleBar { get; }
    /// Close hides instead (panels reused like NSPanel with `isReleasedWhenClosed = false`).
    public bool HideOnClose { get; set; }
    public Scheme Scheme { get; }

    public static Scheme SystemScheme =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? Scheme.Dark : Scheme.Light;

    public MacWindow(string title, Control content, bool hiddenTitleBar = false, Scheme? scheme = null)
    {
        Title = title;
        HiddenTitleBar = hiddenTitleBar;
        Scheme = scheme ?? SystemScheme;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        Background = new ImmutableSolidColorBrush(SColor.WindowBackground.Resolve(Scheme));
        SetValue(Env.FontProperty, SFont.Body);
        SetValue(Env.SchemeProperty, Scheme);
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ChatSpace/Assets/AppIcon.ico"))); } catch { }

        if (hiddenTitleBar) _root.Children.Add(content);
        else
        {
            var dark = Scheme == Scheme.Dark;
            var bar = ZStack(
                    ColorView(dark ? SColor.Hex(0x222222) : SColor.Hex(0xE7E7E7)),
                    Text(title).Font(13, Weight.Bold).Foreground(dark ? SColor.White.Opacity(0.55) : SColor.Black.Opacity(0.7)).LineLimit(1))
                .Frame(height: TitleBarHeight);
            var sep = ColorView(dark ? SColor.Black : SColor.Black.Opacity(0.12)).Frame(height: 1);
            _root.Children.Add(VStack(0, bar, sep, content.Fill()));
        }
        _lights = new TrafficLightsView(this) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _root.Children.Add(_lights);
        _root.Children.Add(_sheets);
        Content = _root;

        Activated += (_, _) => _lights.InvalidateVisual();
        Deactivated += (_, _) => _lights.InvalidateVisual();
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var p = e.GetPosition(this);
            if (p.Y > TitleBarHeight || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Handled) return;
            if (e.ClickCount == 2 && CanResize) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
            BeginMoveDrag(e);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Closing += (_, e) =>
        {
            if (!HideOnClose || App.Quitting) return;
            e.Cancel = true;
            Hide();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Close(); e.Handled = true; }
        };
    }

    public bool ShowZoom { get => _lights.ShowZoom; set => _lights.ShowZoom = value; }

    /// `.sheet`: a modal panel dropping from the title bar, dimming nothing (like macOS) but blocking clicks.
    public void PresentSheet(Func<Action, Control> content) => Sheets.Present(_sheets, content, Scheme);
}

/// Close / minimise / zoom drawn like macOS: coloured when the window is key or hovered, grey otherwise.
public sealed class TrafficLightsView : Control
{
    private readonly Window _window;
    private bool _hover;
    private int _pressed = -1;
    private const double D = 12, Gap = 8, Left = 20, Top = 8;
    public bool ShowZoom { get; set; } = true;

    public TrafficLightsView(Window w)
    {
        _window = w;
        Width = Left + 3 * D + 2 * Gap + 8;
        Height = Top + D + 8;
        PointerEntered += (_, _) => { _hover = true; InvalidateVisual(); };
        PointerExited += (_, _) => { _hover = false; InvalidateVisual(); };
    }

    private int Hit(Point p)
    {
        for (var i = 0; i < 3; i++)
        {
            var c = new Point(Left + D / 2 + i * (D + Gap), Top + D / 2);
            if (Math.Abs(p.X - c.X) <= D / 2 + 2 && Math.Abs(p.Y - c.Y) <= D / 2 + 2) return i;
        }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressed = Hit(e.GetPosition(this));
        if (_pressed >= 0) { e.Handled = true; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var i = Hit(e.GetPosition(this));
        if (i >= 0 && i == _pressed)
        {
            e.Handled = true;
            switch (i)
            {
                case 0: _window.Close(); break;
                case 1: _window.WindowState = WindowState.Minimized; break;
                default:
                    if (ShowZoom && _window.CanResize)
                        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;
            }
        }
        _pressed = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var active = _window.IsActive || App.Headless || _hover;
        var dark = _window.GetValue(Env.SchemeProperty) == Scheme.Dark || _window is ChatSpace.MainWindow;
        Color[] fill = [Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0x28, 0xC8, 0x40)];
        Color[] ring = [Color.FromRgb(0xE2, 0x46, 0x3F), Color.FromRgb(0xE1, 0xA1, 0x16), Color.FromRgb(0x12, 0xAC, 0x28)];
        var inactive = dark ? Color.FromRgb(0x4B, 0x4B, 0x4D) : Color.FromRgb(0xD1, 0xD0, 0xD2);
        for (var i = 0; i < 3; i++)
        {
            var rect = new Rect(Left + i * (D + Gap), Top, D, D);
            var disabled = i == 2 && (!ShowZoom || !_window.CanResize);
            var f = active && !disabled ? fill[i] : inactive;
            if (_pressed == i) f = Color.FromRgb((byte)(f.R * 0.8), (byte)(f.G * 0.8), (byte)(f.B * 0.8));
            ctx.DrawEllipse(new SolidColorBrush(f), new Pen(new SolidColorBrush(active && !disabled ? ring[i] : Color.FromArgb(0x30, 0, 0, 0)), 0.5), rect.Deflate(0.25));
            if (!_hover || disabled) continue;
            var glyph = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
            var pen = new Pen(glyph, 1.1, lineCap: PenLineCap.Round);
            var c = rect.Center;
            switch (i)
            {
                case 0:
                    ctx.DrawLine(pen, c + new Vector(-2.6, -2.6), c + new Vector(2.6, 2.6));
                    ctx.DrawLine(pen, c + new Vector(-2.6, 2.6), c + new Vector(2.6, -2.6));
                    break;
                case 1:
                    ctx.DrawLine(new Pen(glyph, 1.3, lineCap: PenLineCap.Round), c + new Vector(-3.2, 0), c + new Vector(3.2, 0));
                    break;
                default:
                    var g = new StreamGeometry();
                    using (var s = g.Open())
                    {
                        s.BeginFigure(c + new Vector(-3, -3), true); s.LineTo(c + new Vector(1.4, -3)); s.LineTo(c + new Vector(-3, 1.4)); s.EndFigure(true);
                        s.BeginFigure(c + new Vector(3, 3), true); s.LineTo(c + new Vector(-1.4, 3)); s.LineTo(c + new Vector(3, -1.4)); s.EndFigure(true);
                    }
                    ctx.DrawGeometry(glyph, null, g);
                    break;
            }
        }
    }
}

public static class Sheets
{
    /// Adds a sheet to `layer` (a full-window panel): clicks outside are swallowed, Esc cancels.
    public static void Present(Panel layer, Func<Action, Control> content, Scheme scheme)
    {
        Grid? host = null;
        void Dismiss() { if (host != null) layer.Children.Remove(host); }
        var body = content(Dismiss);
        var dark = scheme == Scheme.Dark;
        var card = new Border
        {
            Child = body,
            Background = new ImmutableSolidColorBrush(SColor.WindowBackground.Resolve(scheme)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new ImmutableSolidColorBrush(dark ? Color.FromArgb(0x40, 255, 255, 255) : Color.FromArgb(0x26, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, MacWindow.TitleBarHeight, 0, 0),
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.35, BlurRadius = 30, OffsetY = 8 },
        };
        host = new Grid { Background = new ImmutableSolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)) };
        host.Children.Add(card);
        host.KeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
        layer.Children.Add(host);
    }

    /// Presents on the window that owns `from` (MacWindow or the main window).
    public static void Present(Control from, Func<Action, Control> content)
    {
        var top = TopLevel.GetTopLevel(from);
        if (top is MacWindow mw) { mw.PresentSheet(content); return; }
        if (top is ChatSpace.MainWindow main) { main.PresentSheet(content); return; }
    }
}

/// `NSAlert`: compact centred alert (macOS 11+ layout) as a modal window.
public static class Alert
{
    public sealed record Choice(string Title, bool Prominent = false, bool Destructive = false);

    /// Returns the index of the clicked button (0 = first), or -1 when closed.
    public static async Task<int> Run(string title, string? message, IReadOnlyList<Choice> buttons, Window? owner = null, Control? accessory = null,
        string? iconSymbol = null)
    {
        owner ??= (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows
            .FirstOrDefault(w => w.IsActive) ?? App.Current.MainWindow;
        var result = -1;
        var scheme = MacWindow.SystemScheme;
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        win.SetValue(Env.FontProperty, SFont.Body);
        win.SetValue(Env.SchemeProperty, scheme);
        var btns = new List<Control?>();
        var list = buttons.Count == 0 ? [new Choice("OK", true)] : buttons;
        for (var i = 0; i < list.Count; i++)
        {
            var idx = i;
            var b = list[i];
            var kind = b.Prominent || (i == 0 && !list.Any(x => x.Prominent)) ? ButtonKind.Prominent : ButtonKind.Push;
            if (b.Destructive) kind = ButtonKind.Destructive;
            btns.Add(MacButton.Titled(b.Title, () => { result = idx; win.Close(); }, kind, ControlSize.Large).FillWidth());
        }
        Control buttonsView = list.Count == 2
            ? HStack(8, btns[1], btns[0])
            : VStack(8, btns.ToArray());
        Control icon = iconSymbol != null
            ? Image(iconSymbol).Font(40).Foreground(SColor.Orange).Frame(64, 64)
            : Bitmap(ChatSpace.Core.NativeAssets.Shared.Image("icons/chatspace-128.png"), ContentMode.Fit).Frame(64, 64);
        var body = VStack(0,
                icon.PaddingBottom(12),
                Text(title).Font(13, Weight.Bold).MultilineAlignment(TextAlignment.Center).FixedSize(false, true),
                message != null ? Text(message).Font(11).MultilineAlignment(TextAlignment.Center).FixedSize(false, true).PaddingTop(6) : null,
                accessory?.PaddingTop(10),
                buttonsView.PaddingTop(18))
            .Padding(16, 20, 16, 16)
            .Frame(width: 260)
            .Background(RoundedRectangle(12).Fill(scheme == Scheme.Dark ? SColor.Hex(0x323232) : SColor.Hex(0xECECEC))
                .StrokeBorder(SColor.Black.Opacity(0.2), 0.5))
            .Padding(20);
        body.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.3, BlurRadius = 30, OffsetY = 6 };
        win.Content = body;
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = Math.Max(0, list.ToList().FindIndex(c => c.Prominent)); win.Close(); }
            else if (e.Key == Key.Escape) { result = list.Count > 1 ? list.Count - 1 : 0; win.Close(); }
        };
        win.PointerPressed += (_, e) => { if (e.GetCurrentPoint(win).Properties.IsLeftButtonPressed) win.BeginMoveDrag(e); };
        if (owner != null && owner.IsVisible) await win.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            win.Closed += (_, _) => tcs.TrySetResult();
            win.Show();
            await tcs.Task;
        }
        return result;
    }

    public static Task<int> Info(string title, string? message = null) => Run(title, message, [new Choice("OK", true)]);
}
