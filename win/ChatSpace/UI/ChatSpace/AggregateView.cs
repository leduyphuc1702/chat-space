using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

// Port of UI/ChatSpace/AggregateView.swift: ChatSpace's own screen, a merged list on the left and, on the right, the
// chat rendered by its platform's original conversation UI.

/// What an aggregate list collects.
public readonly record struct AggregateSource(Guid? GroupId)
{
    /// Unread chats from every platform (minus ignored ones).
    public static readonly AggregateSource Inbox = new(null);

    /// A user group: chats whose title matches its keywords.
    public static AggregateSource Group(Guid id) => new(id);

    public bool IsInbox => GroupId == null;

    public RailItem Rail => GroupId is { } id ? RailItem.Group(id) : RailItem.Inbox;

    public static AggregateSource From(RailItem rail) => rail.Kind == RailItem.RailKind.Group ? Group(rail.GroupId) : Inbox;
}

/// `@State` of `AggregateView`, kept for the life of the view.
internal sealed class AggregateState
{
    public readonly State<Platform?> Filter = new(null);
    public readonly State<string> Query = new("");
    /// The open chat stays listed after it is read, until the user picks another one.
    public readonly State<ChatRef?> Sticky = new(null);
    /// Whether the sticky chat was unread when opened; it keeps sorting as such so it doesn't jump.
    public readonly State<bool> StickyWasUnread = new(false);
}

/// `AggregateView(source:)`.
public sealed class AggregateView : Reactive
{
    private const double ListWidth = 340;

    public AggregateView(RailItem rail) : this(AggregateSource.From(rail), new AggregateState()) { }

    private AggregateView(AggregateSource source, AggregateState st) : base(() => Body(source, st)) { }

    private static Control Body(AggregateSource source, AggregateState st)
    {
        // The list and the conversation are separate views: a new message rebuilds the list, not the open chat.
        return HStack(0,
                View(() => Left(source, st)).Frame(width: ListWidth).FillHeight().Background(SColor.ControlBackground),
                Rectangle().Fill(SColor.Black.Opacity(0.1)).Frame(width: 1),
                View(() => Right(source)).Fill())
            .Background(SColor.WindowBackground)
            .Scheme(Scheme.Dark)
            .OnAppear(() =>
            {
                if (AppModel.Shared.AggregateSelection.TryGetValue(source.Rail, out var s)) st.Sticky.Value = s;
            });
    }

    // MARK: Data

    private static CustomGroup? GroupOf(AggregateSource source) =>
        source.GroupId is { } id ? Workspace.Shared.Group(id) : null;

    private static IReadOnlyList<ChatRef> BaseRefs(AggregateSource source, CustomGroup? group) =>
        source.IsInbox ? AppModel.Shared.InboxRefs : group != null ? AppModel.Shared.GroupRefs(group) : [];

    /// The refs shown: the base list with the sticky and selected chats put back, then the filter and the search applied.
    private static List<ChatRef> Refs(AggregateSource source, AggregateState st, CustomGroup? group)
    {
        var model = AppModel.Shared;
        var chats = new Dictionary<ChatRef, Chat>();
        foreach (var p in Platforms.All)
            foreach (var c in model.Session(p).Chats) chats[new ChatRef(p, c.Id)] = c;
        Chat? ChatOf(ChatRef r) => chats.GetValueOrDefault(r);

        var list = BaseRefs(source, group).ToList();
        var sticky = st.Sticky.Value;
        var pinned = new List<ChatRef>();
        if (sticky is { } s1) pinned.Add(s1);
        if (model.AggregateSelection.TryGetValue(source.Rail, out var selected)) pinned.Add(selected);
        foreach (var s in pinned)
        {
            if (ChatOf(s) == null) continue;
            list.RemoveAll(r => r == s);
            list.Insert(Math.Min(Position(s, list), list.Count), s);
        }
        if (st.Filter.Value is { } f) list = list.Where(r => r.Platform == f).ToList();
        var q = CustomGroup.Normalize(st.Query.Value);
        if (q.Length > 0) list = list.Where(r => CustomGroup.Normalize(ChatOf(r)?.Title ?? "").Contains(q, StringComparison.Ordinal)).ToList();
        return list;

        // Where `ref` sorts among the others, treating it as still unread if it was when opened.
        int Position(ChatRef r, List<ChatRef> among)
        {
            if (ChatOf(r) is not { } c) return 0;
            var unread = c.HasUnread || (sticky is { } sr && r == sr && st.StickyWasUnread.Value);
            var unreadFirst = !source.IsInbox;
            var at = among.FindIndex(other =>
            {
                if (ChatOf(other) is not { } o) return false;
                if (unreadFirst && o.HasUnread != unread) return unread;
                return o.Timestamp < c.Timestamp;
            });
            return at < 0 ? among.Count : at;
        }
    }

    private static void Open(AggregateSource source, AggregateState st, ChatRef r)
    {
        var model = AppModel.Shared;
        st.StickyWasUnread.Value = model.Chat(r)?.HasUnread ?? false;
        st.Sticky.Value = r;
        model.AggregateSelection = model.AggregateSelection.SetItem(source.Rail, r);
        model.Session(r.Platform).Prepare(r.ChatId);
    }

    // MARK: Left column

    private static Control Left(AggregateSource source, AggregateState st)
    {
        var group = GroupOf(source);
        var refs = Refs(source, st, group);
        return VStack(HAlign.Leading, 0, Header(source, st, group), ListPane(source, st, refs));
    }

    private static Control Header(AggregateSource source, AggregateState st, CustomGroup? group)
    {
        Control top;
        if (group is { } g)
        {
            top = HStack(10,
                new GroupIcon(g, 30),
                VStack(HAlign.Leading, 1,
                    Text(g.Name).Font(17, Weight.Bold),
                    Text("Từ khoá: " + string.Join(", ", g.Keywords)).Font(11).Foreground(SColor.Secondary).LineLimit(1)),
                Spacer(),
                Button(() => GroupEditor.Present(g),
                        Image("slider.horizontal.3").Font(14).Frame(28, 28).ContentShape())
                    .Foreground(SColor.Secondary).Help("Sửa nhóm"));
        }
        else
        {
            var ignored = Workspace.Shared.Ignored.Count;
            top = HStack(10,
                new AppIconTile(30),
                VStack(HAlign.Leading, 1,
                    Text("Chưa đọc").Font(17, Weight.Bold),
                    Text("Tất cả nền tảng").Font(11).Foreground(SColor.Secondary)),
                Spacer(),
                ignored > 0
                    ? Button(() => SettingsWindow.Show(SettingsSection.Ignored),
                            Label("eye.slash", $"{ignored} bỏ qua")
                                .Font(11).Foreground(SColor.Secondary).ContentShape())
                        .Help("Khôi phục trong Cài đặt → Đã bỏ qua")
                    : null);
        }

        var baseRefs = BaseRefs(source, group);
        int CountFor(Platform? p) => baseRefs.Count(r => p == null || r.Platform == p);

        Control Chip(Platform? p, string title, int count)
        {
            var on = st.Filter.Value == p;
            return Button(() => st.Filter.Value = p,
                HStack(5,
                        p is { } pl ? new PlatformGlyph(pl, 14) : null,
                        Text(title).Font(12, Weight.Medium),
                        Text(count.ToString()).Font(11).Foreground(on ? SColor.White.Opacity(0.8) : SColor.Secondary))
                    .PaddingH(10).Frame(height: 26)
                    .Foreground(on ? SColor.White : SColor.Primary)
                    .Background(Capsule().Fill(on ? SColor.Accent : SColor.Primary.Opacity(0.07)))
                    .ContentShape());
        }

        var chips = new List<Control?> { Chip(null, "Tất cả", CountFor(null)) };
        foreach (var p in Platforms.All)
        {
            var n = CountFor(p);
            if (n > 0 || st.Filter.Value == p) chips.Add(Chip(p, p.DisplayName(), n));
        }

        var field = Keep("aggregate.search", () =>
        {
            var f = new Field("Tìm kiếm", st.Query, FieldStyle.Plain, font: SFont.System(13));
            // The Kit's field is drawn for a light surface: text, caret and placeholder for the dark one.
            var text = new ImmutableSolidColorBrush(SColor.Primary.Resolve(Scheme.Dark));
            f.Box.Foreground = text;
            f.Box.CaretBrush = text;
            f.Children[^1].SetValue(Env.ForegroundProperty, SColor.White.Opacity(0.25));
            return f;
        });

        return VStack(HAlign.Leading, 10,
                top,
                HStack(6, Image("magnifyingglass").Font(12).Foreground(SColor.Secondary), field)
                    .PaddingH(10).Frame(height: 30)
                    .Background(RoundedRectangle(8).Fill(SColor.Primary.Opacity(0.06))),
                // A horizontal ScrollView is as tall as its content (the chips); the Kit's would claim the spare height.
                ScrollView(HStack(6, chips.ToArray()), vertical: false, horizontal: true, indicators: false).Frame(height: 26))
            .PaddingH(14).PaddingTop(38).PaddingBottom(10);
    }

    // MARK: List

    private static Control ListPane(AggregateSource source, AggregateState st, List<ChatRef> items)
    {
        if (items.Count == 0)
        {
            return VStack(10,
                    Image(source.IsInbox ? "checkmark.circle" : "tray").Font(40, Weight.Light).Foreground(SColor.Tertiary),
                    Text(source.IsInbox ? "Đã đọc hết" : "Chưa có đoạn chat nào khớp từ khoá").Font(14, Weight.Medium).Foreground(SColor.Secondary),
                    source.IsInbox
                        ? Text("Tin chưa đọc của Zalo, Messenger, WhatsApp và Telegram sẽ hiện ở đây.")
                            .Font(12).Foreground(SColor.Tertiary).MultilineAlignment(TextAlignment.Center)
                        : null)
                .Padding(30)
                .Fill();
        }

        var model = AppModel.Shared;
        var selection = model.AggregateSelection.TryGetValue(source.Rail, out var sel) ? sel : (ChatRef?)null;
        Control Row(ChatRef r) => model.Chat(r) is { } chat
            ? AggregateRow.Make(r, chat, selection == r)
                .OnTap(() => Open(source, st, r))
                .ChatContextMenu(r.Platform, chat, source.IsInbox)
            : Clear.Frame(height: 0);

        Control content;
        if (source.IsInbox && st.Filter.Value == null)
        {
            var sections = new List<Control?>();
            foreach (var p in Platforms.All)
            {
                var rows = items.Where(r => r.Platform == p).ToList();
                if (rows.Count == 0) continue;
                sections.Add(new StickySection(SectionHeader(p, rows.Count), VStack(0, rows.Select(Row).ToArray())));
            }
            content = VStack(0, sections.ToArray());
        }
        else content = VStack(0, items.Select(Row).ToArray());

        var host = Memo("aggregate.list", () => new LegacyScrollHost(Clear));
        host.SetContent(content.PaddingH(8).PaddingBottom(8));
        return new KeepHost(host);
    }

    private static Control SectionHeader(Platform p, int n) =>
        HStack(7,
                new PlatformGlyph(p, 16),
                Text(p.DisplayName().ToUpperInvariant()).Font(11, Weight.Semibold).Foreground(SColor.Secondary),
                Text(n.ToString()).Font(11).Foreground(SColor.Tertiary),
                Spacer(),
                Button(() => AppModel.Shared.Rail = RailItem.For(p), Text("Xem").Font(11).Foreground(SColor.Accent)))
            .PaddingH(8).PaddingTop(10).PaddingBottom(4)
            .Background(SColor.ControlBackground);

    // MARK: Right pane

    private static Control Right(AggregateSource source)
    {
        var model = AppModel.Shared;
        if (model.AggregateSelection.TryGetValue(source.Rail, out var r) && model.Chat(r) is { } chat)
        {
            // `.id(ref.id)`: the conversation is kept while the list updates and replaced when another chat is picked.
            var conversation = Keep("aggregate.conversation:" + r.Id,
                () => PlatformConversations.Make(r.Platform, model.Session(r.Platform), r.ChatId)).Scheme(Scheme.Light);
            return VStack(0, SourceBar(r, chat), conversation.Fill());
        }
        return Empty(GroupOf(source));
    }

    // A `Label` in a button: icon and title sit about 8 pt apart in the capture.
    private static Control Label(string symbol, string title) => HStack(8, Image(symbol), Text(title));

    private static Control SourceBar(ChatRef r, Chat chat) =>
        HStack(8,
                new PlatformGlyph(r.Platform, 16),
                Text(r.Platform.DisplayName()).Font(12, Weight.Semibold),
                Text("·").Foreground(SColor.Tertiary),
                Text(chat.Title).Font(12).Foreground(SColor.Secondary).LineLimit(1),
                Spacer(),
                Button(() => ChatHeads.Shared.Show(r, expand: true), Label("bubble.left.circle", "Bong bóng").Font(11).ContentShape())
                    .Foreground(SColor.Secondary).Help("Mở đoạn chat này thành bong bóng nổi"),
                Button(() => AppModel.Shared.Open(r.Platform, r.ChatId),
                        Label("arrow.up.forward.app", $"Mở trong {r.Platform.DisplayName()}").Font(11).ContentShape())
                    .Foreground(SColor.Accent))
            .PaddingH(12).Frame(height: 30)
            .Background(SColor.ControlBackground)
            .Overlay(Alignment.Bottom, Rectangle().Fill(SColor.Black.Opacity(0.08)).Frame(height: 1));

    private static Control Empty(CustomGroup? group) =>
        VStack(12,
                group != null ? new GroupIcon(group, 64) : new AppIconTile(64),
                Text(group?.Name ?? "ChatSpace").Font(20, Weight.Semibold),
                Text(group == null
                        ? "Chọn một đoạn chat để trả lời ngay tại đây, bằng giao diện gốc của nền tảng."
                        : $"Mọi đoạn chat có tên khớp {string.Join(", ", group.Keywords.Select(k => $"“{k}”"))} trên các nền tảng.")
                    .Font(13).Foreground(SColor.Secondary).MultilineAlignment(TextAlignment.Center).Frame(maxWidth: 380))
            .Fill()
            .Background(SColor.WindowBackground);
}

/// `AggregateRow`.
internal static class AggregateRow
{
    public static Control Make(ChatRef r, Chat chat, bool selected)
    {
        var session = AppModel.Shared.Session(r.Platform);
        return new HoverBox(hovered => Row(r, chat, selected, hovered)).OnAppear(() => session.RequestAvatar(chat));
    }

    private static Control Row(ChatRef r, Chat chat, bool selected, bool hovered)
    {
        var unread = chat.HasUnread;
        var muted = chat.IsMuted;
        Control? badge = chat.Unread > 0
            ? Text(chat.Unread > 999 ? "999+" : chat.Unread.ToString()).Font(11, Weight.Semibold).Foreground(SColor.White)
                .PaddingH(6).Frame(minWidth: 20, minHeight: 20)
                .Background(Capsule().Fill(muted ? SColor.Gray.Opacity(0.6) : SColor.Accent))
            : chat.MarkedUnread == true
                ? Circle().Fill(muted ? SColor.Gray.Opacity(0.6) : SColor.Accent).Frame(10, 10)
                : null;

        var avatar = AvatarView.Make(chat.Avatar, 46, () =>
                Circle().Fill(SColor.Gray.Opacity(0.25))
                    .Overlay(Text(FirstLetter(chat.Title)).Font(18, Weight.Semibold).Foreground(SColor.White)))
            .Overlay(Alignment.BottomTrailing, new PlatformBadge(r.Platform).Offset(3, 3));

        return HStack(10,
                avatar,
                VStack(HAlign.Leading, 3,
                    HStack(4,
                        Text(chat.Title).Font(14, unread ? Weight.Semibold : Weight.Regular).LineLimit(1),
                        muted ? Image("speaker.slash.fill").Font(10).Foreground(SColor.Tertiary) : null,
                        Spacer(4),
                        Text(TimeText(chat)).Font(11).Foreground(unread && !muted ? SColor.Accent : SColor.Secondary)),
                    HStack(4,
                        Text(Preview(chat)).Font(12.5).Foreground(SColor.Secondary).LineLimit(1),
                        Spacer(4),
                        badge)))
            .PaddingH(8).PaddingV(7)
            .Background(RoundedRectangle(9).Fill(selected ? SColor.Accent.Opacity(0.16) : hovered ? SColor.Primary.Opacity(0.05) : SColor.Clear))
            .ContentShape();
    }

    private static string FirstLetter(string title) =>
        title.Length == 0 ? "" : StringInfo.GetNextTextElement(title).ToUpperInvariant();

    private static string Preview(Chat chat)
    {
        if (chat.LastMessage is not { } m) return "";
        var body = m.Preview(photo: "Hình ảnh", video: "Video", voice: "Tin nhắn thoại", sticker: "Sticker", file: "Tệp", deleted: "Tin nhắn đã thu hồi");
        if (m.FromMe) return "Bạn: " + body;
        if (chat.IsGroup && m.SenderName is { } n) return n + ": " + body;
        return body;
    }

    /// Today: the time. Within the last six days: the weekday as `vi_VN`'s "EEE" writes it. Older: dd/MM.
    private static string TimeText(Chat chat)
    {
        var d = chat.Date;
        var now = DateTime.Now;
        if (d.Date == now.Date) return Fmt.Time(d);
        if ((now - d).TotalSeconds < 6 * 86400)
            return d.DayOfWeek == DayOfWeek.Sunday ? "CN" : "Thứ " + ((int)d.DayOfWeek + 1);
        return d.ToString("dd/MM", Inv.C);
    }
}

/// `PlatformIcon(platform:, size:)` as SwiftUI lays it out. The app icon is `.frame(size * 1.22).frame(size)`: the outer
/// frame is `size` but the image overflows it, so the artwork's transparent margin falls outside and the tile fills `size`.
/// The Kit's frames clamp the inner one (`PlatformIcon` in RootView.cs), shrinking the tile to about 80 %.
internal sealed class PlatformGlyph : Decorator
{
    private readonly Avalonia.Media.Imaging.Bitmap? _icon;
    private readonly double _size;

    public PlatformGlyph(Platform platform, double size)
    {
        _size = size;
        Width = size;
        Height = size;
        _icon = NativeAssets.Shared.AppIcon(platform);
        if (_icon == null) Child = new PlatformIcon(platform, size); // the drawn icons (Messenger) are exactly `size`
        else RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    public override void Render(DrawingContext ctx)
    {
        if (_icon != null)
        {
            var full = _size * 1.22;
            var o = (_size - full) / 2;
            ctx.DrawImage(_icon, new Rect(o, o, full, full));
        }
        base.Render(ctx);
    }
}

/// `PlatformIcon(platform:, size: 17).background(Circle().fill(controlBackgroundColor).padding(-2))`: the icon at its own
/// size with a disc 2 pt wider all round behind it.
internal sealed class PlatformBadge : Decorator
{
    private const double Size = 17, Ring = 2;

    public PlatformBadge(Platform platform)
    {
        Child = new PlatformGlyph(platform, Size);
        Width = Size;
        Height = Size;
    }

    public override void Render(DrawingContext ctx)
    {
        var scheme = GetValue(Env.SchemeProperty);
        ctx.DrawEllipse(new ImmutableSolidColorBrush(SColor.ControlBackground.Resolve(scheme)), null,
            new Rect(0, 0, Size, Size).Inflate(Ring));
        base.Render(ctx);
    }
}

/// A section whose header stays at the top of the scroll view until the next section pushes it away
/// (`LazyVStack(pinnedViews: [.sectionHeaders])`).
internal sealed class StickySection : Panel
{
    private readonly Control _header, _body;
    private readonly TranslateTransform _shift = new();
    private ScrollViewer? _scroll;

    public StickySection(Control header, Control body)
    {
        _header = header;
        _body = body;
        _header.RenderTransform = _shift;
        Children.Add(body);
        Children.Add(header); // drawn, and hit, above the rows
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = available.Width;
        _header.Measure(new Size(w, double.PositiveInfinity));
        _body.Measure(new Size(w, double.PositiveInfinity));
        var width = double.IsInfinity(w) ? Math.Max(_header.DesiredSize.Width, _body.DesiredSize.Width) : w;
        return new Size(width, _header.DesiredSize.Height + _body.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var h = _header.DesiredSize.Height;
        _header.Arrange(new Rect(0, 0, finalSize.Width, h));
        _body.Arrange(new Rect(0, h, finalSize.Width, _body.DesiredSize.Height));
        Update(finalSize.Height);
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll != null) _scroll.ScrollChanged += OnScroll;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroll != null) _scroll.ScrollChanged -= OnScroll;
        _scroll = null;
    }

    private void OnScroll(object? sender, ScrollChangedEventArgs e) => Update(Bounds.Height);

    private void Update(double height)
    {
        if (_scroll == null) return;
        var top = this.TranslatePoint(new Point(0, 0), _scroll)?.Y ?? 0;
        var room = Math.Max(0, height - _header.DesiredSize.Height);
        var shift = Math.Clamp(-top, 0, room);
        if (Math.Abs(_shift.Y - shift) > 0.01) _shift.Y = shift;
    }
}

/// A chat rendered by its own platform's conversation UI (`PlatformConversation`), always in the light scheme.
public static class PlatformConversations
{
    public static Control Make(Platform platform, PlatformSession session, string chatId)
    {
        Control body = platform switch
        {
            Platform.Zalo => new ZaloConversation(session, chatId),
            Platform.Messenger => new MessengerConversation(session, chatId),
            Platform.Whatsapp => new WhatsAppConversation(session, chatId),
            _ => new TelegramConversation(session, chatId),
        };
        return body.Scheme(Scheme.Light);
    }
}
