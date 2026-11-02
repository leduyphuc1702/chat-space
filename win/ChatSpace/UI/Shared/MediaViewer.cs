using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace ChatSpace;

// MARK: - Live Text

/// A word found in a photo; `Box` is in 0...1 of the photo, top-left origin.
public sealed record LiveTextWord(string Text, Rect Box);

public sealed record LiveTextLine(string Text, Rect Box, IReadOnlyList<LiveTextWord> Words);

/// VisionKit's `ImageAnalysis`: the text recognised in a photo.
public sealed record ImageAnalysis(IReadOnlyList<LiveTextLine> Lines)
{
    public static readonly ImageAnalysis Empty = new(Array.Empty<LiveTextLine>());
    public string Transcript => string.Join("\n", Lines.Select(l => l.Text));
}

/// `ImageAnalyzer`: Windows.Media.Ocr on Windows. Other builds have no recogniser, except the offline stand-in
/// for the two sample photos the demo data draws.
public static class ImageAnalyzer
{
    public static async Task<ImageAnalysis?> Analyze(AvBitmap image, string? source)
    {
#if WINDOWS
        if (await WindowsOcr.Recognize(image) is { } found) return found;
#endif
        if (DemoData.Enabled && DemoText.For(source) is { } demo) return await Task.FromResult<ImageAnalysis?>(demo);
        return await Task.FromResult<ImageAnalysis?>(null);
    }

    /// Words of a line spread over its box in proportion to their length.
    internal static LiveTextLine Line(string text, Rect box)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var total = Math.Max(1, parts.Sum(p => p.Length) + parts.Length - 1);
        var words = new List<LiveTextWord>();
        var x = box.X;
        foreach (var p in parts)
        {
            var w = box.Width * p.Length / total;
            words.Add(new LiveTextWord(p, new Rect(x, box.Y, w, box.Height)));
            x += box.Width * (p.Length + 1) / total;
        }
        return new LiveTextLine(text, box, words);
    }
}

/// Offline stand-in for the recogniser under CHATSPACE_DEMO on builds without one: the text of the two
/// photos DemoData draws, placed where DemoData draws it.
internal static class DemoText
{
    public static ImageAnalysis? For(string? source)
    {
        var name = source == null ? "" : Path.GetFileName(source);
        if (name == "chatspace-demo-note.png")
        {
            string[] lines = ["Góp ý thiết kế trang chủ", "", "1. Tăng khoảng cách giữa các khối lên 24px",
                "2. Màu chủ đạo #0068FF, nút phụ màu xám", "3. Logo đặt bên trái thanh điều hướng", "Hạn chót: thứ Sáu 15/11"];
            var found = new List<LiveTextLine>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var size = i == 0 ? 64.0 : 44.0;
                var y = 90.0 + i * 100 - (i == 0 ? 10 : 0);
                found.Add(ImageAnalyzer.Line(lines[i], new Rect(80 / 1200.0, y / 800, lines[i].Length * size * 0.5 / 1200, size * 1.25 / 800)));
            }
            return new ImageAnalysis(found);
        }
        // One receipt per invoice currency (CHATSPACE_DEMO_INVOICE_CURRENCY), "chatspace-demo-receipt-USD.png".
        if (name.StartsWith("chatspace-demo-receipt", StringComparison.Ordinal))
        {
            (string, string)[] rows =
            [
                ("Your Vinted purchase receipt:", ""), ("", ""), ("Seller", "perfecttimingcloset"),
                ("Order", "Victoria's Secret Ivory Satin Nightgown L"), ("Paid", DemoData.InvoiceMoney(14.24)), ("Item", DemoData.InvoiceMoney(11)),
                ("Shipping", DemoData.InvoiceMoney(7.45)), ("Vinted fee", DemoData.InvoiceMoney(1.25)), ("Sales tax", DemoData.InvoiceMoney(0)),
                ("Discount", DemoData.InvoiceMoney(-5.46)),
                ("Payment method", "MasterCard, **********8440"), ("Payment date", "10/30/2026 02:10 AM"), ("Transaction ID", "23728847703"),
            ];
            var found = new List<LiveTextLine>();
            for (var i = 0; i < rows.Length; i++)
            {
                var (label, value) = rows[i];
                if (label.Length == 0) continue;
                var text = value.Length == 0 ? label : label + " " + value;
                var y = 70 + i * 74.0;
                var width = value.Length == 0 ? label.Length * 30 * 0.5 : 780;
                found.Add(ImageAnalyzer.Line(text, new Rect(60 / 900.0, y / 1100, width / 900, 30 * 1.25 / 1100)));
            }
            return new ImageAnalysis(found);
        }
        return null;
    }
}

#if WINDOWS
/// Windows.Media.Ocr over the decoded photo: PNG into an in-memory stream, then BitmapDecoder to SoftwareBitmap.
internal static class WindowsOcr
{
    private static global::Windows.Media.Ocr.OcrEngine? _engine;
    private static bool _engineTried;

    private static global::Windows.Media.Ocr.OcrEngine? Engine()
    {
        if (_engineTried) return _engine;
        _engineTried = true;
        try
        {
            _engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            foreach (var tag in new[] { "vi", "en-US", "en" })
            {
                if (_engine != null) break;
                try { _engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language(tag)); }
                catch { }
            }
        }
        catch { _engine = null; }
        return _engine;
    }

    public static async Task<ImageAnalysis?> Recognize(AvBitmap bitmap)
    {
        var engine = Engine();
        if (engine == null) return null;
        try
        {
            byte[] png;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms);
                png = ms.ToArray();
            }
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var transform = new global::Windows.Graphics.Imaging.BitmapTransform();
            var longSide = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            var limit = global::Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            if (longSide > limit)
            {
                var s = (double)limit / longSide;
                transform.ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * s);
                transform.ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * s);
            }
            using var sb = await decoder.GetSoftwareBitmapAsync(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform,
                global::Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                global::Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var result = await engine.RecognizeAsync(sb);
            double pw = sb.PixelWidth, ph = sb.PixelHeight;
            var lines = new List<LiveTextLine>();
            foreach (var line in result.Lines)
            {
                var words = new List<LiveTextWord>();
                double l = double.MaxValue, t = double.MaxValue, r = 0, b = 0;
                foreach (var w in line.Words)
                {
                    var br = w.BoundingRect;
                    words.Add(new LiveTextWord(w.Text, new Rect(br.X / pw, br.Y / ph, br.Width / pw, br.Height / ph)));
                    l = Math.Min(l, br.X); t = Math.Min(t, br.Y); r = Math.Max(r, br.X + br.Width); b = Math.Max(b, br.Y + br.Height);
                }
                if (words.Count == 0 || string.IsNullOrWhiteSpace(line.Text)) continue;
                lines.Add(new LiveTextLine(line.Text, new Rect(l / pw, t / ph, (r - l) / pw, (b - t) / ph), words));
            }
            return new ImageAnalysis(lines);
        }
        catch { return null; }
    }
}
#endif

// MARK: - Model

/// Photo/video viewer for every platform. Photos get Live Text: drag across text to select and copy it,
/// or copy everything the image contains at once.
public sealed class MediaViewer : Observable
{
    public static readonly MediaViewer Shared = new();
    private MediaViewer() { }

    public sealed record Item(string Id, Message Message);

    private ImmutableList<Item> _items = ImmutableList<Item>.Empty;
    public ImmutableList<Item> Items { get => Get(ref _items); private set => Set(ref _items, value); }
    private int _index;
    public int Index { get => Get(ref _index); private set => Set(ref _index, value); }
    private AvBitmap? _image;
    public AvBitmap? Image { get => Get(ref _image); private set => Set(ref _image, value); }
    private string? _videoUrl;
    public string? VideoUrl { get => Get(ref _videoUrl); private set => Set(ref _videoUrl, value); }
    private bool _loading;
    public bool Loading { get => Get(ref _loading); private set => Set(ref _loading, value); }
    private bool _failed;
    public bool Failed { get => Get(ref _failed); private set => Set(ref _failed, value); }
    private ImageAnalysis? _analysis;
    public ImageAnalysis? Analysis { get => Get(ref _analysis); private set => Set(ref _analysis, value); }
    private double _zoom = 1;
    public double Zoom { get => Get(ref _zoom); private set => Set(ref _zoom, value); }
    private bool _copiedNotice;
    public bool CopiedNotice { get => Get(ref _copiedNotice); set => Set(ref _copiedNotice, value); }

    private bool _showText;
    /// The recognised text listed beside the photo, selectable like any document.
    public bool ShowText
    {
        get => Get(ref _showText);
        set { Set(ref _showText, value); if (value) ShowCost = false; }
    }

    private bool _showCost;
    /// The VietFlow purchase-cost form beside an invoice photo.
    public bool ShowCost
    {
        get => Get(ref _showCost);
        set
        {
            Set(ref _showCost, value);
            if (value) { ShowText = false; PrepareCostEntry(); }
        }
    }

    private ImmutableDictionary<string, PurchaseCostEntry> _costEntries = ImmutableDictionary<string, PurchaseCostEntry>.Empty;
    public ImmutableDictionary<string, PurchaseCostEntry> CostEntries { get => Get(ref _costEntries); private set => Set(ref _costEntries, value); }
    public PurchaseCostEntry? CostEntry => Current is { } c && CostEntries.TryGetValue(c.Id, out var e) ? e : null;

    private PlatformSession? _session;
    private MacWindow? _window;
    private CancellationTokenSource? _loadCts;
    private string? _source;
    /// The live-text view currently on screen (selected text for the copy shortcut).
    internal LiveTextImageView? Overlay;

    /// Long side photos are decoded to: sharp at full screen, and still small enough to zoom smoothly.
    private const double MaxPixel = 4096;

    public Window? Window => _window;

    /// Lets the headless screenshot mode capture the viewer (`--window viewer`).
    public static void RegisterScreenshotTarget() => App.ScreenshotTargets["viewer"] = () => Shared.Window;

    public static bool CanShow(Message m)
    {
        if (m.Media == null || m.IsDeleted) return false;
        return m.Kind is MessageKind.Image or MessageKind.Video or MessageKind.Gif;
    }

    public Message? Current => Items.Count > 0 && Index >= 0 && Index < Items.Count ? Items[Index].Message : null;
    public bool IsVideo => Current is { } m && IsVideoMessage(m);
    public string Transcript => Analysis?.Transcript ?? "";

    private static bool IsVideoMessage(Message m)
    {
        if (m.Kind == MessageKind.Video) return true;
        if (m.Kind != MessageKind.Gif) return false;
        var mime = m.Media?.Mime ?? "";
        var name = (m.Media?.FileName ?? m.Media?.Url ?? "").ToLowerInvariant();
        return mime.StartsWith("video") || name.EndsWith(".mp4") || name.Contains(".mp4?");
    }

    // MARK: Showing

    public void Show(PlatformSession s, Message m, bool cost = false)
    {
        if (!ReferenceEquals(_session, s)) CostEntries = ImmutableDictionary<string, PurchaseCostEntry>.Empty;
        _session = s;
        var all = s.MessagesIn(m.ChatId).Where(CanShow).ToList();
        if (!all.Any(x => x.Id == m.Id)) all = new List<Message> { m };
        Items = all.Select(x => new Item(x.Id, x)).ToImmutableList();
        Index = Math.Max(0, Items.FindIndex(i => i.Id == m.Id));
        var panel = _window ?? MakeWindow();
        _window = panel;
        // Above the floating chat bubble when opened from there.
        panel.Topmost = ChatHeads.Shared.Expanded != null;
        panel.Title = Title;
        if (!panel.IsVisible) panel.Show();
        panel.Activate();
        LoadCurrent();
        if (cost) ShowCost = true;
    }

    public void Close()
    {
        _window?.Hide();
        _loadCts?.Cancel();
        Image = null;
        VideoUrl = null;
        Analysis = null;
        Items = ImmutableList<Item>.Empty;
        CostEntries = ImmutableDictionary<string, PurchaseCostEntry>.Empty;
        ShowCost = false;
    }

    public void Step(int delta)
    {
        var next = Index + delta;
        if (next < 0 || next >= Items.Count) return;
        Index = next;
        if (_window != null) _window.Title = Title;
        LoadCurrent();
    }

    public void SetZoom(double z) => Zoom = Math.Min(Math.Max(z, 1), 8);

    public string Title
    {
        get
        {
            if (Current is not { } m) return "";
            var who = m.FromMe ? "Bạn" : (m.SenderName ?? _session?.Chats.FirstOrDefault(c => c.Id == m.ChatId)?.Title ?? "");
            var d = m.Date;
            var date = $"{d.ToString("HH:mm", Inv.C)} {d.Day} thg {d.Month}, {d.Year}";
            return Items.Count > 1 ? $"{who} · {date} · {Index + 1}/{Items.Count}" : $"{who} · {date}";
        }
    }

    private async void LoadCurrent()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        Image = null;
        VideoUrl = null;
        Analysis = null;
        Failed = false;
        Zoom = 1;
        _source = null;
        if (Current is not { } m || _session is not { } session) return;
        if (ShowCost) PrepareCostEntry();
        Loading = true;
        var video = IsVideoMessage(m);
        var src = await session.MediaSource(m);
        if (ct.IsCancellationRequested || Current?.Id != m.Id) return;
        _source = src;
        if (src == null) { Loading = false; Failed = true; return; }
        if (video)
        {
            VideoUrl = src;
            Loading = false;
            // Avalonia has no video player: the system's default player takes over and the viewer keeps its chrome.
            if (!App.Headless && !DemoData.Enabled) KitShell.Open(src);
            return;
        }
        var data = await ImageLoader.Shared.Data(src);
        if (ct.IsCancellationRequested || Current?.Id != m.Id) return;
        var animated = m.Kind == MessageKind.Gif || (m.Media?.Mime ?? "").Contains("gif");
        var img = data == null ? null : await Task.Run(() =>
        {
            try { return animated ? new AvBitmap(new MemoryStream(data)) : ImageLoader.Downsample(data, MaxPixel); }
            catch { return null; }
        });
        if (ct.IsCancellationRequested || Current?.Id != m.Id) return;
        Image = img;
        Loading = false;
        Failed = img == null;
        if (img != null) await Analyze(img, m.Id, src);
    }

    private async Task Analyze(AvBitmap img, string id, string source)
    {
        ImageAnalysis? a = null;
        try { a = await ImageAnalyzer.Analyze(img, source); } catch { }
        if (Current?.Id != id) return;
        // No recogniser (or nothing found): the text panel says so instead of spinning.
        a ??= ImageAnalysis.Empty;
        Analysis = a;
        if (CostEntries.TryGetValue(id, out var entry)) entry.ApplyTranscript(a.Transcript);
    }

    /// Kept per photo while the viewer is open, so stepping to another photo and back keeps what was typed.
    private void PrepareCostEntry()
    {
        if (Current is not { } m || IsVideoMessage(m) || _session is not { } session) return;
        var entry = CostEntries.TryGetValue(m.Id, out var e) ? e : new PurchaseCostEntry(m, session);
        CostEntries = CostEntries.SetItem(m.Id, entry);
        if (Analysis is { } a) entry.ApplyTranscript(a.Transcript);
    }

    // MARK: Actions

    public void CopyAllText()
    {
        var t = Transcript;
        if (t.Length == 0) return;
        CopyText(t);
    }

    public void CopyText(string text)
    {
        if (_window?.Clipboard is { } clip) _ = clip.SetTextAsync(text);
        FlashCopied();
    }

    public async void CopyImage()
    {
        if (Image is not { } img || _window?.Clipboard is not { } clip) return;
        try { await clip.SetBitmapAsync(img); } catch { }
        FlashCopied();
    }

    private void FlashCopied()
    {
        CopiedNotice = true;
        DispatcherTimer.RunOnce(() => Shared.CopiedNotice = false, TimeSpan.FromSeconds(1.5));
    }

    public async void Save()
    {
        if (Current is not { } m || _source is not { } src || _window == null) return;
        var sp = _window.StorageProvider;
        var name = SuggestedName(m, src);
        var options = new FilePickerSaveOptions
        {
            SuggestedFileName = name,
            DefaultExtension = Path.GetExtension(name).TrimStart('.'),
            SuggestedStartLocation = await sp.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads),
        };
        var file = await sp.SaveFilePickerAsync(options);
        if (file == null) return;
        var data = await ImageLoader.Shared.Data(src);
        if (data == null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await stream.WriteAsync(data);
        }
        catch { }
    }

    public void OpenExternally()
    {
        if (Current is { } m) _session?.OpenExternally(m);
    }

    private static readonly Dictionary<string, string> MimeExtensions = new()
    {
        ["image/jpeg"] = "jpeg", ["image/jpg"] = "jpeg", ["image/png"] = "png", ["image/gif"] = "gif", ["image/webp"] = "webp",
        ["image/heic"] = "heic", ["image/heif"] = "heif", ["image/tiff"] = "tiff", ["image/bmp"] = "bmp",
        ["video/mp4"] = "mp4", ["video/quicktime"] = "mov", ["video/webm"] = "webm", ["video/x-matroska"] = "mkv",
    };

    private string SuggestedName(Message m, string src)
    {
        if (m.Media?.FileName is { Length: > 0 } n) return n;
        var ext = Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.Scheme != "file" ? Path.GetExtension(uri.AbsolutePath) : Path.GetExtension(src);
        ext = ext.TrimStart('.');
        if (ext.Length == 0 || ext.Length > 5)
            ext = m.Media?.Mime is { } mime && MimeExtensions.TryGetValue(mime.ToLowerInvariant(), out var e) ? e : (IsVideoMessage(m) ? "mp4" : "jpg");
        var stamp = m.Date.ToString("yyyy-MM-dd'T'HHmmss", Inv.C);
        return $"ChatSpace {stamp}.{ext}";
    }

    // MARK: Window

    private MacWindow MakeWindow()
    {
        var w = new MacWindow("", MediaViewerView.Make(this), hiddenTitleBar: true, scheme: Scheme.Dark)
        {
            Width = 960,
            Height = 720,
            MinWidth = 420,
            MinHeight = 320,
            Background = Brushes.Black,
            HideOnClose = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        if (!App.Headless && !DemoData.Enabled && Prefs.GetString("mediaViewer.size") is { } saved && saved.Split(',') is [var sw, var sh]
            && double.TryParse(sw, System.Globalization.NumberStyles.Float, Inv.C, out var pw)
            && double.TryParse(sh, System.Globalization.NumberStyles.Float, Inv.C, out var ph))
        {
            w.Width = Math.Max(w.MinWidth, pw);
            w.Height = Math.Max(w.MinHeight, ph);
        }
        w.Closing += (_, _) =>
        {
            if (App.Quitting) return;
            if (!App.Headless && !DemoData.Enabled && w.WindowState == WindowState.Normal)
                Prefs.Set("mediaViewer.size", $"{w.Width.ToString(Inv.C)},{w.Height.ToString(Inv.C)}");
            Close();
        };
        w.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The viewer's hidden title bar keeps the lights 10 pt from the edge (MacWindow's default is 20).
        if (w.Content is Avalonia.Controls.Panel root)
            foreach (var lights in root.Children.OfType<TrafficLightsView>()) lights.Margin = new Thickness(-10, 0, 0, 0);
        return w;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_window == null) return;
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var focus = TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement();
        // Typing in the cost form: arrows move the caret and Esc stays with the field.
        if (focus is TextBox { IsReadOnly: false } && e.Key is Key.Escape or Key.Left or Key.Right) return;
        var handled = true;
        if (e.Key == Key.Escape) _window.Close();
        else if (e.Key == Key.Left && !cmd) Step(-1);
        else if (e.Key == Key.Right && !cmd) Step(1);
        else if (cmd && e.Key is Key.OemPlus or Key.Add) SetZoom(Zoom * 1.5);
        else if (cmd && e.Key is Key.OemMinus or Key.Subtract) SetZoom(Zoom / 1.5);
        else if (cmd && e.Key is Key.D0 or Key.NumPad0) SetZoom(1);
        else if (cmd && e.Key == Key.C)
        {
            // The selected text, else the photo.
            var selected = focus switch
            {
                TextBox tb => tb.SelectionEnd != tb.SelectionStart,
                SelectableTextBlock stb => !string.IsNullOrEmpty(stb.SelectedText),
                _ => false,
            };
            if (selected) handled = false;
            else if (Overlay is { HasSelection: true } o && o.SelectedText.Length > 0) CopyText(o.SelectedText);
            else if (Image != null) CopyImage();
            else handled = false;
        }
        else handled = false;
        if (handled) e.Handled = true;
    }
}

// MARK: - Views

public static class MediaViewerView
{
    private static readonly SColor PanelBackground = SColor.Hex(0x1A1A1A);

    public static Control Make(MediaViewer viewer)
    {
        var hovering = new State<bool>(true);
        var main = ZStack(
            ColorView(SColor.Black),
            ContentView(viewer),
            Fade(Arrows(viewer), hovering),
            VStack(Spacer(), Fade(Toolbar(viewer), hovering)));
        main.PointerEntered += (_, _) => hovering.Value = true;
        main.PointerExited += (_, _) => hovering.Value = false;
        var row = HStack(0,
            main,
            View(() => viewer.ShowText && !viewer.IsVideo ? TextPanel(viewer).Frame(width: 300) : null),
            View(() => viewer.ShowCost && !viewer.IsVideo && viewer.CostEntry is { } entry
                ? Keep(("cost", entry.Message.Id), () => new PurchaseCostPanel(entry, () => viewer.ShowCost = false)).Frame(width: 360)
                : null));
        var notice = View(() => viewer.CopiedNotice
            ? Text("Đã sao chép").Font(13, Weight.Medium).Foreground(SColor.White)
                .PaddingH(14).PaddingV(8).Background(Capsule().Fill(SColor.Black.Opacity(0.75)))
            : null);
        var title = View(() => Text(viewer.Title).Font(13, Weight.Bold).Foreground(SColor.White.Opacity(0.85)).LineLimit(1)
            .PaddingH(80).Frame(height: MacWindow.TitleBarHeight - 3));
        title.IsHitTestVisible = false;
        notice.IsHitTestVisible = false;
        return row.Overlay(Alignment.Center, notice).Overlay(Alignment.Top, title);
    }

    /// `.opacity(hovering ? 1 : 0)` with `.easeOut(duration: 0.15)`.
    private static T Fade<T>(T c, State<bool> on) where T : Control
    {
        c.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() } };
        Watch? w = null;
        c.Opacity = Tracker.Untracked(() => on.Value) ? 1 : 0;
        c.AttachedToVisualTree += (_, _) => w ??= Watch.Effect(() => c.Opacity = on.Value ? 1 : 0);
        c.DetachedFromVisualTree += (_, _) => { w?.Dispose(); w = null; };
        return c;
    }

    private static Control ContentView(MediaViewer v) => View(() =>
    {
        if (v.VideoUrl is { } url) return VideoFallback(v, url);
        if (v.Image is { } image)
        {
            var live = Memo("live", () => new LiveTextImageView(v));
            live.Update(image, v.Analysis, v.Zoom);
            v.Overlay = live;
            return new KeepHost(live);
        }
        if (v.Failed)
            return VStack(8,
                Image("exclamationmark.triangle").Font(28),
                Text("Không tải được nội dung này").Font(13)).Foreground(SColor.White.Opacity(0.7));
        if (v.Current?.Media?.Thumbnail is { } thumb)
            return new RemoteImage(thumb, () => ColorView(SColor.Clear), ContentMode.Fit, 256).Blur(12).Opacity(0.6)
                .Overlay(new ProgressView { Size = ControlSize.Large })
                .Padding(40);
        return new ProgressView { Size = ControlSize.Large };
    });

    /// AVPlayerView stand-in: the system's default player plays the video; the thumbnail and a play button stay.
    private static Control VideoFallback(MediaViewer v, string url)
    {
        var thumb = v.Current?.Media?.Thumbnail;
        return ZStack(
            thumb != null ? new RemoteImage(thumb, () => ColorView(SColor.Clear), ContentMode.Fit, 1024).Opacity(0.6).Padding(40) : null,
            Button(() => KitShell.Open(url),
                Image("play.fill").Font(28).Foreground(SColor.White).Frame(width: 76, height: 76)
                    .Background(Circle().Fill(SColor.Black.Opacity(0.5)))).Help("Mở bằng trình phát mặc định"));
    }

    private static Control Arrows(MediaViewer v) => View(() => v.Items.Count > 1
        ? HStack(
            Arrow("chevron.left", v.Index > 0, () => v.Step(-1)),
            Spacer(),
            Arrow("chevron.right", v.Index < v.Items.Count - 1, () => v.Step(1))).PaddingH(14)
        : null);

    private static Control Arrow(string symbol, bool enabled, Action action) =>
        Button(action, Image(symbol).Font(18, Weight.Semibold).Foreground(SColor.White)
            .Frame(width: 40, height: 40).Background(Circle().Fill(SColor.Black.Opacity(0.5)))).Disabled(!enabled).Invisible(!enabled);

    private static Control Toolbar(MediaViewer v) => View(() =>
    {
        var showText = v.ShowText;
        var showCost = v.ShowCost;
        var video = v.IsVideo;
        return HStack(4,
                !video ? ToolButton("minus.magnifyingglass", "Thu nhỏ (Ctrl+−)", () => v.SetZoom(v.Zoom / 1.5), v.Zoom > 1) : null,
                !video ? ToolButton("plus.magnifyingglass", "Phóng to (Ctrl++)", () => v.SetZoom(v.Zoom * 1.5), v.Image != null) : null,
                !video ? ZStack(ColorView(SColor.Separator), ColorView(SColor.White.Opacity(0.25))).Frame(width: 1, height: 18) : null,
                !video
                    ? ToolButton("text.viewfinder", v.Transcript.Length == 0 ? "Không tìm thấy chữ trong ảnh" : "Văn bản trong ảnh: bôi đen để sao chép",
                            () => v.ShowText = !v.ShowText, !(v.Transcript.Length == 0 && !showText))
                        .Background(RoundedRectangle(6).Fill(showText ? SColor.White.Opacity(0.18) : SColor.Clear))
                    : null,
                !video ? ToolButton("doc.on.doc", "Sao chép ảnh", v.CopyImage, v.Image != null) : null,
                !video
                    ? ToolButton("dollarsign.square", "Nhập chi phí PO (VietFlow)", () => v.ShowCost = !v.ShowCost, true)
                        .Background(RoundedRectangle(6).Fill(showCost ? SColor.White.Opacity(0.18) : SColor.Clear))
                    : null,
                ToolButton("square.and.arrow.down", "Lưu về máy…", v.Save, !(v.Loading || v.Failed)),
                ToolButton("arrow.up.forward.app", "Mở bằng ứng dụng khác", v.OpenExternally, true))
            .PaddingH(8).PaddingV(6)
            .Background(Capsule().Fill(SColor.Black.Opacity(0.6)).Stroke(SColor.White.Opacity(0.12), 1))
            .PaddingBottom(18);
    });

    /// A plain button with a 15 pt icon in a 34x30 slot.
    private static Control ToolButton(string symbol, string help, Action action, bool enabled) =>
        Button(action, Image(symbol).Font(15).Foreground(SColor.White).Frame(width: 34, height: 30).ContentShape())
            .Disabled(!enabled).Help(help);

    private static Control TextPanel(MediaViewer v)
    {
        var transcript = v.Transcript;
        var waiting = v.Analysis == null && v.Image != null;
        var copyAll = Button(v.CopyAllText, Text("Sao chép tất cả").Font(12)).Disabled(transcript.Length == 0);
        return VStack(HAlign.Leading, 0,
                HStack(Text("Văn bản trong ảnh").Font(13, Weight.Semibold), Spacer(), copyAll)
                    .PaddingH(14).PaddingTop(34).PaddingBottom(10),
                ColorView(SColor.Hex(0x565656)).Frame(height: 1),
                waiting ? new ProgressView { Size = ControlSize.Small }.FillWidth().Padding(20) : null,
                waiting ? Spacer() : null,
                !waiting && transcript.Length == 0 ? Text("Không tìm thấy chữ trong ảnh này").Font(12.5).Foreground(SColor.White.Opacity(0.5)).Padding(14) : null,
                !waiting && transcript.Length == 0 ? Spacer() : null,
                !waiting && transcript.Length > 0 ? SelectableText(transcript) : null,
                Text("Bôi đen rồi nhấn Ctrl+C, hoặc kéo chọn chữ ngay trên ảnh.").Font(11).Foreground(SColor.White.Opacity(0.5)).Padding(14))
            .Foreground(SColor.White)
            .Background(PanelBackground);
    }

    /// Read-only text the user can select and copy, used for the photo's recognised text.
    private static Control SelectableText(string text)
    {
        var f = SFont.System(14);
        var tb = new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = f.AvaloniaFamily,
            FontSize = f.Size,
            LetterSpacing = f.LetterSpacing,
            Foreground = Brushes.White,
            SelectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x66, 0x00, 0x7A, 0xFF)),
            Padding = new Thickness(10),
        };
        return ScrollView(tb);
    }
}

// MARK: - Zoomable photo with Live Text

/// Zoomable photo with a Live Text overlay (select text, copy lines) and the Live Text button.
public sealed class LiveTextImageView : Panel, IFlexible
{
    private readonly Surface _surface;
    private readonly Control _button;

    public LiveTextImageView(MediaViewer viewer)
    {
        _surface = new Surface { ZoomRequested = z => viewer.SetZoom(z), CopyRequested = viewer.CopyText, CopyImageRequested = viewer.CopyImage };
        _button = Button(() => _surface.HighlightAll = !_surface.HighlightAll,
            Image("text.viewfinder").Font(13).Foreground(SColor.White.Opacity(0.85))
                .Frame(width: 26, height: 26).Background(Circle().Fill(SColor.Hex(0x272727))));
        _button.IsVisible = false;
        Children.Add(_surface);
        Children.Add(_button);
    }

    public bool IsFlexible(Orientation axis) => true;
    public bool HasSelection => _surface.HasSelection;
    public string SelectedText => _surface.SelectedText;

    /// `updateNSView`: a new photo resets the magnification, a new analysis re-arms the overlay, a new zoom animates.
    public void Update(AvBitmap image, ImageAnalysis? analysis, double zoom)
    {
        _surface.SetImage(image);
        _surface.SetAnalysis(analysis);
        _surface.SetTargetZoom(zoom);
        _button.IsVisible = analysis is { Lines.Count: > 0 };
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size available)
    {
        _surface.Measure(available);
        _button.Measure(Size.Infinity);
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _surface.Arrange(new Rect(final));
        // The Live Text button sits at the photo's bottom-trailing corner, 64 pt above its bottom edge.
        var fit = _surface.FitRect(final);
        var s = _button.DesiredSize;
        var right = Math.Min(fit.Right, final.Width);
        var bottom = Math.Min(fit.Bottom, final.Height);
        _button.Arrange(new Rect(right - s.Width - ButtonInset, bottom - 64 - s.Height, s.Width, s.Height));
        return final;
    }

    private const double ButtonInset = 0;

    private sealed class Surface : Control
    {
        private AvBitmap? _image;
        private ImageAnalysis? _analysis;
        private readonly List<(string Text, Rect Box, int Line)> _words = new();
        private double _zoom = 1;
        private Point _origin;
        private int _anchor = -1, _focus = -1;
        private bool _selecting, _panning, _highlightAll;
        private Point _panStart, _panOrigin, _pressPoint;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        private double _animFrom, _animTo;
        private DateTime _animStart;
        private Point _animCenter;

        public Action<double>? ZoomRequested { get; init; }
        public Action<string>? CopyRequested { get; init; }
        public Action? CopyImageRequested { get; init; }

        private static readonly IBrush SelectionFill = new ImmutableSolidColorBrush(Color.FromArgb(0x59, 0x00, 0x7A, 0xFF));
        private static readonly IBrush HighlightFill = new ImmutableSolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xD6, 0x0A));
        private static readonly IPen HighlightPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1.2);

        public Surface()
        {
            ClipToBounds = true;
            Focusable = false;
            _timer.Tick += (_, _) => Tick();
            RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            ContextMenu = Menus.Context(() => new MenuEntry[]
            {
                MenuEntry.Item("Sao chép", () => CopyRequested?.Invoke(SelectedText), enabled: HasSelection && SelectedText.Length > 0),
                MenuEntry.Item("Chọn tất cả", SelectAll, enabled: _words.Count > 0),
                MenuEntry.Separator,
                MenuEntry.Item("Sao chép ảnh", () => CopyImageRequested?.Invoke()),
            });
        }

        public bool HighlightAll { get => _highlightAll; set { _highlightAll = value; InvalidateVisual(); } }
        public bool HasSelection => _anchor >= 0 && _focus >= 0;

        public string SelectedText
        {
            get
            {
                if (!HasSelection) return "";
                int a = Math.Min(_anchor, _focus), b = Math.Max(_anchor, _focus);
                var sb = new StringBuilder();
                for (var i = a; i <= b && i < _words.Count; i++)
                {
                    if (i > a) sb.Append(_words[i].Line == _words[i - 1].Line ? ' ' : '\n');
                    sb.Append(_words[i].Text);
                }
                return sb.ToString();
            }
        }

        private void SelectAll()
        {
            if (_words.Count == 0) return;
            _anchor = 0;
            _focus = _words.Count - 1;
            InvalidateVisual();
        }

        private void ClearSelection()
        {
            _anchor = _focus = -1;
            _selecting = false;
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);

        public void SetImage(AvBitmap? img)
        {
            if (ReferenceEquals(img, _image)) return;
            _image = img;
            _zoom = 1;
            _origin = default;
            _timer.Stop();
            ClearSelection();
            InvalidateVisual();
        }

        public void SetAnalysis(ImageAnalysis? a)
        {
            if (ReferenceEquals(a, _analysis)) return;
            _analysis = a;
            _words.Clear();
            ClearSelection();
            if (a != null)
            {
                for (var li = 0; li < a.Lines.Count; li++)
                    foreach (var w in a.Lines[li].Words) _words.Add((w.Text, w.Box, li));
            }
            InvalidateVisual();
        }

        public void SetTargetZoom(double z)
        {
            if (Math.Abs(_zoom - z) <= 0.01) return;
            if (_timer.IsEnabled && Math.Abs(_animTo - z) < 1e-6) return;
            var v = Bounds.Size;
            _animFrom = _zoom;
            _animTo = z;
            _animStart = DateTime.UtcNow;
            _animCenter = new Point((_origin.X + v.Width / 2) / _zoom, (_origin.Y + v.Height / 2) / _zoom);
            _timer.Start();
        }

        private void Tick()
        {
            var t = Math.Min(1, (DateTime.UtcNow - _animStart).TotalMilliseconds / 180);
            var e = 1 - Math.Pow(1 - t, 2);
            _zoom = _animFrom + (_animTo - _animFrom) * e;
            var v = Bounds.Size;
            _origin = new Point(_animCenter.X * _zoom - v.Width / 2, _animCenter.Y * _zoom - v.Height / 2);
            ClampOrigin();
            InvalidateVisual();
            if (t >= 1) { _zoom = _animTo; _timer.Stop(); }
        }

        private void ClampOrigin()
        {
            var v = Bounds.Size;
            var x = Math.Clamp(_origin.X, 0, Math.Max(0, v.Width * _zoom - v.Width));
            var y = Math.Clamp(_origin.Y, 0, Math.Max(0, v.Height * _zoom - v.Height));
            _origin = new Point(x, y);
        }

        /// `.scaleProportionallyUpOrDown` inside the unmagnified view.
        public Rect FitRect(Size view)
        {
            if (_image == null || _image.PixelSize.Width == 0 || _image.PixelSize.Height == 0) return new Rect(view);
            double iw = _image.PixelSize.Width, ih = _image.PixelSize.Height;
            var s = Math.Min(view.Width / iw, view.Height / ih);
            var w = iw * s;
            var h = ih * s;
            return new Rect((view.Width - w) / 2, (view.Height - h) / 2, w, h);
        }

        private Rect ImageRect()
        {
            var f = FitRect(Bounds.Size);
            return new Rect(f.X * _zoom - _origin.X, f.Y * _zoom - _origin.Y, f.Width * _zoom, f.Height * _zoom);
        }

        private static Rect Screen(Rect norm, Rect image) =>
            new(image.X + norm.X * image.Width, image.Y + norm.Y * image.Height, norm.Width * image.Width, norm.Height * image.Height);

        public override void Render(DrawingContext ctx)
        {
            var bounds = new Rect(Bounds.Size);
            ctx.DrawRectangle(Brushes.Transparent, null, bounds);
            if (_image == null || bounds.Width <= 0 || bounds.Height <= 0) return;
            ClampOrigin();
            var r = ImageRect();
            using (ctx.PushClip(bounds))
            {
                ctx.DrawImage(_image, new Rect(0, 0, _image.PixelSize.Width, _image.PixelSize.Height), r);
                if (_analysis == null) return;
                if (_highlightAll)
                {
                    foreach (var line in _analysis.Lines)
                        ctx.DrawRectangle(HighlightFill, HighlightPen, Screen(line.Box, r).Inflate(3), 4, 4);
                }
                if (HasSelection)
                {
                    int a = Math.Min(_anchor, _focus), b = Math.Max(_anchor, _focus);
                    // One rounded band per line of selected words.
                    for (var i = a; i <= b && i < _words.Count;)
                    {
                        var line = _words[i].Line;
                        var box = Screen(_words[i].Box, r);
                        var j = i + 1;
                        while (j <= b && j < _words.Count && _words[j].Line == line) { box = box.Union(Screen(_words[j].Box, r)); j++; }
                        ctx.DrawRectangle(SelectionFill, null, box.Inflate(2), 3, 3);
                        i = j;
                    }
                }
            }
        }

        // MARK: Interaction

        private int WordAt(Point p, bool nearest)
        {
            if (_analysis == null || _words.Count == 0) return -1;
            var r = ImageRect();
            var best = -1;
            var bestD = double.MaxValue;
            for (var i = 0; i < _words.Count; i++)
            {
                var box = Screen(_words[i].Box, r).Inflate(2);
                if (box.Contains(p)) return i;
                if (!nearest) continue;
                var dx = Math.Max(Math.Max(box.Left - p.X, 0), p.X - box.Right);
                var dy = Math.Max(Math.Max(box.Top - p.Y, 0), p.Y - box.Bottom);
                var d = dx * dx + dy * dy * 4;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed || _image == null) return;
            var p = point.Position;
            _pressPoint = p;
            var hit = WordAt(p, false);
            if (hit >= 0)
            {
                _anchor = hit;
                _focus = e.ClickCount >= 2 ? hit : -1;
                if (e.ClickCount >= 3)
                {
                    int line = _words[hit].Line, first = hit, last = hit;
                    while (first > 0 && _words[first - 1].Line == line) first--;
                    while (last < _words.Count - 1 && _words[last + 1].Line == line) last++;
                    _anchor = first;
                    _focus = last;
                }
                _selecting = true;
                e.Pointer.Capture(this);
            }
            else
            {
                ClearSelection();
                if (_zoom > 1.001)
                {
                    _panning = true;
                    _panStart = p;
                    _panOrigin = _origin;
                    Cursor = new Cursor(StandardCursorType.SizeAll);
                    e.Pointer.Capture(this);
                }
            }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            if (_panning)
            {
                _origin = new Point(_panOrigin.X - (p.X - _panStart.X), _panOrigin.Y - (p.Y - _panStart.Y));
                ClampOrigin();
                InvalidateVisual();
                return;
            }
            if (_selecting && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var i = WordAt(p, true);
                if (i >= 0 && (i != _anchor || Math.Sqrt((p.X - _pressPoint.X) * (p.X - _pressPoint.X) + (p.Y - _pressPoint.Y) * (p.Y - _pressPoint.Y)) > 3) && i != _focus)
                {
                    _focus = i;
                    InvalidateVisual();
                }
                return;
            }
            Cursor = WordAt(p, false) >= 0 ? new Cursor(StandardCursorType.Ibeam) : Cursor.Default;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_panning || _selecting) e.Pointer.Capture(null);
            _panning = _selecting = false;
            Cursor = Cursor.Default;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_image == null) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                var current = _timer.IsEnabled ? _animTo : _zoom;
                ZoomRequested?.Invoke(current * Math.Pow(1.2, e.Delta.Y));
                e.Handled = true;
            }
            else if (_zoom > 1.001)
            {
                _origin = new Point(_origin.X - e.Delta.X * 48, _origin.Y - e.Delta.Y * 48);
                ClampOrigin();
                InvalidateVisual();
                e.Handled = true;
            }
        }
    }
}
