using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// Telegram for macOS look: light theme, blue accent, green outgoing bubbles on the pattern wallpaper.
public static class TG
{
    public static readonly SColor Accent = SColor.Hex(0x3390EC);
    public static readonly SColor OutBubble = SColor.Hex(0xEFFDDE);
    public static readonly SColor InBubble = SColor.White;
    public static readonly SColor OutMeta = SColor.Hex(0x4FAE4E);
    public static readonly SColor InMeta = SColor.Hex(0x9AA3AC);
    public static readonly SColor Secondary = SColor.Hex(0x8E8E93);
    public static readonly SColor Separator = SColor.Hex(0xE7E7E7);
    public static readonly SColor ListBackground = SColor.White;
    public static readonly SColor UnreadMuted = SColor.Hex(0xB8B8BD);

    public static readonly (uint, uint)[] AvatarGradients =
    [
        (0xFF885E, 0xFF516A), (0xFFCD6A, 0xFFA85C), (0x82B1FF, 0x665FFF), (0xA0DE7E, 0x54CB68),
        (0x53EDD6, 0x28C9B7), (0x72D5FD, 0x2A9EF1), (0xE0A2F3, 0xD669ED),
    ];
    public static readonly uint[] NameColors = [0xFC5C51, 0xFA790F, 0x895DD5, 0x0FB297, 0x0FC9D6, 0x3CA5EC, 0xD54FAF];

    private static readonly CultureInfo En = new("en-US");

    /// Whether the text contains an emoji (drawn by the fallback emoji font).
    public static bool HasEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var r in text.EnumerateRunes())
        {
            var v = r.Value;
            if (Emoji.IsEmojiPresentation(v) || (Emoji.IsEmoji(v) && v > 0x238C) || v == 0xFE0F) return true;
        }
        return false;
    }

    public static SColor NameColor(string id) => SColor.Hex(NameColors[id.StableHash() % NameColors.Length]);

    public static string DayTitle(DateTime d) => MessageGrouping.DayTitle(d, "Today", "Yesterday", En, "MMMM d");

    public static string ListTime(DateTime d)
    {
        if (d.Date == DateTime.Today) return Fmt.Time(d);
        var days = (int)Math.Floor((DateTime.Now - d).TotalDays);
        return d.ToString(days < 7 ? "ddd" : "dd.MM.yy", En);
    }

    /// `RelativeDateTimeFormatter` (en_US, full units, numeric): "5 minutes ago".
    public static string RelativeAgo(DateTime d)
    {
        var s = (DateTime.Now - d).TotalSeconds;
        var future = s < 0;
        s = Math.Abs(s);
        (double span, string unit)[] units =
            [(365.25 * 86400, "year"), (30.44 * 86400, "month"), (7 * 86400, "week"), (86400, "day"), (3600, "hour"), (60, "minute"), (1, "second")];
        foreach (var (span, unit) in units)
        {
            if (s < span) continue;
            var n = (int)Math.Floor(s / span);
            var label = $"{n} {unit}{(n == 1 ? "" : "s")}";
            return future ? $"in {label}" : $"{label} ago";
        }
        return "now";
    }

    public static Control FallbackWallpaper() => new DoodleWallpaper(
        new LinearGradient([SColor.Hex(0xDBDDBB), SColor.Hex(0x88B884), SColor.Hex(0xD5D88D), SColor.Hex(0x6BA587)],
            LinearGradient.TopLeading, LinearGradient.BottomTrailing),
        SColor.Black.Opacity(0.09),
        ["paperplane", "cat", "star", "heart", "music.note", "camera", "gift", "moon", "cloud", "leaf", "balloon", "bicycle", "cup.and.saucer", "airplane"],
        spacing: 70, glyphSize: 22);

    /// `NativeIcon(name:catalog: .telegram, fallback:size:template:)`.
    public static Control Icon(string name, string fallback, double? size = null, bool template = true)
    {
        if (NativeImage.Exists("telegram", name)) return new NativeImage("telegram", name) { Size = size, Template = template };
        return new Symbol(fallback) { OwnFont = SFont.System((size ?? 16) * 0.85) };
    }

    public static Control Avatar(string id, string title, string? source, double size = 50, bool isSaved = false)
    {
        if (isSaved)
            return Circle().Fill(new LinearGradient([SColor.Hex(0x72D5FD), SColor.Hex(0x2A9EF1)], LinearGradient.Top, LinearGradient.Bottom))
                .Frame(width: size, height: size)
                .Overlay(Icon("Icon_SavedMessages", "bookmark.fill", size * 0.42).Foreground(SColor.White));
        return AvatarView.Make(source, size, () =>
        {
            var g = AvatarGradients[id.StableHash() % AvatarGradients.Length];
            return Circle().Fill(new LinearGradient([SColor.Hex(g.Item1), SColor.Hex(g.Item2)], LinearGradient.Top, LinearGradient.Bottom))
                .Overlay(Text(title.Initials()).Font(size * 0.38, Weight.Semibold).Foreground(SColor.White));
        });
    }

    public static Control Checks(DeliveryStatus? status, SColor color)
    {
        switch (status)
        {
            case DeliveryStatus.Pending:
                return Icon("Icon_ChatStateSending", "clock", 12).Foreground(color);
            case DeliveryStatus.Failed:
                return Icon("Icon_MessageSentFailed", "exclamationmark.circle.fill", 14, template: false).Foreground(SColor.Red);
            case DeliveryStatus.Read:
                return ZStack(
                        Icon("Icon_MessageCheckMark1", "checkmark", 12).Offset(-2.5),
                        Icon("Icon_MessageCheckmark2", "checkmark", 12).Offset(2.5))
                    .Foreground(color).Frame(width: 17);
            default:
                return Icon("Icon_MessageCheckMark1", "checkmark", 12).Foreground(color).Frame(width: 12);
        }
    }

    /// `.opacity(show ? 1 : 0).allowsHitTesting(show)`: keeps the layout slot, re-evaluated when observed state changes.
    public static Control ShowWhen(Control content, Func<bool> show)
    {
        var host = new KeepHostless(content);
        Watch? w = null;
        void Apply(bool on) { host.Opacity = on ? 1 : 0; host.IsHitTestVisible = on; }
        Apply(Tracker.Untracked(show));
        host.AttachedToVisualTree += (_, _) => w ??= Watch.Effect(() => Apply(show()));
        host.DetachedFromVisualTree += (_, _) => { w?.Dispose(); w = null; };
        return host;
    }
}

/// Telegram's default "classic" look: the green-yellow gradient with its own builtin doodle pattern
/// (read from the installed Telegram.app), or a drawn stand-in.
public static class TelegramWallpaper
{
    public static Control Make() =>
        NativeAssets.Shared.TelegramPattern() is { } pattern ? new TelegramPatternWallpaper(pattern) : TG.FallbackWallpaper();
}

/// The gradient with the pattern tiled over it as a template in black at 13 %.
public sealed class TelegramPatternWallpaper : Control, IFlexible
{
    private static readonly LinearGradient Gradient = new(
        [SColor.Hex(0xDBDDBB), SColor.Hex(0x88B884), SColor.Hex(0xD5D88D), SColor.Hex(0x6BA587)],
        LinearGradient.TopLeading, LinearGradient.BottomTrailing);

    private readonly Avalonia.Media.Imaging.Bitmap _pattern;

    public TelegramPatternWallpaper(Avalonia.Media.Imaging.Bitmap pattern)
    {
        _pattern = pattern;
        IsHitTestVisible = false;
    }

    public bool IsFlexible(Orientation axis) => true;

    protected override Size MeasureOverride(Size a) =>
        new(double.IsInfinity(a.Width) ? 0 : a.Width, double.IsInfinity(a.Height) ? 0 : a.Height);

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Gradient.Brush(Scheme.Light, bounds), bounds);
        // The pattern is pure black with alpha, so drawing it at 13 % opacity equals a template in black.opacity(0.13).
        var src = new Rect(0, 0, _pattern.PixelSize.Width, _pattern.PixelSize.Height);
        double tw = src.Width / 2, th = src.Height / 2;
        using (ctx.PushClip(bounds))
        using (ctx.PushOpacity(0.13))
        {
            for (var y = 0.0; y < bounds.Height; y += th)
                for (var x = 0.0; x < bounds.Width; x += tw)
                    ctx.DrawImage(_pattern, src, new Rect(x, y, tw, th));
        }
    }
}

public sealed class TelegramRootView : Reactive
{
    public TelegramRootView(PlatformSession s) : base(() => Build(s)) { }

    private static Control Build(PlatformSession session)
    {
        Control body;
        if (session.AuthState == AuthState.LoggedIn)
        {
            var sidebar = View(() => session.Folders.IsEmpty
                ? null
                : HStack(0, TelegramFolderSidebar.Make(session), Rectangle().Fill(TG.Separator).Frame(width: 1)));
            var conversation = View(() => session.SelectedChatId is { } id && Tracker.Untracked(() => session.SelectedChat) != null
                ? Keep(("conv", id), () => new TelegramConversation(session, id))
                : ZStack(
                    TelegramWallpaper.Make(),
                    Text("Select a chat to start messaging")
                        .Font(13, Weight.Medium).Foreground(SColor.White)
                        .PaddingH(10).PaddingV(4)
                        .Background(Capsule().Fill(SColor.Black.Opacity(0.25)))));
            body = HStack(0,
                sidebar,
                TelegramChatList.Make(session).Frame(width: 330),
                Rectangle().Fill(TG.Separator).Frame(width: 1),
                conversation);
        }
        else if (session.AuthState == AuthState.Connecting || (session.BridgeError != null && session.AuthState != AuthState.Error))
            body = BridgeStatusView.Make(session, TG.Accent);
        else
            body = TelegramLogin.Make(session);
        return body.Background(SColor.White).Scheme(Scheme.Light);
    }
}

// MARK: Chat list

public static class TelegramChatList
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        var query = Remember("");
        var search = Keep("search", () => new Field("Search", query, font: SFont.System(13)));

        var top = HStack(10,
                HStack(6, TG.Icon("Icon_SearchField", "magnifyingglass", 12).Foreground(TG.Secondary), search)
                    .PaddingH(10).Frame(height: 30)
                    .Background(RoundedRectangle(15).Fill(SColor.Hex(0xF1F1F4))),
                TG.Icon("Icon_NewMessage", "square.and.pencil", 26).Foreground(TG.Accent)
                    .Tool("New Message", () => ChatTools.NativeOnly(Platform.Telegram, "Tạo tin nhắn mới")))
            .Padding(30, 12, 8, 12);

        Control? status = session.Connection != "online" && session.Connection != "connecting"
            ? Text(session.Connection == "offline" ? "Waiting for network…" : "Connecting…")
                .Font(12).Foreground(TG.Secondary).PaddingBottom(6)
            : null;

        var list = ScrollView(ForEach(() => Filtered(session, query.Value).Select(c => (c, c.Id == session.SelectedChatId)).ToList(),
            x => x.c.Id, x =>
                TelegramChatRow.Make(session, x.c, x.Item2)
                    .OnTap(() => session.Select(x.c.Id))
                    .ChatContextMenu(session.Platform, x.c)));

        return VStack(0, top, status, list.FillHeight(Alignment.Top)).Background(TG.ListBackground);
    });

    /// Like Telegram, search covers every chat regardless of the selected folder.
    private static IReadOnlyList<Chat> Filtered(PlatformSession session, string query)
    {
        if (query.Length == 0) return session.VisibleChats;
        return session.Chats.Where(c => c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }
}

/// Telegram for macOS "folders on the left": All Chats + each dialog filter with its icon and badge.
public static class TelegramFolderSidebar
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        var items = new List<Control?> { Item(session, null) };
        foreach (var f in session.Folders) items.Add(Item(session, f));
        return VStack(0,
                Clear.Frame(height: 28),
                // The scroll view takes everything between the top inset and the tool: the Kit stack would split
                // the space with a Spacer, cutting the list off where SwiftUI sizes it to its content.
                ScrollView(VStack(2, items.ToArray()).PaddingV(4), vertical: true, horizontal: false, indicators: false)
                    .FillHeight(Alignment.Top),
                TG.Icon("Icon_LeftSidebarEditFolders", "slider.horizontal.3", 24)
                    .Foreground(TG.Secondary)
                    .Tool("Edit Folders", () => ChatTools.NativeOnly(Platform.Telegram, "Sửa thư mục chat"), 40)
                    .PaddingBottom(12))
            .Frame(width: 78)
            .Background(SColor.Hex(0xF4F4F5));
    });

    private static Control Item(PlatformSession session, ChatFolder? f)
    {
        var selected = session.SelectedFolderId == f?.Id;
        var badge = session.FolderBadge(f);
        var tint = selected ? TG.Accent : TG.Secondary;
        Control? badgeView = badge.count > 0
            ? Text(badge.count > 99 ? "99+" : $"{badge.count}")
                .Font(10.5, Weight.Semibold).Foreground(SColor.White)
                .PaddingH(4.5).Frame(minWidth: 18, minHeight: 18)
                .Background(Capsule().Fill(badge.muted ? TG.UnreadMuted : TG.Accent))
                .Overlay(Capsule().Stroke(SColor.Hex(0xF4F4F5), 1.5))
                .Offset(11, -5)
            : null;
        var label = VStack(3,
                TG.Icon(f?.IconName ?? "Icon_Sidebar_AllChats", f == null ? "bubble.left.and.bubble.right.fill" : "folder.fill", 30)
                    .Foreground(tint)
                    .Overlay(Alignment.TopTrailing, badgeView),
                Text(f?.Title ?? "All Chats")
                    .Font(11, selected ? Weight.Semibold : Weight.Regular)
                    .Foreground(tint)
                    .LineLimit(2).MultilineAlignment(TextAlignment.Center)
                    .Frame(minHeight: 14) // SwiftUI rounds an 11 pt line up to 14 pt, the Kit's text to 13
                    .Frame(maxWidth: 70))
            .PaddingV(7)
            .Frame(width: 74)
            .ContentShape();
        return Button(() => session.SelectedFolderId = f?.Id, label)
            .ContextMenu(() =>
            [
                MenuEntry.Item("Mark All as Read", () =>
                {
                    foreach (var c in session.Chats)
                        if (c.HasUnread && (f?.Contains(c) ?? true)) session.MarkRead(c.Id);
                }),
            ]);
    }
}

public static class TelegramChatRow
{
    public static Control Make(PlatformSession session, Chat chat, bool selected)
    {
        var fg = selected ? SColor.White : SColor.Black;
        var sub = selected ? SColor.White.Opacity(0.85) : TG.Secondary;

        Control? online = chat.Online == true && chat.Kind == ChatKind.Direct
            ? Circle().Fill(SColor.Hex(0x4DCD5E)).Frame(width: 12, height: 12)
                .Overlay(Circle().Stroke(selected ? TG.Accent : SColor.White, 2))
            : null;

        Control? badge = null;
        if (chat.Unread > 0)
            badge = Text($"{chat.Unread}").Font(12, Weight.Semibold)
                .Foreground(selected ? TG.Accent : SColor.White)
                .PaddingH(6).Frame(minWidth: 20, minHeight: 20)
                .Background(Capsule().Fill(selected ? SColor.White : (chat.IsMuted ? TG.UnreadMuted : TG.Accent)));
        else if (chat.MarkedUnread == true)
            badge = Circle().Fill(selected ? SColor.White : (chat.IsMuted ? TG.UnreadMuted : TG.Accent)).Frame(width: 20, height: 20);
        else if (chat.IsPinned)
            badge = TG.Icon("Icon_ChatListPinned", "pin.fill", 20).Foreground(sub);

        return HStack(10,
                TG.Avatar(chat.Id, chat.Title, chat.Avatar, 50, chat.Kind == ChatKind.Self)
                    .Overlay(Alignment.BottomTrailing, online),
                VStack(HAlign.Leading, 3,
                    HStack(4,
                        chat.Kind == ChatKind.Group ? Image("person.2.fill").Font(11).Foreground(fg) : null,
                        chat.Kind == ChatKind.Channel ? Image("megaphone.fill").Font(11).Foreground(fg) : null,
                        Text(chat.Kind == ChatKind.Self ? "Saved Messages" : chat.Title)
                            .Font(14, Weight.Semibold).Foreground(fg).LineLimit(1),
                        chat.IsMuted ? TG.Icon("Icon_DialogMute", "speaker.slash.fill", 10).Foreground(sub) : null,
                        Spacer(4),
                        chat.LastMessage is { FromMe: true } lm ? TG.Checks(lm.Status, selected ? SColor.White : TG.OutMeta) : null,
                        Text(TG.ListTime(chat.Date)).Font(12).Foreground(sub)),
                    HStack(VAlign.Top, 4,
                        Preview(session, chat, selected, fg, sub).Font(13).LineLimit(2).FillWidth(Alignment.Leading),
                        badge)))
            .PaddingH(10).Frame(height: 66)
            .Background(selected ? TG.Accent : SColor.Clear)
            .ContentShape()
            .OnAppear(() => session.RequestAvatar(chat));
    }

    private static Control Preview(PlatformSession session, Chat chat, bool selected, SColor fg, SColor sub)
    {
        static Control Runs(params Span[] spans) => new LinkText(spans) { Selectable = false };

        if (session.Typing.GetValueOrDefault(chat.Id) is { Count: > 0 } t)
        {
            var who = chat.IsGroup ? (t[0].Name ?? "") + " is " : "";
            return Runs(new Span(who + "typing…", selected ? SColor.White : TG.Accent));
        }
        if (chat.LastMessage is not { } m) return Runs();
        var spans = new List<Span>();
        if (chat.IsGroup || chat.Kind == ChatKind.Channel)
        {
            if (m.FromMe) spans.Add(new Span("You: ", fg));
            else if (m.SenderName is { } n && chat.Kind == ChatKind.Group) spans.Add(new Span(n.Split(' ')[0] + ": ", fg));
        }
        var isMedia = m.Kind is MessageKind.Image or MessageKind.Video or MessageKind.Voice or MessageKind.Audio or MessageKind.File
            or MessageKind.Sticker or MessageKind.Gif && string.IsNullOrEmpty(m.Text);
        spans.Add(new Span(m.Kind == MessageKind.System ? m.Text ?? "" : m.Preview(), isMedia ? (selected ? SColor.White : TG.Accent) : sub));
        return Runs(spans.ToArray());
    }
}

// MARK: Conversation

public sealed class TelegramConversation : Decorator, IFlexible
{
    public TelegramConversation(PlatformSession session, string chatId)
    {
        Chat? Current() => session.Chats.FirstOrDefault(c => c.Id == chatId);
        Child = new CompactAware(compact =>
        {
            var initial = Tracker.Untracked(Current) ?? new Chat { Id = chatId };
            var scroll = new ChatScroll(session, chatId, row =>
                {
                    var chat = Tracker.Untracked(Current) ?? initial;
                    return VStack(0,
                        row.ShowDayHeader ? TelegramServicePill.Make(TG.DayTitle(row.Message.Date)).PaddingV(8) : null,
                        TelegramMessageRow.Make(session, chat, row, compact).PaddingTop(row.IsFirstInGroup ? 6 : 2));
                }, 0, new Thickness(0, 10, 0, 10), header: () => new TelegramBottomAnchor());
            return VStack(0,
                View(() => Header(session, Current() ?? initial, compact)),
                Rectangle().Fill(TG.Separator).Frame(height: 1),
                ZStack(TelegramWallpaper.Make(), scroll,
                    View(() => session.BotToast.GetValueOrDefault(chatId) is { } toast ? VStack(0, TelegramToast.Make(toast), Spacer()) : null)).FillHeight(),
                View(() => TelegramComposer.Make(session, Current() ?? initial, compact)
                    .WithMentionSuggestions(session, Current() ?? initial, TG.Accent)));
        });
    }

    public bool IsFlexible(Orientation axis) => true;

    private static Control Header(PlatformSession session, Chat chat, bool compact)
    {
        var showSearch = Remember(false);
        var showInfo = Remember(false);
        // The three tools sit in a `Group` with .font(16), .foregroundStyle and .padding(.leading, 12): each gets them.
        static Control Styled(Control c) => c.Font(16).Foreground(TG.Secondary).PaddingLeading(12);
        return HStack(10,
                TG.Avatar(chat.Id, chat.Title, chat.Avatar, 36, chat.Kind == ChatKind.Self),
                VStack(HAlign.Leading, 1,
                    Text(chat.Kind == ChatKind.Self ? "Saved Messages" : chat.Title).Font(14, Weight.Semibold).LineLimit(1),
                    Subtitle(session, chat).Font(12.5).LineLimit(1)),
                Spacer(),
                Styled(TG.Icon("Icon_Entertainment_Search", "magnifyingglass", 26)
                    .ToolPopover("Search", showSearch, done => InChatSearch.Make(session, chat.Id, done, "Search"))),
                chat.Kind == ChatKind.Direct
                    ? Styled(TG.Icon("Icon_Peer_AudioCall", "phone", 26).Tool("Call", () => ChatTools.Call(Platform.Telegram, video: false)))
                    : null,
                Styled(TG.Icon("Icon_ChatActionInfo", "ellipsis", 26)
                    .ToolPopover("Info", showInfo, _ => ChatInfoPanel.Make(session, chat))))
            .PaddingH(compact ? 12 : 16).Frame(height: compact ? 50 : 56).PaddingTop(compact ? 0 : 8)
            .Background(SColor.White);
    }

    private static Control Subtitle(PlatformSession session, Chat chat)
    {
        if (session.Typing.GetValueOrDefault(chat.Id) is { Count: > 0 } t)
            return Text(chat.IsGroup ? $"{t[0].Name ?? ""} is typing…" : "typing…").Foreground(TG.Accent);
        switch (chat.Kind)
        {
            case ChatKind.Group: return Text(chat.MemberCount is { } g ? $"{g} members" : "group").Foreground(TG.Secondary);
            case ChatKind.Channel: return Text(chat.MemberCount is { } c ? $"{c} subscribers" : "channel").Foreground(TG.Secondary);
            case ChatKind.Bot: return Text("bot").Foreground(TG.Secondary);
            case ChatKind.Self: return Text("");
            default:
                if (chat.Online == true) return Text("online").Foreground(TG.Accent);
                if (chat.LastSeen is { } ls) return Text($"last seen {TG.RelativeAgo(Time.Local(ls))}").Foreground(TG.Secondary);
                return Text("last seen recently").Foreground(TG.Secondary);
        }
    }
}

/// SwiftUI's `.defaultScrollAnchor(.bottom)` keeps a short conversation at the bottom of the viewport, while the
/// shared ChatScroll leaves it at the top. Placed as ChatScroll's header, this spacer takes whatever height the
/// rows leave unfilled, which pushes them down.
public sealed class TelegramBottomAnchor : Control
{
    private ScrollViewer? _sv;
    private double _height;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sv = this.FindAncestorOfType<ScrollViewer>();
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;
        _sv = null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_sv == null) return;
        var rest = _sv.Extent.Height - _height;
        var h = Math.Max(0, _sv.Viewport.Height - rest);
        if (Math.Abs(h - _height) < 0.5) return;
        _height = h;
        // The stack above measured this control at its old height, so a re-measure of this control alone would be
        // clamped to that; the whole chain up to the scroll viewer has to measure again.
        for (Visual? v = this; v != null && v != _sv; v = v.GetVisualParent())
            (v as Layoutable)?.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, _height);
}

public static class TelegramServicePill
{
    public static Control Make(string text) =>
        Text(text).Font(13, Weight.Medium).Foreground(SColor.White)
            .MultilineAlignment(TextAlignment.Center)
            .PaddingH(9).PaddingV(3)
            .Background(Capsule().Fill(SColor.Hex(0x4A6A3A).Opacity(0.45)))
            .FillWidth();
}

public static class TelegramMessageRow
{
    public static Control Make(PlatformSession session, Chat chat, MessageRow row, bool compact)
    {
        var m = row.Message;
        if (m.Kind == MessageKind.System) return TelegramServicePill.Make(m.Text ?? "").PaddingV(4);

        var hovered = Remember(false);
        var picking = Remember(false);
        bool Active() => hovered.Value || picking.Value;

        // Telegram Desktop's reaction button beside a hovered bubble; the slot is kept so bubbles don't shift.
        Control ReactButton() =>
            TG.ShowWhen(
                    Button(() => picking.Value = true,
                            Image("heart").Font(13, Weight.Semibold).Foreground(TG.Accent)
                                .Frame(width: 28, height: 28)
                                .Background(Circle().Fill(SColor.White).Shadow(SColor.Black.Opacity(0.18), 1.5, 0, 0.5))
                                .ContentShape())
                        .Help("React"),
                    () => Active() && !m.IsDeleted && session.Can("reactions"))
                .WithReactionPicker(picking, session, m, ReactionSets.Telegram)
                .PaddingBottom(2);

        Control TranslateButton() =>
            TG.ShowWhen(
                    TranslateToggle.Make(m, on =>
                        Image("translate").Font(12, Weight.Semibold).Foreground(on ? TG.Accent : TG.Secondary)
                            .Frame(width: 28, height: 28)
                            .Background(Circle().Fill(on ? SColor.Hex(0xE8F2FC) : SColor.White).Shadow(SColor.Black.Opacity(0.18), 1.5, 0, 0.5))
                            .ContentShape()),
                    () => Active() && Translator.CanTranslate(m))
                .PaddingBottom(2);

        Control bubble = TelegramBubble.Make(session, chat, row)
            .MessageContextMenu(session, m, MenuLabels.English, ReactionSets.Telegram);
        if (m.Buttons is { Count: > 0 } keys && !m.IsDeleted)
            bubble = VStack(m.FromMe ? HAlign.Trailing : HAlign.Leading, 4, bubble, TelegramInlineKeyboard.Make(session, m, keys));
        return HStack(VAlign.Bottom, 6,
                m.FromMe ? Spacer(ChatMetrics.Gap(80, compact)) : null,
                !m.FromMe && chat.Kind == ChatKind.Group
                    ? row.IsLastInGroup
                        ? TG.Avatar(m.SenderId, m.SenderName ?? "?", m.SenderAvatar, 34)
                        : Clear.Frame(width: 34, height: 1)
                    : null,
                m.FromMe ? TranslateButton() : null,
                m.FromMe ? ReactButton() : null,
                bubble,
                !m.FromMe ? ReactButton() : null,
                !m.FromMe ? TranslateButton() : null,
                !m.FromMe ? Spacer(ChatMetrics.Gap(80, compact)) : null)
            .PaddingH(compact ? 10 : 14)
            .ContentShape()
            .TrackHover(hovered);
    }
}

public static class TelegramBubble
{
    public static Control Make(PlatformSession session, Chat chat, MessageRow row)
    {
        var m = row.Message;
        var metaColor = m.FromMe ? TG.OutMeta : TG.InMeta;
        var mediaOnly = m.Kind is MessageKind.Image or MessageKind.Video or MessageKind.Gif && string.IsNullOrEmpty(m.Text);
        var isSticker = m.Kind == MessageKind.Sticker || (m.Kind == MessageKind.Text && Jumbo.IsJumboEmoji(m.Text));

        Control Meta() => HStack(3,
                m.IsEdited ? Text("edited") : null,
                Text(Fmt.Time(m.Date)),
                m.FromMe ? TG.Checks(m.Status, metaColor) : null)
            .Font(11.5).Foreground(metaColor);

        if (isSticker)
        {
            return VStack(m.FromMe ? HAlign.Trailing : HAlign.Leading, 4,
                m.Kind == MessageKind.Sticker ? MediaContent.Make(session, m) : Text(m.Text ?? "").Font(56),
                Meta().PaddingH(6).PaddingV(2)
                    .Background(Capsule().Fill(SColor.Black.Opacity(0.25)))
                    .Scheme(Scheme.Dark));
        }

        var col = new BubbleColumn(4,
            !m.FromMe && chat.Kind == ChatKind.Group && row.IsFirstInGroup && !mediaOnly
                ? Text(m.SenderName ?? "").Font(13, Weight.Semibold).Foreground(TG.NameColor(m.SenderId))
                : null,
            m.Forwarded == true
                ? Text("Forwarded message").Font(13).Foreground(m.FromMe ? TG.OutMeta : TG.Accent)
                : null,
            m.ReplyTo is { } r ? BubbleColumn.Fill(Reply(session, m, r)) : null);
        col.Children.Add(Content(session, m, mediaOnly, metaColor, Meta));
        col.Children.Add(View(() => TranslationQuote.Make(m, m.FromMe ? TG.OutMeta : TG.Accent, SColor.Black.Opacity(0.7))
                .PaddingH(mediaOnly ? 8 : 0).PaddingBottom(mediaOnly ? 6 : 0))
            .VisibleWhen(() => Translator.Shared.IsShown(m)));
        if (m.Reactions is { Count: > 0 } rs) col.Children.Add(Reactions(session, m, rs));

        var fill = m.FromMe ? TG.OutBubble : TG.InBubble;
        const double big = 16, small = 6;
        var tail = row.IsLastInGroup;
        // Corner radii in Swift's order: topLeft, topRight, bottomLeft, bottomRight.
        var (tl, tr, bl, br) = m.FromMe
            ? (big, row.IsFirstInGroup ? big : small, big, tail ? 2.0 : small)
            : (row.IsFirstInGroup ? big : small, big, tail ? 2.0 : small, big);
        Geometry BubblePath(Rect rect) => Shapes.RoundedRect(rect, tl, tr, br, bl);

        Control bubble = new PaddingBox { Child = col, Insets = new Thickness(mediaOnly ? 0 : 10, mediaOnly ? 0 : 6) }
            .Background(PathShape(BubblePath).Fill(fill));
        bubble = new ClipBox { Child = bubble, Kind = ShapeKind.Custom, Path = BubblePath };
        if (row.IsLastInGroup && !mediaOnly)
            bubble = bubble.Background(
                Shapes.Tail(m.FromMe ? Shapes.TailCorner.BottomRight : Shapes.TailCorner.BottomLeft)
                    .Fill(fill).Frame(width: 10, height: 14).Offset(m.FromMe ? 6 : -6, 0),
                m.FromMe ? Alignment.BottomTrailing : Alignment.BottomLeading);
        return new CapWidth(480, bubble.Shadow(SColor.Black.Opacity(0.12), 0.5, 0, 0.5));
    }

    private static Control Content(PlatformSession session, Message m, bool mediaOnly, SColor metaColor, Func<Control> meta)
    {
        if (m.IsDeleted)
            return Text("Message deleted").Font(SFont.System(14) with { Italic = true }).Foreground(TG.Secondary);
        switch (m.Kind)
        {
            case MessageKind.Image or MessageKind.Video or MessageKind.Gif:
                return VStack(HAlign.Leading, 4,
                    MediaContent.Make(session, m, maxWidth: 360, cornerRadius: mediaOnly ? 0 : 10)
                        .Overlay(Alignment.BottomTrailing, mediaOnly
                            ? meta().Scheme(Scheme.Dark).PaddingH(6).PaddingV(2)
                                .Background(Capsule().Fill(SColor.Black.Opacity(0.4))).Padding(6)
                            : null),
                    !mediaOnly ? TextWithMeta(m, meta) : null);
            case MessageKind.File or MessageKind.Audio or MessageKind.Voice:
                return VStack(HAlign.Leading, 4,
                    FileContent.Make(session, m, m.FromMe ? TG.OutMeta : TG.Accent, subtitleColor: metaColor),
                    m.Text is { Length: > 0 } t && m.Kind == MessageKind.File && m.Media?.FileName != null && t != m.Media?.FileName
                        ? TextWithMeta(m, meta)
                        : HStack(Spacer(), meta()));
            default:
                return TextWithMeta(m, meta);
        }
    }

    /// The Kit tracks every run of a LinkText with the 14.5 pt block font, while SwiftUI tracks the 11.5 pt reserve with
    /// its own (looser) value; the difference is about 0.22 pt a character, which narrows the reserve and with it the
    /// bubble. Drawing the invisible run slightly larger (a character is about 0.47 em wide) restores the width.
    private static readonly double ReserveSize = 11.5 + (Tracking.At(11.5) - Tracking.At(14.5)) / 0.47;

    /// Text with the time/checks tucked into the last line, as Telegram does.
    private static Control TextWithMeta(Message m, Func<Control> meta)
    {
        var link = m.FromMe ? SColor.Hex(0x3A8E26) : TG.Accent;
        var reserve = "  " + (m.IsEdited ? "edited " : "") + Fmt.Time(m.Date) + (m.FromMe ? "  ✓✓" : "");
        var spans = LinkSpans.Spans(m.Text ?? "", link).ToList();
        spans.Add(new Span(reserve, SColor.Clear, Size: ReserveSize));
        var lt = new LinkText(spans) { OwnFont = SFont.System(14.5) };
        // The emoji fallback font has taller line metrics than SF Pro (20 pt against 18 pt at this size), which makes a
        // line with an emoji taller than SwiftUI's; pin the line height to the text font's own.
        if (TG.HasEmoji(m.Text)) lt.LineHeight = 18;
        var text = lt.Foreground(SColor.Black).FixedSize(horizontal: false, vertical: true);
        return ZStack(Alignment.BottomTrailing, text, meta().Offset(0, 2)).Frame(alignment: Alignment.Leading);
    }

    private static Control Reply(PlatformSession session, Message m, ReplyRef r)
    {
        var color = m.FromMe ? TG.OutMeta : TG.NameColor(r.SenderName ?? r.Id);
        var kind = r.Kind is { } k ? k.ToString() : "";
        return HStack(6,
                RoundedRectangle(1.5).Fill(color).Frame(width: 3),
                VStack(HAlign.Leading, 1,
                    Text(r.SenderName ?? "Reply").Font(13, Weight.Semibold).Foreground(color),
                    Text(r.Text ?? kind).Font(13).Foreground(SColor.Black).LineLimit(1)))
            .PaddingV(3).PaddingTrailing(8)
            .Frame(minWidth: 100, alignment: Alignment.Leading)
            .Background(RoundedRectangle(4).Fill(color.Opacity(0.12)))
            .OnTap(() =>
            {
                session.ScrollTarget = session.ScrollTarget.Remove(m.ChatId);
                Dispatcher.UIThread.Post(() => session.ScrollTarget = session.ScrollTarget.SetItem(m.ChatId, r.Id));
            });
    }

    private static Control Reactions(PlatformSession session, Message m, IReadOnlyList<Reaction> rs)
    {
        var tint = m.FromMe ? TG.OutMeta : TG.Accent;
        return HStack(4, rs.Select(r => (Control?)HStack(3,
                    Text(r.Emoji).Font(14).Frame(height: 17),
                    Text($"{r.Count}").Font(13, Weight.Semibold).Foreground(r.FromMe ? SColor.White : tint))
                .Offset(0, 1) // the Kit's text sits 1 pt higher in its line box than SwiftUI's at these sizes
                .PaddingH(7).PaddingV(3)
                .Background(Capsule().Fill(r.FromMe ? tint : tint.Opacity(0.14)))
                .OnTap(() => session.React(m, r.Emoji))).ToArray());
    }
}

public static class TelegramComposer
{
    public static Control Make(PlatformSession session, Chat chat, bool compact = false)
    {
        var draft = Composer.Draft(session, chat.Id);
        bool Collapsed() => compact && session.Draft(chat.Id).Length > 0;

        var bar = View(() =>
        {
            if (session.Editing.GetValueOrDefault(chat.Id) is { } e)
            {
                var err = session.EditError.GetValueOrDefault(chat.Id);
                return HStack(10,
                        TG.Icon("Icon_ChatAction_EditMessage", "pencil", 26).Foreground(TG.Accent),
                        RoundedRectangle(1).Fill(TG.Accent).Frame(width: 2, height: 32),
                        VStack(HAlign.Leading, 1,
                            Text("Edit Message").Font(13, Weight.Semibold).Foreground(TG.Accent),
                            err != null
                                ? Text(err).Font(13).LineLimit(2).Foreground(SColor.Red)
                                : Text(e.Preview()).Font(13).LineLimit(1).Foreground(SColor.Black)),
                        Spacer(),
                        Button(() => session.CancelEdit(chat.Id),
                            TG.Icon("Icon_ChatAction_Close", "xmark", 22).Foreground(TG.Secondary)).Help("Cancel editing (Esc)"))
                    .PaddingH(16).PaddingTop(8);
            }
            if (session.ReplyingTo.GetValueOrDefault(chat.Id) is not { } r) return null;
            return HStack(10,
                    TG.Icon("Icon_ChatAction_ReplyMessage", "arrowshape.turn.up.left", 26).Foreground(TG.Accent),
                    RoundedRectangle(1).Fill(TG.Accent).Frame(width: 2, height: 32),
                    VStack(HAlign.Leading, 1,
                        Text($"Reply to {(r.FromMe ? "yourself" : (r.SenderName ?? chat.Title))}").Font(13, Weight.Semibold).Foreground(TG.Accent),
                        Text(r.Preview()).Font(13).LineLimit(1).Foreground(SColor.Black)),
                    Spacer(),
                    Button(() => session.ReplyingTo = session.ReplyingTo.Remove(chat.Id),
                        TG.Icon("Icon_ChatAction_Close", "xmark", 22).Foreground(TG.Secondary)))
                .PaddingH(16).PaddingTop(8);
        });

        var placeholder = Tracker.Untracked(() => session.BotKeyboard(chat.Id)?.Keyboard.Placeholder) is { Length: > 0 } hint ? hint
            : chat.Kind == ChatKind.Channel ? "Broadcast a message…" : "Write a message…";
        var editor = Composer.Editor(chat.Id, draft, placeholder,
            onSubmit: () => session.Send(session.Draft(chat.Id), chat.Id),
            font: SFont.System(14), placeholderColor: TG.Secondary,
            maxHeight: compact ? 220 : 140,
            onTypingChange: on => session.SetTyping(chat.Id, on),
            mentionSession: session, mentionChatId: chat.IsGroup ? chat.Id : null,
            onFiles: files => ChatTools.SendPasted(files, session, chat.Id),
            onCommand: key => session.ComposerCommand(key, chat.Id));

        var trailing = View(() =>
        {
            if (session.Editing.ContainsKey(chat.Id))
                return Button(() => session.Send(session.Draft(chat.Id), chat.Id),
                    TG.Icon("Icon_SaveEditedMessage", "checkmark.circle.fill", 30).Foreground(TG.Accent)).PaddingBottom(1).Help("Save");
            return session.Draft(chat.Id).Length == 0 && !session.Attachments.ContainsKey(chat.Id)
                ? VoiceTools.VoiceTool(() => TG.Icon("Icon_RecordVoice", "mic", 30).Foreground(TG.Secondary), session, chat.Id).PaddingBottom(1)
                : Button(() => session.Send(session.Draft(chat.Id), chat.Id),
                    TG.Icon("Icon_SendMessage", "paperplane.fill", 26, template: false).Foreground(TG.Accent)).PaddingBottom(3);
        });

        var input = HStack(VAlign.Bottom, compact ? 10 : 14,
                View(() => Collapsed() ? null
                    : Button(() => ChatTools.Attach(session, chat.Id), TG.Icon("Icon_ChatAttach", "paperclip", 30).Foreground(TG.Secondary)).PaddingBottom(1)),
                editor.PaddingV(4).ComposerTapArea(),
                OutgoingLanguageButton.Make(session, chat.Id, TG.Accent, TG.Secondary, 18, 30, hideWhenIdle: Collapsed).PaddingBottom(1),
                View(() => Collapsed() ? null
                    : TG.Icon("Icon_Entertainment_Emoji", "face.smiling", 30).Foreground(TG.Secondary)
                        .Tool("Emoji", Composer.Emoji).PaddingBottom(1)),
                // Telegram's keyboard button: folds the bot's custom keyboard away and brings it back.
                View(() =>
                {
                    if (Collapsed() || session.BotKeyboard(chat.Id) == null) return null;
                    var shown = !session.CollapsedKeyboards.Contains(chat.Id);
                    return Image(shown ? "keyboard.chevron.compact.down" : "keyboard").Font(20).Foreground(shown ? TG.Accent : TG.Secondary)
                        .Frame(width: 30, height: 30)
                        .Tool(shown ? "Hide bot keyboard" : "Show bot keyboard", () => session.ToggleBotKeyboard(chat.Id))
                        .PaddingBottom(1);
                }),
                trailing)
            .PaddingH(16).PaddingV(10);

        return VStack(0,
                Rectangle().Fill(TG.Separator).Frame(height: 1),
                bar,
                TranslationNotice.Make(session, chat.Id),
                MathSendPreview.Make(session, chat.Id, TG.Accent),
                ComposerAttachments.Make(session, chat.Id, TG.Accent),
                input,
                View(() => session.BotKeyboard(chat.Id) is { } kb && !session.CollapsedKeyboards.Contains(chat.Id)
                    ? TelegramBotKeyboardPanel.Make(session, kb.Message, kb.Keyboard, compact)
                    : null))
            .Background(SColor.White);
    }
}

// MARK: Bot keyboards

/// One button of a bot keyboard. Inline (under a bubble): white text on a translucent dark pill over the wallpaper,
/// as Telegram draws them. Keyboard (under the composer): dark text on light grey.
public static class TelegramKeyButton
{
    public static Control Make(BotButton button, Action action, bool inline = true, Func<bool>? busy = null) => View(() =>
    {
        var fg = inline ? SColor.White : SColor.Hex(0x3E4B5B);
        var badge = Badge(button.Kind);
        Control label = Text(button.Text).Font(13, Weight.Medium).LineLimit(1)
            .PaddingH(10).PaddingTrailing(badge == null ? 0 : 6)
            .Frame(height: inline ? 34 : 38, alignment: Alignment.Center).FillWidth(Alignment.Center)
            .Foreground(fg)
            .Background(RoundedRectangle(6).Fill(inline ? SColor.Black.Opacity(0.14) : SColor.Hex(0xF1F3F5)));
        if (badge != null)
            label = label.Overlay(Alignment.TopTrailing, Image(badge).Font(8, Weight.Bold).Foreground(fg.Opacity(inline ? 0.85 : 0.7)).Padding(4));
        var pressing = busy?.Invoke() ?? false;
        return Button(action, label)
            .Opacity(!button.IsSupported ? 0.55 : pressing ? 0.6 : 1)
            .Help(button.Url ?? button.Insert ?? button.Text);
    });

    /// Telegram's corner glyph for buttons that leave the chat.
    private static string? Badge(BotButtonKind kind) => kind switch
    {
        BotButtonKind.Url or BotButtonKind.Login => "arrow.up.right",
        BotButtonKind.SwitchInline => "arrow.turn.up.right",
        BotButtonKind.WebApp => "rectangle.on.rectangle",
        BotButtonKind.Copy => "doc.on.doc",
        _ => null,
    };
}

/// Inline keyboard under a bot's bubble.
public static class TelegramInlineKeyboard
{
    public static Control Make(PlatformSession session, Message m, IReadOnlyList<IReadOnlyList<BotButton>> rows)
    {
        var lines = new List<Control?>();
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = new List<Control?>();
            for (var c = 0; c < rows[r].Count; c++)
            {
                var (ri, ci, b) = (r, c, rows[r][c]);
                var key = $"{m.ChatId}|{m.Id}|{ri}|{ci}";
                cells.Add(TelegramKeyButton.Make(b, () => session.PressButton(b, ri, ci, m), inline: true,
                    busy: () => session.PressingButtons.Contains(key)));
            }
            lines.Add(HStack(4, cells.ToArray()));
        }
        return VStack(4, lines.ToArray()).Frame(minWidth: 200, maxWidth: 480);
    }
}

/// A bot's custom keyboard under the composer (Telegram's ReplyKeyboardMarkup); a press sends the label.
public static class TelegramBotKeyboardPanel
{
    public static Control Make(PlatformSession session, Message m, ReplyKeyboard keyboard, bool compact)
    {
        var lines = new List<Control?>();
        for (var r = 0; r < keyboard.Rows.Count; r++)
        {
            var cells = new List<Control?>();
            for (var c = 0; c < keyboard.Rows[r].Count; c++)
            {
                var (ri, ci, b) = (r, c, keyboard.Rows[r][c]);
                cells.Add(TelegramKeyButton.Make(b, () => session.PressButton(b, ri, ci, m, keyboard), inline: false));
            }
            lines.Add(HStack(6, cells.ToArray()));
        }
        Control rows = VStack(6, lines.ToArray()).PaddingH(compact ? 10 : 16).PaddingBottom(10);
        if (keyboard.Rows.Count > 4)
            rows = new ScrollViewer
            {
                Content = rows,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                MaxHeight = 4 * 44 + 10,
            };
        return VStack(0, Rectangle().Fill(TG.Separator).Frame(height: 1).PaddingBottom(8), rows);
    }
}

/// A bot's answer to a pressed button (messages.getBotCallbackAnswer without `alert`), shown at the top of the chat.
public static class TelegramToast
{
    public static Control Make(string text) =>
        Text(text).Font(13).Foreground(SColor.White).MultilineAlignment(TextAlignment.Center).LineLimit(4)
            .PaddingH(12).PaddingV(8)
            .Background(RoundedRectangle(10).Fill(SColor.Black.Opacity(0.72)))
            .Frame(maxWidth: 360).PaddingTop(10);
}

// MARK: Login

public static class TelegramLogin
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        var phone = Remember("");
        var code = Remember("");
        var password = Remember("");
        var usePhone = Remember(false);
        var apiId = Remember(AppSettings.TelegramApiId);
        var apiHash = Remember(AppSettings.TelegramApiHash);

        var needsApiKeys = AppSettings.TelegramApiId.Length == 0 && (session.AuthError?.Contains("API") ?? false);

        Control step = session.AuthState switch
        {
            AuthState.AwaitingPassword => PasswordStep(session, password),
            AuthState.AwaitingCode => CodeStep(session, phone, code),
            _ => needsApiKeys ? ApiStep(session, apiId, apiHash)
                : usePhone.Value ? PhoneStep(session, phone, usePhone)
                : QrStep(session, usePhone),
        };
        return VStack(18,
                Spacer(),
                step,
                session.AuthError is { } e && !needsApiKeys
                    ? Text(e).Font(13).Foreground(SColor.Red).MultilineAlignment(TextAlignment.Center).Frame(maxWidth: 360)
                    : null,
                Spacer())
            .Fill()
            .Background(SColor.White);
    });

    private static Control QrStep(PlatformSession session, State<bool> usePhone) => View(() =>
    {
        var card = RoundedRectangle(16).Fill(SColor.White).Frame(width: 240, height: 240).Shadow(SColor.Black.Opacity(0.08), 8);
        Control qr = session.QrImage is { } img
            ? ZStack(card,
                new BitmapView(img) { Smooth = false, Mode = ContentMode.Stretch }.Frame(width: 210, height: 210),
                Circle().Fill(TG.Accent).Frame(width: 46, height: 46)
                    .Overlay(Image("paperplane.fill").Foreground(SColor.White).Font(20))
                    .Overlay(Circle().Stroke(SColor.White, 4)),
                QRExpiryOverlay.Make(session, TG.Accent, "Tap to generate a new QR code").Frame(width: 210, height: 210))
            : ZStack(card, new ProgressView());
        return VStack(20,
                qr,
                Text("Log in to Telegram by QR Code").Font(20, Weight.Semibold),
                VStack(HAlign.Leading, 10,
                        Step(1, "Open Telegram on your phone"),
                        Step(2, "Go to **Settings** → **Devices** → **Link Desktop Device**"),
                        Step(3, "Point your phone at this screen to confirm login"))
                    .Frame(width: 330, alignment: Alignment.Leading),
                Button(() => usePhone.Value = true,
                    Text("Log in by phone Number").Foreground(TG.Accent).Font(14, Weight.Medium)));
    }).OnAppear(session.EnsureQR).OnDisappear(session.StopWantingQR);

    private static Control Step(int n, string s) =>
        HStack(VAlign.Top, 10,
            Text($"{n}").Font(12, Weight.Semibold).Foreground(SColor.White)
                .Frame(width: 20, height: 20).Background(Circle().Fill(TG.Accent)),
            LinkText.Markdown(s).Font(14));

    private static Control PhoneStep(PlatformSession session, State<string> phone, State<bool> usePhone) =>
        VStack(14,
            TelegramLogoCircle.Make(),
            Text("Your Phone Number").Font(20, Weight.Semibold),
            Text("Please confirm your country code\nand enter your phone number.").MultilineAlignment(TextAlignment.Center).Foreground(TG.Secondary),
            TgField("phone", "+84 …", phone),
            TgButton("Next", () => session.Login(method: "phone", phone: phone.Value)),
            Button(() => usePhone.Value = false, Text("Log in by QR Code").Foreground(TG.Accent)));

    private static Control CodeStep(PlatformSession session, State<string> phone, State<string> code) =>
        VStack(14,
            TelegramLogoCircle.Make(),
            Text(phone.Value.Length == 0 ? "Enter Code" : phone.Value).Font(20, Weight.Semibold),
            Text("We've sent the code to the Telegram app\non your other device.").MultilineAlignment(TextAlignment.Center).Foreground(TG.Secondary),
            TgField("code", "Code", code),
            TgButton("Next", () => session.Submit(code: code.Value)));

    private static Control PasswordStep(PlatformSession session, State<string> password) =>
        VStack(14,
            TG.Icon("Icon_FastSettingsLock", "lock.fill", 40).Foreground(TG.Accent),
            Text("Enter Password").Font(20, Weight.Semibold),
            Text("Your account is protected with\nan additional password.").MultilineAlignment(TextAlignment.Center).Foreground(TG.Secondary),
            Keep("password", () => new Field("Password", password, secure: true, font: SFont.System(13))
                    { OnSubmit = () => session.Submit(password: password.Value) })
                .Padding(10).Frame(width: 300)
                .Overlay(RoundedRectangle(10).Stroke(TG.Accent, 1.5)),
            TgButton("Next", () => session.Submit(password: password.Value)));

    private static Control ApiStep(PlatformSession session, State<string> apiId, State<string> apiHash) =>
        VStack(14,
            TelegramLogoCircle.Make(),
            Text("Telegram API").Font(20, Weight.Semibold),
            Text("Bên thứ ba cần API ID/Hash riêng của bạn.\nTạo tại my.telegram.org → API development tools.")
                .MultilineAlignment(TextAlignment.Center).Foreground(TG.Secondary),
            TgField("apiId", "API ID", apiId),
            TgField("apiHash", "API Hash", apiHash),
            TgButton("Save", () =>
            {
                AppSettings.TelegramApiId = apiId.Value.Trim();
                AppSettings.TelegramApiHash = apiHash.Value.Trim();
                session.AuthError = null;
                session.Restart();
            }));

    private static Control TgField(string key, string title, State<string> text) =>
        Keep(key, () => new Field(title, text, font: SFont.System(15)))
            .Padding(10).Frame(width: 300)
            .Overlay(RoundedRectangle(10).Stroke(SColor.Hex(0xDADCE0), 1));

    private static Control TgButton(string title, Action action) =>
        Button(action,
            Text(title.ToUpperInvariant()).Font(14, Weight.Semibold).Foreground(SColor.White)
                .Frame(width: 300, height: 44).Background(RoundedRectangle(10).Fill(TG.Accent)));
}

public static class TelegramLogoCircle
{
    public static Control Make() =>
        Circle().Fill(new LinearGradient([SColor.Hex(0x37BBFE), SColor.Hex(0x007DBB)], LinearGradient.Top, LinearGradient.Bottom))
            .Frame(width: 96, height: 96)
            .Overlay(Image("paperplane.fill").Font(42).Foreground(SColor.White).Offset(-3, 2));
}
