using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using ChatSpace.Kit;

namespace ChatSpace.Core;

/// What the left rail shows: the ChatSpace inbox, one platform's own UI, or a user group.
public sealed record RailItem
{
    public enum RailKind { Inbox, Platform, Group }
    public RailKind Kind { get; init; }
    public Platform Platform { get; init; }
    public Guid GroupId { get; init; }

    public static readonly RailItem Inbox = new() { Kind = RailKind.Inbox };
    public static RailItem For(Platform p) => new() { Kind = RailKind.Platform, Platform = p };
    public static RailItem Group(Guid id) => new() { Kind = RailKind.Group, GroupId = id };

    public bool IsPlatform(out Platform p) { p = Platform; return Kind == RailKind.Platform; }

    public string Key => Kind switch
    {
        RailKind.Inbox => "inbox",
        RailKind.Platform => "platform:" + Platform.Raw(),
        _ => "group:" + GroupId.ToString().ToUpperInvariant(),
    };

    public static RailItem? FromKey(string key)
    {
        if (key == "inbox") return Inbox;
        var parts = key.Split(':', 2);
        if (parts.Length != 2) return null;
        if (parts[0] == "platform" && Platforms.Parse(parts[1]) is { } p) return For(p);
        if (parts[0] == "group" && Guid.TryParse(parts[1], out var id)) return Group(id);
        return null;
    }
}

/// A chat on a specific platform.
public readonly record struct ChatRef(Platform Platform, string ChatId)
{
    [JsonIgnore] public string Id => Platform.Raw() + "|" + ChatId;
}

/// User-defined "platform": every chat (any platform) whose title matches one of the keywords.
public sealed record CustomGroup
{
    public enum MatchKind { Contains, Prefix, Word }

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public MatchKind Match { get; init; } = MatchKind.Contains;
    /// Empty = all platforms.
    public IReadOnlyList<Platform> Platforms { get; init; } = [];
    public string? Symbol { get; init; } = "folder.fill";
    /// Custom image copied into %APPDATA%/ChatSpace/groups.
    public string? ImagePath { get; init; }
    public uint ColorHex { get; init; } = 0x5E5CE6;

    public bool Matches(Chat chat, Platform platform)
    {
        if (Platforms.Count > 0 && !Platforms.Contains(platform)) return false;
        var title = Normalize(chat.Title);
        return Keywords.Any(raw =>
        {
            var k = Normalize(raw);
            if (k.Length == 0) return false;
            switch (Match)
            {
                case MatchKind.Contains: return title.Contains(k, StringComparison.Ordinal);
                case MatchKind.Prefix: return title.StartsWith(k, StringComparison.Ordinal);
                default:
                    // "VF" matches "VF 1", "VF-2", "VF3" but not "AVF".
                    var from = 0;
                    while (from <= title.Length)
                    {
                        var at = title.IndexOf(k, from, StringComparison.Ordinal);
                        if (at < 0) return false;
                        if (at == 0 || !char.IsLetter(title[at - 1])) return true;
                        from = at + 1;
                    }
                    return false;
            }
        });
    }

    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('đ', 'd').Trim();
    }

    public bool Equals(CustomGroup? o) => o is not null && Id == o.Id && Name == o.Name && Keywords.SequenceEqual(o.Keywords) && Match == o.Match
        && Platforms.SequenceEqual(o.Platforms) && Symbol == o.Symbol && ImagePath == o.ImagePath && ColorHex == o.ColorHex;
    public override int GetHashCode() => Id.GetHashCode();
}

/// A chat the user chose to skip in the ChatSpace inbox ("Bỏ qua cho lần sau").
public sealed record IgnoredChat(ChatRef Ref, string Title, DateTime IgnoredAt)
{
    [JsonIgnore] public string Id => Ref.Id;
}

/// Settings → Dữ liệu và bộ nhớ (modelled on Telegram's Data and Storage).
public sealed record StorageSettings
{
    /// 0 = forever.
    public int KeepMediaDays { get; init; } = 7;
    /// 0 = unlimited.
    public int MaxCacheMB { get; init; } = 2048;
    public bool CompressImages { get; init; } = true;
    /// 0 = unlimited.
    public int MaxStoredMessagesPerChat { get; init; } = 1000;
    public bool AutoDownloadPhotos { get; init; } = true;
    public bool AutoDownloadVideos { get; init; }
    public bool AutoDownloadFiles { get; init; }
    public double AutoDownloadMaxMB { get; init; } = 10;

    public bool AutoDownloads(Message m)
    {
        if (m.Media?.Size is { } size && size > AutoDownloadMaxMB * 1_048_576) return false;
        return m.Kind switch
        {
            MessageKind.Image or MessageKind.Sticker => AutoDownloadPhotos,
            MessageKind.Video or MessageKind.Gif => AutoDownloadVideos,
            MessageKind.Voice or MessageKind.Audio => true,
            _ => AutoDownloadFiles,
        };
    }

    [JsonIgnore]
    public JsonObject BridgeParams => new()
    {
        ["keepMediaDays"] = KeepMediaDays, ["maxCacheMB"] = MaxCacheMB,
        ["compressImages"] = CompressImages, ["maxStoredMessagesPerChat"] = MaxStoredMessagesPerChat,
    };
}

/// Floating chat heads (Telegram Android "bubbles").
public sealed record BubbleSettings
{
    public enum TriggerKind { Off, NewMessages, Manual }
    public TriggerKind Trigger { get; init; } = TriggerKind.NewMessages;
    /// Only pop a bubble when ChatSpace is not the frontmost app.
    public bool OnlyInBackground { get; init; }
    /// Show the incoming message beside its bubble for a few seconds.
    public bool ShowPreview { get; init; } = true;
    public int MaxBubbles { get; init; } = 5;
    public bool IncludeGroups { get; init; } = true;
}

/// Everything ChatSpace itself owns (as opposed to platform data), persisted in settings.json.
public sealed class Workspace : Observable
{
    public static readonly Workspace Shared = new();

    private ImmutableList<CustomGroup> _groups;
    public ImmutableList<CustomGroup> Groups { get => Get(ref _groups); set { Set(ref _groups, value); Save(value, "workspace.groups"); } }
    private ImmutableList<IgnoredChat> _ignored;
    public ImmutableList<IgnoredChat> Ignored { get => Get(ref _ignored); set { Set(ref _ignored, value); Save(value, "workspace.ignored"); } }
    private StorageSettings _storage;
    public StorageSettings Storage
    {
        get => Get(ref _storage);
        set
        {
            var changed = !Equals(_storage, value);
            Set(ref _storage, value);
            Save(value, "workspace.storage");
            if (changed) PushStorage();
        }
    }
    private BubbleSettings _bubbles;
    public BubbleSettings Bubbles { get => Get(ref _bubbles); set { Set(ref _bubbles, value); Save(value, "workspace.bubbles"); } }
    private bool _showInbox;
    public bool ShowInbox { get => Get(ref _showInbox); set { Set(ref _showInbox, value); Save(value, "workspace.showInbox"); } }
    private TranslationSettings _translation;
    public TranslationSettings Translation { get => Get(ref _translation); set { Set(ref _translation, value); Save(value, "workspace.translation"); } }
    /// Offer sending typed arithmetic with its result before a message goes out.
    private bool _mathPreview;
    public bool MathPreview { get => Get(ref _mathPreview); set { Set(ref _mathPreview, value); Save(value, "workspace.mathPreview"); } }

    public static Guid DemoGroupId => new("00000000-0000-0000-0000-0000000000F1");

    private Workspace()
    {
        _groups = Load<List<CustomGroup>>("workspace.groups")?.ToImmutableList() ?? [];
        _ignored = Load<List<IgnoredChat>>("workspace.ignored")?.ToImmutableList() ?? [];
        _storage = Load<StorageSettings>("workspace.storage") ?? new StorageSettings();
        _bubbles = Load<BubbleSettings>("workspace.bubbles") ?? new BubbleSettings();
        _translation = Load<TranslationSettings>("workspace.translation") ?? new TranslationSettings();
        _showInbox = DemoData.Enabled ? true : Prefs.GetBool("workspace.showInbox") ?? true;
        _mathPreview = DemoData.Enabled ? true : Prefs.GetBool("workspace.mathPreview") ?? true;
        if (DemoData.Enabled && _groups.IsEmpty)
        {
            var list = new List<CustomGroup>
            {
                new() { Id = DemoGroupId, Name = "VF", Keywords = ["VF"], Match = CustomGroup.MatchKind.Word, Symbol = "car.fill", ColorHex = 0x0A84FF },
            };
            // CHATSPACE_DEMO_GROUPS=n adds n more, to make the rail scroll.
            var extra = int.TryParse(Environment.GetEnvironmentVariable("CHATSPACE_DEMO_GROUPS"), out var n) ? n : 0;
            for (var i = 0; i < Math.Max(0, extra); i++) list.Add(new CustomGroup { Name = $"Nhóm {i + 1}", Keywords = ["Nhóm"], ColorHex = 0x34C759 });
            _groups = list.ToImmutableList();
        }
    }

    public bool IsIgnored(ChatRef r) => Ignored.Any(i => i.Ref == r);

    public void Ignore(ChatRef r, string title)
    {
        if (IsIgnored(r)) return;
        Ignored = Ignored.Add(new IgnoredChat(r, title, DateTime.Now));
    }

    public void Restore(ChatRef r) => Ignored = Ignored.RemoveAll(i => i.Ref == r);

    public CustomGroup? Group(Guid id) => Groups.FirstOrDefault(g => g.Id == id);

    public void Upsert(CustomGroup g)
    {
        var i = Groups.FindIndex(x => x.Id == g.Id);
        Groups = i >= 0 ? Groups.SetItem(i, g) : Groups.Add(g);
    }

    public void Delete(Guid id)
    {
        if (Group(id)?.ImagePath is { } path) { try { File.Delete(path); } catch { } }
        Groups = Groups.RemoveAll(g => g.Id == id);
        if (AppModel.Shared.Rail == RailItem.Group(id)) AppModel.Shared.Rail = RailItem.Inbox;
    }

    /// Copies a user-picked image next to the settings so the group icon survives the original moving.
    public static string? ImportGroupImage(string path, Guid id)
    {
        try
        {
            var dir = Path.Combine(Platforms.BaseDirectory, "groups");
            Directory.CreateDirectory(dir);
            using var src = new Bitmap(path);
            // Icons are shown at ≤ 44 pt; 176 px PNG keeps them sharp on high-DPI screens without bloating disk.
            const int side = 176;
            var s = Math.Min(src.PixelSize.Width, src.PixelSize.Height);
            using var rt = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(side, side));
            using (var ctx = rt.CreateDrawingContext())
            {
                var sr = new Avalonia.Rect((src.PixelSize.Width - s) / 2.0, (src.PixelSize.Height - s) / 2.0, s, s);
                ctx.DrawImage(src, sr, new Avalonia.Rect(0, 0, side, side));
            }
            var dest = Path.Combine(dir, id.ToString().ToUpperInvariant() + ".png");
            rt.Save(dest);
            return dest;
        }
        catch { return null; }
    }

    public void PushStorage()
    {
        foreach (var s in AppModel.Shared.Sessions.Values) s.ConfigureStorage();
    }

    /// Demo mode never touches the real user's saved groups, ignored chats or settings.
    private static void Save<T>(T v, string key)
    {
        if (DemoData.Enabled) return;
        Prefs.Set(key, v);
    }

    private static T? Load<T>(string key) => DemoData.Enabled ? default : Prefs.Get<T>(key);
}
