using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using QRCoder;

namespace ChatSpace.Core;

public static class AppSettings
{
    public static string TelegramApiId
    {
        get => Prefs.GetString("telegram.apiId") ?? "";
        set => Prefs.Set("telegram.apiId", value);
    }

    public static string TelegramApiHash
    {
        get => Prefs.GetString("telegram.apiHash") ?? "";
        set => Prefs.Set("telegram.apiHash", value);
    }

    public static JsonObject BridgeConfig(Platform p)
    {
        if (p == Platform.Telegram && TelegramApiId.Length > 0 && TelegramApiHash.Length > 0)
            return new JsonObject { ["apiId"] = double.TryParse(TelegramApiId, NumberStyles.Any, Inv.C, out var id) ? id : 0, ["apiHash"] = TelegramApiHash };
        return new JsonObject();
    }
}

public static class QRCode
{
    public static Bitmap? Image(string payload, int scale = 10)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(scale, false);
            return new Bitmap(new MemoryStream(png));
        }
        catch { return null; }
    }
}

/// Loads avatars/media from http(s) URLs, local paths and data: URLs, with an in-memory cache.
/// Images are decoded straight to the size they are shown at: a full-resolution photo is ~48 MB of pixels.
public sealed class ImageLoader
{
    public static readonly ImageLoader Shared = new();
    public const double DefaultMaxPixel = 1024;

    private readonly Dictionary<string, (Bitmap bmp, long cost)> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private long _total;
    private const long Limit = 96L << 20;
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return c;
    }

    public static Bitmap? DecodeDataUrl(string s)
    {
        var bytes = DecodeDataUrlBytes(s);
        if (bytes == null) return null;
        try { return new Bitmap(new MemoryStream(bytes)); } catch { return null; }
    }

    private static byte[]? DecodeDataUrlBytes(string s)
    {
        if (!s.StartsWith("data:")) return null;
        var comma = s.IndexOf(',');
        if (comma < 0) return null;
        try { return Convert.FromBase64String(s[(comma + 1)..].Trim()); } catch { return null; }
    }

    /// Decodes at most `maxPixel` on the long side (never upscales).
    public static Bitmap? Downsample(byte[] data, double maxPixel)
    {
        try
        {
            using var probe = new Bitmap(new MemoryStream(data));
            var w = probe.PixelSize.Width;
            var h = probe.PixelSize.Height;
            var max = Math.Max(16, (int)Math.Ceiling(maxPixel));
            if (Math.Max(w, h) <= max) return new Bitmap(new MemoryStream(data));
            return w >= h
                ? Bitmap.DecodeToWidth(new MemoryStream(data), max, BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToHeight(new MemoryStream(data), max, BitmapInterpolationMode.HighQuality);
        }
        catch { return null; }
    }

    private static string Key(string source, double maxPixel) => $"{(int)maxPixel}|{source}";

    public Bitmap? Cached(string source, double maxPixel = DefaultMaxPixel)
    {
        lock (_cache) return _cache.TryGetValue(Key(source, maxPixel), out var e) ? e.bmp : null;
    }

    public static bool IsLocalPath(string s) => Path.IsPathRooted(s) && !s.StartsWith("//") || s.StartsWith("file://");

    /// Raw bytes behind a data: URL, local path or http(s) URL.
    public async Task<byte[]?> Data(string source)
    {
        if (source.StartsWith("data:")) return DecodeDataUrlBytes(source);
        if (IsLocalPath(source))
        {
            var path = source.StartsWith("file://") ? new Uri(source).LocalPath : source;
            return await Task.Run(() => { try { return File.ReadAllBytes(path); } catch { return null; } });
        }
        if (source.StartsWith("avares://"))
        {
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(new Uri(source));
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
        var task = _inflight.GetOrAdd(source, src => Fetch(src));
        try { return await task; }
        finally { _inflight.TryRemove(source, out _); }
    }

    private static async Task<byte[]?> Fetch(string src)
    {
        try
        {
            using var resp = await Http.GetAsync(src);
            if ((int)resp.StatusCode >= 400) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch { return null; }
    }

    public async Task<Bitmap?> Load(string source, double maxPixel = DefaultMaxPixel)
    {
        if (Cached(source, maxPixel) is { } hit) return hit;
        var data = await Data(source);
        if (data == null) return null;
        var img = await Task.Run(() => Downsample(data, maxPixel));
        return img == null ? null : Store(source, maxPixel, img);
    }

    /// Small local files (group icons, avatars already on disk) decoded on the spot.
    public Bitmap? LoadLocalNow(string path, double maxPixel = DefaultMaxPixel)
    {
        if (Cached(path, maxPixel) is { } hit) return hit;
        byte[] data;
        try { data = File.ReadAllBytes(path); } catch { return null; }
        return Downsample(data, maxPixel) is { } img ? Store(path, maxPixel, img) : null;
    }

    private Bitmap Store(string source, double maxPixel, Bitmap img)
    {
        var key = Key(source, maxPixel);
        var cost = Math.Max(1L, (long)img.PixelSize.Width * img.PixelSize.Height * 4);
        lock (_cache)
        {
            if (_cache.ContainsKey(key)) return _cache[key].bmp;
            _cache[key] = (img, cost);
            _lru.AddLast(key);
            _total += cost;
            while (_total > Limit && _lru.First is { } first)
            {
                _lru.RemoveFirst();
                if (_cache.Remove(first.Value, out var old)) _total -= old.cost;
            }
        }
        return img;
    }
}

public static class Strings
{
    /// Up to two initials, the way most messengers render avatar placeholders.
    public static string Initials(this string s)
    {
        var words = s.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var letters = new List<string>();
        foreach (var w in words.Take(2))
        {
            var e = StringInfo.GetTextElementEnumerator(w);
            while (e.MoveNext())
            {
                var el = (string)e.Current;
                if (char.IsLetterOrDigit(el, 0)) { letters.Add(el); break; }
            }
        }
        if (letters.Count == 0)
        {
            if (s.Length == 0) return "?";
            var e = StringInfo.GetTextElementEnumerator(s);
            e.MoveNext();
            return ((string)e.Current).ToUpperInvariant();
        }
        return string.Concat(letters).ToUpperInvariant();
    }

    public static int StableHash(this string s)
    {
        ulong h = 5381;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) h = unchecked(h * 33 + b);
        return (int)(h % 1_000_000);
    }

    /// Number of user-perceived characters (Swift's `String.count`).
    public static int GraphemeCount(this string s) => new StringInfo(s).LengthInTextElements;
}

/// A run of text, optionally a link.
public sealed record TextSegment(string Text, string? Url);

public static class Linkify
{
    private static readonly Regex Url = new(
        @"(?<![\w@])((?:https?://|www\.)[^\s<>""']+|[a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)*\.(?:com|vn|net|org|io|me|app|dev|co|info|edu|gov)(?:/[^\s<>""']*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Text with URLs split out as links (NSDataDetector's link type).
    public static List<TextSegment> Segments(string text)
    {
        var list = new List<TextSegment>();
        var at = 0;
        foreach (Match m in Url.Matches(text))
        {
            var value = m.Value.TrimEnd('.', ',', ')', '!', '?', ';', ':');
            if (value.Length == 0) continue;
            if (m.Index > at) list.Add(new TextSegment(text[at..m.Index], null));
            var href = value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : "http://" + value;
            list.Add(new TextSegment(value, href));
            at = m.Index + value.Length;
        }
        if (at < text.Length) list.Add(new TextSegment(text[at..], null));
        return list;
    }

    public static IEnumerable<string> Urls(string text) => Segments(text).Where(s => s.Url != null).Select(s => s.Url!);
}

public static class Jumbo
{
    /// Emoji-only short messages are shown big and bubble-less by every platform.
    public static bool IsJumboEmoji(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.GraphemeCount() > 3) return false;
        foreach (var r in t.EnumerateRunes())
        {
            var v = r.Value;
            if (!(Emoji.IsEmojiPresentation(v) || (Emoji.IsEmoji(v) && v > 0x238C) || v == 0xFE0F || v == 0x200D || v is >= 0x1F3FB and <= 0x1F3FF)) return false;
        }
        return true;
    }
}

public static class Fmt
{
    public static string Time(DateTime d) => d.ToString("HH:mm", Inv.C);

    public static string DayKey(DateTime d) => $"{d.Year}-{d.Month}-{d.Day}";
}

public static class Shell
{
    /// `NSWorkspace.open`: a file in its default app, a URL in the browser.
    public static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", [target]);
            else Process.Start("xdg-open", [target]);
        }
        catch { }
    }

    /// Shows a file selected in Explorer (Finder's "Show in Finder").
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
        }
        catch { }
    }
}
