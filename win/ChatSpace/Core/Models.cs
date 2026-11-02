using System.Globalization;
using System.Text.Json.Serialization;

namespace ChatSpace.Core;

// Mirrors docs/PROTOCOL.md.

public enum AuthState { LoggedOut, Connecting, AwaitingQr, AwaitingPassword, AwaitingCode, AwaitingInput, LoggedIn, Error }

public sealed record SelfInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Avatar { get; init; }
    public string? Phone { get; init; }
}

public enum ChatKind { Direct, Group, Channel, Bot, Self }

public static class Time
{
    public static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static DateTime Local(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
}

public sealed record Chat
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Avatar { get; init; }
    public ChatKind Kind { get; init; }
    public int Unread { get; init; }
    public bool? MarkedUnread { get; init; }
    public bool? Pinned { get; init; }
    public bool? Muted { get; init; }
    public double? MutedUntil { get; init; }
    public double Timestamp { get; init; }
    public Message? LastMessage { get; init; }
    public int? MemberCount { get; init; }
    public bool? Online { get; init; }
    public double? LastSeen { get; init; }
    public bool? IsContact { get; init; }
    public bool? Archived { get; init; }
    public bool? Encrypted { get; init; }
    /// Last message from before the chat switched to end-to-end encryption; encrypted ones after it may be missing.
    public string? EncryptedAfter { get; init; }

    [JsonIgnore] public bool IsGroup => Kind is ChatKind.Group or ChatKind.Channel;
    [JsonIgnore] public bool IsPinned => Pinned ?? false;
    [JsonIgnore] public bool IsMuted => MutedUntil is { } until ? until < 0 || until > Time.NowMs : Muted ?? false;
    /// Unread messages, or manually marked as unread.
    [JsonIgnore] public bool HasUnread => Unread > 0 || (MarkedUnread ?? false);
    [JsonIgnore] public DateTime Date => Time.Local(Timestamp);
}

/// Telegram dialog filter (`chats.folders` / `folders.update`).
public sealed record ChatFolder
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Emoticon { get; init; }
    public IReadOnlyList<string> IncludeChatIds { get; init; } = [];
    public IReadOnlyList<string> ExcludeChatIds { get; init; } = [];
    public IReadOnlyList<string> PinnedChatIds { get; init; } = [];
    public bool? Contacts { get; init; }
    public bool? NonContacts { get; init; }
    public bool? Groups { get; init; }
    public bool? Channels { get; init; }
    public bool? Bots { get; init; }
    public bool? ExcludeMuted { get; init; }
    public bool? ExcludeRead { get; init; }
    public bool? ExcludeArchived { get; init; }

    public bool Contains(Chat c)
    {
        if (ExcludeChatIds.Contains(c.Id)) return false;
        if (PinnedChatIds.Contains(c.Id) || IncludeChatIds.Contains(c.Id)) return true;
        if (ExcludeMuted == true && c.IsMuted) return false;
        if (ExcludeRead == true && !c.HasUnread) return false;
        if (ExcludeArchived == true && c.Archived == true) return false;
        return c.Kind switch
        {
            ChatKind.Direct or ChatKind.Self => c.IsContact == true ? Contacts == true : NonContacts == true,
            ChatKind.Bot => Bots == true,
            ChatKind.Group => Groups == true,
            _ => Channels == true,
        };
    }

    private static readonly Dictionary<string, string> ByEmoticon = new()
    {
        ["🐱"] = "Animal", ["📕"] = "Book", ["💰"] = "Coin", ["🎮"] = "Game", ["🏠"] = "Home", ["💡"] = "Lamp",
        ["👌"] = "Like", ["🔒"] = "Lock", ["❤️"] = "Love", ["🎭"] = "Mask", ["➗"] = "Math", ["🎵"] = "Music",
        ["🎨"] = "Paint", ["✈️"] = "Plane", ["⚽️"] = "Sport", ["⭐"] = "Star", ["🎓"] = "Student", ["🛫"] = "Telegram",
        ["👤"] = "Personal", ["👥"] = "Group", ["💬"] = "AllChats", ["✅"] = "Read", ["🔔"] = "Unmuted",
        ["📁"] = "Folder", ["🗂"] = "Folder", ["⚡"] = "Flash", ["📢"] = "Channel", ["🤖"] = "Bot", ["🍷"] = "Wine",
        ["💼"] = "Work", ["🏛"] = "Work", ["📚"] = "Book", ["💸"] = "Coin",
    };

    /// Telegram for macOS picks the sidebar icon from the emoticon, else from the folder's rules.
    [JsonIgnore]
    public string IconName
    {
        get
        {
            if (Emoticon is { } e && (ByEmoticon.TryGetValue(e, out var n) || ByEmoticon.TryGetValue(e.Replace("\uFE0F", ""), out n)))
                return "Icon_Sidebar_" + n;
            var flags = new[] { Contacts == true || NonContacts == true, Groups == true, Channels == true, Bots == true };
            if (IncludeChatIds.Count == 0 && flags.Count(f => f) == 1)
            {
                if (Bots == true) return "Icon_Sidebar_Bot";
                if (Channels == true) return "Icon_Sidebar_Channel";
                if (Groups == true) return "Icon_Sidebar_Group";
                return "Icon_Sidebar_Personal";
            }
            if (ExcludeRead == true) return "Icon_Sidebar_Unread";
            if (ExcludeMuted == true) return "Icon_Sidebar_Unmuted";
            return "Icon_Sidebar_Folder";
        }
    }
}

public enum MessageKind { Text, Image, Video, Audio, Voice, File, Sticker, Gif, Location, Contact, Link, Call, System, Unsupported }

public sealed record Media
{
    public string? Mime { get; init; }
    public string? Url { get; init; }
    public string? Path { get; init; }
    public string? Thumbnail { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double? Duration { get; init; }
    public string? FileName { get; init; }
    public double? Size { get; init; }

    [JsonIgnore]
    public double? Aspect => Width is { } w && Height is { } h && w > 0 && h > 0 ? w / h : null;
}

public sealed record Reaction
{
    private readonly string _emoji = "";
    public string Emoji { get => _emoji; init => _emoji = value.EmojiPresentation(); }
    public int Count { get; init; }
    public bool FromMe { get; init; }

    public Reaction() { }
    public Reaction(string emoji, int count, bool fromMe) { Emoji = emoji; Count = count; FromMe = fromMe; }
}

public static class EmojiText
{
    /// "❤" without U+FE0F renders as a black text glyph; platforms such as Telegram send it bare.
    public static string EmojiPresentation(this string s)
    {
        var runes = s.EnumerateRunes().ToList();
        if (runes.Count != 1) return s;
        var v = runes[0].Value;
        if (v <= 0x7F || !Emoji.IsEmoji(v) || Emoji.IsEmojiPresentation(v)) return s;
        return s + "\uFE0F";
    }
}

/// Minimal Unicode emoji property tables (Emoji / Emoji_Presentation), enough for reactions and jumbo-emoji checks.
public static class Emoji
{
    public static bool IsEmojiPresentation(int v) =>
        v is >= 0x1F300 and <= 0x1F5FF or >= 0x1F600 and <= 0x1F64F or >= 0x1F680 and <= 0x1F6FF or >= 0x1F900 and <= 0x1F9FF
            or >= 0x1FA70 and <= 0x1FAFF or >= 0x1F1E6 and <= 0x1F1FF or >= 0x1F004 and <= 0x1F0CF or >= 0x1F18E and <= 0x1F251
            or 0x231A or 0x231B or >= 0x23E9 and <= 0x23EC or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE or 0x2614 or 0x2615
            or >= 0x2648 and <= 0x2653 or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE or 0x26C4 or 0x26C5
            or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD or 0x2705 or 0x270A or 0x270B
            or 0x2728 or 0x274C or 0x274E or >= 0x2753 and <= 0x2755 or 0x2757 or >= 0x2795 and <= 0x2797 or 0x27B0 or 0x27BF
            or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55;

    public static bool IsEmoji(int v) =>
        IsEmojiPresentation(v) || v is 0x23 or 0x2A or >= 0x30 and <= 0x39 or 0xA9 or 0xAE or 0x203C or 0x2049 or 0x2122 or 0x2139
            or >= 0x2194 and <= 0x2199 or 0x21A9 or 0x21AA or 0x2328 or 0x23CF or >= 0x23ED and <= 0x23EF or 0x23F1 or 0x23F2
            or >= 0x23F8 and <= 0x23FA or 0x24C2 or 0x25AA or 0x25AB or 0x25B6 or 0x25C0 or >= 0x25FB and <= 0x25FE
            or >= 0x2600 and <= 0x27BF or 0x2934 or 0x2935 or >= 0x2B05 and <= 0x2B07 or 0x3030 or 0x303D or 0x3297 or 0x3299
            or >= 0x1F000 and <= 0x1FAFF;
}

public sealed record ReplyRef
{
    public string Id { get; init; } = "";
    public string? Text { get; init; }
    public string? SenderName { get; init; }
    public MessageKind? Kind { get; init; }
}

public enum DeliveryStatus { Pending, Sent, Delivered, Read, Failed }

public sealed record Message
{
    public string Id { get; init; } = "";
    public string ChatId { get; init; } = "";
    public string SenderId { get; init; } = "";
    public string? SenderName { get; init; }
    public string? SenderAvatar { get; init; }
    public bool FromMe { get; init; }
    public double Timestamp { get; init; }
    public MessageKind Kind { get; init; }
    public string? Text { get; init; }
    public Media? Media { get; init; }
    public ReplyRef? ReplyTo { get; init; }
    public bool? Forwarded { get; init; }
    public bool? Edited { get; init; }
    public bool? Deleted { get; init; }
    public IReadOnlyList<Reaction>? Reactions { get; init; }
    public DeliveryStatus? Status { get; init; }

    [JsonIgnore] public DateTime Date => Time.Local(Timestamp);
    [JsonIgnore] public bool IsDeleted => Deleted ?? false;
    [JsonIgnore] public bool IsEdited => Edited ?? false;
    [JsonIgnore] public bool HasReactions => Reactions is { Count: > 0 };

    /// One-line preview for chat lists.
    public string Preview(string photo = "Photo", string video = "Video", string voice = "Voice message",
        string sticker = "Sticker", string file = "File", string deleted = "Message deleted")
    {
        if (IsDeleted) return deleted;
        var t = Text?.Trim() ?? "";
        return Kind switch
        {
            MessageKind.Image => t.Length == 0 ? photo : "🖼 " + t,
            MessageKind.Video or MessageKind.Gif => t.Length == 0 ? video : "📹 " + t,
            MessageKind.Voice or MessageKind.Audio => voice,
            MessageKind.Sticker => t.Length == 0 ? sticker : t,
            MessageKind.File => Media?.FileName ?? (t.Length == 0 ? file : t),
            MessageKind.Location => "📍 " + (t.Length == 0 ? "Location" : t),
            _ => t,
        };
    }

    /// Replaces `media.path`, creating the media record if needed.
    public Message WithMediaPath(string? path) => this with { Media = (Media ?? new Media()) with { Path = path } };
}

/// One screen of a multi-step native login (`auth.step`).
public sealed record AuthStep
{
    public sealed record Field
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public IReadOnlyList<string>? Options { get; init; }
        public string? Default { get; init; }
    }
    public string StepId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? Instructions { get; init; }
    public IReadOnlyList<Field>? Fields { get; init; }
    public IReadOnlyList<string>? Images { get; init; }
    public bool? CanCancel { get; init; }
    public string? Error { get; init; }
}

public sealed record TypingInfo(string UserId, string? Name);

public sealed record ChatMember
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Avatar { get; init; }
}

/// A member picked from the "@" list; its visible text is "@" + name.
public sealed record MentionPick(string UserId, string Name);

public enum HistoryBackup { Unknown, None, Locked, Restored }

/// Progress of a Zalo phone sync, as reported by the bridge (docs/PROTOCOL.md, history.phoneSync).
public sealed record PhoneSyncProgress
{
    public enum SyncState { Idle, Preparing, Waiting, Paused, Chats, Messages, Done, Failed, Cancelled }
    public SyncState State { get; init; }
    public int? Chats { get; init; }
    public int? ChatsDone { get; init; }
    public int? Messages { get; init; }
    public int? Skipped { get; init; }
    public string? Code { get; init; }
    public string? Error { get; init; }

    [JsonIgnore]
    public bool Running => State is SyncState.Preparing or SyncState.Waiting or SyncState.Paused or SyncState.Chats or SyncState.Messages;
}

public abstract record HistoryRestoreResult
{
    public sealed record Restored : HistoryRestoreResult;
    public sealed record WrongPin(int? AttemptsLeft) : HistoryRestoreResult;
    /// The account never set up Messenger's secure storage.
    public sealed record NoBackup : HistoryRestoreResult;
    public sealed record Failed(string Message) : HistoryRestoreResult;
}

public static class Inv
{
    public static readonly CultureInfo C = CultureInfo.InvariantCulture;
}
