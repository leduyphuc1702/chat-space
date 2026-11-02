using System.Collections.Immutable;
using Avalonia.Controls;
using ChatSpace.Kit;

namespace ChatSpace.Core;

public sealed class AppModel : Observable
{
    public static readonly AppModel Shared = new();

    private RailItem _rail;
    /// What the main window shows.
    public RailItem Rail
    {
        get => Get(ref _rail);
        set
        {
            Set(ref _rail, value);
            Prefs.Set("currentRail", value.Key);
            if (value.IsPlatform(out var p)) Current = p;
            MarkVisibleRead();
        }
    }

    private Platform _current;
    /// Last platform whose own UI was shown.
    public Platform Current { get => Get(ref _current); private set => Set(ref _current, value); }

    private ImmutableDictionary<RailItem, ChatRef> _aggregateSelection = ImmutableDictionary<RailItem, ChatRef>.Empty;
    /// The chat open in the ChatSpace inbox and in each custom group.
    public ImmutableDictionary<RailItem, ChatRef> AggregateSelection
    {
        get => Get(ref _aggregateSelection);
        set { Set(ref _aggregateSelection, value); MarkVisibleRead(); }
    }

    public IReadOnlyDictionary<Platform, PlatformSession> Sessions { get; }

    private AppModel()
    {
        var env = Environment.GetEnvironmentVariable("CHATSPACE_PLATFORM");
        var saved = env != null ? (Platforms.Parse(env) != null ? "platform:" + env : env) : Prefs.GetString("currentRail");
        var r = (saved != null ? RailItem.FromKey(saved) : null) ?? RailItem.For(Platform.Zalo);
        _rail = r;
        _current = r.IsPlatform(out var p) ? p : Platform.Zalo;
        Sessions = Platforms.All.ToDictionary(x => x, x => new PlatformSession(x));
    }

    public PlatformSession Session(Platform p) => Sessions[p];

    /// The chat open in the main window right now (null when the inbox/group has nothing selected).
    public ChatRef? MainWindowChat
    {
        get
        {
            var rail = Rail;
            if (rail.IsPlatform(out var p))
                return Session(p).SelectedChatId is { } id ? new ChatRef(p, id) : null;
            return AggregateSelection.TryGetValue(rail, out var r) ? r : null;
        }
    }

    /// Headless screenshots stand in for the frontmost window of the macOS captures.
    public static bool MainWindowActive =>
        App.Current.MainWindow is { } w && w.IsVisible && (w.IsActive || App.Headless) && w.WindowState != WindowState.Minimized;

    /// The user is looking at this chat: in the frontmost main window, or in a focused chat bubble.
    public bool IsDisplaying(ChatRef r)
    {
        if (ChatHeads.Shared.IsFocused(r)) return true;
        return MainWindowChat == r && MainWindowActive;
    }

    public void MarkVisibleRead()
    {
        if (MainWindowChat is not { } r || !IsDisplaying(r)) return;
        Session(r.Platform).MarkRead(r.ChatId);
    }

    private Watch? _badgeWatch;

    public void StartAll()
    {
        foreach (var p in Platforms.All) Sessions[p].Start();
        _badgeWatch = Watch.Effect(() => DockBadge.Show(DockBadgeCount));
    }

    /// Sum of each platform's own icon-badge number.
    public int DockBadgeCount => Platforms.All.Sum(p => Session(p).UnreadTotal);

    public void StopAll()
    {
        foreach (var s in Sessions.Values) s.Stop();
        Prefs.Flush();
    }

    public void Open(Platform platform, string chatId)
    {
        Rail = RailItem.For(platform);
        Session(platform).Select(chatId);
    }

    /// Chats with something unread across every platform, newest first (ChatSpace inbox).
    public IReadOnlyList<ChatRef> InboxRefs
    {
        get
        {
            var ws = Workspace.Shared;
            return Platforms.All.SelectMany(p => Session(p).Chats
                    .Where(c => c.HasUnread && !ws.IsIgnored(new ChatRef(p, c.Id)))
                    .Select(c => (r: new ChatRef(p, c.Id), t: c.Timestamp)))
                .OrderByDescending(x => x.t).Select(x => x.r).ToList();
        }
    }

    /// Badge on the ChatSpace rail item: unread, unmuted, not ignored chats.
    public int InboxBadge
    {
        get
        {
            var ws = Workspace.Shared;
            return Platforms.All.Sum(p => Session(p).Chats.Count(c => c.HasUnread && !c.IsMuted && !ws.IsIgnored(new ChatRef(p, c.Id))));
        }
    }

    public IReadOnlyList<ChatRef> GroupRefs(CustomGroup g) =>
        Platforms.All.SelectMany(p => Session(p).Chats.Where(c => g.Matches(c, p)).Select(c => (r: new ChatRef(p, c.Id), c)))
            .OrderByDescending(x => x.c.HasUnread).ThenByDescending(x => x.c.Timestamp).Select(x => x.r).ToList();

    public int GroupBadge(CustomGroup g) =>
        Platforms.All.Sum(p => Session(p).Chats.Count(c => g.Matches(c, p) && c.HasUnread && !c.IsMuted));

    public Chat? Chat(ChatRef r) => Session(r.Platform).Chats.FirstOrDefault(c => c.Id == r.ChatId);
}
