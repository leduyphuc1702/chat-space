using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChatSpace.Kit;

namespace ChatSpace.Core;

public enum SoundKind { Incoming, Sent }

/// Original artwork, fonts and sounds of the official apps (WhatsApp, Telegram, Zalo), exported once
/// from the macOS apps into the bundle (Assets/Native) by win/tools/ExportAssets.swift.
public sealed class NativeAssets
{
    public static readonly NativeAssets Shared = new();
    private const string Base = "avares://ChatSpace/Assets/Native/";

    private readonly Dictionary<string, Bitmap?> _images = new();

    public static bool AssetExists(string relative) => AssetLoader.Exists(new Uri(Base + relative));

    public Bitmap? Image(string relative)
    {
        lock (_images)
        {
            if (_images.TryGetValue(relative, out var b)) return b;
            try { b = new Bitmap(AssetLoader.Open(new Uri(Base + relative))); } catch { b = null; }
            _images[relative] = b;
            return b;
        }
    }

    /// The official app's own icon (icons/whatsapp.png …); Messenger has none on this Mac.
    public Bitmap? AppIcon(Platform p) => p == Platform.Messenger ? null : Image($"icons/{p.Raw()}.png");

    /// Telegram's classic doodle pattern, 1440×2960 px, tiled at half size.
    public Bitmap? TelegramPattern() => Image("telegram/pattern.png");

    /// Zalo PC web-bundle images by name prefix: logo-new, inapp-welcome-screen-01…
    public Bitmap? ZaloImage(string prefix)
    {
        var name = prefix.TrimEnd('.', '-');
        return Image($"zalo/{name}.png");
    }

    public static bool ZaloFontsReady => true;

    /// How to start the official desktop app: an executable path or a protocol URI; null when it isn't installed.
    public string? AppLaunch(Platform p)
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] candidates = p switch
            {
                Platform.Zalo => [Path.Combine(local, "Programs", "Zalo", "Zalo.exe")],
                Platform.Telegram => [Path.Combine(roaming, "Telegram Desktop", "Telegram.exe")],
                _ => [],
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return p switch
            {
                Platform.Whatsapp => "whatsapp:",
                Platform.Messenger => "https://www.messenger.com/",
                Platform.Telegram => "tg://",
                _ => null,
            };
        }
        var mac = p switch
        {
            Platform.Zalo => "/Applications/Zalo.app", Platform.Whatsapp => "/Applications/WhatsApp.app",
            Platform.Telegram => "/Applications/Telegram.app", _ => "/Applications/Messenger.app",
        };
        return Directory.Exists(mac) ? mac : null;
    }

    // MARK: Sounds

    private static string? SoundFile(Platform p, SoundKind kind) => (p, kind) switch
    {
        (Platform.Whatsapp, SoundKind.Incoming) => "whatsapp-incoming.wav",
        (Platform.Whatsapp, SoundKind.Sent) => "whatsapp-sent.wav",
        (Platform.Telegram, SoundKind.Incoming) => "telegram-incoming.wav",
        (Platform.Telegram, SoundKind.Sent) => "telegram-sent.wav",
        (Platform.Zalo, SoundKind.Incoming) => "pop.wav",
        (_, SoundKind.Incoming) => "tink.wav",
        _ => null,
    };

    private readonly Dictionary<string, string> _extracted = new();

    /// The sound copied out of the bundle to a real file (players and toasts need a path).
    public string? SoundPath(Platform p, SoundKind kind)
    {
        if (SoundFile(p, kind) is not { } name) return null;
        lock (_extracted)
        {
            if (_extracted.TryGetValue(name, out var path)) return path;
            try
            {
                var dir = Path.Combine(Platforms.BaseDirectory, "sounds");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, name);
                if (!File.Exists(path))
                {
                    using var src = AssetLoader.Open(new Uri(Base + "sounds/" + name));
                    using var dst = File.Create(path);
                    src.CopyTo(dst);
                }
                _extracted[name] = path;
                return path;
            }
            catch { return null; }
        }
    }

#if WINDOWS
    private readonly Dictionary<string, System.Media.SoundPlayer> _players = new();
#endif

    public void Play(Platform p, SoundKind kind)
    {
        if (App.Headless || SoundPath(p, kind) is not { } path) return;
        try
        {
#if WINDOWS
            if (!_players.TryGetValue(path, out var player))
            {
                player = new System.Media.SoundPlayer(path);
                player.Load();
                _players[path] = player;
            }
            player.Stop();
            player.Play();
#else
            if (OperatingSystem.IsMacOS()) Process.Start("afplay", [path]);
#endif
        }
        catch { }
    }
}

/// Codepoints of the zalo-font glyphs used by the Zalo UI (from the font's SVG glyph names).
public static class ZaloGlyphs
{
    public const int MessageFilled = 0xEC5A, MessageLine = 0xEC5B;
    public const int ContactLine = 0xEABE, ContactFilled = 0xEABD;
    public const int TodoLine = 0xEDB7, CloudLine = 0xEAA5, SettingLine = 0xED25;
    public const int Search = 0xED16, AddUser = 0xEA14, CreateGroup = 0xEB45;
    public const int Emoji = 0xEA62, Attach = 0xEA60, Photo = 0xEA70;
    public const int Sticker = 0xEA77, File = 0xEA63;
    public const int BellSlash = 0xEA2B, Pin = 0xECCC;
    public const int Check = 0xEA7B, DoubleCheck = 0xEAE7;
    public const int Call = 0xEA41, Video = 0xEDF1;
    public const int Quote = 0xEAD0, Forward = 0xEACE, More = 0xEACF, Reaction = 0xEAD1;
    public const int AddGroupMember = 0xEA0F, Sidebar = 0xED30, Screenshot = 0xEA76;
    public const int Namecard = 0xEC73, TextFormat = 0xEDAC, MoreH = 0xEC67;
    public const int EmojiLine = 0xEAF9, QuoteLine = 0xECEE, Close = 0xEA9F;
    public const int ChevronDown = 0xEA8C, Like = 0xEC43, PinFilled = 0xECCE;
    public const int Group = 0xEB44, Briefcase = 0xEB4A, AttachLine = 0xEA22;
}

/// Zalo PC's UI typeface (Segoe UI Variable, shipped inside Zalo) as SFonts.
public static class ZaloFont
{
    private const string Dir = "avares://ChatSpace/Assets/Native/zalo";
    public const string IconFamily = Dir + "#zalo-font";

    /// `Font.zalo(size, weight)` of the macOS app: Text optical size below 20 pt, Display from 20 pt.
    public static SFont Of(double size, Weight weight = Weight.Regular)
    {
        // Avalonia files Zalo's faces under their typographic family (name ID 16), not "Zalo Segoe UI … Semibold";
        // only Display has a Bold face.
        const string display = "Segoe UI Variable Static Display", text = "Segoe UI Variable Static Text";
        var (family, w) = weight switch
        {
            Weight.Semibold or Weight.Medium => (size >= 20 ? display : text, Weight.Semibold),
            Weight.Bold or Weight.Heavy or Weight.Black => (display, Weight.Bold),
            _ => (size >= 20 ? display : text, Weight.Regular),
        };
        return SFont.Custom($"{Dir}#{family}", size, w);
    }

    public static SFont Icon(double size) => SFont.Custom(IconFamily, size);
}
