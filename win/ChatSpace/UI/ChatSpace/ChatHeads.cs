using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// Floating chat heads, after Messenger's and Telegram for Android's bubbles: avatars docked to a screen
/// edge, above every app. Each one is its own window, dragged and dismissed on its own.
/// Tapping one opens that chat in a small window rendered by the platform's own conversation UI;
/// dragging one to the ✕ at the bottom closes it.
///
/// Geometry follows AppKit: frames are in points with the origin at the bottom-left of the screen's work
/// area ("visible frame"), converted to Avalonia's pixel positions only when a window is placed.
public sealed class ChatHeads : Observable
{
    public static readonly ChatHeads Shared = new();

    public sealed record ChatHeadPreviewItem(Guid Id, ChatRef Ref, string Title, string Text);

    private ImmutableList<ChatRef> _heads = [];
    private ChatRef? _expanded;
    private ImmutableDictionary<ChatRef, int> _bounces = ImmutableDictionary<ChatRef, int>.Empty;
    private ChatHeadPreviewItem? _preview;
    private ChatRef? _dragged;
    private bool _overTrash;
    private double _trashGrow;

    public ImmutableList<ChatRef> Heads { get => Get(ref _heads); private set => Set(ref _heads, value); }
    public ChatRef? Expanded { get => Get(ref _expanded); private set => Set(ref _expanded, value); }
    /// Bumped per chat on every new message; each bump makes that bubble bounce.
    public ImmutableDictionary<ChatRef, int> Bounces { get => Get(ref _bounces); private set => Set(ref _bounces, value); }
    public ChatHeadPreviewItem? Preview { get => Get(ref _preview); private set => Set(ref _preview, value); }
    /// The head being dragged, if any.
    public ChatRef? Dragged { get => Get(ref _dragged); private set => Set(ref _dragged, value); }
    public bool OverTrash { get => Get(ref _overTrash); set { Set(ref _overTrash, value); AnimateTrash(value); } }
    public bool Dragging => Dragged != null;
    /// 0…1 while the ✕ grows under a dragged head (the spring on `over`).
    public double TrashGrow { get => Get(ref _trashGrow); private set => Set(ref _trashGrow, value); }

    private readonly Dictionary<ChatRef, FloatingPanel> _headPanels = new();
    private FloatingPanel? _chatPanel, _trashPanel, _previewPanel;
    private readonly List<IDisposable> _clickMonitors = [];
    private IDisposable? _trashTween;

    public const double HeadSize = 54;
    public const double Spacing = 10;
    /// Room around the heads for the bounce and the badges.
    public const double Inset = 16;
    private static readonly Size ChatSize = new(400, 580);
    private static readonly Size PreviewSize = new(300, 96);
    private const int PreviewSeconds = 5;

    /// Windows that belong to the open chat: the bubbles, the chat itself, its popovers and the media viewer.
    public const string KeepsChatOpenIdentifier = "chatspace.keepsBubbleOpen";

    public bool IsFocused(ChatRef r) => Expanded == r && _chatPanel is { IsActive: true, IsVisible: true };

    /// The head window of the newest bubble / the open chat window, for the headless screenshot mode.
    public TopLevel? HeadWindowForScreenshot => Heads.Count > 0 && _headPanels.TryGetValue(Heads[0], out var p) ? p : null;
    public TopLevel? ChatWindowForScreenshot => _chatPanel;

    // MARK: Triggers

    /// A live, unmuted incoming message arrived for a chat that is not on screen (after the notification is posted).
    public void Incoming(Platform platform, Chat chat, Message message)
    {
        var cfg = Workspace.Shared.Bubbles;
        if (cfg.Trigger != BubbleSettings.TriggerKind.NewMessages) return;
        if (cfg.OnlyInBackground && AppIsActive() && !ActiveWindowIsPanel()) return;
        if (!cfg.IncludeGroups && chat.IsGroup) return;
        var r = new ChatRef(platform, chat.Id);
        var isNew = !Heads.Contains(r);
        Show(r, expand: false);
        if (!isNew) Bounces = Bounces.SetItem(r, Bounces.GetValueOrDefault(r) + 1);
        if (!cfg.ShowPreview || Expanded == r || Dragging) return;
        var body = message.Preview(photo: "📷 Hình ảnh", video: "🎥 Video", voice: "🎤 Tin nhắn thoại", sticker: "Nhãn dán",
            file: "📎 Tệp", deleted: "Tin nhắn đã thu hồi");
        var sender = string.IsNullOrEmpty(message.SenderName) ? null : message.SenderName;
        ShowPreview(new ChatHeadPreviewItem(Guid.NewGuid(), r, chat.Title, chat.IsGroup && sender != null ? $"{sender}: {body}" : body));
    }

    public void Show(ChatRef r, bool expand)
    {
        var heads = Heads.Remove(r).Insert(0, r);
        var cap = Math.Max(1, Workspace.Shared.Bubbles.MaxBubbles);
        while (heads.Count > cap)
        {
            var i = heads.FindLastIndex(x => x != Expanded && x != r);
            if (i < 0) break;
            var victim = heads[i];
            heads = heads.RemoveAll(x => x == victim);
            ClosePanel(victim);
        }
        Heads = heads;
        if (!_headPanels.ContainsKey(r)) PlaceNewHead(r);
        else _headPanels[r].Show();
        if (expand) Open(r);
    }

    public void Toggle(ChatRef r)
    {
        if (Expanded == r) Collapse(); else Open(r);
    }

    public void Open(ChatRef r)
    {
        HidePreview();
        Expanded = r;
        AppModel.Shared.Session(r.Platform).Prepare(r.ChatId);
        var panel = _chatPanel ?? MakeChatPanel();
        _chatPanel = panel;
        PositionChatPanel();
        panel.Show();
        panel.Activate();
        WatchOutsideClicks();
    }

    public void Collapse()
    {
        Expanded = null;
        _chatPanel?.Hide();
        foreach (var m in _clickMonitors) m.Dispose();
        _clickMonitors.Clear();
    }

    // MARK: Click outside closes the chat

    private void WatchOutsideClicks()
    {
        if (_clickMonitors.Count > 0) return;
        // Local monitor: a press in any ChatSpace window that is not part of the chat.
        _clickMonitors.Add(InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((tl, _) =>
        {
            if (!BelongsToChat(tl)) Collapse();
        }, RoutingStrategies.Tunnel, handledEventsToo: true));
        // Global monitor: the chat window lost focus to another app (not the emoji picker or the snipping tool).
        if (_chatPanel is { } panel)
        {
            void Lost(object? s, EventArgs e) =>
                DispatcherTimer.RunOnce(() =>
                {
                    if (Expanded == null || AppIsActive() || BubbleNative.IsSystemHelperForeground()) return;
                    Collapse();
                }, TimeSpan.FromMilliseconds(150));
            panel.Deactivated += Lost;
            _clickMonitors.Add(new Disposer(() => panel.Deactivated -= Lost));
        }
    }

    private bool BelongsToChat(TopLevel? window)
    {
        if (window == null) return true;
        // Popups and menus opened from the chat are separate roots parented to it.
        if (window is PopupRoot) return true;
        if (window is not Window w) return true;
        if (w == _chatPanel || w == _previewPanel || w == _trashPanel) return true;
        if (_headPanels.Values.Contains(w)) return true;
        if (w.Tag as string == KeepsChatOpenIdentifier) return true;
        // Owned windows: alerts, save panels and other modal dialogs.
        for (var o = w.Owner as Window; o != null; o = o.Owner as Window)
            if (o == _chatPanel) return true;
        return w.Owner != null;
    }

    private static IEnumerable<Window> AppWindows() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? [];

    private static bool AppIsActive() => AppWindows().Any(w => w.IsActive);

    /// `NSApp.keyWindow is NSPanel`: the active window is one of the floating panels.
    private static bool ActiveWindowIsPanel() =>
        AppWindows().FirstOrDefault(w => w.IsActive) is FloatingPanel or { Tag: KeepsChatOpenIdentifier };

    public void Dismiss(ChatRef r)
    {
        Heads = Heads.RemoveAll(x => x == r);
        Bounces = Bounces.Remove(r);
        if (Preview?.Ref == r) HidePreview();
        if (Expanded == r) Collapse();
        ClosePanel(r);
    }

    public void DismissAll()
    {
        foreach (var r in Heads.ToList()) ClosePanel(r);
        Heads = [];
        Bounces = ImmutableDictionary<ChatRef, int>.Empty;
        HidePreview();
        Collapse();
    }

    // MARK: Message preview

    private void ShowPreview(ChatHeadPreviewItem p)
    {
        Preview = p;
        var panel = _previewPanel ?? MakePanel(PreviewSize, canKey: false, new ChatHeadPreview());
        _previewPanel = panel;
        PositionPreview();
        panel.Opacity = 1;
        panel.Show();
        DispatcherTimer.RunOnce(() => { if (Preview?.Id == p.Id) HidePreview(); }, TimeSpan.FromSeconds(PreviewSeconds));
    }

    public void HidePreview()
    {
        if (Preview == null) return;
        Preview = null;
        if (_previewPanel is not { IsVisible: true } panel) return;
        Tween.Run(0.2, Tween.Linear, t => panel.Opacity = 1 - t, () => { if (Preview == null) panel.Hide(); });
    }

    /// Beside the bubble that just popped, on the side facing the screen.
    private void PositionPreview()
    {
        if (_previewPanel is not { } panel || Preview?.Ref is not { } r || !_headPanels.TryGetValue(r, out var head)) return;
        var vf = VisibleFrame(head);
        var hf = FrameOf(head, vf);
        var size = PreviewSize;
        var right = hf.MidX > vf.MidX;
        var x = right ? hf.MinX - size.Width + 6 : hf.MaxX - 6;
        x = Math.Min(Math.Max(x, vf.MinX + 4), vf.MaxX - size.Width - 4);
        var y = hf.MidY - size.Height / 2;
        y = Math.Min(Math.Max(y, vf.MinY + 4), vf.MaxY - size.Height);
        SetFrame(panel, vf, x, y, size.Width, size.Height);
    }

    public bool PreviewOnRight =>
        Preview?.Ref is { } r && _headPanels.TryGetValue(r, out var head) && FrameOf(head, VisibleFrame(head)).MidX <= VisibleFrame(head).MidX;

    public void OpenInMainWindow(ChatRef r)
    {
        Collapse();
        App.ActivateMainWindow();
        AppModel.Shared.Open(r.Platform, r.ChatId);
    }

    // MARK: Geometry

    private readonly record struct Vf(PixelRect Area, double Scale)
    {
        public double MinX => 0;
        public double MinY => 0;
        public double MaxX => Area.Width / Scale;
        public double MaxY => Area.Height / Scale;
        public double MidX => MaxX / 2;
        public double MidY => MaxY / 2;
    }

    private readonly record struct Frm(double X, double Y, double W, double H)
    {
        public double MinX => X;
        public double MinY => Y;
        public double MaxX => X + W;
        public double MaxY => Y + H;
        public double MidX => X + W / 2;
        public double MidY => Y + H / 2;
        public Frm Inset(double dx, double dy) => new(X + dx, Y + dy, W - 2 * dx, H - 2 * dy);
        public bool Intersects(Frm o) => X < o.MaxX && o.X < MaxX && Y < o.MaxY && o.Y < MaxY;
    }

    /// `(window?.screen ?? NSScreen.main).visibleFrame`
    private static Vf VisibleFrame(Window? window)
    {
        try
        {
            var screens = (window ?? App.Current.MainWindow)?.Screens;
            var screen = (window != null ? screens?.ScreenFromWindow(window) : null) ?? screens?.Primary;
            if (screen != null) return new Vf(screen.WorkingArea, screen.Scaling);
        }
        catch { }
        return new Vf(new PixelRect(0, 0, 1920, 1040), 1);
    }

    private static Frm FrameOf(Window w, Vf vf) =>
        new((w.Position.X - vf.Area.X) / vf.Scale, (vf.Area.Bottom - w.Position.Y) / vf.Scale - w.Height, w.Width, w.Height);

    private static void SetOrigin(Window w, Vf vf, double x, double y) =>
        w.Position = new PixelPoint(vf.Area.X + (int)Math.Round(x * vf.Scale), vf.Area.Y + (int)Math.Round((vf.MaxY - y - w.Height) * vf.Scale));

    private static void SetFrame(Window w, Vf vf, double x, double y, double width, double height)
    {
        w.Width = width;
        w.Height = height;
        SetOrigin(w, vf, x, y);
    }

    // MARK: Head windows

    private static readonly Size HeadWindowSize = new(HeadSize + 2 * Inset, HeadSize + 2 * Inset);
    private const double Slot = HeadSize + Spacing;

    private FloatingPanel MakePanel(Size size, bool canKey, Control content)
    {
        var panel = new FloatingPanel(size, canKey) { Content = content };
        return panel;
    }

    /// A new head goes to the bottom-right corner, or the first free spot above the heads already there;
    /// the top-right corner is where the system shows notifications.
    private void PlaceNewHead(ChatRef r)
    {
        var panel = MakePanel(HeadWindowSize, canKey: false, new ChatHeadSlot(r));
        var vf = VisibleFrame(null);
        var size = HeadWindowSize;
        var x = vf.MaxX - size.Width - 4;
        SetOrigin(panel, vf, x, FreeY(x, vf.MinY + 24, vf, r));
        _headPanels[r] = panel;
        panel.Opacity = 0;
        panel.Show();
        Tween.Run(0.18, Tween.Linear, t => panel.Opacity = t);
    }

    /// Nearest height to `y` where a head at `x` overlaps no other head.
    private double FreeY(double x, double y, Vf vf, ChatRef excluding)
    {
        var size = HeadWindowSize;
        double top = vf.MaxY - size.Height, bottom = vf.MinY + 8;
        var others = _headPanels.Where(p => p.Key != excluding).Select(p => FrameOf(p.Value, vf)).ToList();
        bool Free(double cy)
        {
            var rect = new Frm(x, cy, size.Width, size.Height).Inset(Inset, Inset - Spacing / 2);
            return !others.Any(o => o.Inset(Inset, Inset - Spacing / 2).Intersects(rect));
        }
        var start = Math.Min(Math.Max(y, bottom), top);
        for (var step = 0; step < 40; step++)
            foreach (var candidate in new[] { start + step * Slot, start - step * Slot })
                if (candidate >= bottom && candidate <= top && Free(candidate)) return candidate;
        return start;
    }

    private void ClosePanel(ChatRef r)
    {
        if (!_headPanels.Remove(r, out var panel)) return;
        Tween.Run(0.15, Tween.Linear, t => panel.Opacity = 1 - t, () => panel.Close());
    }

    // MARK: Dragging (called from the pointer handlers with screen pixels)

    private (ChatRef Ref, PixelPoint Mouse, PixelPoint Origin)? _dragStart;
    private Vf _dragVf;

    public void DragChanged(ChatRef r, PixelPoint mouse)
    {
        if (!_headPanels.TryGetValue(r, out var panel)) return;
        if (_dragStart is not { } start || start.Ref != r)
        {
            _dragVf = VisibleFrame(panel);
            _dragStart = start = (r, mouse, panel.Position);
        }
        double dx = mouse.X - start.Mouse.X, dy = mouse.Y - start.Mouse.Y;
        if (Dragged == null && Math.Sqrt(dx * dx + dy * dy) / _dragVf.Scale < 4) return;
        if (Dragged == null)
        {
            Dragged = r;
            HidePreview();
            Collapse();
            ShowTrash(panel);
        }
        panel.Position = new PixelPoint(start.Origin.X + (int)dx, start.Origin.Y + (int)dy);
        if (_trashPanel is { } t)
        {
            var c = FrameOf(t, _dragVf);
            var mx = (mouse.X - _dragVf.Area.X) / _dragVf.Scale;
            var my = (_dragVf.Area.Bottom - mouse.Y) / _dragVf.Scale;
            OverTrash = Math.Sqrt((mx - c.MidX) * (mx - c.MidX) + (my - c.MidY) * (my - c.MidY)) < 70;
        }
    }

    /// Returns true if the gesture was a drag (so the tap action must not fire).
    public bool DragEnded(ChatRef r)
    {
        try
        {
            if (Dragged != r) return false;
            Dragged = null;
            _trashPanel?.Hide();
            if (OverTrash)
            {
                OverTrash = false;
                Dismiss(r);
                return true;
            }
            SnapToEdge(r);
            return true;
        }
        finally { _dragStart = null; }
    }

    private void SnapToEdge(ChatRef r)
    {
        if (!_headPanels.TryGetValue(r, out var panel)) return;
        var vf = VisibleFrame(panel);
        var f = FrameOf(panel, vf);
        var x = f.MidX > vf.MidX ? vf.MaxX - f.W - 4 : vf.MinX + 4;
        var y = FreeY(x, f.Y, vf, r);
        double x0 = f.X, y0 = f.Y;
        Tween.Run(0.22, Tween.EaseOut, t => SetOrigin(panel, vf, x0 + (x - x0) * t, y0 + (y - y0) * t));
    }

    private void ShowTrash(Window head)
    {
        var panel = _trashPanel ?? MakePanel(new Size(120, 120), canKey: false, new ChatHeadTrash());
        panel.IsHitTestVisible = false;
        panel.ClickThrough = true;
        _trashPanel = panel;
        var vf = VisibleFrame(head);
        SetOrigin(panel, vf, vf.MidX - 60, vf.MinY + 24);
        panel.Show();
    }

    private void AnimateTrash(bool over)
    {
        _trashTween?.Dispose();
        var from = TrashGrow;
        var to = over ? 1.0 : 0.0;
        _trashTween = Tween.Run(0.2, Tween.EaseOut, t => TrashGrow = from + (to - from) * t);
    }

    // MARK: Chat window

    private FloatingPanel MakeChatPanel()
    {
        var p = MakePanel(ChatSize, canKey: true, new ChatHeadWindow());
        p.Activated += (_, _) =>
        {
            if (Expanded is { } r) AppModel.Shared.Session(r.Platform).MarkRead(r.ChatId);
        };
        return p;
    }

    /// Beside the head it was opened from, level with its top (or its bottom, for a head low on the screen).
    private void PositionChatPanel()
    {
        if (_chatPanel is not { } chat || Expanded is not { } r || !_headPanels.TryGetValue(r, out var head)) return;
        var vf = VisibleFrame(head);
        var hf = FrameOf(head, vf);
        var size = ChatSize;
        var right = hf.MidX > vf.MidX;
        var x = right ? hf.MinX - size.Width + 6 : hf.MaxX - 6;
        x = Math.Min(Math.Max(x, vf.MinX + 4), vf.MaxX - size.Width - 4);
        var y = hf.MidY < vf.MidY ? hf.MinY + 6 : hf.MaxY - size.Height - 6;
        y = Math.Min(Math.Max(y, vf.MinY + 4), vf.MaxY - size.Height);
        SetFrame(chat, vf, x, y, size.Width, size.Height);
    }
}

public sealed class Disposer(Action action) : IDisposable
{
    private Action? _action = action;
    public void Dispose() { _action?.Invoke(); _action = null; }
}

/// Borderless panel that floats above every app: transparent, topmost, off the taskbar. A panel that
/// cannot become key (`canKey: false`) never takes the focus from the app the user is working in.
public sealed class FloatingPanel : Window
{
    private readonly bool _canKey;
    /// `ignoresMouseEvents`: clicks go to whatever is underneath.
    public bool ClickThrough { get; set; }

    public FloatingPanel(Size size, bool canKey)
    {
        _canKey = canKey;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = canKey;
        SizeToContent = SizeToContent.Manual;
        Width = size.Width;
        Height = size.Height;
        SetValue(Env.FontProperty, SFont.Body);
        SetValue(Env.SchemeProperty, Scheme.Light);
        Opened += (_, _) => BubbleNative.StylePanel(this, noActivate: !_canKey, clickThrough: ClickThrough);
    }
}

/// Time-based animations on the UI thread (`withAnimation`, `NSAnimationContext`).
public static class Tween
{
    public static double Linear(double t) => t;
    public static double EaseOut(double t) => 1 - (1 - t) * (1 - t);
    public static double EaseInOut(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    /// `Animation.spring(response:dampingFraction:)`: progress 0→1 with the overshoot of a damped spring.
    public static Func<double, double> Spring(double response, double damping, double seconds) => t =>
    {
        var s = t * seconds;
        var w = 2 * Math.PI / response;
        var wd = w * Math.Sqrt(1 - damping * damping);
        var decay = Math.Exp(-damping * w * s);
        return t >= 1 ? 1 : 1 - decay * (Math.Cos(wd * s) + damping * w / wd * Math.Sin(wd * s));
    };

    public static IDisposable Run(double seconds, Func<double, double> ease, Action<double> apply, Action? done = null)
    {
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        var finished = false;
        void Finish(bool complete)
        {
            if (finished) return;
            finished = true;
            timer.Stop();
            if (complete) { apply(ease(1)); done?.Invoke(); }
        }
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, sw.Elapsed.TotalSeconds / seconds);
            if (t >= 1) Finish(true); else apply(ease(t));
        };
        apply(ease(0));
        timer.Start();
        return new Disposer(() => Finish(false));
    }
}

/// Win32 touches the panels need (no-activate, tool window, click-through, foreground helper check).
internal static class BubbleNative
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

    /// `nonactivatingPanel`, `.ignoresCycle` (no Alt+Tab entry) and `ignoresMouseEvents`.
    public static void StylePanel(Window w, bool noActivate, bool clickThrough)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (w.TryGetPlatformHandle()?.Handle is not { } h || h == 0) return;
            var style = (long)GetWindowLongPtr(h, GwlExStyle) | WsExToolWindow;
            if (noActivate) style |= WsExNoActivate;
            if (clickThrough) style |= WsExTransparent | WsExLayered;
            SetWindowLongPtr(h, GwlExStyle, (nint)style);
        }
        catch { }
    }

    /// The emoji picker and the screenshot overlay run in their own processes; using them is part of
    /// composing the message, not leaving the chat.
    public static bool IsSystemHelperForeground()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var h = GetForegroundWindow();
            if (h == 0) return false;
            GetWindowThreadProcessId(h, out var pid);
            string[] helpers = ["TextInputHost", "ScreenClippingHost", "SnippingTool", "ScreenSketch", "TabTip", "ChsIME", "ctfmon"];
            return helpers.Contains(Process.GetProcessById((int)pid).ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

// MARK: Content

/// A view whose scale, vertical offset and opacity are animated from code (`scaleEffect`, `offset`, `opacity`).
public sealed class HeadAnim : Decorator, IFlexible
{
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _move = new();

    public HeadAnim(Control child)
    {
        Child = child;
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = new TransformGroup { Children = { _scale, _move } };
    }

    public double Scale { get => _scale.ScaleX; set { _scale.ScaleX = value; _scale.ScaleY = value; } }
    public double OffsetY { get => _move.Y; set => _move.Y = value; }
    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);
}

/// One head's window content; it shrinks while held over the ✕.
public sealed class ChatHeadSlot : Decorator
{
    private readonly ChatRef _ref;
    private readonly HeadAnim _anim;
    private readonly Watch _watch;
    private double _appear = 0.3;
    private double _trashT;
    private double _trashTarget;
    private bool _appeared;
    private IDisposable? _trashTween;

    public ChatHeadSlot(ChatRef r)
    {
        _ref = r;
        _anim = new HeadAnim(new ChatHeadBubble(r).Padding(ChatHeads.Inset).Fill());
        Child = _anim;
        Apply();
        _watch = Watch.Effect(() =>
        {
            var heads = ChatHeads.Shared;
            var trashing = heads.OverTrash && heads.Dragged == _ref;
            Trash(trashing ? 1 : 0);
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_appeared) return;
        _appeared = true;
        // `withAnimation(.spring(response: 0.38, dampingFraction: 0.6)) { appeared = true }`
        Tween.Run(1.0, Tween.Spring(0.38, 0.6, 1.0), t => { _appear = 0.3 + 0.7 * t; Apply(); });
    }

    private void Trash(double target)
    {
        if (target == _trashTarget) return;
        _trashTarget = target;
        _trashTween?.Dispose();
        var from = _trashT;
        _trashTween = Tween.Run(0.25, Tween.EaseOut, t => { _trashT = from + (target - from) * t; Apply(); });
    }

    private void Apply()
    {
        _anim.Scale = _appear + (0.85 - _appear) * _trashT;
        _anim.Opacity = 1 - 0.3 * _trashT;
    }
}

public sealed class ChatHeadBubble : Decorator, ICustomHitTest
{
    private readonly ChatRef _ref;
    private readonly HeadAnim _anim;
    private readonly Watch _bounceWatch;
    private double _hoverScale = 1, _bounceScale = 1, _bounceY;
    private IDisposable? _hoverTween, _bounceTween;
    private int? _lastBounce;
    private bool _down;

    public ChatHeadBubble(ChatRef r)
    {
        _ref = r;
        _anim = new HeadAnim(View(Content).Frame(width: ChatHeads.HeadSize, height: ChatHeads.HeadSize));
        Child = _anim;
        this.ContextMenu(Menu);
        _bounceWatch = Watch.Effect(() =>
        {
            var n = ChatHeads.Shared.Bounces.GetValueOrDefault(_ref);
            if (_lastBounce is { } last && last != n) Bounce();
            _lastBounce = n;
        });
    }

    /// `.contentShape(Circle())`
    public bool HitTest(Point point)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        return Math.Sqrt((point.X - c.X) * (point.X - c.X) + (point.Y - c.Y) * (point.Y - c.Y)) <= ChatHeads.HeadSize / 2;
    }

    private Control Content()
    {
        var model = AppModel.Shared;
        var chat = model.Chat(_ref);
        var size = ChatHeads.HeadSize;
        var open = ChatHeads.Shared.Expanded == _ref;
        var avatar = AvatarView.Make(chat?.Avatar, size, () =>
            Circle().Fill(new LinearGradient([SColor.Hex(0x72D5FD), SColor.Hex(0x2A9EF1)], LinearGradient.Top, LinearGradient.Bottom))
                .Overlay(Text(ForwardRow.FirstLetter(chat?.Title ?? "?")).Font(22, Weight.Semibold).Foreground(SColor.White)));
        var ringed = avatar.ClipCircle().Overlay(Circle().Stroke(SColor.White.Opacity(open ? 1 : 0), 3));
        ringed.Shadow(SColor.Black.Opacity(0.35), 6, 0, 3);
        var view = ringed
            .Overlay(Alignment.BottomTrailing,
                new PlatformIcon(_ref.Platform, 18).Background(Circle().Fill(SColor.White).Padding(-2)).Offset(2, 2))
            .Overlay(Alignment.TopTrailing,
                chat is { HasUnread: true } c ? new RailBadge(Math.Max(c.Unread, 1)).Offset(4, -4) : null)
            .Frame(width: size, height: size)
            // Chats whose list row never scrolled into view haven't had their avatar resolved yet.
            .OnAppear(() => { if (chat is { Avatar: null }) model.Session(_ref.Platform).RequestAvatar(chat); })
            .Help(chat != null ? $"{chat.Title} — {_ref.Platform.DisplayName()}" : _ref.Platform.DisplayName());
        return view;
    }

    private IEnumerable<MenuEntry> Menu()
    {
        var heads = ChatHeads.Shared;
        var model = AppModel.Shared;
        var chat = Tracker.Untracked(() => model.Chat(_ref));
        yield return MenuEntry.Item("Mở trong ChatSpace", () => heads.OpenInMainWindow(_ref));
        if (chat is { HasUnread: true }) yield return MenuEntry.Item("Đánh dấu đã đọc", () => model.Session(_ref.Platform).MarkRead(_ref.ChatId));
        yield return MenuEntry.Separator;
        yield return MenuEntry.Item("Đóng bong bóng này", () => heads.Dismiss(_ref));
        yield return MenuEntry.Item("Đóng tất cả bong bóng", heads.DismissAll);
    }

    // MARK: Gestures and animations

    private PixelPoint Screen(PointerEventArgs e) =>
        TopLevel.GetTopLevel(this) is Window w ? w.PointToScreen(e.GetPosition(w)) : default;

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); Hover(1.06); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); Hover(1); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _down = true;
        e.Pointer.Capture(this);
        ChatHeads.Shared.DragChanged(_ref, Screen(e));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_down) ChatHeads.Shared.DragChanged(_ref, Screen(e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_down) return;
        _down = false;
        e.Pointer.Capture(null);
        if (!ChatHeads.Shared.DragEnded(_ref)) ChatHeads.Shared.Toggle(_ref);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_down) return;
        _down = false;
        ChatHeads.Shared.DragEnded(_ref);
    }

    private void Apply()
    {
        _anim.Scale = _hoverScale * _bounceScale;
        _anim.OffsetY = _bounceY;
    }

    /// `.scaleEffect(hovered ? 1.06 : 1).animation(.easeOut(duration: 0.12))`
    private void Hover(double target)
    {
        _hoverTween?.Dispose();
        var from = _hoverScale;
        _hoverTween = Tween.Run(0.12, Tween.EaseOut, t => { _hoverScale = from + (target - from) * t; Apply(); });
    }

    private static readonly (double T, double V)[] BounceScale = [(0, 1), (0.14, 1.16), (0.28, 0.94), (0.42, 1.05), (0.62, 1)];
    private static readonly (double T, double V)[] BounceY = [(0, 0), (0.14, -9), (0.30, 0), (0.42, -4), (0.62, 0)];

    private static double Track((double T, double V)[] keys, double time)
    {
        for (var i = 1; i < keys.Length; i++)
        {
            if (time > keys[i].T) continue;
            var span = keys[i].T - keys[i - 1].T;
            var u = span <= 0 ? 1 : (time - keys[i - 1].T) / span;
            return keys[i - 1].V + (keys[i].V - keys[i - 1].V) * Tween.EaseInOut(u);
        }
        return keys[^1].V;
    }

    /// `keyframeAnimator(trigger: bounces[ref])`: scale 1.16 / 0.94 / 1.05 / 1 while hopping 9 and 4 points up.
    private void Bounce()
    {
        _bounceTween?.Dispose();
        _bounceTween = Tween.Run(0.62, Tween.Linear, t =>
        {
            _bounceScale = Track(BounceScale, t * 0.62);
            _bounceY = Track(BounceY, t * 0.62);
            Apply();
        });
    }
}

/// The newest message, shown for a few seconds beside the bubble that just popped; clicking it opens the chat.
public sealed class ChatHeadPreview : Reactive
{
    public ChatHeadPreview() : base(Build) { }

    private static Control? Build()
    {
        var heads = ChatHeads.Shared;
        if (heads.Preview is not { } p) return Clear;
        var onRight = heads.PreviewOnRight;
        var card = Button(() => heads.Open(p.Ref),
                HStack(VAlign.Top, 8,
                        new PlatformIcon(p.Ref.Platform, 16).PaddingTop(1),
                        VStack(HAlign.Leading, 2,
                            Text(p.Title).Font(13, Weight.Semibold).LineLimit(1),
                            Text(p.Text).Font(13).LineLimit(2).Foreground(SColor.Hex(0x3C3C43))))
                    .Foreground(SColor.Black)
                    .PaddingH(12).PaddingV(9)
                    .Frame(maxWidth: 270, alignment: Alignment.Leading)
                    .Background(RoundedRectangle(16, true).Fill(SColor.White).Shadow(SColor.Black.Opacity(0.22), 8, 0, 3))
                    .ContentShape())
            .Help("Mở đoạn chat");
        return card.Padding(12)
            .Frame(maxWidth: Mod.Inf, maxHeight: Mod.Inf, alignment: onRight ? Alignment.Leading : Alignment.Trailing)
            .Scheme(Scheme.Light);
    }
}

public sealed class ChatHeadTrash : Reactive
{
    public ChatHeadTrash() : base(Build) { }

    private static Control Build()
    {
        var heads = ChatHeads.Shared;
        var over = heads.OverTrash;
        var grow = heads.TrashGrow;
        var diameter = 62 + 14 * grow;
        return Circle().Fill(SColor.Black.Opacity(over ? 0.75 : 0.5))
            .Overlay(Image("xmark").Font(22 + 4 * grow, Weight.Semibold).Foreground(SColor.White))
            .Frame(width: diameter, height: diameter)
            .Shadow(SColor.Black.Opacity(0.33), 8)
            .Frame(width: 120, height: 120);
    }
}

/// The expanded bubble: a compact title bar plus the platform's own conversation view.
public sealed class ChatHeadWindow : Reactive
{
    public ChatHeadWindow() : base(Build) { }

    private static Control? Build()
    {
        var heads = ChatHeads.Shared;
        if (heads.Expanded is not { } r || AppModel.Shared.Chat(r) is not { } chat) return Clear;
        var bar = HStack(8,
                new PlatformIcon(r.Platform, 16),
                // Rendered with vibrancy on macOS: darker than the 0.5 secondary of the buttons (93 vs 116 on the 231 bar).
                Text(r.Platform.DisplayName()).Font(12, Weight.Semibold).Foreground(SColor.Black.Opacity(0.6)),
                Spacer(),
                BarButton("arrow.up.left.and.arrow.down.right", "Mở trong cửa sổ ChatSpace", () => heads.OpenInMainWindow(r)),
                BarButton("minus", "Thu nhỏ về bong bóng", heads.Collapse),
                BarButton("xmark", "Đóng bong bóng", () => heads.Dismiss(r)))
            .Font(11, Weight.Semibold).Foreground(SColor.Secondary)
            .PaddingH(10).Frame(height: 30)
            .Background(SColor.Hex(0xE7E7E7))
            // `.regularMaterial` fades 2 px darker over its last point (231 -> 229 in the captures).
            .Overlay(Alignment.Bottom, Rectangle().Fill(new LinearGradient([SColor.Hex(0xE6E6E6), SColor.Hex(0xE5E5E5)], LinearGradient.Top, LinearGradient.Bottom)).Frame(height: 1));
        return VStack(0,
                bar,
                Keep(("conversation", r.Id), () => ChatMetrics.CompactChat(PlatformConversations.Make(r.Platform, AppModel.Shared.Session(r.Platform), r.ChatId))).Fill())
            .Background(SColor.White)
            .Scheme(Scheme.Light)
            .ClipRounded(16);
    }

    private static Control BarButton(string symbol, string help, Action action) =>
        Button(action, Image(symbol).Frame(width: 24, height: 22).ContentShape()).Help(help);
}
