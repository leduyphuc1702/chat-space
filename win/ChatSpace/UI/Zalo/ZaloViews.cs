using System.Globalization;
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

/// Zalo PC look: blue nav bar, "Ưu tiên / Khác" tabs, light-blue outgoing bubbles.
public static class ZL
{
    public static readonly SColor Blue = SColor.Hex(0x0068FF);
    public static readonly SColor NavBar = SColor.Hex(0x005AE0);
    public static readonly SColor NavSelected = SColor.Hex(0x004BC2);
    public static readonly SColor TextColor = SColor.Hex(0x081C36);
    public static readonly SColor Secondary = SColor.Hex(0x7589A3);
    public static readonly SColor OutBubble = SColor.Hex(0xE5EFFF);
    public static readonly SColor OutBorder = SColor.Hex(0xC7E0FF);
    public static readonly SColor InBubble = SColor.White;
    public static readonly SColor ChatBackground = SColor.Hex(0xEEF0F1);
    public static readonly SColor SelectedRow = SColor.Hex(0xE5EFFF);
    public static readonly SColor HoverRow = SColor.Hex(0xF3F5F6);
    public static readonly SColor Search = SColor.Hex(0xEAEDF0);
    public static readonly SColor Badge = SColor.Hex(0xFA5555);
    public static readonly SColor Divider = SColor.Hex(0xDBDDE1);

    public static readonly (uint, uint)[] AvatarColors =
    [
        (0x4A9DFF, 0x0068FF), (0xFFB257, 0xFF8A00), (0x7ED8A8, 0x2DB971), (0xC59BFF, 0x8A4FFF), (0xFF8FA3, 0xF04466), (0x6FD6E8, 0x15A3BF),
    ];

    public static SFont F(double size, Weight weight = Weight.Regular) => ZaloFont.Of(size, weight);

    /// A glyph from Zalo PC's `zalo-font` icon font.
    public static SText Glyph(int code, double size = 24) => Text(char.ConvertFromUtf32(code), ZaloFont.Icon(size));

    public static string ListTime(DateTime d)
    {
        var s = (DateTime.Now - d).TotalSeconds;
        if (s < 60) return "Vài giây";
        if (s < 3600) return $"{(int)(s / 60)} phút";
        if (s < 86400 && d.Date == DateTime.Today) return $"{(int)(s / 3600)} giờ";
        if (d.Date == DateTime.Today.AddDays(-1)) return "Hôm qua";
        if (s < 7 * 86400) return $"{(int)(s / 86400)} ngày";
        return d.ToString("dd/MM", Inv.C);
    }

    public static string Separator(DateTime d)
    {
        if (d.Date == DateTime.Today) return $"{Fmt.Time(d)} Hôm nay";
        if (d.Date == DateTime.Today.AddDays(-1)) return $"{Fmt.Time(d)} Hôm qua";
        return d.ToString("HH:mm dd/MM/yyyy", Inv.C);
    }

    public static Control Avatar(string id, string title, string? source, double size = 48, bool group = false) =>
        AvatarView.Make(source, size, () =>
        {
            var c = AvatarColors[id.StableHash() % AvatarColors.Length];
            Control inner = group && title.Length == 0
                ? Glyph(ZaloGlyphs.Group, size * 0.45).Foreground(SColor.White)
                : Text(title.Initials()).Font(F(size * 0.36, Weight.Semibold)).Foreground(SColor.White);
            return Circle().Fill(new LinearGradient([SColor.Hex(c.Item1), SColor.Hex(c.Item2)], LinearGradient.TopLeading, LinearGradient.BottomTrailing))
                .Overlay(inner);
        });
}

public sealed class ZaloRootView : Reactive
{
    public ZaloRootView(PlatformSession s) : base(() => Build(s)) { }

    private static Control Build(PlatformSession session)
    {
        Control body;
        if (session.AuthState == AuthState.LoggedIn)
        {
            var conversation = View(() => session.SelectedChatId is { } id && Tracker.Untracked(() => session.SelectedChat) != null
                ? Keep(("conv", id), () => new ZaloConversation(session, id))
                : Keep("welcome", () => new ZaloWelcome(session)));
            body = HStack(0,
                ZaloNavBar.Make(session),
                ZaloChatList.Make(session).Frame(width: 344),
                Rectangle().Fill(ZL.Divider).Frame(width: 1),
                conversation);
        }
        else if (session.AuthState == AuthState.Connecting || (session.BridgeError != null && session.AuthState != AuthState.Error))
            body = BridgeStatusView.Make(session, ZL.Blue);
        else
            body = ZaloLogin.Make(session);
        return body.Background(SColor.White).Scheme(Scheme.Light);
    }
}

public static class ZaloNavBar
{
    public static Control Make(PlatformSession session) => View(() =>
        VStack(4,
                Clear.Frame(height: 30),
                ZL.Avatar(session.SelfInfo?.Id ?? "me", session.SelfInfo?.Name ?? "", session.SelfInfo?.Avatar, 48)
                    .Overlay(Circle().Stroke(SColor.White.Opacity(0.9), 1.5))
                    .PaddingBottom(14),
                Item(ZaloGlyphs.MessageFilled, selected: true, badge: session.UnreadTotal),
                Item(ZaloGlyphs.ContactLine),
                Item(ZaloGlyphs.TodoLine),
                Spacer(),
                Item(ZaloGlyphs.CloudLine),
                Item(ZaloGlyphs.Briefcase),
                Item(ZaloGlyphs.SettingLine).PaddingBottom(10))
            .Frame(width: 64)
            .Background(ZL.NavBar));

    private static Control Item(int glyph, bool selected = false, int badge = 0) =>
        ZL.Glyph(glyph, 28).Foreground(SColor.White)
            .Frame(width: 64, height: 56)
            .Background(selected ? ZL.NavSelected : SColor.Clear)
            .Overlay(Alignment.TopTrailing, badge > 0
                ? Text(badge > 5 ? "5+" : $"{badge}").Font(ZL.F(10, Weight.Bold)).Foreground(SColor.White)
                    .PaddingH(4).Frame(minWidth: 18, minHeight: 18)
                    .Background(Capsule().Fill(ZL.Badge)).Offset(-8, 8)
                : null);
}

public sealed class ZaloWelcome : Decorator
{
    private static readonly string[] Slides = ["inapp-welcome-screen-01", "inapp-welcome-screen-02", "inapp-welcome-screen-03"];
    private readonly State<int> _slide = new(0);
    private readonly DispatcherTimer _timer;

    public ZaloWelcome(PlatformSession session)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => _slide.Value = (_slide.Value + 1) % Slides.Length;
        Child = View(() =>
        {
            var slide = _slide.Value;
            Control art;
            Control? dots = null;
            if (NativeAssets.Shared.ZaloImage(Slides[slide]) is { } img)
            {
                art = Bitmap(img, ContentMode.Fit).Frame(width: 380, height: 380 * img.PixelSize.Height / (double)img.PixelSize.Width).PaddingTop(24);
                dots = HStack(8, Slides.Select((_, i) => (Control?)Circle().Fill(i == slide ? ZL.Blue : SColor.Hex(0xC5CED9))
                    .Frame(width: 8, height: 8).OnTap(() => _slide.Value = i)).ToArray()).PaddingBottom(20);
            }
            else art = Image("bubble.left.and.text.bubble.right.fill").Font(110).Foreground(ZL.Blue.Opacity(0.18)).PaddingV(30);
            return VStack(14,
                    Spacer(),
                    LinkText.Markdown("Chào mừng đến với **Zalo PC**!", ZL.F(22, Weight.Bold)).Font(ZL.F(22)).Foreground(ZL.TextColor),
                    Text("Khám phá những tiện ích hỗ trợ làm việc và trò chuyện cùng\nngười thân, bạn bè được tối ưu hoá cho máy tính của bạn.")
                        .Font(ZL.F(14)).Foreground(ZL.TextColor.Opacity(0.8)).MultilineAlignment(TextAlignment.Center).LineLimit(null),
                    art,
                    dots,
                    session.Connection == "offline"
                        ? HStack(6, Image("exclamationmark.triangle.fill"), Text(session.ConnectionDetail ?? "Mất kết nối")).Font(ZL.F(13)).Foreground(SColor.Orange)
                        : null,
                    Spacer())
                .Fill()
                .Background(SColor.White);
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }
}

// MARK: Chat list

public static class ZaloChatList
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        var query = Remember("");
        var tab = Remember(0);
        var moreMenu = Remember(false);
        var syncHidden = Remember(false);

        var search = Keep("search", () => new Field("Tìm kiếm", query, font: ZL.F(14)));
        var top = HStack(6,
                HStack(6, ZL.Glyph(ZaloGlyphs.Search, 16).Foreground(ZL.Secondary), search)
                    .PaddingH(10).Frame(height: 32)
                    .Background(RoundedRectangle(6).Fill(ZL.Search)),
                ZL.Glyph(ZaloGlyphs.AddUser, 20).Tool("Thêm bạn", () => ChatTools.NativeOnly(Platform.Zalo, "Thêm bạn")),
                ZL.Glyph(ZaloGlyphs.CreateGroup, 20).Tool("Tạo nhóm chat", () => ChatTools.NativeOnly(Platform.Zalo, "Tạo nhóm chat")))
            .Font(ZL.F(15)).Foreground(ZL.TextColor)
            .Padding(30, 16, 8, 16);

        var tabs = HStack(18,
                TabButton("Ưu tiên", 0, tab),
                TabButton("Khác", 1, tab),
                Spacer(),
                HStack(3, Text("Phân loại"), ZL.Glyph(ZaloGlyphs.ChevronDown, 14)).Font(ZL.F(13)).Foreground(ZL.TextColor),
                ZL.Glyph(ZaloGlyphs.MoreH, 18).Foreground(ZL.TextColor)
                    .ToolPopover("Thêm", moreMenu, done => VStack(HAlign.Leading, 0,
                            MenuRow("Đánh dấu tất cả đã đọc", () => { done(); session.MarkAllRead(); }),
                            session.Can("phone_sync")
                                ? MenuRow("Đồng bộ tin nhắn từ điện thoại", () => { done(); syncHidden.Value = false; session.StartPhoneSync(); })
                                    .Disabled(session.PhoneSync?.Running == true)
                                : null)
                        .PaddingV(6).Frame(width: 260).Scheme(Scheme.Light), 26))
            .PaddingH(16).Frame(height: 36)
            .Overlay(Alignment.Bottom, Rectangle().Fill(ZL.Divider).Frame(height: 1));

        Control? offline = session.Connection == "offline"
            ? HStack(8,
                    Image("exclamationmark.circle.fill").Foreground(SColor.Orange),
                    Text(session.ConnectionDetail ?? "Mất kết nối, đang thử kết nối lại…").Font(ZL.F(12.5)).Foreground(ZL.TextColor),
                    Spacer())
                .PaddingH(16).PaddingV(8).Background(SColor.Hex(0xFFF6E0))
            : null;

        Control? banner = session.Can("phone_sync") && !syncHidden.Value && ShowSyncBanner(session)
            ? ZaloSyncBanner.Make(session, () => syncHidden.Value = true)
            : null;

        var list = Filtered(session, query.Value, tab.Value);
        Control content;
        if (list.Count == 0 && query.Value.Length == 0 && tab.Value == 0 && !session.Chats.IsEmpty)
        {
            content = VStack(10,
                    ZL.Glyph(ZaloGlyphs.MessageLine, 34).Foreground(ZL.Secondary),
                    Text("Chưa có tin nhắn mới").Font(ZL.F(14, Weight.Semibold)).Foreground(ZL.TextColor),
                    Text(session.Can("phone_sync")
                            ? "Lịch sử chat Zalo nằm trên điện thoại. Đồng bộ tin nhắn từ điện thoại để xem lại các cuộc trò chuyện cũ; tin nhắn mới sẽ tự hiện ở đây."
                            : "Zalo lưu lịch sử chat trên điện thoại và không cho máy tính mới tải lại tin cũ, nên ChatSpace hiện các đoạn chat ở đây khi có tin nhắn mới đến. Bạn vẫn nhắn được cho mọi bạn bè và nhóm.")
                        .Font(ZL.F(12.5)).Foreground(ZL.Secondary).MultilineAlignment(TextAlignment.Center).LineLimit(null),
                    Button(() => tab.Value = 1, Text("Xem tất cả bạn bè và nhóm").Font(ZL.F(13, Weight.Medium)).Foreground(ZL.Blue)))
                .Padding(28).Fill();
        }
        else
        {
            var selected = session.SelectedChatId;
            content = ScrollView(ForEach(() => list.Select(c => (c, c.Id == selected)).ToList(), x => x.c.Id, x =>
                ZaloChatRow.Make(session, x.c, x.Item2)
                    .OnTap(() => session.Select(x.c.Id))
                    .ChatContextMenu(session.Platform, x.c)));
        }

        return VStack(0, top, tabs, offline, banner, content.FillHeight(Alignment.Top))
            .Background(SColor.White)
            .OnAppear(() => _ = session.LoadPhoneSyncStatus());
    });

    /// Offered until the first successful sync; afterwards only while one runs or just ended.
    private static bool ShowSyncBanner(PlatformSession session)
    {
        var syncedAt = Prefs.GetDouble("zaloPhoneSyncedAt") ?? 0;
        if (session.PhoneSync is not { } p) return syncedAt == 0;
        return p.State is PhoneSyncProgress.SyncState.Idle or PhoneSyncProgress.SyncState.Cancelled ? syncedAt == 0 : true;
    }

    private static Control MenuRow(string title, Action action) =>
        Button(action, View(() => Text(title).Font(ZL.F(13.5)).Foreground(ZL.TextColor)
                .FillWidth(Alignment.Leading).PaddingH(14).PaddingV(8))
            .HoverBackground(SColor.Clear, ZL.HoverRow));

    private static Control TabButton(string title, int i, State<int> tab)
    {
        var on = tab.Value == i;
        return Text(title).Font(ZL.F(13.5, on ? Weight.Semibold : Weight.Regular))
            .Foreground(on ? ZL.Blue : ZL.Secondary)
            .Frame(height: 36)
            .Overlay(Alignment.Bottom, on ? Rectangle().Fill(ZL.Blue).Frame(height: 2) : null)
            .OnTap(() => tab.Value = i);
    }

    /// "Khác" holds chats with strangers/unknown names and muted chats, approximating Zalo's split.
    private static List<Chat> Filtered(PlatformSession session, string query, int tab) =>
        session.Chats.Where(c =>
        {
            var other = c.IsMuted || c.LastMessage == null;
            var matches = query.Length == 0 || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            return matches && (query.Length == 0 ? (tab == 0 ? !other : other) : true);
        }).ToList();
}

/// "Đồng bộ tin nhắn" banner at the top of the chat list, like Zalo PC's: offer, phone prompt, progress, result.
public static class ZaloSyncBanner
{
    private static readonly CultureInfo Vi = new("vi-VN");

    public static Control Make(PlatformSession session, Action onClose)
    {
        var p = session.PhoneSync ?? new PhoneSyncProgress { State = PhoneSyncProgress.SyncState.Idle };
        var detail = Detail(p);
        Control? progress = p.State is PhoneSyncProgress.SyncState.Messages or PhoneSyncProgress.SyncState.Paused && p.Chats is > 0
            ? new ProgressView { Value = (p.ChatsDone ?? 0) / (double)p.Chats!.Value }.Tint(ZL.Blue).PaddingTop(2)
            : null;
        Control? act = p.State switch
        {
            PhoneSyncProgress.SyncState.Idle or PhoneSyncProgress.SyncState.Cancelled => Action("Đồng bộ ngay", ZL.Blue, session.StartPhoneSync),
            PhoneSyncProgress.SyncState.Failed => Action((p.ChatsDone ?? 0) > 0 ? "Tiếp tục" : "Thử lại", ZL.Blue, session.StartPhoneSync),
            PhoneSyncProgress.SyncState.Done => null,
            _ => Action("Huỷ", ZL.Secondary, session.CancelPhoneSync),
        };
        return HStack(VAlign.Top, 10,
                Icon(p),
                VStack(HAlign.Leading, 4,
                    Text(Title(p)).Font(ZL.F(13.5, Weight.Semibold)).Foreground(ZL.TextColor),
                    detail != null ? Text(detail).Font(ZL.F(12.5)).Foreground(ZL.Secondary).LineLimit(null) : null,
                    progress,
                    act != null ? HStack(14, act).PaddingTop(2) : null),
                Spacer(0),
                !p.Running
                    ? Button(onClose, Image("xmark").Font(11, Weight.Semibold).Foreground(ZL.Secondary).Frame(width: 20, height: 20).ContentShape()).Help("Ẩn")
                    : null)
            .Padding(12)
            .Background(RoundedRectangle(8).Fill(SColor.Hex(0xF0F6FF)))
            .Overlay(RoundedRectangle(8).Stroke(ZL.OutBorder, 1))
            .PaddingH(12).PaddingV(8);
    }

    private static Control Icon(PhoneSyncProgress p)
    {
        Control c = p.State switch
        {
            PhoneSyncProgress.SyncState.Preparing or PhoneSyncProgress.SyncState.Waiting or PhoneSyncProgress.SyncState.Chats
                or PhoneSyncProgress.SyncState.Messages => new ProgressView { Size = ControlSize.Small },
            PhoneSyncProgress.SyncState.Paused => Image("pause.circle.fill").Foreground(SColor.Orange),
            PhoneSyncProgress.SyncState.Done => Image("checkmark.circle.fill").Foreground(SColor.Hex(0x2DB971)),
            PhoneSyncProgress.SyncState.Failed => Image("exclamationmark.circle.fill").Foreground(SColor.Orange),
            _ => Image("iphone.and.arrow.forward").Foreground(ZL.Blue),
        };
        return c.Font(18).Frame(width: 28, height: 28);
    }

    private static string Title(PhoneSyncProgress p) => p.State switch
    {
        PhoneSyncProgress.SyncState.Preparing => "Đang chuẩn bị đồng bộ…",
        PhoneSyncProgress.SyncState.Waiting => "Xác nhận trên điện thoại",
        PhoneSyncProgress.SyncState.Paused => "Đồng bộ đang tạm dừng",
        PhoneSyncProgress.SyncState.Chats => "Đang tải danh sách trò chuyện…",
        PhoneSyncProgress.SyncState.Messages => "Đang đồng bộ tin nhắn",
        PhoneSyncProgress.SyncState.Done => "Đã đồng bộ xong",
        PhoneSyncProgress.SyncState.Failed => "Chưa đồng bộ được",
        _ => "Đồng bộ tin nhắn từ điện thoại",
    };

    private static string? Detail(PhoneSyncProgress p)
    {
        var n = (p.Messages ?? 0).ToString("N0", Vi);
        switch (p.State)
        {
            case PhoneSyncProgress.SyncState.Preparing: return null;
            case PhoneSyncProgress.SyncState.Waiting: return "Mở Zalo trên điện thoại và chọn Đồng ý để cho phép ChatSpace nhận tin nhắn.";
            case PhoneSyncProgress.SyncState.Paused:
                return $"Zalo trên điện thoại đang chạy nền nên ngừng gửi tin. Mở Zalo trên điện thoại (giữ màn hình sáng) để tiếp tục. {p.ChatsDone ?? 0}/{p.Chats ?? 0} cuộc trò chuyện · {n} tin nhắn.";
            case PhoneSyncProgress.SyncState.Chats: return "Giữ Zalo trên điện thoại mở cho đến khi xong.";
            case PhoneSyncProgress.SyncState.Messages:
                return $"{p.ChatsDone ?? 0}/{p.Chats ?? 0} cuộc trò chuyện · {n} tin nhắn. Giữ Zalo trên điện thoại mở cho đến khi xong.";
            case PhoneSyncProgress.SyncState.Done:
                var s = $"Đã thêm {n} tin nhắn từ {p.Chats ?? 0} cuộc trò chuyện.";
                if (p.Skipped is > 0 and var k) s += $" {k} cuộc trò chuyện không xác định được người nhận nên được bỏ qua.";
                return s;
            case PhoneSyncProgress.SyncState.Failed:
                var resume = (p.ChatsDone ?? 0) > 0 ? $" Đã lưu {p.ChatsDone ?? 0}/{p.Chats ?? 0} cuộc trò chuyện; bấm Tiếp tục để đồng bộ phần còn lại." : "";
                return (Failure(p) ?? "Đã có lỗi xảy ra.") + resume;
            default: return "Tải lịch sử trò chuyện từ Zalo trên điện thoại về máy này. Điện thoại cần mở Zalo và có mạng.";
        }
    }

    private static string? Failure(PhoneSyncProgress p) => p.Code switch
    {
        "rejected" => "Yêu cầu đã bị từ chối trên điện thoại.",
        "busy" => p.Error != null ? $"Điện thoại đang bận ({p.Error})." : "Điện thoại đang bận, thử lại sau.",
        "timeout" => "Điện thoại không phản hồi. Mở Zalo trên điện thoại rồi thử lại.",
        "disconnected" => "Mất kết nối Zalo. Thử lại khi đã kết nối.",
        "unavailable" => "Cần cài Zalo PC trên máy này: ChatSpace dùng thư viện giải mã của Zalo PC.",
        _ => p.Error,
    };

    private static Control Action(string title, SColor tint, Action run) =>
        Button(run, Text(title).Font(ZL.F(13, Weight.Semibold)).Foreground(tint));
}

public static class ZaloChatRow
{
    public static Control Make(PlatformSession session, Chat chat, bool selected)
    {
        var unread = chat.HasUnread;
        Control? badge = chat.Unread > 0
            ? Text(chat.Unread > 5 ? "5+" : $"{chat.Unread}").Font(ZL.F(11, Weight.Bold)).Foreground(SColor.White)
                .PaddingH(4).Frame(minWidth: 18, minHeight: 18)
                .Background(Capsule().Fill(chat.IsMuted ? ZL.Secondary : ZL.Badge))
            : chat.MarkedUnread == true
                ? Circle().Fill(chat.IsMuted ? ZL.Secondary : ZL.Badge).Frame(width: 10, height: 10)
                : null;
        var row = HStack(12,
                ZL.Avatar(chat.Id, chat.Title, chat.Avatar, 48, chat.IsGroup),
                VStack(HAlign.Leading, 5,
                    HStack(4,
                        chat.IsGroup ? ZL.Glyph(ZaloGlyphs.Group, 15).Foreground(ZL.Secondary) : null,
                        Text(chat.Title).Font(ZL.F(15, unread ? Weight.Semibold : Weight.Regular)).Foreground(ZL.TextColor).LineLimit(1),
                        Spacer(4),
                        Text(ZL.ListTime(chat.Date)).Font(ZL.F(12)).Foreground(ZL.Secondary)),
                    HStack(4,
                        Text(Preview(session, chat)).Font(ZL.F(13.5, unread ? Weight.Semibold : Weight.Regular))
                            .Foreground(unread ? ZL.TextColor : ZL.Secondary).LineLimit(1),
                        Spacer(4),
                        chat.IsMuted ? ZL.Glyph(ZaloGlyphs.BellSlash, 14).Foreground(ZL.Secondary) : null,
                        chat.IsPinned ? ZL.Glyph(ZaloGlyphs.PinFilled, 13).Foreground(ZL.Secondary) : null,
                        badge)))
            .PaddingH(16).Frame(height: 74);
        return (selected ? row.Background(ZL.SelectedRow) : row.HoverBackground(SColor.White, ZL.HoverRow))
            .ContentShape()
            .OnAppear(() => session.RequestAvatar(chat));
    }

    private static string Preview(PlatformSession session, Chat chat)
    {
        if (session.Typing.GetValueOrDefault(chat.Id) is { Count: > 0 } t)
            return chat.IsGroup ? $"{t[0].Name} đang soạn tin…" : "Đang soạn tin…";
        if (chat.LastMessage is not { } m) return "";
        if (m.IsDeleted) return m.FromMe ? "Bạn: Tin nhắn đã được thu hồi" : "Tin nhắn đã được thu hồi";
        var who = m.FromMe ? "Bạn: " : chat.Kind == ChatKind.Group && m.SenderName is { } n ? n.Split(' ')[^1] + ": " : "";
        var body = m.Kind switch
        {
            MessageKind.Image => "[Hình ảnh]",
            MessageKind.Video => "[Video]",
            MessageKind.Sticker => "[Sticker]",
            MessageKind.Voice or MessageKind.Audio => "[Tin nhắn thoại]",
            MessageKind.File => "[File] " + (m.Media?.FileName ?? ""),
            MessageKind.Link => "[Link] " + (m.Text ?? ""),
            MessageKind.System => null,
            _ => m.Text ?? "",
        };
        return body == null ? m.Text ?? "" : who + body;
    }
}

// MARK: Conversation

public sealed class ZaloConversation : Decorator, IFlexible
{
    public ZaloConversation(PlatformSession session, string chatId)
    {
        Chat? Current() => session.Chats.FirstOrDefault(c => c.Id == chatId);
        Child = new CompactAware(compact =>
        {
            var initial = Tracker.Untracked(Current) ?? new Chat { Id = chatId };
            var scroll = new ChatScroll(session, chatId, row =>
                {
                    var chat = Tracker.Untracked(Current) ?? initial;
                    var gap = row.ShowTimeGap
                        ? Text(ZL.Separator(row.Message.Date)).Font(ZL.F(12)).Foreground(SColor.White)
                            .PaddingH(10).PaddingV(3)
                            .Background(Capsule().Fill(SColor.Hex(0x8A99AD).Opacity(0.75)))
                            .FillWidth().PaddingV(12)
                        : null;
                    return VStack(0, gap,
                        ZaloMessageRow.Make(session, chat, row, row.IsLastOutgoing, compact)
                            .PaddingTop(row.IsFirstInGroup && !row.ShowTimeGap ? 10 : 3));
                }, 0, new Thickness(0, 12, 0, 12));
            return VStack(0,
                View(() => Header(session, Current() ?? initial, compact)),
                ZStack(ColorView(ZL.ChatBackground), scroll).FillHeight(),
                View(() => ZaloComposer.Make(session, Current() ?? initial, compact)
                    .WithMentionSuggestions(session, Current() ?? initial, ZL.Blue)));
        });
    }

    public bool IsFlexible(Orientation axis) => true;

    private static Control Header(PlatformSession session, Chat chat, bool compact)
    {
        var showSearch = Remember(false);
        var showInfo = Remember(false);
        return HStack(compact ? 10 : 12,
                ZL.Avatar(chat.Id, chat.Title, chat.Avatar, compact ? 38 : 48, chat.IsGroup),
                VStack(HAlign.Leading, 3,
                    Text(chat.Title).Font(ZL.F(compact ? 15 : 17, Weight.Semibold)).Foreground(ZL.TextColor).LineLimit(1),
                    Subtitle(session, chat).LineLimit(1)),
                Spacer(8),
                HStack(compact ? 12 : 18,
                        ZL.Glyph(ZaloGlyphs.AddGroupMember, 22).Tool(chat.IsGroup ? "Thêm thành viên" : "Tạo nhóm trò chuyện",
                            () => ChatTools.NativeOnly(Platform.Zalo, chat.IsGroup ? "Thêm thành viên" : "Tạo nhóm")),
                        ZL.Glyph(ZaloGlyphs.Search, 20).ToolPopover("Tìm tin nhắn", showSearch, done => InChatSearch.Make(session, chat.Id, done)),
                        ZL.Glyph(ZaloGlyphs.Video, 22).Tool("Cuộc gọi video", () => ChatTools.Call(Platform.Zalo, video: true)),
                        ZL.Glyph(ZaloGlyphs.Sidebar, 20).ToolPopover("Thông tin hội thoại", showInfo, _ => ChatInfoPanel.Make(session, chat)))
                    .Font(ZL.F(16)).Foreground(ZL.TextColor))
            .PaddingH(compact ? 12 : 16).Frame(height: compact ? 56 : 68).PaddingTop(compact ? 0 : 4)
            .Background(SColor.White)
            .Overlay(Alignment.Bottom, Rectangle().Fill(ZL.Divider).Frame(height: 1));
    }

    private static Control Subtitle(PlatformSession session, Chat chat)
    {
        if (session.Typing.GetValueOrDefault(chat.Id) is { Count: > 0 } t)
            return Text(chat.IsGroup ? $"{t[0].Name} đang soạn tin…" : "Đang soạn tin…").Font(ZL.F(13)).Foreground(ZL.Blue);
        if (chat.IsGroup)
            return HStack(4, Image("person"), Text(chat.MemberCount is { } n ? $"{n} thành viên" : "Nhóm")).Font(ZL.F(13)).Foreground(ZL.Secondary);
        if (chat.Online == true)
            return HStack(4, Circle().Fill(SColor.Hex(0x2DB971)).Frame(width: 8, height: 8), Text("Vừa truy cập").Font(ZL.F(13)).Foreground(ZL.Secondary));
        if (chat.LastSeen is { } ls)
            return Text($"Truy cập {ZL.ListTime(Time.Local(ls))} trước").Font(ZL.F(13)).Foreground(ZL.Secondary);
        return Text("Bấm để xem thông tin").Font(ZL.F(13)).Foreground(ZL.Secondary);
    }
}

public static class ZaloMessageRow
{
    public static Control Make(PlatformSession session, Chat chat, MessageRow row, bool showStatus, bool compact)
    {
        var m = row.Message;
        if (m.Kind == MessageKind.System)
            return Text(m.Text ?? "").Font(ZL.F(12.5)).Foreground(ZL.Secondary).MultilineAlignment(TextAlignment.Center).LineLimit(null)
                .PaddingH(10).PaddingV(4)
                .Background(Capsule().Fill(SColor.White.Opacity(0.85)))
                .FillWidth().PaddingV(6);

        var hovered = Remember(false);
        var picking = Remember(false);
        bool Active() => hovered.Value || picking.Value;
        var avatarSize = compact ? 32 : 40;
        Control HoverActions() => Button(() => session.StartReply(m),
                ZL.Glyph(ZaloGlyphs.QuoteLine, 15).Foreground(ZL.Secondary)
                    .Frame(width: 26, height: 26).Background(Circle().Fill(SColor.White)))
            .Help("Trả lời").PaddingTop(6).VisibleWhen(Active);

        var bubble = ZaloBubble.Make(session, chat, row, Active, picking, showStatus && m.FromMe && m.Status != null, compact)
            .MessageContextMenu(session, m, MenuLabels.Vietnamese, ReactionSets.Zalo);
        var line = HStack(VAlign.Top, compact ? 8 : 10,
            m.FromMe ? Spacer(ChatMetrics.Gap(120, compact)) : null,
            !m.FromMe
                ? row.IsFirstInGroup
                    ? ZL.Avatar(m.SenderId, m.SenderName ?? chat.Title, m.SenderAvatar ?? (chat.IsGroup ? null : chat.Avatar), avatarSize)
                    : Clear.Frame(width: avatarSize, height: 1)
                : null,
            m.FromMe ? HoverActions() : null,
            bubble,
            !m.FromMe ? HoverActions() : null,
            !m.FromMe ? Spacer(ChatMetrics.Gap(120, compact)) : null);
        Control? status = showStatus && m.FromMe && m.Status is { } s
            ? Text(StatusText(s)).Font(ZL.F(11)).Foreground(SColor.White)
                .PaddingH(8).PaddingV(2)
                .Background(Capsule().Fill(s == DeliveryStatus.Failed ? SColor.Red : SColor.Hex(0x8A99AD).Opacity(0.8)))
            : null;
        return VStack(m.FromMe ? HAlign.Trailing : HAlign.Leading, 4, line, status)
            .PaddingH(compact ? 10 : 16)
            .ContentShape()
            .TrackHover(hovered);
    }

    private static string StatusText(DeliveryStatus s) => s switch
    {
        DeliveryStatus.Pending => "Đang gửi",
        DeliveryStatus.Sent => "Đã gửi",
        DeliveryStatus.Delivered => "Đã nhận",
        DeliveryStatus.Read => "Đã xem",
        _ => "Gửi lỗi",
    };
}

public static class ZaloBubble
{
    public static Control Make(PlatformSession session, Chat chat, MessageRow row, Func<bool> hovered, State<bool> picking, bool reserveBadge, bool compact)
    {
        var m = row.Message;
        var mediaOnly = m.Kind is MessageKind.Image or MessageKind.Video or MessageKind.Gif && string.IsNullOrEmpty(m.Text);
        var isSticker = m.Kind == MessageKind.Sticker || (m.Kind == MessageKind.Text && Jumbo.IsJumboEmoji(m.Text));

        Control body;
        if (isSticker && !m.IsDeleted)
            body = m.Kind == MessageKind.Sticker ? MediaContent.Make(session, m, maxWidth: 130) : Text(m.Text ?? "").Font(ZL.F(44));
        else if (mediaOnly && !m.IsDeleted)
            body = MediaContent.Make(session, m, maxWidth: 340, cornerRadius: 8);
        else
        {
            var col = new BubbleColumn(4,
                !m.FromMe && chat.IsGroup && row.IsFirstInGroup ? Text(m.SenderName ?? "").Font(ZL.F(12.5)).Foreground(ZL.Secondary) : null,
                m.ReplyTo is { } r && !m.IsDeleted ? BubbleColumn.Fill(Reply(session, m, r)) : null);
            foreach (var c in Content(session, m, compact)) col.Children.Add(c);
            col.Children.Add(View(() => Translator.Shared.IsShown(m) ? TranslationQuote.Make(m, ZL.Blue, ZL.TextColor.Opacity(0.75)) : null));
            if (row.IsLastInGroup || m.Kind != MessageKind.Text) col.Children.Add(Text(Fmt.Time(m.Date)).Font(ZL.F(11.5)).Foreground(ZL.Secondary));
            body = col.Padding(12)
                .Background(RoundedRectangle(8, true).Fill(m.FromMe ? ZL.OutBubble : ZL.InBubble)
                    .Overlay(RoundedRectangle(8, true).Stroke(m.FromMe ? ZL.OutBorder : SColor.Hex(0xDDE2E8), 0.6)));
        }

        var canReact = !m.IsDeleted && session.Can("reactions");
        Control? translate = Translator.CanTranslate(m)
            ? TranslateToggle.Make(m, on => Image("translate").Font(11, Weight.Medium).Foreground(on ? ZL.Blue : ZL.Secondary)
                    .Frame(width: 22, height: 22).Background(Circle().Fill(SColor.White).Shadow(SColor.Black.Opacity(0.15), 1)))
                .VisibleWhen(hovered)
            : null;
        Control? reaction = null;
        if (m.Reactions is { Count: > 0 } rs)
        {
            var total = rs.Sum(x => x.Count);
            var chips = rs.Take(3).Select(x => (Control?)Text(x.Emoji).Font(ZL.F(12))).ToList();
            chips.Add(Text($"{total}").Font(ZL.F(11)).Foreground(ZL.Secondary).PaddingLeading(1));
            reaction = Button(() => { if (canReact) picking.Value = true; },
                    HStack(2, chips.ToArray()).PaddingH(6).Frame(height: 20)
                        .Background(Capsule().Fill(SColor.White).Shadow(SColor.Black.Opacity(0.15), 1, 0, 0.5)))
                .Help("Bày tỏ cảm xúc")
                .WithReactionPicker(picking, session, m, ReactionSets.Zalo);
        }
        else if (canReact)
        {
            reaction = Button(() => picking.Value = true,
                    ZL.Glyph(ZaloGlyphs.Like, 13).Foreground(ZL.Secondary)
                        .Frame(width: 22, height: 22).Background(Circle().Fill(SColor.White).Shadow(SColor.Black.Opacity(0.15), 1)))
                .Help("Bày tỏ cảm xúc")
                .WithReactionPicker(picking, session, m, ReactionSets.Zalo)
                .VisibleWhen(hovered);
        }
        var badges = HStack(4, translate, reaction);
        return new CapWidth(520, body
            .Overlay(Alignment.BottomTrailing, new BottomHang(badges, 8))
            .PaddingBottom(m.HasReactions || reserveBadge ? 11 : 0));
    }

    private static IEnumerable<Control> Content(PlatformSession session, Message m, bool compact)
    {
        if (m.IsDeleted)
        {
            yield return Text("Tin nhắn đã được thu hồi").Font(ZL.F(15)).Foreground(ZL.Secondary);
            yield break;
        }
        switch (m.Kind)
        {
            case MessageKind.Image or MessageKind.Video or MessageKind.Gif:
                yield return MediaContent.Make(session, m, maxWidth: 320, cornerRadius: 6);
                if (m.Text is { Length: > 0 } t) yield return TextBody(t);
                break;
            case MessageKind.File or MessageKind.Audio or MessageKind.Voice:
                yield return FileContent.Make(session, m, ZL.Blue, titleColor: ZL.TextColor, subtitleColor: ZL.Secondary);
                break;
            case MessageKind.Link:
                yield return VStack(HAlign.Leading, 6,
                    (m.Media?.Thumbnail ?? m.Media?.Url) is { } thumb
                        ? new RemoteImage(thumb, () => Clear, maxPixel: 640).Frame(maxWidth: compact ? ChatMetrics.CompactMediaWidth : 320, maxHeight: 170).Clipped()
                        : null,
                    TextBody(m.Text ?? ""));
                break;
            default:
                yield return TextBody(m.Text ?? "");
                break;
        }
    }

    private static Control TextBody(string t) =>
        new LinkText(LinkSpans.Spans(t, ZL.Blue)) { OwnFont = ZL.F(15) }.Foreground(ZL.TextColor);

    private static Control Reply(PlatformSession session, Message m, ReplyRef r) =>
        HStack(8,
                Rectangle().Fill(ZL.Blue).Frame(width: 2),
                VStack(HAlign.Leading, 2,
                        Text(r.SenderName ?? "Tin nhắn").Font(ZL.F(13, Weight.Semibold)).Foreground(ZL.TextColor).LineLimit(1),
                        Text(ReplyPreview(r)).Font(ZL.F(13)).Foreground(ZL.Secondary).LineLimit(2))
                    .PaddingV(6).PaddingTrailing(10))
            .Frame(minWidth: 120, alignment: Alignment.Leading)
            .Background(RoundedRectangle(4).Fill(m.FromMe ? SColor.Hex(0xC7E0FF).Opacity(0.6) : SColor.Hex(0xEEF0F1)))
            .ClipRounded(4)
            .OnTap(() =>
            {
                session.ScrollTarget = session.ScrollTarget.Remove(m.ChatId);
                Dispatcher.UIThread.Post(() => session.ScrollTarget = session.ScrollTarget.SetItem(m.ChatId, r.Id));
            });

    private static string ReplyPreview(ReplyRef r)
    {
        if (r.Text is { Length: > 0 } t) return t;
        return r.Kind switch
        {
            MessageKind.Image or MessageKind.Gif => "[Hình ảnh]",
            MessageKind.Video => "[Video]",
            MessageKind.Sticker => "[Sticker]",
            MessageKind.File => "[File]",
            MessageKind.Voice or MessageKind.Audio => "[Tin nhắn thoại]",
            MessageKind.Link => "[Link]",
            _ => "[Tin nhắn]",
        };
    }
}

/// Hangs its child so the child's vertical center sits on the host's bottom edge, `inset` from the trailing edge.
public sealed class BottomHang : Decorator
{
    private readonly double _inset;
    public BottomHang(Control child, double inset) { Child = child; _inset = inset; }

    protected override Size MeasureOverride(Size available)
    {
        Child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return default;
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child is { } c)
        {
            var host = Parent is Control p ? p.Bounds.Size : final;
            var s = c.DesiredSize;
            c.Arrange(new Rect(host.Width - _inset - s.Width - Bounds.X, host.Height - s.Height / 2 - Bounds.Y, s.Width, s.Height));
        }
        return final;
    }
}

public static class ZaloComposer
{
    public static Control Make(PlatformSession session, Chat chat, bool compact)
    {
        var showMore = Remember(false);
        var r = new ChatRef(Platform.Zalo, chat.Id);
        var icon = compact ? 19.0 : 22.0;
        var draft = Composer.Draft(session, chat.Id);

        Control MoreItem(string title, string symbol, Action done, Action action) =>
            Button(() => { done(); action(); },
                HStack(8, Image(symbol), Text(title)).Font(ZL.F(14)).FillWidth(Alignment.Leading)
                    .PaddingH(10).Frame(height: 32).ContentShape());

        var toolbar = HStack(compact ? 6 : 8,
                ZL.Glyph(ZaloGlyphs.Sticker, icon).Tool("Gửi Sticker", Composer.Emoji),
                Button(() => ChatTools.Attach(session, chat.Id), ZL.Glyph(ZaloGlyphs.Photo, icon)),
                Button(() => ChatTools.Attach(session, chat.Id), ZL.Glyph(ZaloGlyphs.Attach, icon)),
                !compact ? ZL.Glyph(ZaloGlyphs.Screenshot, icon).Tool("Chụp màn hình và gửi", () => ChatTools.Screenshot(session, chat.Id)) : null,
                !compact ? ZL.Glyph(ZaloGlyphs.Namecard, icon).Tool("Gửi danh thiếp", () => ChatTools.NativeOnly(Platform.Zalo, "Gửi danh thiếp")) : null,
                !compact ? ZL.Glyph(ZaloGlyphs.TextFormat, icon).Tool("Định dạng tin nhắn", () => ChatTools.NativeOnly(Platform.Zalo, "Định dạng tin nhắn")) : null,
                OutgoingLanguageButton.Make(session, chat.Id, ZL.Blue, ZL.TextColor, icon - 4),
                ZL.Glyph(ZaloGlyphs.MoreH, icon).ToolPopover("Thêm", showMore, done => VStack(HAlign.Leading, 2,
                        MoreItem("Ghi âm tin nhắn thoại", "mic", done, () => VoiceRecorder.Shared.Toggle(r)),
                        MoreItem("Gửi tệp", "paperclip", done, () => ChatTools.Attach(session, chat.Id)),
                        MoreItem("Chụp màn hình", "camera.viewfinder", done, () => ChatTools.Screenshot(session, chat.Id)))
                    .Padding(6).Frame(width: 220)),
                View(() => VoiceRecorder.Shared.IsRecording(r) ? VoiceTools.VoiceTool(() => Clear.Frame(width: 1), session, chat.Id) : null),
                Spacer())
            .Font(ZL.F(compact ? 15 : 17)).Foreground(ZL.TextColor)
            .PaddingH(compact ? 10 : 16).Frame(height: compact ? 36 : 44)
            .Overlay(Alignment.Top, Rectangle().Fill(ZL.Divider).Frame(height: 1))
            .Overlay(Alignment.Bottom, Rectangle().Fill(ZL.Divider).Frame(height: 1));

        var replyBar = View(() =>
        {
            if (session.ReplyingTo.GetValueOrDefault(chat.Id) is not { } rp) return null;
            return HStack(8,
                    Rectangle().Fill(ZL.Blue).Frame(width: 2),
                    VStack(HAlign.Leading, 2,
                        HStack(6, ZL.Glyph(ZaloGlyphs.QuoteLine, 14),
                                LinkText.Markdown($"Trả lời **{(rp.FromMe ? "bạn" : rp.SenderName ?? chat.Title)}**", ZL.F(13, Weight.Bold)))
                            .Font(ZL.F(13)).Foreground(ZL.TextColor),
                        Text(rp.Preview()).Font(ZL.F(13)).Foreground(ZL.Secondary).LineLimit(1)),
                    Spacer(),
                    Button(() => session.ReplyingTo = session.ReplyingTo.Remove(chat.Id), ZL.Glyph(ZaloGlyphs.Close, 16).Foreground(ZL.Secondary)))
                .Padding(8)
                .Background(RoundedRectangle(6).Fill(SColor.Hex(0xF3F5F6)))
                .FixedSize(horizontal: false, vertical: true)
                .PaddingH(compact ? 10 : 16).PaddingTop(8);
        });

        var editor = Composer.Editor(chat.Id, draft, $"Nhập @, tin nhắn tới {chat.Title}",
            onSubmit: () => session.Send(session.Draft(chat.Id), chat.Id),
            font: ZL.F(15), textColor: ZL.TextColor, placeholderColor: ZL.Secondary,
            maxHeight: compact ? 220 : 140,
            onTypingChange: on => session.SetTyping(chat.Id, on),
            mentionSession: session, mentionChatId: chat.IsGroup ? chat.Id : null,
            onFiles: files => ChatTools.SendPasted(files, session, chat.Id));

        var send = View(() => session.Draft(chat.Id).Length == 0 && !session.Attachments.ContainsKey(chat.Id)
            ? Button(() => session.Send("👍", chat.Id), Text("👍").Font(ZL.F(22))).PaddingBottom(9)
            : Button(() => session.Send(session.Draft(chat.Id), chat.Id), Text("GỬI").Font(ZL.F(14, Weight.Bold)).Foreground(ZL.Blue)).PaddingBottom(13));

        var input = HStack(VAlign.Bottom, compact ? 10 : 12,
                editor.PaddingV(compact ? 8 : 10).ComposerTapArea(),
                ZL.Glyph(ZaloGlyphs.EmojiLine, icon).Foreground(ZL.TextColor).Tool("Biểu tượng cảm xúc", Composer.Emoji).PaddingBottom(5),
                send)
            .PaddingH(compact ? 10 : 16);

        return VStack(0,
                toolbar,
                replyBar,
                TranslationNotice.Make(session, chat.Id),
                MathSendPreview.Make(session, chat.Id, ZL.Blue),
                ComposerAttachments.Make(session, chat.Id, ZL.Blue),
                input)
            .Background(SColor.White);
    }
}

// MARK: Login

public static class ZaloLogin
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        Control logo = NativeAssets.Shared.ZaloImage("logo-new") is { } img
            ? Bitmap(img, ContentMode.Fit).Frame(width: 114, height: 114 * img.PixelSize.Height / (double)img.PixelSize.Width).PaddingBottom(6)
            : Text("Zalo").Font(ZL.F(56, Weight.Heavy)).Foreground(ZL.Blue);
        Control qr = session.QrImage is { } q
            ? new BitmapView(q) { Smooth = false, Mode = ContentMode.Stretch }.Frame(width: 210, height: 210)
                .Overlay(QRExpiryOverlay.Make(session, ZL.Blue, "Mã QR hết hạn\nLấy mã mới"))
            : new ProgressView();
        var card = VStack(0,
                Text("Đăng nhập qua mã QR").Font(ZL.F(15, Weight.Semibold)).Foreground(ZL.TextColor)
                    .FillWidth().Frame(height: 52)
                    .Overlay(Alignment.Bottom, Rectangle().Fill(ZL.Divider).Frame(height: 1)),
                VStack(18,
                        ZStack(RoundedRectangle(16).Stroke(ZL.Divider, 1).Frame(width: 240, height: 240), qr),
                        Text("Chỉ dùng để đăng nhập").Font(ZL.F(14)).Foreground(ZL.Blue),
                        Text("Zalo trên máy tính").Font(ZL.F(14)).Foreground(ZL.TextColor.Opacity(0.7)).Padding(-14, 0, 0, 0),
                        Text("Mở ứng dụng Zalo trên điện thoại, chọn biểu tượng quét mã QR để đăng nhập.")
                            .Font(ZL.F(13)).Foreground(ZL.Secondary).MultilineAlignment(TextAlignment.Center).LineLimit(null),
                        session.AuthError is { } e ? Text(e).Font(ZL.F(13)).Foreground(SColor.Red).MultilineAlignment(TextAlignment.Center).LineLimit(null) : null,
                        session.AuthError != null ? Button(() => session.RefreshQR(), Text("Lấy mã mới").Foreground(ZL.Blue)) : null)
                    .Padding(28))
            .Frame(width: 400)
            .Background(RoundedRectangle(8).Fill(SColor.White).Shadow(SColor.Hex(0x1B60C5).Opacity(0.12), 10, 0, 4));
        return VStack(10,
                Spacer(),
                logo,
                Text("Đăng nhập tài khoản Zalo\nđể kết nối với ứng dụng Zalo PC").MultilineAlignment(TextAlignment.Center).LineLimit(null)
                    .Font(ZL.F(17)).Foreground(ZL.TextColor.Opacity(0.85)).PaddingBottom(16),
                card,
                Spacer())
            .Fill()
            .Background(SColor.Hex(0xE8F3FF))
            .OnAppear(session.EnsureQR)
            .OnDisappear(session.StopWantingQR);
    });
}
