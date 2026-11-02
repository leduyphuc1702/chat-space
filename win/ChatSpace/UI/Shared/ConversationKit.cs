using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// A message with the layout facts every platform UI needs.
public sealed record MessageRow(Message Message, bool IsFirstInGroup, bool IsLastInGroup, bool ShowDayHeader, bool ShowTimeGap,
    bool IsLastOutgoing)
{
    public string Id => Message.Id;
}

public static class MessageGrouping
{
    public static IReadOnlyList<MessageRow> Rows(IReadOnlyList<Message> messages, double groupWindow = 5 * 60, double gap = 15 * 60)
    {
        var lastOut = messages.LastOrDefault(m => m.FromMe)?.Id;
        var rows = new List<MessageRow>(messages.Count);
        static bool SameGroup(Message? a, Message b, double window) =>
            a != null && a.Kind != MessageKind.System && b.Kind != MessageKind.System
            && a.SenderId == b.SenderId && a.FromMe == b.FromMe
            && Math.Abs((b.Date - a.Date).TotalSeconds) < window
            && Fmt.DayKey(a.Date) == Fmt.DayKey(b.Date);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            var prev = i > 0 ? messages[i - 1] : null;
            var next = i + 1 < messages.Count ? messages[i + 1] : null;
            var newDay = prev == null || Fmt.DayKey(prev.Date) != Fmt.DayKey(m.Date);
            var bigGap = prev == null || (m.Date - prev.Date).TotalSeconds > gap;
            rows.Add(new MessageRow(m,
                !SameGroup(prev, m, groupWindow) || newDay,
                next == null || !SameGroup(m, next, groupWindow) || Fmt.DayKey(next.Date) != Fmt.DayKey(m.Date),
                newDay, bigGap, m.Id == lastOut));
        }
        return rows;
    }

    /// `format` is a .NET date format for this year; ", yyyy" is appended for other years.
    public static string DayTitle(DateTime d, string today, string yesterday, CultureInfo culture, string format)
    {
        var now = DateTime.Now;
        if (d.Date == now.Date) return today;
        if (d.Date == now.Date.AddDays(-1)) return yesterday;
        return d.ToString(d.Year == now.Year ? format : format + ", yyyy", culture);
    }
}

/// Bottom-anchored message list with lazy history loading, shared by all platforms. Keep it across
/// rebuilds (`Keep(("scroll", chatId), ...)`) so the reading position survives.
public sealed class ChatScroll : Decorator, IFlexible
{
    private readonly PlatformSession _s;
    private readonly string _chatId;
    private readonly ScrollViewer _sv;
    private readonly ForEachStack<MessageRow> _rows;
    private bool _settled, _atBottom = true, _attached;
    private string? _lastId;
    private string? _lastTarget;
    private Watch? _watch;
    private int _generation;
    private readonly double _bottomInset;

    public ChatScroll(PlatformSession session, string chatId, Func<MessageRow, Control> row, double spacing = 0,
        Thickness? padding = null, Func<Control?>? header = null, Func<Control?>? footer = null)
    {
        _s = session;
        _chatId = chatId;
        _rows = new ForEachStack<MessageRow>(() => MessageGrouping.Rows(_s.MessagesIn(_chatId)), r => r.Id, row,
            spacing: spacing, h: HAlign.Center);
        var top = View(() =>
        {
            _ = _settledState.Value;
            return _settled && !_s.ReachedStart.Contains(_chatId) && !_s.MessagesIn(_chatId).IsEmpty
                ? new ProgressView { Size = ControlSize.Small }.Padding(8)
                : null;
        });
        var content = VStack(HAlign.Center, spacing, top, header != null ? View(header) : null, _rows,
            footer != null ? View(footer) : null, Clear.Frame(height: 1));
        var pad = padding ?? new Thickness(0, 8, 0, 8);
        _bottomInset = pad.Bottom;
        // `.defaultScrollAnchor(.bottom)`: a chat shorter than the viewport sits against the composer.
        _sv = ScrollView(new PaddingBox { Child = content, Insets = pad });
        ((Control)_sv.Content!).VerticalAlignment = VerticalAlignment.Bottom;
        _sv.ScrollChanged += OnScrollChanged;
        var overlay = View(() => _s.MessagesIn(_chatId).IsEmpty && _s.LoadingHistory.Contains(_chatId) ? new ProgressView() : null);
        overlay.IsHitTestVisible = false;
        var grid = new Panel();
        grid.Children.Add(_sv);
        grid.Children.Add(overlay);
        Child = grid;
    }

    private readonly State<int> _settledState = new(0);

    public bool IsFlexible(Orientation axis) => true;

    private double MaxOffset => Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);

    /// `scrollTo("bottom", anchor: .bottom)`: the 1 pt marker meets the viewport's bottom edge, leaving the padding below it out of view.
    private void ScrollToBottom() => _sv.Offset = new Vector(0, Math.Max(0, MaxOffset - _bottomInset));

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0)
        {
            // Images loading and older pages arriving change the height; keep the distance to the bottom.
            var y = Math.Clamp(_sv.Offset.Y + e.ExtentDelta.Y, 0, MaxOffset);
            if (Math.Abs(y - _sv.Offset.Y) > 0.5) _sv.Offset = new Vector(0, y);
        }
        var near = _sv.Offset.Y + _sv.Viewport.Height >= _sv.Extent.Height - 80;
        if (near) _atBottom = true;
        else if (e.ExtentDelta.Y == 0) _atBottom = false;
        MaybeLoadOlder();
    }

    private void MaybeLoadOlder()
    {
        if (!_settled || _s.ReachedStart.Contains(_chatId) || _s.LoadingHistory.Contains(_chatId) || _s.MessagesIn(_chatId).IsEmpty) return;
        if (_sv.Offset.Y < 40) _s.LoadOlder(_chatId);
    }

    private async void Land(int[] delays, bool settleAfter)
    {
        var gen = _generation;
        foreach (var ms in delays)
        {
            if (ms > 0) await Task.Delay(ms);
            if (!_attached || gen != _generation) return;
            ScrollToBottom();
        }
        _atBottom = true;
        if (settleAfter && !_s.MessagesIn(_chatId).IsEmpty)
        {
            _settled = true;
            _settledState.Value++;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _generation++;
        if (!_settled) Land([0, 80, 250, 600], true);
        _watch = Watch.Effect(() =>
        {
            var msgs = _s.MessagesIn(_chatId);
            var last = msgs.IsEmpty ? null : msgs[^1];
            var target = _s.ScrollTarget.GetValueOrDefault(_chatId);
            _ = _s.LoadingHistory.Contains(_chatId);
            Dispatcher.UIThread.Post(() => React(msgs.IsEmpty, last, target), DispatcherPriority.Background);
        });
    }

    private void React(bool empty, Message? last, string? target)
    {
        if (!_attached) return;
        if (last?.Id != _lastId)
        {
            var had = _lastId != null;
            _lastId = last?.Id;
            if (!empty && !_settled && !had) Land([0, 80, 250], true);
            else if (had && (!_settled || _atBottom || last?.FromMe == true)) Land([0, 120], false);
        }
        if (target != _lastTarget)
        {
            _lastTarget = target;
            if (target != null) Dispatcher.UIThread.Post(() => ScrollTo(target), DispatcherPriority.Background);
        }
        MaybeLoadOlder();
    }

    private void ScrollTo(string id)
    {
        if (_rows.ViewFor(id) is not { } v || v.TranslatePoint(new Point(0, 0), _sv.Content as Visual ?? _sv) is not { } p) return;
        var y = Math.Clamp(p.Y + v.Bounds.Height / 2 - _sv.Viewport.Height / 2, 0, MaxOffset);
        _sv.Offset = new Vector(0, y);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _watch?.Dispose();
        _watch = null;
    }
}

/// Caps the width offered to a bubble without stretching it to the cap the way `.frame(maxWidth:)` does.
public sealed class CapWidth : Decorator, IFlexible
{
    public double Max { get; }
    public CapWidth(double max, Control child) { Max = max; Child = child; }
    public bool IsFlexible(Orientation axis) => false;

    protected override Size MeasureOverride(Size available)
    {
        if (Child == null) return default;
        Child.Measure(new Size(Math.Min(available.Width, Max), available.Height));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(final));
        return final;
    }
}

/// Leading-aligned bubble content where every child gets the width of the widest one. Children marked
/// `BubbleColumn.Fill` (reply quotes) count only their natural width.
public sealed class BubbleColumn : Panel, IFlexible
{
    public static readonly AttachedProperty<bool> FillProperty =
        AvaloniaProperty.RegisterAttached<BubbleColumn, Control, bool>("Fill");

    public double Spacing { get; init; } = 4;

    public BubbleColumn(double spacing, params Control?[] children)
    {
        Spacing = spacing;
        foreach (var c in children) if (c != null) Children.Add(c);
    }

    public static Control Fill(Control c)
    {
        var f = c.FillWidth(Alignment.Leading);
        f.SetValue(FillProperty, true);
        return f;
    }

    public bool IsFlexible(Orientation axis) => false;

    private static bool Shown(Control c) => c.IsVisible && !Flex.IsEmpty(c);

    protected override Size MeasureOverride(Size available)
    {
        double w = 0;
        foreach (var c in Children)
        {
            if (c.IsVisible && Flex.IsEmpty(c)) c.Measure(available);
            if (!Shown(c)) continue;
            c.Measure(c.GetValue(FillProperty) ? new Size(double.PositiveInfinity, double.PositiveInfinity) : new Size(available.Width, double.PositiveInfinity));
            w = Math.Max(w, Math.Min(c.DesiredSize.Width, available.Width));
        }
        double h = 0;
        var n = 0;
        foreach (var c in Children)
        {
            if (!Shown(c)) continue;
            c.Measure(new Size(w, double.PositiveInfinity));
            h += c.DesiredSize.Height;
            n++;
        }
        return new Size(w, h + Spacing * Math.Max(n - 1, 0));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (var c in Children)
        {
            if (c.IsVisible && Flex.IsEmpty(c)) c.Arrange(default);
            if (!Shown(c)) continue;
            c.Measure(new Size(final.Width, double.PositiveInfinity));
            var h = c.DesiredSize.Height;
            c.Arrange(new Rect(0, y, final.Width, h));
            y += h + Spacing;
        }
        return final;
    }
}

public static class ReactionSets
{
    public static readonly string[] Zalo = ["❤️", "👍", "😆", "😮", "😭", "😡"];
    public static readonly string[] Messenger = ["❤️", "😆", "😮", "😢", "😠", "👍"];
    public static readonly string[] WhatsApp = ["👍", "❤️", "😂", "😮", "😢", "🙏"];
    public static readonly string[] Telegram = ["👍", "❤️", "🔥", "🥰", "👏", "😁", "🤔", "😢"];
}

public static class ReactionPicker
{
    public static Control Make(PlatformSession session, Message message, IReadOnlyList<string> emojis, Action done)
    {
        var mine = message.Reactions?.FirstOrDefault(r => r.FromMe)?.Emoji;
        var others = (message.Reactions ?? []).Where(r => r.Count > 0).ToList();
        var choices = HStack(2, emojis.Select(e => Choice(e, mine == e, () => { session.React(message, e); done(); })).ToArray());
        Control? footer = null;
        if (others.Count > 0)
        {
            var parts = others.Select(r => (Control?)Text($"{r.Emoji} {r.Count}").Font(11).Foreground(SColor.Secondary)).ToList();
            if (mine != null) parts.Add(Text("· Bấm lại để gỡ").Font(11).Foreground(SColor.Tertiary));
            footer = HStack(8, parts.ToArray());
        }
        return VStack(4, choices, footer).Padding(6).Scheme(Scheme.Light);
    }

    private static Control Choice(string emoji, bool selected, Action action) =>
        Button(action, new HoverBox(h =>
            Text(emoji).Font(24).ScaleEffect(h ? 1.25 : 1)
                .Frame(width: 38, height: 38)
                .Background(Circle().Fill(selected ? SColor.Accent.Opacity(0.2) : h ? SColor.Black.Opacity(0.06) : SColor.Clear))));

    /// The emoji strip each app opens from its reaction button or an existing reaction.
    public static Control WithReactionPicker(this Control c, State<bool> isPresented, PlatformSession session, Message message, IReadOnlyList<string> emojis) =>
        c.Popover(isPresented, done => Make(session, message, emojis, done));
}

public sealed record MenuLabels(string Reply, string Edit, string React, string Translate, string HideTranslation, string Copy, string Open,
    string Forward, string DeleteForEveryone, string DeleteForMe)
{
    public static readonly MenuLabels English = new("Reply", "Edit", "React", "Translate", "Hide Translation", "Copy Text", "Open", "Forward…",
        "Delete for Everyone", "Delete for Me");
    public static readonly MenuLabels Vietnamese = new("Trả lời", "Chỉnh sửa", "Bày tỏ cảm xúc", "Dịch", "Ẩn bản dịch", "Sao chép", "Mở", "Chuyển tiếp…",
        "Thu hồi", "Xóa chỉ ở phía tôi");
}

public static class Clip
{
    private static TopLevel? Top()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.Windows.FirstOrDefault(w => w.IsActive) ?? l.MainWindow;
        return null;
    }

    public static void SetText(string text) => _ = Top()?.Clipboard?.SetTextAsync(text);

    /// Text selected in a message (the focused selectable text), if any.
    public static string? SelectedMessageText() =>
        Top()?.FocusManager?.GetFocusedElement() is SelectableTextBlock s && !string.IsNullOrEmpty(s.SelectedText) ? s.SelectedText : null;
}

public static class MessageMenu
{
    public static IEnumerable<MenuEntry> Build(PlatformSession session, Message message, MenuLabels labels, IReadOnlyList<string> reactions)
    {
        var items = new List<MenuEntry>();
        if (!message.IsDeleted && session.Can("reply"))
            items.Add(MenuEntry.Item(labels.Reply, () => session.StartReply(message)));
        if (session.CanEdit(message))
            items.Add(MenuEntry.Item(labels.Edit, () => session.BeginEdit(message)));
        if (!message.IsDeleted && session.Can("reactions"))
            items.Add(MenuEntry.Sub(labels.React, reactions.Select(e =>
                MenuEntry.Item(e, () => session.React(message, e), check: message.Reactions?.Any(r => r.FromMe && r.Emoji == e) == true))));
        if (Translator.CanTranslate(message))
        {
            var shown = Translator.Shared.IsShown(message);
            items.Add(MenuEntry.Item(shown ? labels.HideTranslation : labels.Translate, () => Translator.Shared.Toggle(message)));
        }
        if (message.Text is { Length: > 0 } t)
        {
            var selected = Clip.SelectedMessageText();
            items.Add(MenuEntry.Item(labels.Copy, () => Clip.SetText(selected ?? t)));
        }
        if (message.Media != null) items.Add(MenuEntry.Item(labels.Open, () => session.OpenMedia(message)));
        if (message.Kind == MessageKind.Image && MediaViewer.CanShow(message))
            items.Add(MenuEntry.Item("Nhập chi phí PO (VietFlow)…", () => MediaViewer.Shared.Show(session, message, cost: true)));
        if (Forwarder.CanForward(message)) items.Add(MenuEntry.Item(labels.Forward, () => Forwarder.Shared.Show(message, session)));
        if (session.Can("delete") && !message.IsDeleted)
        {
            items.Add(MenuEntry.Separator);
            if (message.FromMe) items.Add(MenuEntry.Item(labels.DeleteForEveryone, () => session.Delete(message, forEveryone: true)));
            if (session.Can("delete_for_me") || session.Platform != Platform.Messenger)
                items.Add(MenuEntry.Item(labels.DeleteForMe, () => session.Delete(message, forEveryone: false)));
        }
        return items;
    }

    /// Right-click actions shared by every platform (labels are localized per platform).
    public static T MessageContextMenu<T>(this T c, PlatformSession session, Message message, MenuLabels? labels = null,
        IReadOnlyList<string>? reactions = null) where T : Control =>
        c.ContextMenu(() => Build(session, message, labels ?? MenuLabels.English, reactions ?? ReactionSets.WhatsApp));
}

/// Conversations are also shown in the narrow chat-bubble window.
public sealed class ChatMetrics
{
    public static readonly AttachedProperty<bool> CompactProperty =
        AvaloniaProperty.RegisterAttached<ChatMetrics, Control, bool>("Compact", false, inherits: true);

    /// Minimum empty space on the far side of a bubble.
    public static double Gap(double regular, bool compact) => compact ? 36 : regular;
    public const double CompactMediaWidth = 220;

    public static T CompactChat<T>(T c, bool compact = true) where T : Control { c.SetValue(CompactProperty, compact); return c; }
}

/// Builds its content when attached, with the inherited compact-chat flag.
public sealed class CompactAware : Decorator, IFlexible
{
    private readonly Func<bool, Control> _body;
    private bool? _built;
    public CompactAware(Func<bool, Control> body) { _body = body; }
    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var c = GetValue(ChatMetrics.CompactProperty);
        if (_built == c) return;
        _built = c;
        Child = _body(c);
    }
}

/// Image/video/sticker content inside a bubble; resolves encrypted media on demand.
public static class MediaContent
{
    public static Control Make(PlatformSession session, Message message, double maxWidth = 320, double maxHeight = 360, double cornerRadius = 0) =>
        new CompactAware(compact => Build(session, message, maxWidth, maxHeight, cornerRadius, compact));

    public static Size FittedSize(Message message, Media m, double maxWidth, double maxHeight, bool compact)
    {
        var widthLimit = compact ? Math.Min(maxWidth, ChatMetrics.CompactMediaWidth) : maxWidth;
        var heightLimit = compact ? Math.Min(maxHeight, 280) : maxHeight;
        if (message.Kind == MessageKind.Sticker) return compact ? new Size(120, 120) : new Size(160, 160);
        if (m.Aspect is not { } aspect) return new Size(widthLimit * 0.8, widthLimit * 0.6);
        var w = widthLimit;
        var h = w / aspect;
        if (h > heightLimit) { h = heightLimit; w = h * aspect; }
        return new Size(Math.Max(w, 120), Math.Max(h, 80));
    }

    private static Control Build(PlatformSession session, Message message, double maxWidth, double maxHeight, double radius, bool compact)
    {
        var media = message.Media ?? new Media();
        var source = media.Path ?? media.Url ?? media.Thumbnail;
        var size = FittedSize(message, media, maxWidth, maxHeight, compact);
        var image = new RemoteImage(source, () =>
            media.Thumbnail is { } thumb && thumb != source
                ? new RemoteImage(thumb, () => ColorView(SColor.Gray.Opacity(0.2)), maxPixel: 96).Blur(6)
                : Rectangle().Fill(SColor.Gray.Opacity(0.18)).Overlay(new ProgressView { Size = ControlSize.Small }),
            ContentMode.Fill, Math.Max(size.Width, size.Height) * 2)
            .Frame(width: size.Width, height: size.Height).Clipped();
        Control? play = message.Kind is MessageKind.Video or MessageKind.Gif
            ? Circle().Fill(SColor.Black.Opacity(0.45)).Frame(width: 44, height: 44)
                .Overlay(Image("play.fill").Foreground(SColor.White).Font(18))
            : null;
        var view = ZStack(image, play).ClipRounded(radius, true).OnTap(() => session.OpenMedia(message)).Cursor(StandardCursorType.Hand);
        return view.OnAppear(() =>
        {
            if (media.Path == null && media.Url == null && Workspace.Shared.Storage.AutoDownloads(message)) session.EnsureMedia(message);
        });
    }
}

/// Generic file attachment row (icon, name, size).
public static class FileContent
{
    public static Control Make(PlatformSession session, Message message, SColor iconColor, string iconSymbol = "doc.fill",
        SColor? titleColor = null, SColor? subtitleColor = null)
    {
        var m = message.Media;
        var voice = message.Kind is MessageKind.Voice or MessageKind.Audio;
        return HStack(10,
                ZStack(Circle().Fill(iconColor).Frame(width: 42, height: 42),
                    Image(voice ? "play.fill" : iconSymbol).Foreground(SColor.White).Font(17, Weight.Semibold)),
                VStack(HAlign.Leading, 2,
                    Text(Title(message, m)).Font(14, Weight.Medium).Foreground(titleColor ?? SColor.Primary).LineLimit(1),
                    Text(Subtitle(message, m)).Font(12).Foreground(subtitleColor ?? SColor.Secondary)))
            .OnTap(() => session.OpenMedia(message));
    }

    private static string Title(Message message, Media? m) =>
        message.Kind == MessageKind.Voice ? "🎤 " + Duration(m?.Duration) : m?.FileName ?? message.Text ?? "File";

    private static string Subtitle(Message message, Media? m)
    {
        var parts = new List<string>();
        if (m?.Size is { } s) parts.Add(Bytes(s));
        if (message.Kind == MessageKind.Audio && m?.Duration is { } d) parts.Add(Duration(d));
        if (m?.FileName is { } f && Path.GetExtension(f).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext) parts.Add(ext);
        return string.Join(" · ", parts);
    }

    public static string Duration(double? d) => d is { } v ? $"{(int)v / 60}:{(int)v % 60:00}" : "";

    /// `ByteCountFormatter` with `.file` style (decimal units).
    public static string Bytes(double b)
    {
        if (b < 1000) return $"{b:0} byte";
        string[] units = ["KB", "MB", "GB", "TB"];
        var v = b / 1000;
        var i = 0;
        while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
        return (i == 0 ? v.ToString("0", Inv.C) : v.ToString("0.#", Inv.C)) + " " + units[i];
    }
}

/// Status shown in place of the chat UI while a bridge is starting or has failed.
public static class BridgeStatusView
{
    public static Control Make(PlatformSession session, SColor tint) => View(() =>
        (session.BridgeError is { } err
            ? VStack(14,
                Image("exclamationmark.triangle.fill").Font(34).Foreground(SColor.Orange),
                Text(err).MultilineAlignment(TextAlignment.Center).Foreground(SColor.Secondary).Frame(maxWidth: 420),
                MacButton.Titled("Khởi động lại bridge", session.Restart).Tint(tint))
            : VStack(14, new ProgressView(), Text($"Đang kết nối {session.Platform.DisplayName()}…").Foreground(SColor.Secondary)))
        .Fill());
}

/// Doodle wallpaper: scattered glyphs over a fill.
public sealed class DoodleWallpaper : Control, IFlexible
{
    private readonly Paint _background;
    private readonly SColor _glyph;
    private readonly string[] _symbols;
    private readonly double _spacing, _glyphSize;

    public DoodleWallpaper(Paint background, SColor glyphColor, string[] symbols, double spacing = 64, double glyphSize = 18)
    {
        _background = background;
        _glyph = glyphColor;
        _symbols = symbols;
        _spacing = spacing;
        _glyphSize = glyphSize;
        IsHitTestVisible = false;
    }

    public bool IsFlexible(Orientation axis) => true;
    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 0 : a.Width, double.IsInfinity(a.Height) ? 0 : a.Height);

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(_background.Brush(Scheme.Light, new Rect(size)), new Rect(size));
        if (_symbols.Length == 0) return;
        var rng = new SeededRandom(42);
        var brush = new ImmutableSolidColorBrush(_glyph.Resolve(Scheme.Light));
        var masks = _symbols.Select(s => Symbol.Mask(s, "regular")).ToArray();
        double y = 8;
        var row = 0;
        using var clip = ctx.PushClip(new Rect(size));
        while (y < size.Height + _spacing)
        {
            var x = row % 2 == 0 ? 8 : _spacing / 2;
            while (x < size.Width + _spacing)
            {
                var mask = masks[(int)(rng.Next() % (ulong)masks.Length)];
                var jx = (double)(rng.Next() % 16) - 8;
                var jy = (double)(rng.Next() % 16) - 8;
                var angle = ((double)(rng.Next() % 50) - 25) * Math.PI / 180;
                if (mask != null)
                {
                    var aspect = mask.PixelSize.Width / (double)Math.Max(1, mask.PixelSize.Height);
                    var w = aspect >= 1 ? _glyphSize : _glyphSize * aspect;
                    var h = aspect >= 1 ? _glyphSize / aspect : _glyphSize;
                    var m = Matrix.CreateRotation(angle) * Matrix.CreateTranslation(x + jx, y + jy);
                    using (ctx.PushTransform(m)) Symbol.Draw(ctx, mask, new Rect(-w / 2, -h / 2, w, h), brush);
                }
                x += _spacing;
            }
            y += _spacing * 0.8;
            row++;
        }
    }
}

public struct SeededRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        unchecked { _state = _state * 6364136223846793005UL + 1442695040888963407UL; }
        return _state >> 33;
    }
}

public static class Shapes
{
    /// Rounded rectangle with per-corner radii (Messenger grouping, WhatsApp/Telegram tails).
    public static Geometry RoundedRect(Rect r, double tl, double tr, double br, double bl)
    {
        var max = Math.Min(r.Width, r.Height) / 2;
        tl = Math.Min(tl, max); tr = Math.Min(tr, max); br = Math.Min(br, max); bl = Math.Min(bl, max);
        var g = new StreamGeometry();
        using var c = g.Open();
        // Continuous corners: a slightly longer, flatter curve than a circular arc.
        const double k = 0.62;
        c.BeginFigure(new Point(r.Left + tl * 1.2, r.Top), true);
        c.LineTo(new Point(r.Right - tr * 1.2, r.Top));
        Corner(c, new Point(r.Right - tr * 1.2, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Top + tr * 1.2), k);
        c.LineTo(new Point(r.Right, r.Bottom - br * 1.2));
        Corner(c, new Point(r.Right, r.Bottom - br * 1.2), new Point(r.Right, r.Bottom), new Point(r.Right - br * 1.2, r.Bottom), k);
        c.LineTo(new Point(r.Left + bl * 1.2, r.Bottom));
        Corner(c, new Point(r.Left + bl * 1.2, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, r.Bottom - bl * 1.2), k);
        c.LineTo(new Point(r.Left, r.Top + tl * 1.2));
        Corner(c, new Point(r.Left, r.Top + tl * 1.2), new Point(r.Left, r.Top), new Point(r.Left + tl * 1.2, r.Top), k);
        c.EndFigure(true);
        return g;
    }

    private static void Corner(StreamGeometryContext c, Point from, Point corner, Point to, double k) =>
        c.CubicBezierTo(from + (corner - from) * k, to + (corner - to) * k, to);

    public static ShapeView Bubble(double tl, double tr, double br, double bl) => PathShape(r => RoundedRect(r, tl, tr, br, bl));

    public enum TailCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    /// Small curved tail drawn next to a bubble corner.
    public static ShapeView Tail(TailCorner corner) => PathShape(r =>
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            switch (corner)
            {
                case TailCorner.TopRight:
                    c.BeginFigure(new Point(r.Left, r.Top), true);
                    c.LineTo(new Point(r.Right, r.Top));
                    c.QuadraticBezierTo(new Point(r.Left + r.Width * 0.3, r.Top + r.Height * 0.2), new Point(r.Left, r.Bottom));
                    break;
                case TailCorner.TopLeft:
                    c.BeginFigure(new Point(r.Right, r.Top), true);
                    c.LineTo(new Point(r.Left, r.Top));
                    c.QuadraticBezierTo(new Point(r.Right - r.Width * 0.3, r.Top + r.Height * 0.2), new Point(r.Right, r.Bottom));
                    break;
                case TailCorner.BottomRight:
                    c.BeginFigure(new Point(r.Left, r.Top), true);
                    c.QuadraticBezierTo(new Point(r.Left + r.Width * 0.2, r.Bottom), new Point(r.Right, r.Bottom));
                    c.LineTo(new Point(r.Left, r.Bottom));
                    break;
                default:
                    c.BeginFigure(new Point(r.Right, r.Top), true);
                    c.QuadraticBezierTo(new Point(r.Right - r.Width * 0.2, r.Bottom), new Point(r.Left, r.Bottom));
                    c.LineTo(new Point(r.Right, r.Bottom));
                    break;
            }
            c.EndFigure(true);
        }
        return g;
    });
}

/// Avatar circle from any source with a platform-specific placeholder.
public static class AvatarView
{
    public static Control Make(string? source, double size, Func<Control> placeholder) =>
        new RemoteImage(source, placeholder, ContentMode.Fill, size * 2).Frame(width: size, height: size).ClipCircle();
}

/// Covers a QR code once it can no longer be scanned.
public static class QRExpiryOverlay
{
    public static Control Make(PlatformSession session, SColor tint, string label = "Nhấp để tải lại mã QR") =>
        Timeline(TimeSpan.FromSeconds(1), _ =>
        {
            var expired = session.IsQRExpired;
            if (!session.QrRefreshing && !expired) return null;
            Control inner = session.QrRefreshing
                ? new ProgressView { Size = ControlSize.Large }
                : Button(() => session.RefreshQR(), VStack(10,
                    Circle().Fill(tint).Frame(width: 72, height: 72)
                        .Overlay(Image("arrow.clockwise").Font(28, Weight.Semibold).Foreground(SColor.White)),
                    Text(label).Font(13, Weight.Medium).Foreground(SColor.Black.Opacity(0.75)).MultilineAlignment(TextAlignment.Center))
                    .ContentShape());
            return ZStack(Rectangle().Fill(SColor.White.Opacity(0.88)), inner)
                .OnAppear(() => { if (expired && !session.QrRefreshing) session.RefreshQR(auto: true); });
        });
}
