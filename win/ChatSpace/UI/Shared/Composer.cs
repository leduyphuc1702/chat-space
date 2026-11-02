using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChatSpace.Core;
using ChatSpace.Kit;

namespace ChatSpace;

/// Multi-line text input that sends on Return and inserts a newline on Shift/Alt+Return, matching the
/// desktop behaviour of all four platforms. Create it through `Composer.Editor` so it survives rebuilds.
public sealed class ComposerEditor : Panel, IFlexible
{
    public readonly TextBox Box;
    private readonly SText _placeholder;
    private Binding<string> _text = Binding<string>.Constant("");
    private bool _pushing, _typing;
    private Watch? _watch;
    private SFont _font = SFont.System(14);

    public Action OnSubmit { get; set; } = () => { };
    public Action<bool>? OnTypingChange { get; set; }
    public PlatformSession? MentionSession { get; set; }
    public string? MentionChatId { get; set; }
    public Action<IReadOnlyList<string>>? OnFiles { get; set; }
    public double MaxHeightLimit { get; set; } = 140;
    /// Key commands (↑, Esc…) the mention list didn't take; true = handled.
    public Func<Key, bool>? OnCommand { get; set; }

    internal static readonly List<WeakReference<ComposerEditor>> Live = new();

    public ComposerEditor()
    {
        Box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0, 4),
            VerticalContentAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(Box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _placeholder = new SText("") { IsHitTestVisible = false, Margin = new Thickness(0, 4), VerticalAlignment = VerticalAlignment.Top };
        _placeholder.SetValue(Env.LineLimitProperty, 1);
        Children.Add(Box);
        Children.Add(_placeholder);
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Background = Brushes.Transparent;

        Box.TextChanged += (_, _) =>
        {
            var t = Box.Text ?? "";
            _placeholder.IsVisible = t.Length == 0;
            if (!_pushing)
            {
                _pushing = true;
                try { _text.Value = t; } finally { _pushing = false; }
            }
            var now = t.Length > 0;
            if (now != _typing) { _typing = now; OnTypingChange?.Invoke(now); }
            UpdateMention();
            InvalidateMeasure();
        };
        Box.PropertyChanged += (_, e) => { if (e.Property == TextBox.CaretIndexProperty) UpdateMention(); };
        Box.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Box.AddHandler(TextBox.PastingFromClipboardEvent, OnPaste, RoutingStrategies.Bubble);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { if (OnFiles != null && e.Data.Contains(DataFormats.Files)) { e.DragEffects = DragDropEffects.Copy; e.Handled = true; } });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (OnFiles == null || e.Data.GetFiles() is not { } items) return;
            var files = items.Select(i => i.TryGetLocalPath()).Where(p => p != null && File.Exists(p)).Cast<string>().ToList();
            if (files.Count == 0) return;
            e.Handled = true;
            Dispatcher.UIThread.Post(() => OnFiles?.Invoke(files));
        });
        // Clicks on the insets (or below a short line) still land in the text.
        PointerPressed += (_, e) => { if (e.Source == this) { Box.Focus(); Box.CaretIndex = (Box.Text ?? "").Length; } };
    }

    public void Configure(Binding<string> text, string placeholder, SFont font, SColor textColor, SColor placeholderColor)
    {
        _text = text;
        _placeholder.Text = placeholder;
        if (_font != font || Box.FontSize != font.Size)
        {
            _font = font;
            Box.FontFamily = font.AvaloniaFamily;
            Box.FontSize = font.Size;
            Box.FontWeight = font.AvaloniaWeight;
            Box.LetterSpacing = font.LetterSpacing;
            _placeholder.OwnFont = font;
            _placeholder.FontFamily = font.AvaloniaFamily;
            _placeholder.FontSize = font.Size;
        }
        var tc = textColor.Resolve(Scheme.Light);
        Box.Foreground = new ImmutableSolidColorBrush(tc);
        Box.CaretBrush = new ImmutableSolidColorBrush(tc);
        _placeholder.SetValue(Env.ForegroundProperty, placeholderColor);
        var v = Tracker.Untracked(() => text.Value);
        if (v != (Box.Text ?? "")) Push(v);
        _placeholder.IsVisible = string.IsNullOrEmpty(Box.Text);
        if (MentionSession != null && MentionChatId != null)
            Mentions.Shared.Register(Box, new ChatRef(MentionSession.Platform, MentionChatId));
    }

    private void Push(string v)
    {
        _pushing = true;
        try { Box.Text = v; Box.CaretIndex = v.Length; } finally { _pushing = false; }
    }

    public bool IsFlexible(Orientation axis) => axis == Orientation.Horizontal;

    private double LineHeight => _font.Size * 1.25 + 8;

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 300 : available.Width;
        Box.Measure(new Size(w, double.PositiveInfinity));
        _placeholder.Measure(new Size(w, double.PositiveInfinity));
        var h = Math.Min(Math.Max(Box.DesiredSize.Height, LineHeight), MaxHeightLimit);
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Box.Arrange(new Rect(final));
        _placeholder.Arrange(new Rect(0, 0, final.Width, _placeholder.DesiredSize.Height + 8));
        return final;
    }

    private void UpdateMention()
    {
        if (MentionSession is { } s && MentionChatId is { } c) Mentions.Shared.Update(Box, s, c);
    }

    private bool Mention(Mentions.Command cmd) =>
        MentionSession is { } s && MentionChatId is { } c && Mentions.Shared.Handle(cmd, s, c);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Enter:
                if ((mods & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0)
                {
                    var t = Box.Text ?? "";
                    var start = Math.Min(Box.SelectionStart, Box.SelectionEnd);
                    var end = Math.Max(Box.SelectionStart, Box.SelectionEnd);
                    Box.Text = t[..start] + "\n" + t[end..];
                    Box.CaretIndex = start + 1;
                }
                else if (!Mention(Mentions.Command.Pick)) OnSubmit();
                e.Handled = true;
                break;
            case Key.Up: if (Mention(Mentions.Command.Up) || OnCommand?.Invoke(e.Key) == true) e.Handled = true; break;
            case Key.Down: if (Mention(Mentions.Command.Down)) e.Handled = true; break;
            case Key.Tab: if (Mention(Mentions.Command.Pick)) e.Handled = true; break;
            case Key.Escape: if (Mention(Mentions.Command.Cancel) || OnCommand?.Invoke(e.Key) == true) e.Handled = true; break;
        }
    }

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (OnFiles == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        e.Handled = true;
        var files = await Composer.ClipboardAttachments(clip);
        if (files is { Count: > 0 }) { OnFiles?.Invoke(files); return; }
        var text = await clip.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        var t = Box.Text ?? "";
        var start = Math.Min(Box.SelectionStart, Box.SelectionEnd);
        var end = Math.Max(Box.SelectionStart, Box.SelectionEnd);
        Box.Text = t[..start] + text + t[end..];
        Box.CaretIndex = start + text.Length;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        lock (Live) { Live.RemoveAll(w => !w.TryGetTarget(out var x) || x == this); Live.Add(new WeakReference<ComposerEditor>(this)); }
        _watch = Watch.Effect(() =>
        {
            var v = _text.Value;
            if (_pushing || v == (Box.Text ?? "")) return;
            Push(v);
        });
        // Focus the composer when a chat opens, like the official apps, unless the user is typing elsewhere.
        DispatcherTimer.RunOnce(() =>
        {
            if (!this.IsAttachedToVisualTree() || TopLevel.GetTopLevel(this) is not { } top) return;
            if (top.FocusManager?.GetFocusedElement() is TextBox other && other != Box) return;
            Box.Focus();
        }, TimeSpan.FromMilliseconds(50));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _watch?.Dispose();
        _watch = null;
    }
}

public static class Composer
{
    /// The chat composer: kept across rebuilds under `key` (use the chat id), reconfigured on every build.
    public static Control Editor(object key, Binding<string> text, string placeholder, Action onSubmit, SFont? font = null,
        SColor? textColor = null, SColor? placeholderColor = null, double maxHeight = 140, Action<bool>? onTypingChange = null,
        PlatformSession? mentionSession = null, string? mentionChatId = null, Action<IReadOnlyList<string>>? onFiles = null,
        Func<Key, bool>? onCommand = null)
    {
        var ed = UI.Memo(("composer", key), () => new ComposerEditor());
        ed.OnCommand = onCommand;
        ed.OnSubmit = onSubmit;
        ed.OnTypingChange = onTypingChange;
        ed.MentionSession = mentionSession;
        ed.MentionChatId = mentionChatId;
        ed.OnFiles = onFiles;
        ed.MaxHeightLimit = maxHeight;
        ed.Configure(text, placeholder, font ?? SFont.System(14), textColor ?? SColor.Black.Opacity(0.85),
            placeholderColor ?? SColor.Black.Opacity(0.25));
        return new KeepHost(ed);
    }

    /// The session's draft for a chat as a binding.
    public static Binding<string> Draft(PlatformSession s, string chatId) => new(() => s.Draft(chatId), v => s.SetDraft(chatId, v));

    private static ComposerEditor? Visible()
    {
        lock (ComposerEditor.Live)
        {
            ComposerEditor? fallback = null;
            for (var i = ComposerEditor.Live.Count - 1; i >= 0; i--)
            {
                if (!ComposerEditor.Live[i].TryGetTarget(out var ed) || !ed.IsAttachedToVisualTree() || !ed.IsEffectivelyVisible) continue;
                if (TopLevel.GetTopLevel(ed) is Window { IsActive: true }) return ed;
                fallback ??= ed;
            }
            return fallback;
        }
    }

    /// Focuses the chat composer of the active window (used by emoji/sticker buttons).
    public static void Focus() => Visible()?.Box.Focus();

    /// Focuses the composer and opens the system emoji picker, which inserts into it.
    public static void Emoji()
    {
        Focus();
        if (OperatingSystem.IsWindows()) DispatcherTimer.RunOnce(Win.OpenEmojiPanel, TimeSpan.FromMilliseconds(60));
    }

    /// Clicking empty space around the composer focuses it.
    public static Control ComposerTapArea(this Control c) => c.OnTap(Focus);

    private static readonly string[] ImageFormats = ["PNG", "image/png", "public.png", "image/jpeg", "JFIF", "public.jpeg", "public.tiff", "Bitmap", "DeviceIndependentBitmap"];

    /// Files on the clipboard, or its image written to a temporary file; null means paste text as usual.
    public static async Task<IReadOnlyList<string>?> ClipboardAttachments(IClipboard clip)
    {
        var formats = await clip.GetFormatsAsync();
        if (formats.Contains(DataFormats.Files) && await clip.GetDataAsync(DataFormats.Files) is IEnumerable<IStorageItem> items)
        {
            var files = items.Select(i => i.TryGetLocalPath()).Where(p => p != null && File.Exists(p)).Cast<string>().ToList();
            if (files.Count > 0) return files;
        }
        var type = ImageFormats.FirstOrDefault(formats.Contains);
        if (type == null) return null;
        // Text copied from Office also carries a picture of itself; that is a text paste. An image copied
        // from a browser may come with just its address as text.
        if ((await clip.GetTextAsync())?.Trim() is { Length: > 0 } s)
        {
            var isLink = !s.Any(char.IsWhiteSpace) && (s.StartsWith("http://") || s.StartsWith("https://"));
            if (!isLink) return null;
        }
        var data = await clip.GetDataAsync(type) as byte[];
        if (data == null || data.Length == 0) return null;
        var ext = type.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || type == "JFIF" ? "jpg" : "png";
        if (type is "DeviceIndependentBitmap" or "Bitmap")
        {
            data = Win.DibToPng(data);
            if (data == null) return null;
            ext = "png";
        }
        var path = Path.Combine(Path.GetTempPath(), $"chatspace-{Guid.NewGuid()}.{ext}");
        await File.WriteAllBytesAsync(path, data);
        return [path];
    }
}

/// Small Windows-only helpers for the composer.
internal static class Win
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// Win+. opens the Windows emoji panel, which types into the focused field.
    public static void OpenEmojiPanel()
    {
        if (!OperatingSystem.IsWindows()) return;
        const byte LWin = 0x5B, Period = 0xBE;
        const uint Up = 0x2;
        keybd_event(LWin, 0, 0, UIntPtr.Zero);
        keybd_event(Period, 0, 0, UIntPtr.Zero);
        keybd_event(Period, 0, Up, UIntPtr.Zero);
        keybd_event(LWin, 0, Up, UIntPtr.Zero);
    }

    /// CF_DIB (BITMAPINFOHEADER + pixels) to PNG bytes.
    public static byte[]? DibToPng(byte[] dib)
    {
        try
        {
            var headerSize = BitConverter.ToInt32(dib, 0);
            var bitCount = BitConverter.ToInt16(dib, 14);
            var compression = BitConverter.ToInt32(dib, 16);
            var colors = BitConverter.ToInt32(dib, 32);
            var palette = (colors == 0 && bitCount <= 8 ? 1 << bitCount : colors) * 4;
            if (compression == 3 && headerSize == 40) palette += 12;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(14 + headerSize + palette).CopyTo(bmp, 10);
            dib.CopyTo(bmp, 14);
            using var input = new MemoryStream(bmp);
            using var image = new Avalonia.Media.Imaging.Bitmap(input);
            using var output = new MemoryStream();
            image.Save(output);
            return output.ToArray();
        }
        catch { return null; }
    }
}
