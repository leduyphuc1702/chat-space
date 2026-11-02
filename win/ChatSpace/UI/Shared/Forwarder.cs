using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

public enum ForwardState { Sending, Sent, Failed }

/// `Forwarder.Status`: sending, sent, or failed(message).
public sealed record ForwardStatus(ForwardState State, string? Error = null)
{
    public static readonly ForwardStatus Sending = new(ForwardState.Sending);
    public static readonly ForwardStatus Sent = new(ForwardState.Sent);
    public static ForwardStatus Failed(string error) => new(ForwardState.Failed, error);
}

/// Forwards one message to any number of chats on any platform. Within a platform that has capability
/// `forward` the platform's own forward is used (its "Forwarded" label, nothing re-uploaded); everywhere
/// else the text is sent again and the media file is downloaded from the source and uploaded to the target.
public sealed class Forwarder : Observable
{
    public static readonly Forwarder Shared = new();

    private Message? _message;
    private Platform? _sourcePlatform;
    private ImmutableList<ChatRef> _picked = [];
    private ImmutableDictionary<ChatRef, ForwardStatus> _status = ImmutableDictionary<ChatRef, ForwardStatus>.Empty;
    private bool _running;
    private string _note = "";
    private string _query = "";
    private Platform? _filter;
    private ImmutableDictionary<ChatRef, double> _recency = ImmutableDictionary<ChatRef, double>.Empty;

    public Message? Message { get => Get(ref _message); private set => Set(ref _message, value); }
    public Platform? SourcePlatform { get => Get(ref _sourcePlatform); private set => Set(ref _sourcePlatform, value); }
    public ImmutableList<ChatRef> Picked { get => Get(ref _picked); private set => Set(ref _picked, value); }
    public ImmutableDictionary<ChatRef, ForwardStatus> Status { get => Get(ref _status); private set => Set(ref _status, value); }
    public bool Running { get => Get(ref _running); private set => Set(ref _running, value); }
    public string Note { get => Get(ref _note); set => Set(ref _note, value); }
    public string Query { get => Get(ref _query); set => Set(ref _query, value); }
    public Platform? Filter { get => Get(ref _filter); set => Set(ref _filter, value); }
    /// Chat activity when the picker opened: the list keeps that order while the forwards themselves
    /// bump the picked chats to the top of their platforms.
    public ImmutableDictionary<ChatRef, double> Recency { get => Get(ref _recency); private set => Set(ref _recency, value); }

    private MacWindow? _window;
    private ForwardView? _view;
    private Task<PreparedFile>? _prepared;
    private HashSet<ChatRef> _noteSent = [];

    /// The picker window, for the headless screenshot mode.
    public TopLevel? ScreenshotWindow => _window;

    public static bool CanForward(Message m)
    {
        if (m.IsDeleted || m.Status is DeliveryStatus.Pending or DeliveryStatus.Failed) return false;
        if (m.Kind is MessageKind.System or MessageKind.Call or MessageKind.Unsupported) return false;
        return CarriesFile(m) || !string.IsNullOrWhiteSpace(m.Text);
    }

    public static bool CarriesFile(Message m) =>
        m.Media != null && m.Kind is MessageKind.Image or MessageKind.Video or MessageKind.Gif or MessageKind.Audio
            or MessageKind.Voice or MessageKind.File or MessageKind.Sticker;

    // MARK: Window

    public void Show(Message message, PlatformSession session)
    {
        if (!CanForward(message)) return;
        if (!Running)
        {
            Message = message;
            SourcePlatform = session.Platform;
            Picked = [];
            Status = ImmutableDictionary<ChatRef, ForwardStatus>.Empty;
            Note = "";
            Query = "";
            Filter = null;
            _noteSent = [];
            _prepared = null;
            var recency = ImmutableDictionary.CreateBuilder<ChatRef, double>();
            foreach (var p in Platforms.All)
                foreach (var c in AppModel.Shared.Session(p).Chats) recency[new ChatRef(p, c.Id)] = c.Timestamp;
            Recency = recency.ToImmutable();
        }
        var win = _window ?? MakeWindow();
        _window = win;
        // Above the floating chat bubble when opened from there.
        win.Topmost = ChatHeads.Shared.Expanded != null;
        var wasVisible = win.IsVisible;
        if (!wasVisible) win.Show();
        if (!wasVisible) FwChrome.Center(win);
        win.Activate();
        _view?.FocusSearch();
    }

    public void Close() => _window?.Hide();

    private MacWindow MakeWindow()
    {
        _view = new ForwardView(this);
        var win = new MacWindow("Chuyển tiếp tin nhắn", _view.Fill(), scheme: FwChrome.Current())
        {
            Width = 440,
            Height = MacWindow.TitleBarHeight + 620,
            MinWidth = 380,
            MinHeight = MacWindow.TitleBarHeight + 460,
            HideOnClose = true,
            Tag = ChatHeads.KeepsChatOpenIdentifier,
        };
        FwChrome.Apply(win);
        win.Background = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(FwChrome.PanelBackground(win.Scheme).Resolve(win.Scheme));
        FwChrome.DimMinimize(win);
        return win;
    }

    // MARK: Picking and sending

    public void Toggle(ChatRef r)
    {
        if (Running || StatusOf(r)?.State == ForwardState.Sent) return;
        var i = Picked.IndexOf(r);
        if (i >= 0)
        {
            Picked = Picked.RemoveAt(i);
            Status = Status.Remove(r);
        }
        else Picked = Picked.Add(r);
    }

    public ForwardStatus? StatusOf(ChatRef r) => Status.TryGetValue(r, out var s) ? s : null;

    /// Picked chats that still need sending (not yet sent, or failed).
    public IReadOnlyList<ChatRef> Outstanding => Picked.Where(r => StatusOf(r)?.State != ForwardState.Sent).ToList();
    public bool HasFailures => Status.Values.Any(s => s.State == ForwardState.Failed);

    public void Send()
    {
        if (Message is not { } message || SourcePlatform is not { } sourcePlatform || Running) return;
        var targets = Outstanding;
        if (targets.Count == 0) return;
        var source = AppModel.Shared.Session(sourcePlatform);
        var note = Note.Trim();
        Running = true;
        foreach (var t in targets) Status = Status.SetItem(t, ForwardStatus.Sending);
        _ = Run(message, source, targets, note);
    }

    private async Task Run(Message message, PlatformSession source, IReadOnlyList<ChatRef> targets, string note)
    {
        // One after another: a burst of parallel sends looks like spam to the platforms.
        foreach (var t in targets)
        {
            try
            {
                await Deliver(message, source, t, note);
                Status = Status.SetItem(t, ForwardStatus.Sent);
            }
            catch (Exception e)
            {
                Status = Status.SetItem(t, ForwardStatus.Failed(e.Message));
            }
        }
        Running = false;
        if (!AllSent()) return;
        await Task.Delay(700);
        if (!Running && Message?.Id == message.Id && AllSent()) Close();
    }

    private bool AllSent() => Picked.All(r => StatusOf(r)?.State == ForwardState.Sent);

    private async Task Deliver(Message m, PlatformSession source, ChatRef r, string note)
    {
        var target = AppModel.Shared.Session(r.Platform);
        if (target.AuthState != AuthState.LoggedIn) throw new ForwardError($"{r.Platform.DisplayName()} chưa đăng nhập");
        if (note.Length > 0 && !_noteSent.Contains(r))
        {
            await target.Post(note, r.ChatId);
            _noteSent.Add(r);
        }
        if (r.Platform == source.Platform && source.Can("forward"))
        {
            await source.ForwardNatively(m, r.ChatId);
            return;
        }
        var text = (m.Text ?? "").Trim();
        if (!CarriesFile(m))
        {
            await target.Post(text, r.ChatId);
            return;
        }
        var file = await PreparedFileFor(m, source);
        // A sticker's text is its emoji and a voice note's a transcript: neither is a caption.
        var caption = m.Kind is MessageKind.Sticker or MessageKind.Voice ? "" : text;
        await target.Post(caption, r.ChatId, file.Path, file.Kind);
    }

    /// The media is fetched once per forward, however many chats it goes to.
    private async Task<PreparedFile> PreparedFileFor(Message m, PlatformSession source)
    {
        if (_prepared != null) return await _prepared;
        var task = ForwardFile.Prepare(m, source);
        _prepared = task;
        try { return await task; }
        catch
        {
            _prepared = null;
            throw;
        }
    }
}

public sealed class ForwardError(string message) : Exception(message);

public sealed record PreparedFile(string Path, MessageKind Kind);

/// Turns a message's media into a local file another platform will accept: the original file name, and
/// photos as JPEG/PNG (cached WhatsApp/Telegram photos may be HEIC, stickers WebP, which other platforms
/// would deliver as a document).
public static class ForwardFile
{
    public static readonly string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChatSpace-forward");
    private static readonly HttpClient Http = new();

    public static async Task<PreparedFile> Prepare(Message m, PlatformSession session)
    {
        var source = await session.MediaSource(m)
            ?? throw new ForwardError($"Không tải được tệp gốc từ {session.Platform.DisplayName()}");
        var media = m.Media ?? new Media();
        var videoGif = m.Kind == MessageKind.Gif && ((media.Mime ?? "").StartsWith("video") || source.Contains(".mp4", StringComparison.OrdinalIgnoreCase));
        var kind = m.Kind switch
        {
            MessageKind.Image or MessageKind.Sticker => MessageKind.Image,
            MessageKind.Gif => videoGif ? MessageKind.Video : MessageKind.Image,
            MessageKind.Video => MessageKind.Video,
            MessageKind.Voice => MessageKind.Voice,
            MessageKind.Audio => MessageKind.Audio,
            _ => MessageKind.File,
        };
        var stamp = m.Date.ToUniversalTime().ToString("yyyy-MM-dd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var baseName = NonEmpty(media.FileName?.Trim()) ?? $"ChatSpace {stamp}";
        var mimeExt = media.Mime is { } mime ? ExtensionFor(mime) : null;
        var fallbackExt = kind switch
        {
            MessageKind.Image => "jpg", MessageKind.Video => "mp4", MessageKind.Voice => "ogg", MessageKind.Audio => "m4a", _ => "bin",
        };
        var path = await Task.Run(async () =>
        {
            var local = await Fetch(source);
            var dir = System.IO.Path.Combine(Directory, Guid.NewGuid().ToString().ToUpperInvariant());
            System.IO.Directory.CreateDirectory(dir);
            var ext = new[] { System.IO.Path.GetExtension(baseName), System.IO.Path.GetExtension(local), mimeExt ?? "" }
                .Select(e => e.TrimStart('.').ToLowerInvariant()).FirstOrDefault(e => e.Length is >= 1 and <= 5) ?? fallbackExt;
            if (kind == MessageKind.Image && WriteImage(local, dir, Stem(baseName)) is { } image) return image;
            var name = System.IO.Path.GetExtension(baseName).Length == 0 ? $"{Safe(baseName)}.{ext}" : Safe(baseName);
            var dest = System.IO.Path.Combine(dir, name);
            File.Copy(local, dest);
            return dest;
        });
        return new PreparedFile(path, kind);
    }

    /// Local file for a path or an http URL (Zalo and Messenger serve media straight from their CDN).
    private static async Task<string> Fetch(string source)
    {
        if (System.IO.Path.IsPathRooted(source) && !source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return source;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var url)) throw new ForwardError("Đường dẫn tệp không hợp lệ");
        if (url.IsFile) return url.LocalPath;
        System.IO.Directory.CreateDirectory(Directory);
        var ext = System.IO.Path.GetExtension(url.AbsolutePath).TrimStart('.');
        var kept = System.IO.Path.Combine(Directory, Guid.NewGuid().ToString().ToUpperInvariant() + (ext.Length is >= 1 and <= 5 ? "." + ext : ""));
        // The demo never reaches a server: the "download" is an empty stand-in file.
        if (DemoData.Enabled)
        {
            await File.WriteAllBytesAsync(kept, []);
            return kept;
        }
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new ForwardError($"Tải tệp gốc thất bại (HTTP {(int)response.StatusCode})");
        await using (var output = File.Create(kept))
            await response.Content.CopyToAsync(output);
        return kept;
    }

    /// JPEG, PNG and GIF go as they are; any other image format is re-encoded (PNG when it has transparency).
    /// Null when the file is not a decodable image, so it is sent as a plain file instead.
    private static string? WriteImage(string local, string dir, string stem)
    {
        var head = new byte[12];
        try
        {
            using var fs = File.OpenRead(local);
            if (fs.Read(head, 0, head.Length) < 4) return null;
        }
        catch { return null; }
        string? kept = head switch
        {
            [0xFF, 0xD8, 0xFF, ..] => "jpg",
            [0x89, 0x50, 0x4E, 0x47, ..] => "png",
            [0x47, 0x49, 0x46, 0x38, ..] => "gif",
            _ => null,
        };
        if (kept != null)
        {
            var dest = System.IO.Path.Combine(dir, stem + "." + kept);
            File.Copy(local, dest);
            return dest;
        }
        try
        {
            using var bmp = new Avalonia.Media.Imaging.Bitmap(local);
            var dest = System.IO.Path.Combine(dir, stem + ".png");
            bmp.Save(dest);
            return dest;
        }
        catch { return null; }
    }

    private static string Stem(string name)
    {
        var s = Safe(System.IO.Path.GetFileNameWithoutExtension(name));
        return s.Length == 0 ? "image" : s;
    }

    private static string Safe(string name)
    {
        var s = name.Replace('/', '-').Replace(':', '-');
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// `UTType(mimeType:).preferredFilenameExtension` for the types chats carry.
    private static string? ExtensionFor(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpg", "image/png" => "png", "image/gif" => "gif", "image/webp" => "webp",
        "image/heic" => "heic", "image/heif" => "heif", "image/bmp" => "bmp", "image/tiff" => "tiff",
        "video/mp4" => "mp4", "video/quicktime" => "mov", "video/webm" => "webm", "video/x-matroska" => "mkv",
        "audio/ogg" or "audio/opus" => "ogg", "audio/mpeg" => "mp3", "audio/mp4" or "audio/x-m4a" => "m4a", "audio/aac" => "aac",
        "audio/wav" or "audio/x-wav" => "wav", "application/pdf" => "pdf", "application/zip" => "zip", "text/plain" => "txt",
        "application/msword" => "doc", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
        "application/vnd.ms-excel" => "xls", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
        _ => null,
    };
}

// MARK: Window chrome helpers (local to the forward and bubble windows)

/// Appearance plumbing shared by the picker and the bubble panels: the system scheme, dark Avalonia theme
/// for the built-in controls, centring (`NSWindow.center`), and a disabled minimise light (`NSPanel` without `.miniaturizable`).
internal static class FwChrome
{
    /// `CHATSPACE_SCHEME=dark|light` overrides (screenshots); otherwise the OS setting, like NSPanel following the system appearance.
    public static Scheme Current()
    {
        if (Environment.GetEnvironmentVariable("CHATSPACE_SCHEME") is { } env)
            return env.Equals("dark", StringComparison.OrdinalIgnoreCase) ? Scheme.Dark : Scheme.Light;
        try
        {
            if (Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark) return Scheme.Dark;
        }
        catch { }
        return MacWindow.SystemScheme;
    }

    public static void Apply(Window win)
    {
        var scheme = win is MacWindow m ? m.Scheme : Current();
        win.RequestedThemeVariant = scheme == Scheme.Dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    /// `NSColor.windowBackgroundColor` of a titled panel: the dark panels in the macOS captures are 0x242424.
    public static SColor PanelBackground(Scheme scheme) => scheme == Scheme.Dark ? SColor.Hex(0x242424) : SColor.WindowBackground;

    /// A text field with SwiftUI's accent-coloured caret, and text and placeholder that follow the dark scheme
    /// (the Kit field is drawn for light).
    public static Field Themed(Field f, Scheme scheme)
    {
        f.Box.CaretBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(SColor.Accent.Resolve(scheme));
        if (scheme != Scheme.Dark) return f;
        f.Box.Foreground = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255));
        foreach (var t in f.Children.OfType<SText>()) t.SetValue(Env.ForegroundProperty, SColor.White.Opacity(0.42));
        return f;
    }

    /// `NSWindow.center()`: horizontally centred, a little above the vertical middle of the work area.
    public static void Center(Window win)
    {
        try
        {
            var screen = win.Screens.ScreenFromWindow(win) ?? win.Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            var s = screen.Scaling;
            var w = (int)(win.Width * s);
            var h = (int)(win.Height * s);
            win.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - w) / 2), area.Y + Math.Max(0, (int)((area.Height - h) * 0.45)));
        }
        catch { }
    }

    /// The panels are not miniaturizable: the yellow light is drawn grey like AppKit does.
    public static void DimMinimize(MacWindow win)
    {
        if (win.Content is Panel root) root.Children.Insert(root.Children.Count - 1, new DimmedLight(win.Scheme));
    }

    private sealed class DimmedLight : Control
    {
        private readonly Scheme _scheme;

        public DimmedLight(Scheme scheme)
        {
            _scheme = scheme;
            Width = 12;
            Height = 12;
            Margin = new Thickness(40, 8, 0, 0);
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext ctx)
        {
            var fill = _scheme == Scheme.Dark ? Color.FromRgb(0x4B, 0x4B, 0x4D) : Color.FromRgb(0xD1, 0xD0, 0xD2);
            ctx.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 0.5), new Rect(Bounds.Size).Deflate(0.25));
        }
    }
}

/// `TextField` with `.roundedBorder` as AppKit draws it in the forward picker: a 20 pt bezel in a 22 pt frame, 1 pt in
/// from the sides, text inset 10 pt, and the dark bezel's brighter top and bottom edges.
internal sealed class FwRoundedField : Decorator, IFlexible
{
    private readonly Field _field;
    private readonly Scheme _scheme;

    public FwRoundedField(Field field, Scheme scheme)
    {
        _field = field;
        _scheme = scheme;
        Child = field.PaddingH(11).Frame(height: 22);
        field.Box.GotFocus += (_, _) => InvalidateVisual();
        field.Box.LostFocus += (_, _) => InvalidateVisual();
    }

    public bool IsFlexible(Orientation axis) => axis == Orientation.Horizontal;

    protected override Size MeasureOverride(Size available)
    {
        Child!.Measure(new Size(available.Width, 22));
        return new Size(double.IsInfinity(available.Width) ? Child.DesiredSize.Width : available.Width, 22);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child!.Arrange(new Rect(finalSize));
        return finalSize;
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(1, 1, Math.Max(0, Bounds.Width - 2), 20);
        var dark = _scheme == Scheme.Dark;
        const double radius = 5;
        if (dark)
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromArgb(0x2B, 255, 255, 255)), 1), r.Deflate(0.5), radius, radius);
            // The top shading follows the bezel's rounded top corners (a rounded rect with square bottom corners).
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x2C, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
            }, null, new RoundedRect(new Rect(r.X, r.Y, r.Width, 5), new CornerRadius(radius, radius, 0, 0)));
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), 1), new Point(r.X + radius, r.Y + 0.5), new Point(r.Right - radius, r.Y + 0.5));
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1), new Point(r.X + radius, r.Bottom - 0.5), new Point(r.Right - radius, r.Bottom - 0.5));
        }
        else
            ctx.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 0.5), r.Deflate(0.25), radius, radius);
        if (_field.Box.IsFocused)
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x7A, 0xFF)), 3), r.Inflate(1), radius + 1, radius + 1);
    }
}

/// `.buttonStyle(.borderedProminent)` as the picker shows it: 20 pt tall inside a 22 pt frame, the accent fill while
/// there is something to send, and the flat grey bezel while disabled.
internal sealed class FwSendButton : Decorator
{
    private readonly Action _action;
    private readonly Scheme _scheme;
    private readonly bool _enabled;
    private bool _pressed;

    public FwSendButton(string title, Action action, Scheme scheme, bool enabled)
    {
        _action = action;
        _scheme = scheme;
        _enabled = enabled;
        var dark = scheme == Scheme.Dark;
        var label = Text(title).Font(13).Frame(minWidth: 70)
            .Foreground(enabled ? SColor.White : dark ? SColor.White.Opacity(0.27) : SColor.Black.Opacity(0.25));
        Child = label.PaddingH(8).Frame(height: 22);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_enabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(0, 1, Bounds.Width, 20);
        const double radius = 5;
        if (_enabled)
        {
            var c = SColor.Accent.Resolve(_scheme);
            if (_pressed) c = Color.FromRgb((byte)(c.R * 0.85), (byte)(c.G * 0.85), (byte)(c.B * 0.85));
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x25, 0, 0, 0)), null, r.Translate(new Vector(0, 0.5)), radius, radius);
            ctx.DrawRectangle(new SolidColorBrush(c), null, r, radius, radius);
        }
        else if (_scheme == Scheme.Dark)
        {
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x20, 0, 0, 0)), 1), r.Inflate(0.5), radius + 0.5, radius + 0.5);
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x0C, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x08, 255, 255, 255), 1) },
            }, null, r, radius, radius);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), 1), new Point(r.X + radius, r.Y + 0.5), new Point(r.Right - radius, r.Y + 0.5));
        }
        else
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x0D, 0, 0, 0)),
                new Pen(new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)), 0.5), r.Deflate(0.25), radius, radius);
        }
    }
}

// MARK: Picker

public sealed record ForwardRowItem(ChatRef Ref, Chat Chat, bool Picked, ForwardStatus? Status);

public sealed class ForwardView : Decorator
{
    private readonly Forwarder _f;
    private readonly Scheme _scheme;
    private readonly Field _search;
    private readonly Field _note;
    private readonly FwRoundedField _noteBox;

    public ForwardView(Forwarder forwarder)
    {
        _f = forwarder;
        _scheme = FwChrome.Current();
        _search = FwChrome.Themed(new Field("Tìm cuộc trò chuyện trên mọi nền tảng", new Binding<string>(() => _f.Query, v => _f.Query = v)), _scheme);
        _note = FwChrome.Themed(new Field("Thêm lời nhắn (không bắt buộc)", new Binding<string>(() => _f.Note, v => _f.Note = v)), _scheme);
        _note.OnSubmit = _f.Send;
        _noteBox = new FwRoundedField(_note, _scheme);
        Child = VStack(0,
            View(() => _f.Message is { } m && _f.SourcePlatform is { } p ? ForwardPreview.Make(m, p).Padding(12) : null),
            SearchArea(),
            View(PickedStrip),
            Divider(),
            ChatList(),
            Divider(),
            View(Footer));
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    public void FocusSearch() => _search.FocusField();

    /// `.onExitCommand` closes the panel; Return is the default action (Send).
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _f.Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { _f.Send(); e.Handled = true; }
    }

    private static IEnumerable<Platform> LoggedIn() =>
        Platforms.All.Where(p => AppModel.Shared.Session(p).AuthState == AuthState.LoggedIn);

    private Control SearchArea() => View(() =>
    {
        var clear = _f.Query.Length > 0
            ? Button(() => _f.Query = "", Image("xmark.circle.fill").Foreground(SColor.Tertiary))
            : null;
        return VStack(8,
                HStack(6, Image("magnifyingglass").Foreground(SColor.Secondary), new KeepHost(_search), clear)
                    .PaddingH(10).Frame(height: 30)
                    .Background(RoundedRectangle(8).Fill(SColor.Primary.Opacity(0.06))),
                PlatformFilter())
            .PaddingH(12).PaddingBottom(8);
    });

    private Control PlatformFilter()
    {
        var items = new List<Control?> { Chip("Tất cả", null, _f.Filter == null, () => _f.Filter = null) };
        foreach (var p in LoggedIn())
        {
            var platform = p;
            items.Add(Chip(platform.DisplayName(), platform, _f.Filter == platform, () => _f.Filter = _f.Filter == platform ? null : platform));
        }
        items.Add(Spacer(0));
        return HStack(6, items.ToArray());
    }

    private static Control Chip(string title, Platform? icon, bool on, Action action) =>
        Button(action,
            HStack(4,
                    icon is { } p ? new PlatformIcon(p, 13) : null,
                    Text(title).Font(11.5, on ? Weight.Semibold : Weight.Regular))
                .PaddingH(8).Frame(height: 22)
                .Background(Capsule().Fill(on ? SColor.Accent.Opacity(0.18) : SColor.Primary.Opacity(0.05)))
                .Foreground(on ? SColor.Accent : SColor.Primary)
                .ContentShape());

    private IReadOnlyList<ForwardRowItem> Rows()
    {
        var needle = CustomGroup.Normalize(_f.Query);
        var filter = _f.Filter;
        var picked = _f.Picked;
        var status = _f.Status;
        var recency = _f.Recency;
        var all = LoggedIn().Where(p => filter == null || filter == p)
            .SelectMany(p => AppModel.Shared.Session(p).Chats.Select(c => (r: new ChatRef(p, c.Id), c)));
        return all.Where(x => needle.Length == 0 || CustomGroup.Normalize(x.c.Title).Contains(needle))
            .OrderByDescending(x => recency.TryGetValue(x.r, out var t) ? t : x.c.Timestamp)
            .Take(300)
            .Select(x => new ForwardRowItem(x.r, x.c, picked.Contains(x.r), status.TryGetValue(x.r, out var s) ? s : null))
            .ToList();
    }

    private Control ChatList() =>
        ScrollView(ForEach(Rows, x => x.Ref, x => ForwardRow.Make(x, _scheme, () => _f.Toggle(x.Ref))).PaddingV(4).PaddingH(6))
            .FillHeight()
            .Overlay(Alignment.Center, View(() => Rows().Count == 0
                ? Text(LoggedIn().Any() ? "Không có cuộc trò chuyện phù hợp" : "Chưa đăng nhập nền tảng nào").Font(13).Foreground(SColor.Secondary)
                : null));

    private Control? PickedStrip()
    {
        if (_f.Picked.IsEmpty) return null;
        var chips = new List<Control?>();
        foreach (var r in _f.Picked)
        {
            var ref_ = r;
            var title = AppModel.Shared.Session(ref_.Platform).Chats.FirstOrDefault(c => c.Id == ref_.ChatId)?.Title ?? ref_.ChatId;
            var removable = !_f.Running && _f.StatusOf(ref_)?.State != ForwardState.Sent;
            chips.Add(Button(() => _f.Toggle(ref_),
                HStack(4,
                        new PlatformIcon(ref_.Platform, 13),
                        Text(title).Font(11.5).LineLimit(1),
                        removable ? Image("xmark").Font(8, Weight.Bold).Foreground(SColor.Secondary) : null)
                    .PaddingH(8).Frame(height: 22)
                    .Background(Capsule().Fill(SColor.Accent.Opacity(0.12)))
                    .ContentShape()));
        }
        return ScrollView(HStack(6, chips.ToArray()).PaddingH(12), vertical: false, horizontal: true, indicators: false).PaddingBottom(8);
    }

    private bool CrossPlatform()
    {
        if (_f.SourcePlatform is not { } source) return false;
        var native = AppModel.Shared.Session(source).Can("forward");
        return _f.Picked.Any(r => r.Platform != source || !native);
    }

    private Control Footer()
    {
        var n = _f.Outstanding.Count;
        var running = _f.Running;
        var title = running ? "Đang gửi…" : _f.HasFailures && n > 0 ? $"Gửi lại ({n})" : n > 0 ? $"Gửi ({n})" : "Gửi";
        return VStack(HAlign.Leading, 6,
                CrossPlatform()
                    ? Text("Nội dung và tệp sẽ được gửi lại như tin nhắn mới ở những nơi không hỗ trợ chuyển tiếp gốc.")
                        .Font(11).Foreground(SColor.Secondary).FixedSize(false, true)
                    : null,
                HStack(8,
                    new KeepHost(_noteBox),
                    new FwSendButton(title, _f.Send, _scheme, enabled: n > 0 && !running)))
            .Padding(12);
    }
}

public static class ForwardPreview
{
    public static Control Make(Message message, Platform platform)
    {
        var media = message.Media;
        Control? thumb = null;
        if (message.Kind is MessageKind.Image or MessageKind.Sticker or MessageKind.Gif or MessageKind.Video
            && (media?.Path ?? media?.Url ?? media?.Thumbnail) is { } src)
            thumb = new RemoteImage(src, () => ColorView(SColor.Gray.Opacity(0.2)), ContentMode.Fill, 96)
                .Frame(width: 44, height: 44).ClipRounded(6);
        else if (media != null)
            thumb = RoundedRectangle(6).Fill(SColor.Accent.Opacity(0.15))
                .Overlay(Image(Icon(message)).Foreground(SColor.Accent))
                .Frame(width: 44, height: 44);
        return HStack(10,
                thumb,
                VStack(HAlign.Leading, 3,
                    HStack(4,
                        new PlatformIcon(platform, 13),
                        Text(message.FromMe ? "Tin nhắn của bạn" : $"Tin nhắn của {message.SenderName ?? "người gửi"}")
                            .Font(11.5, Weight.Semibold).Foreground(SColor.Secondary).LineLimit(1)),
                    Text(message.Preview(photo: "Hình ảnh", video: "Video", voice: "Tin nhắn thoại", sticker: "Sticker", file: "Tệp"))
                        .Font(13).LineLimit(2)),
                Spacer(0))
            .Padding(10)
            .Background(RoundedRectangle(10).Fill(SColor.Primary.Opacity(0.05)))
            .Overlay(Alignment.Leading, RoundedRectangle(1.5).Fill(SColor.Accent).Frame(width: 3).PaddingV(8));
    }

    private static string Icon(Message m) => m.Kind switch
    {
        MessageKind.Voice or MessageKind.Audio => "waveform",
        MessageKind.Video or MessageKind.Gif => "play.rectangle.fill",
        _ => "doc.fill",
    };
}

public static class ForwardRow
{
    public static Control Make(ForwardRowItem item, Scheme scheme, Action toggle) =>
        new HoverBox(hovered => Button(toggle,
            HStack(10,
                    AvatarView.Make(item.Chat.Avatar, 36, () =>
                            Circle().Fill(SColor.Gray.Opacity(0.25))
                                .Overlay(Text(FirstLetter(item.Chat.Title)).Font(15, Weight.Semibold).Foreground(SColor.White)))
                        .Overlay(Alignment.BottomTrailing,
                            new PlatformIcon(item.Ref.Platform, 14)
                                .Background(Circle().Fill(FwChrome.PanelBackground(scheme)).Padding(-1.5))
                                .Offset(3, 3))
                        .OnAppear(() => AppModel.Shared.Session(item.Ref.Platform).RequestAvatar(item.Chat)),
                    VStack(HAlign.Leading, 2,
                        Text(item.Chat.Title).Font(13.5).LineLimit(1),
                        Text(Subtitle(item)).Font(11).Foreground(SColor.Secondary).LineLimit(1)),
                    Spacer(6),
                    Trailing(item))
                .PaddingH(8).PaddingV(6)
                .Background(RoundedRectangle(8).Fill(item.Picked ? SColor.Accent.Opacity(0.10) : hovered ? SColor.Primary.Opacity(0.05) : SColor.Clear))
                .ContentShape()));

    /// `String(title.prefix(1)).uppercased()`: the first character (grapheme), not the initials.
    internal static string FirstLetter(string title)
    {
        if (title.Length == 0) return "";
        var e = StringInfo.GetTextElementEnumerator(title);
        return e.MoveNext() ? ((string)e.Current).ToUpperInvariant() : "";
    }

    private static string Subtitle(ForwardRowItem item)
    {
        var chat = item.Chat;
        var kind = chat.Kind switch
        {
            ChatKind.Direct => "Cá nhân",
            ChatKind.Group => chat.MemberCount is { } n ? $"Nhóm · {n} thành viên" : "Nhóm",
            ChatKind.Channel => "Kênh",
            ChatKind.Bot => "Bot",
            _ => "Tin nhắn đã lưu",
        };
        return item.Ref.Platform.DisplayName() + " · " + kind;
    }

    private static Control Trailing(ForwardRowItem item) => item.Status?.State switch
    {
        ForwardState.Sending => new ProgressView { Size = ControlSize.Small }.Frame(width: 20),
        ForwardState.Sent => Image("checkmark.circle.fill").Font(18).Foreground(SColor.Green).Help("Đã gửi"),
        ForwardState.Failed => Image("exclamationmark.circle.fill").Font(18).Foreground(SColor.Red).Help(item.Status.Error),
        _ => item.Picked
            ? Image("checkmark.circle.fill").Font(18).Foreground(SColor.Accent)
            : Image("circle").Font(18).Foreground(SColor.Secondary).Opacity(0.5),
    };
}
