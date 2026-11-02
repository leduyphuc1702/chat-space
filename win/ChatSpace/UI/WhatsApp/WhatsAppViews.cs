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
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace ChatSpace;

/// WhatsApp Desktop (light) look, Vietnamese strings as in the official localization.
public static class WA
{
    public static readonly SColor Green = SColor.Hex(0x1DAA61);
    public static readonly SColor Teal = SColor.Hex(0x008069);
    public static readonly SColor OutBubble = SColor.Hex(0xD9FDD3);
    public static readonly SColor InBubble = SColor.White;
    public static readonly SColor TextColor = SColor.Hex(0x111B21);
    public static readonly SColor Secondary = SColor.Hex(0x667781);
    public static readonly SColor Icon = SColor.Hex(0x54656F);
    public static readonly SColor Panel = SColor.Hex(0xF0F2F5);
    public static readonly SColor Separator = SColor.Hex(0xE9EDEF);
    public static readonly SColor BlueTick = SColor.Hex(0x53BDEB);
    public static readonly SColor ChatBackground = SColor.Hex(0xEFEAE2);
    public static readonly SColor ChipSelected = SColor.Hex(0xD9FDD3);
    public static readonly SColor ChipSelectedText = SColor.Hex(0x15603E);
    /// `Color(hex: 0x8696A0)`: muted/pinned glyphs, the composer placeholder, the encryption footer.
    public static readonly SColor Muted = SColor.Hex(0x8696A0);
    /// `Color(hex: 0x41525D)`: the big titles of the placeholder and login screens.
    public static readonly SColor Title = SColor.Hex(0x41525D);
    /// `Color(hex: 0x0B141A)`: the 1 pt shadow under bubbles and chips.
    public static readonly SColor BubbleShadow = SColor.Hex(0x0B141A);

    public static readonly uint[] NameColors = [0xE542A3, 0x1F7AEC, 0xD3A01B, 0x02A698, 0xFA6533, 0x7F66FF, 0x00A884, 0xC4532D];

    public static SColor NameColor(string id) => SColor.Hex(NameColors[id.StableHash() % NameColors.Length]);

    private static readonly CultureInfo Vi = new("vi-VN");

    public static string DayTitle(DateTime d) =>
        MessageGrouping.DayTitle(d, "HÔM NAY", "HÔM QUA", Vi, "dd/MM").ToUpperInvariant();

    public static string ListTime(DateTime d)
    {
        if (d.Date == DateTime.Today) return Fmt.Time(d);
        if (d.Date == DateTime.Today.AddDays(-1)) return "Hôm qua";
        var days = (DateTime.Now - d).Days;
        return d.ToString(days < 7 ? "dddd" : "dd/MM/yyyy", Vi);
    }

    /// A control can only have one parent, so the stand-in wallpaper is made fresh each time.
    public static DoodleWallpaper FallbackWallpaper() => new(
        ChatBackground,
        SColor.Hex(0xC9BFB1).Opacity(0.32),
        ["camera", "music.note", "heart", "gift", "phone", "cloud", "cup.and.saucer", "bicycle", "umbrella", "star", "moon", "house", "headphones", "lightbulb", "pencil", "airplane"],
        56, 18);
}

public static class WAMods
{
    /// `.opacity(show ? 1 : 0).allowsHitTesting(show)`: hidden but keeping its slot in the layout.
    public static T Slot<T>(this T c, Func<bool> show) where T : Control
    {
        Watch? w = null;
        void Apply(bool s) { c.Opacity = s ? 1 : 0; c.IsHitTestVisible = s; }
        Apply(Tracker.Untracked(show));
        c.AttachedToVisualTree += (_, _) => w ??= Watch.Effect(() => Apply(show()));
        c.DetachedFromVisualTree += (_, _) => { w?.Dispose(); w = null; };
        return c;
    }
}

/// `.defaultScrollAnchor(.bottom)` for content shorter than the viewport: a spacer that takes whatever height the
/// enclosing ScrollViewer has left over, so the messages sit at the bottom. Place it first inside the scrolled content.
public sealed class WABottomAnchor : Control
{
    private ScrollViewer? _sv;
    private double _height;

    protected override Size MeasureOverride(Size available) => new(0, _height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _sv = this.FindAncestorOfType<ScrollViewer>();
        LayoutUpdated += Update;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= Update;
        _sv = null;
    }

    private void Update(object? sender, EventArgs e)
    {
        // The extent is never smaller than the viewport, so measure the content itself.
        if (_sv is not { Content: Control content } sv || sv.Viewport.Height <= 0) return;
        var others = content.DesiredSize.Height - _height;
        var want = Math.Max(0, Math.Floor(sv.Viewport.Height - others));
        if (Math.Abs(want - _height) < 0.5) return;
        _height = want;
        // The size change does not reach the stacks above on its own, so invalidate up to the scrolled content.
        for (Visual? v = this; v != null && v != sv; v = v.GetVisualParent())
            (v as Layoutable)?.InvalidateMeasure();
    }
}

/// `.frame(maxWidth: N, alignment:)` with a finite N. Takes min(offered, N) like SwiftUI, but unlike Kit's FrameBox it is
/// not flexible, so a stack never stretches it past N when its content (wrapping text) is flexible.
public sealed class WAMaxWidth : Decorator, IFlexible
{
    private readonly double _max;
    private readonly HAlign _align;

    public WAMaxWidth(double max, Control child, HAlign align = HAlign.Center)
    {
        _max = max;
        _align = align;
        Child = child;
    }

    public bool IsFlexible(Orientation axis) => false;

    protected override Size MeasureOverride(Size available)
    {
        var w = Math.Min(available.Width, _max);
        if (Child == null) return new Size(w, 0);
        Child.Measure(new Size(w, available.Height));
        return new Size(w, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child == null) return finalSize;
        var cw = Flex.Of(Child, Orientation.Horizontal) ? finalSize.Width : Math.Min(Child.DesiredSize.Width, finalSize.Width);
        Child.Arrange(new Rect(Alignment.Place(_align, finalSize.Width, cw), 0, cw, finalSize.Height));
        return finalSize;
    }
}

/// `Image(nsImage:).resizable(resizingMode: .tile)` over a fill: the tile is drawn at its natural size in points
/// from the top-leading corner, clipped to the bounds.
public sealed class WATile : Control, IFlexible
{
    private readonly AvBitmap _tile;
    private readonly SColor _background;
    private readonly double _opacity;
    private readonly Size _size;

    public WATile(AvBitmap tile, Size size, SColor background, double opacity)
    {
        _tile = tile;
        _size = size;
        _background = background;
        _opacity = opacity;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public bool IsFlexible(Orientation axis) => true;

    protected override Size MeasureOverride(Size a) =>
        new(double.IsInfinity(a.Width) ? 0 : a.Width, double.IsInfinity(a.Height) ? 0 : a.Height);

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(new ImmutableSolidColorBrush(_background.Resolve(Scheme.Light)), b);
        if (_size.Width <= 0 || _size.Height <= 0) return;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        var src = new Rect(0, 0, _tile.PixelSize.Width, _tile.PixelSize.Height);
        using (ctx.PushClip(b))
        using (ctx.PushOpacity(_opacity))
            for (double y = 0; y < b.Height; y += _size.Height)
                for (double x = 0; x < b.Width; x += _size.Width)
                    ctx.DrawImage(_tile, src, new Rect(x, y, _size.Width, _size.Height));
    }
}

/// WhatsApp's own doodle (DoodleOverlay from the installed app) over its beige, or a drawn stand-in.
public static class WhatsAppWallpaper
{
    public static Control Make()
    {
        if (NativeAssets.Shared.Image("whatsappShared/DoodleOverlay.png") is { } doodle)
        {
            var (w, h) = NativeImage.NaturalSize("whatsappShared", "DoodleOverlay") ?? (doodle.PixelSize.Width, doodle.PixelSize.Height);
            return new WATile(doodle, new Size(w, h), WA.ChatBackground, 0.9);
        }
        return WA.FallbackWallpaper();
    }
}

/// Icon from the installed WhatsApp (main or SharedModules catalog), tinted like the original.
public static class WAIcon
{
    public static Control Make(string name, string fallback, double size = 20, bool shared = false)
    {
        var catalog = shared ? "whatsappShared" : "whatsapp";
        if (NativeImage.Exists(catalog, name)) return new NativeImage(catalog, name) { Size = size };
        return Image(fallback).Font(size * 0.85);
    }
}

public static class WhatsAppAvatar
{
    public static Control Make(string? source, bool group = false, double size = 49) =>
        AvatarView.Make(source, size, () =>
        {
            var name = group ? "GroupChatPlaceholderV2" : "PersonalChatPlaceholderV2";
            Control inner = NativeImage.Exists("whatsappShared", name)
                ? new NativeImage("whatsappShared", name) { Size = size }
                : Image(group ? "person.2.fill" : "person.fill").Font(size * (group ? 0.4 : 0.55)).Foreground(SColor.White)
                    .Offset(0, group ? 0 : size * 0.08);
            return Circle().Fill(SColor.Hex(0xDFE5E7)).Overlay(inner).ClipCircle();
        });
}

public static class WhatsAppTicks
{
    public static Control Make(DeliveryStatus? status, double size = 11)
    {
        switch (status)
        {
            case DeliveryStatus.Pending:
                return WAIcon.Make("clock", "clock", size, shared: true).Foreground(WA.Secondary);
            case DeliveryStatus.Failed:
                return Image("exclamationmark.circle").Font(size).Foreground(SColor.Red);
            case DeliveryStatus.Sent:
                return WAIcon.Make("checkmark", "checkmark", size + 5).Foreground(WA.Secondary);
            default:
                if (NativeImage.Exists("whatsapp", "wds_ic_read"))
                    return WAIcon.Make("wds_ic_read", "checkmark", size + 7)
                        .Foreground(status == DeliveryStatus.Read ? WA.BlueTick : WA.Secondary);
                return ZStack(Image("checkmark").Offset(-2.5), Image("checkmark").Offset(2.5))
                    .Font(size - 1, Weight.Semibold)
                    .Foreground(status == DeliveryStatus.Read ? WA.BlueTick : WA.Secondary)
                    .Frame(width: size + 6);
        }
    }
}

public sealed class WhatsAppRootView : Reactive
{
    public WhatsAppRootView(PlatformSession s) : base(() => Build(s)) { }

    private static Control Build(PlatformSession session)
    {
        Control body;
        if (session.AuthState == AuthState.LoggedIn)
        {
            var conversation = View(() => session.SelectedChatId is { } id && Tracker.Untracked(() => session.SelectedChat) != null
                ? Keep(("conv", id), () => new WhatsAppConversation(session, id))
                : WhatsAppPlaceholder.Make());
            body = HStack(0,
                WhatsAppNavBar.Make(session),
                Rectangle().Fill(WA.Separator).Frame(width: 1),
                WhatsAppChatList.Make(session).Frame(width: 360),
                Rectangle().Fill(WA.Separator).Frame(width: 1),
                conversation);
        }
        else if (session.AuthState == AuthState.Connecting || (session.BridgeError != null && session.AuthState != AuthState.Error))
            body = BridgeStatusView.Make(session, WA.Teal);
        else
            body = WhatsAppLogin.Make(session);
        return body.Background(SColor.White).Scheme(Scheme.Light);
    }
}

public static class WhatsAppNavBar
{
    public static Control Make(PlatformSession session) => View(() =>
        VStack(6,
                Clear.Frame(height: 34),
                NavIcon("TabBarChatsActive", "message.fill", selected: true, badge: session.UnreadTotal),
                NavIcon("TabBarCalls", "phone"),
                NavIcon("TabBarUpdates", "circle.dashed"),
                NavIcon("TabBarCommunities", "person.3"),
                Spacer(),
                NavIcon("IconStar", "star"),
                NavIcon("ArchivedEntryIcon", "archivebox"),
                Rectangle().Fill(WA.Separator).Frame(width: 30, height: 1).PaddingV(4),
                NavIcon("TabBarSettings", "gearshape"),
                WhatsAppAvatar.Make(session.SelfInfo?.Avatar, size: 30).PaddingBottom(14).PaddingTop(4))
            .Frame(width: 60)
            .Background(SColor.Hex(0xF7F5F3)));

    private static Control NavIcon(string name, string fallback, bool selected = false, int badge = 0) =>
        WAIcon.Make(name, fallback, 24).Foreground(WA.Icon)
            .Frame(width: 40, height: 40)
            .Background(Circle().Fill(selected ? SColor.Black.Opacity(0.08) : SColor.Clear))
            .Overlay(Alignment.TopTrailing, badge > 0
                ? Text($"{badge}").Font(10, Weight.Bold).Foreground(SColor.White)
                    .PaddingH(4).Frame(minWidth: 17, minHeight: 17)
                    .Background(Capsule().Fill(WA.Green)).Offset(2, -2)
                : null);
}

public static class WhatsAppPlaceholder
{
    public static Control Make()
    {
        Control logo = NativeImage.Exists("whatsapp", "PlaceholderLogo")
            // `.opacity(0.6)` on a template image: Avalonia compounds Control.Opacity with the template draw (0.6 x 0.6),
            // so the alpha is baked into the tint instead (primary label colour 0.85 x 0.6).
            ? new NativeImage("whatsapp", "PlaceholderLogo") { Size = 168 }.Foreground(SColor.Black.Opacity(0.85 * 0.6))
            : Image("laptopcomputer.and.iphone").Font(90, Weight.UltraLight).Foreground(SColor.Hex(0xB5C4CB));
        return VStack(18,
                logo,
                Text("WhatsApp cho Windows").Font(30, Weight.Light).Foreground(WA.Title),
                Text("Gửi và nhận tin nhắn mà không cần giữ điện thoại trực tuyến.\nSử dụng WhatsApp trên tối đa 4 thiết bị liên kết và 1 điện thoại cùng lúc.")
                    .Font(14).MultilineAlignment(TextAlignment.Center).LineLimit(null).Foreground(WA.Secondary),
                Spacer().Frame(height: 30),
                HStack(5, WAIcon.Make("lock-e2ee", "lock.fill", 11), Text("Được mã hóa đầu cuối"))
                    .Font(13).Foreground(WA.Muted))
            .Fill()
            .Background(SColor.Hex(0xF0F2F5))
            .Overlay(Alignment.Bottom, Rectangle().Fill(WA.Green).Frame(height: 6));
    }
}

// MARK: Chat list

public static class WhatsAppChatList
{
    private static readonly string[] Filters = ["Tất cả", "Chưa đọc", "Yêu thích", "Nhóm"];

    public static Control Make(PlatformSession session) => View(() =>
    {
        var query = Remember("");
        var filter = Remember(0);

        var search = Keep("search", () => new Field("Tìm kiếm hoặc bắt đầu đoạn chat mới", query, font: SFont.System(14)));

        var header = HStack(
                Text("Đoạn chat").Font(22, Weight.Bold).Foreground(WA.TextColor),
                Spacer(),
                WAIcon.Make("IconNewChat", "square.and.pencil", 22, shared: true)
                    .Tool("Đoạn chat mới", () => ChatTools.NativeOnly(Platform.Whatsapp, "Bắt đầu đoạn chat mới")),
                WAIcon.Make("IconFilterLandscape", "line.3.horizontal.decrease", 20, shared: true)
                    .Tool("Đánh dấu tất cả đã đọc", session.MarkAllRead).PaddingLeading(6))
            .Foreground(WA.Icon)
            .PaddingH(16).PaddingTop(34).PaddingBottom(10);

        var searchRow = HStack(10,
                WAIcon.Make("ListSearchGlyphMask", "magnifyingglass", 13).Foreground(WA.Icon),
                search)
            .PaddingH(12).Frame(height: 35)
            .Background(RoundedRectangle(8).Fill(WA.Panel))
            .PaddingH(12);

        var chips = new List<Control?>();
        for (var i = 0; i < Filters.Length; i++)
        {
            var index = i;
            var on = filter.Value == i;
            chips.Add(Text(Filters[i]).Font(14).LineLimit(1).FixedSize()
                .Foreground(on ? WA.ChipSelectedText : WA.Icon)
                .PaddingH(12).PaddingV(6)
                .Background(Capsule().Fill(on ? WA.ChipSelected : WA.Panel))
                .OnTap(() => filter.Value = index));
        }
        chips.Add(Spacer());
        var chipRow = HStack(8, chips.ToArray()).PaddingH(12).PaddingV(10);

        Control? offline = session.Connection == "offline"
            ? HStack(12,
                    Image("wifi.slash").Font(20).Foreground(SColor.White)
                        .Frame(width: 44, height: 44).Background(Circle().Fill(SColor.Hex(0xFFD279))),
                    VStack(HAlign.Leading, null,
                        Text("Máy tính không có kết nối").Font(15).Foreground(WA.TextColor),
                        Text(session.ConnectionDetail ?? "Đang kết nối lại…").Font(13).Foreground(WA.Secondary)),
                    Spacer())
                .Padding(12).Background(SColor.Hex(0xFFF7D9))
            : null;

        var list = Filtered(session, query.Value, filter.Value);
        var selected = session.SelectedChatId;
        var rows = ScrollView(ForEach(() => list.Select(c => (c, c.Id == selected)).ToList(), x => x.c.Id, x =>
            WhatsAppChatRow.Make(session, x.c, x.Item2)
                .OnTap(() => session.Select(x.c.Id))
                .ChatContextMenu(session.Platform, x.c)));

        return VStack(0, header, searchRow, chipRow, offline, rows.FillHeight(Alignment.Top))
            .Background(SColor.White);
    });

    private static List<Chat> Filtered(PlatformSession session, string query, int filter) =>
        session.Chats.Where(c =>
            (query.Length == 0 || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
            (filter != 1 || c.HasUnread) && (filter != 3 || c.IsGroup) && (filter != 2 || c.IsPinned)).ToList();
}

public static class WhatsAppChatRow
{
    public static Control Make(PlatformSession session, Chat chat, bool selected)
    {
        var accent = chat.IsMuted ? WA.Muted : WA.Green;
        Control? badge = chat.Unread > 0
            ? Text($"{chat.Unread}").Font(12, Weight.Semibold).Foreground(SColor.White)
                .PaddingH(5).Frame(minWidth: 20, minHeight: 20)
                .Background(Capsule().Fill(accent))
            : chat.MarkedUnread == true
                ? Circle().Fill(accent).Frame(width: 20, height: 20)
                : null;

        var row = HStack(13,
                WhatsAppAvatar.Make(chat.Avatar, chat.IsGroup, 49),
                VStack(HAlign.Leading, 3,
                        HStack(VAlign.FirstBaseline, null,
                            Text(chat.Kind == ChatKind.Self ? $"{chat.Title} (Bạn)" : chat.Title)
                                .Font(16).Foreground(WA.TextColor).LineLimit(1),
                            Spacer(6),
                            Text(WA.ListTime(chat.Date)).Font(12)
                                .Foreground(chat.HasUnread && !chat.IsMuted ? WA.Green : WA.Secondary)),
                        HStack(4,
                            Preview(session, chat),
                            Spacer(4),
                            chat.IsMuted ? WAIcon.Make("MuteIndicatorV2", "speaker.slash.fill", 17, shared: true).Foreground(WA.Muted) : null,
                            chat.IsPinned ? WAIcon.Make("ChatCellPinned", "pin.fill", 17, shared: true).Foreground(WA.Muted) : null,
                            badge))
                    .PaddingV(12)
                    .Overlay(Alignment.Bottom, Rectangle().Fill(WA.Separator).Frame(height: 1)))
            .PaddingLeading(13).PaddingTrailing(15);

        return (selected ? row.Background(WA.Panel) : row.HoverBackground(SColor.White, SColor.Hex(0xF5F6F6)))
            .ContentShape()
            .OnAppear(() => session.RequestAvatar(chat));
    }

    private static Control? Preview(PlatformSession session, Chat chat)
    {
        if (session.Typing.GetValueOrDefault(chat.Id) is { Count: > 0 } t)
            return Text(chat.IsGroup ? $"{t[0].Name ?? ""} đang soạn tin…" : "đang soạn tin…")
                .Font(14).Foreground(WA.Green).LineLimit(1);
        if (chat.LastMessage is not { } m) return null;
        return HStack(3,
                m.FromMe ? WhatsAppTicks.Make(m.Status, 12) : null,
                chat.IsGroup && !m.FromMe && m.SenderName is { } n ? Text(n + ":").LineLimit(1) : null,
                m.IsDeleted ? Image("nosign").Font(12) : null,
                Text(m.IsDeleted
                        ? (m.FromMe ? "Bạn đã xóa tin nhắn này" : "Tin nhắn này đã bị xóa")
                        : m.Preview(photo: "📷 Ảnh", video: "🎥 Video", voice: "🎤 Tin nhắn thoại", sticker: "Nhãn dán", file: "📄 Tài liệu"))
                    .LineLimit(1))
            .Font(14).Foreground(WA.Secondary);
    }
}

// MARK: Conversation

public sealed class WhatsAppConversation : Decorator, IFlexible
{
    public WhatsAppConversation(PlatformSession session, string chatId)
    {
        Chat? Current() => session.Chats.FirstOrDefault(c => c.Id == chatId);
        Child = new CompactAware(compact =>
        {
            var initial = Tracker.Untracked(Current) ?? new Chat { Id = chatId };
            var scroll = new ChatScroll(session, chatId, row =>
                {
                    var chat = Tracker.Untracked(Current) ?? initial;
                    return VStack(0,
                        row.ShowDayHeader ? WhatsAppSystemChip.Make(WA.DayTitle(row.Message.Date)).PaddingV(8) : null,
                        WhatsAppMessageRow.Make(session, chat, row, compact).PaddingTop(row.IsFirstInGroup ? 10 : 2));
                }, spacing: 0, padding: new Thickness(0, 12, 0, 12),
                header: () => VStack(0,
                    new WABottomAnchor(),
                    session.ReachedStart.Contains(chatId)
                        ? Text("🔒 Tin nhắn và cuộc gọi được mã hóa đầu cuối. Chỉ những người tham gia đoạn chat này mới có thể đọc, nghe hoặc chia sẻ. Nhấp để tìm hiểu thêm.")
                            .Font(12.5).Foreground(WA.Icon).MultilineAlignment(TextAlignment.Center).LineLimit(null)
                            .PaddingH(12).PaddingV(6)
                            .Background(RoundedRectangle(7.5).Fill(SColor.Hex(0xFFEECD)))
                            .Frame(maxWidth: 500).PaddingBottom(8)
                        : null));
            return VStack(0,
                View(() => Header(session, Current() ?? initial, compact)),
                ZStack(WhatsAppWallpaper.Make(), scroll).FillHeight(),
                View(() => WhatsAppComposer.Make(session, Current() ?? initial, compact)
                    .WithMentionSuggestions(session, Current() ?? initial, WA.Teal)));
        });
    }

    public bool IsFlexible(Orientation axis) => true;

    private static Control Header(PlatformSession session, Chat chat, bool compact)
    {
        var showSearch = Remember(false);
        return HStack(14,
                WhatsAppAvatar.Make(chat.Avatar, chat.IsGroup, 40),
                VStack(HAlign.Leading, 1,
                    Text(chat.Title).Font(16).Foreground(WA.TextColor).LineLimit(1),
                    Subtitle(session, chat).Font(13).LineLimit(1)),
                Spacer(),
                HStack(6,
                        WAIcon.Make("VideoCamera", "video", 20, shared: true)
                            .Tool("Cuộc gọi video", () => ChatTools.Call(Platform.Whatsapp, video: true)),
                        WAIcon.Make("Phone", "phone", 20, shared: true)
                            .Tool("Cuộc gọi thoại", () => ChatTools.Call(Platform.Whatsapp, video: false)),
                        WAIcon.Make("IconSearch", "magnifyingglass", 18, shared: true)
                            .ToolPopover("Tìm kiếm", showSearch, done => InChatSearch.Make(session, chat.Id, done, "Tìm kiếm...")))
                    .Font(17).Foreground(WA.Icon))
            .PaddingH(compact ? 12 : 16).Frame(height: compact ? 54 : 60).PaddingTop(compact ? 0 : 6)
            .Background(WA.Panel)
            .Overlay(Alignment.Bottom, Rectangle().Fill(WA.Separator).Frame(height: 1));
    }

    private static SText Subtitle(PlatformSession session, Chat chat)
    {
        if (session.Typing.GetValueOrDefault(chat.Id) is { Count: > 0 } t)
            return Text(chat.IsGroup ? $"{t[0].Name ?? ""} đang soạn tin…" : "đang soạn tin…").Foreground(WA.Green);
        if (chat.IsGroup) return Text("bấm vào đây để xem thông tin nhóm").Foreground(WA.Secondary);
        if (chat.Online == true) return Text("trực tuyến").Foreground(WA.Secondary);
        if (chat.LastSeen is { } ls)
        {
            var d = Time.Local(ls);
            var day = d.Date == DateTime.Today ? "hôm nay" : d.Date == DateTime.Today.AddDays(-1) ? "hôm qua" : WA.ListTime(d);
            return Text($"truy cập lần cuối {day} lúc {Fmt.Time(d)}").Foreground(WA.Secondary);
        }
        return Text("bấm vào đây để xem thông tin liên hệ").Foreground(WA.Secondary);
    }
}

public static class WhatsAppSystemChip
{
    public static Control Make(string text) =>
        Text(text).Font(12.5).Foreground(WA.Icon).MultilineAlignment(TextAlignment.Center).LineLimit(null)
            .PaddingH(12).PaddingV(5)
            .Background(RoundedRectangle(7.5).Fill(SColor.White).Shadow(WA.BubbleShadow.Opacity(0.13), 0.5, 0, 1))
            .FillWidth();
}

public static class WhatsAppMessageRow
{
    public static Control Make(PlatformSession session, Chat chat, MessageRow row, bool compact)
    {
        var m = row.Message;
        if (m.Kind == MessageKind.System) return WhatsAppSystemChip.Make(m.Text ?? "").PaddingV(4);

        var hovered = Remember(false);
        var picking = Remember(false);
        bool Active() => hovered.Value || picking.Value;

        /// WhatsApp's grey smiley beside a hovered bubble; the slot is kept so bubbles don't shift.
        Control ReactButton() =>
            Button(() => picking.Value = true,
                    Image("face.smiling").Font(15).Foreground(WA.Icon)
                        .Frame(width: 26, height: 26).Background(Circle().Fill(SColor.Black.Opacity(0.05))))
                .Help("Bày tỏ cảm xúc")
                .Slot(() => Active() && !m.IsDeleted && session.Can("reactions"))
                .WithReactionPicker(m.HasReactions ? new State<bool>(false) : picking, session, m, ReactionSets.WhatsApp);

        Control TranslateButton() =>
            TranslateToggle.Make(m, on =>
                    Image("translate").Font(13).Foreground(on ? WA.Teal : WA.Icon)
                        .Frame(width: 26, height: 26).Background(Circle().Fill(SColor.Black.Opacity(0.05))))
                .Slot(() => Active() && Translator.CanTranslate(m));

        var bubble = WhatsAppBubble.Make(session, chat, row, picking)
            .MessageContextMenu(session, m, MenuLabels.Vietnamese, ReactionSets.WhatsApp);
        var gap = ChatMetrics.Gap(120, compact);
        return HStack(8,
                m.FromMe ? Spacer(gap) : null,
                m.FromMe ? TranslateButton() : null,
                m.FromMe ? ReactButton() : null,
                bubble,
                !m.FromMe ? ReactButton() : null,
                !m.FromMe ? TranslateButton() : null,
                !m.FromMe ? Spacer(gap) : null)
            .PaddingH(compact ? 12 : 60)
            .ContentShape()
            .TrackHover(hovered);
    }
}

public static class WhatsAppBubble
{
    public static Control Make(PlatformSession session, Chat chat, MessageRow row, State<bool> picking)
    {
        var m = row.Message;
        var mediaOnly = m.Kind is MessageKind.Image or MessageKind.Video or MessageKind.Gif && string.IsNullOrEmpty(m.Text);
        var isSticker = m.Kind == MessageKind.Sticker || (m.Kind == MessageKind.Text && Jumbo.IsJumboEmoji(m.Text));
        var fill = m.FromMe ? WA.OutBubble : WA.InBubble;

        Control body;
        if (isSticker)
        {
            body = VStack(HAlign.Trailing, 2,
                m.Kind == MessageKind.Sticker ? MediaContent.Make(session, m) : Text(m.Text ?? "").Font(48),
                Meta(m).PaddingH(5).PaddingV(1).Background(RoundedRectangle(6).Fill(SColor.White.Opacity(0.9))));
        }
        else
        {
            var col = new BubbleColumn(3,
                !m.FromMe && chat.IsGroup && row.IsFirstInGroup
                    ? Text(m.SenderName ?? "").Font(12.8, Weight.Semibold).Foreground(WA.NameColor(m.SenderId)).PaddingH(mediaOnly ? 4 : 0)
                    : null,
                m.Forwarded == true
                    ? HStack(4, Image("arrowshape.turn.up.right"), Text("Đã chuyển tiếp"))
                        .Font(SFont.System(12.5) with { Italic = true }).Foreground(WA.Secondary)
                    : null,
                m.ReplyTo is { } r ? BubbleColumn.Fill(Reply(session, m, r)) : null,
                Content(session, m, mediaOnly),
                Translator.Shared.IsShown(m)
                    ? TranslationQuote.Make(m, WA.Teal, WA.TextColor.Opacity(0.75), 13.5).PaddingH(mediaOnly ? 4 : 0).PaddingTop(2)
                    : null);
            body = col.Padding(mediaOnly ? 3.0 : 0.0)
                .PaddingH(mediaOnly ? 0 : 9).PaddingTop(mediaOnly ? 0 : 6).PaddingBottom(mediaOnly ? 0 : 7)
                .Background(RoundedRectangle(7.5, true).Fill(fill).Shadow(WA.BubbleShadow.Opacity(0.13), 0.5, 0, 1))
                .Overlay(m.FromMe ? Alignment.TopTrailing : Alignment.TopLeading,
                    row.IsFirstInGroup
                        ? Shapes.Tail(m.FromMe ? Shapes.TailCorner.TopRight : Shapes.TailCorner.TopLeft).Fill(fill)
                            .Frame(width: 8, height: 13).Offset(m.FromMe ? 8 : -8)
                        : null);
        }

        Control? reaction = null;
        if (m.Reactions is { Count: > 0 } rs)
        {
            var total = rs.Sum(x => x.Count);
            var chips = rs.Select(x => (Control?)Text(x.Emoji).Font(13)).ToList();
            if (total > 1) chips.Add(Text($"{total}").Font(12).Foreground(WA.Secondary));
            reaction = Button(() => { if (session.Can("reactions")) picking.Value = true; },
                    HStack(2, chips.ToArray()).PaddingH(6).Frame(height: 22)
                        .Background(Capsule().Fill(SColor.White).Shadow(SColor.Black.Opacity(0.15), 1, 0, 0.5)))
                .WithReactionPicker(picking, session, m, ReactionSets.WhatsApp)
                .Offset(m.FromMe ? 10 : -10, 18);
        }
        return new CapWidth(520, body
            .Overlay(m.FromMe ? Alignment.BottomLeading : Alignment.BottomTrailing, reaction)
            .PaddingBottom(m.HasReactions ? 18 : 0));
    }

    private static Control Content(PlatformSession session, Message m, bool mediaOnly)
    {
        if (m.IsDeleted)
            return HStack(5,
                    Image("nosign"),
                    Text(m.FromMe ? "Bạn đã xóa tin nhắn này" : "Tin nhắn này đã bị xóa").Font(SFont.System(14) with { Italic = true }),
                    Meta(m).PaddingLeading(6))
                .Font(14).Foreground(WA.Secondary);
        switch (m.Kind)
        {
            case MessageKind.Image or MessageKind.Video or MessageKind.Gif:
                return VStack(HAlign.Leading, 4,
                    MediaContent.Make(session, m, maxWidth: 330, cornerRadius: 6)
                        .Overlay(Alignment.BottomTrailing, mediaOnly
                            ? Meta(m).Scheme(Scheme.Dark).Foreground(SColor.White)
                                .PaddingH(6).PaddingV(3)
                                .Background(new LinearGradient([SColor.Black.Opacity(0), SColor.Black.Opacity(0.35)], LinearGradient.Top, LinearGradient.Bottom))
                                .Padding(4)
                            : null),
                    !mediaOnly ? TextWithMeta(m).PaddingH(2) : null);
            case MessageKind.File or MessageKind.Audio or MessageKind.Voice:
                return VStack(HAlign.Leading, 4,
                    FileContent.Make(session, m, m.Kind == MessageKind.Voice ? WA.Teal : WA.Muted,
                            titleColor: WA.TextColor, subtitleColor: WA.Secondary)
                        .Padding(8)
                        .Background(RoundedRectangle(6).Fill(SColor.Black.Opacity(0.04))),
                    HStack(Spacer(), Meta(m)));
            default:
                return TextWithMeta(m);
        }
    }

    /// The text followed by invisible room for the time and ticks, which sit in the bottom-trailing corner.
    private static Control TextWithMeta(Message m)
    {
        var reserve = "    " + (m.IsEdited ? "Đã chỉnh sửa " : "") + Fmt.Time(m.Date) + (m.FromMe ? "  ✓✓" : "");
        var spans = LinkSpans.Spans(m.Text ?? "", SColor.Hex(0x027EB5)).ToList();
        spans.Add(new Span(reserve, SColor.Clear, Size: 11));
        var text = new LinkText(spans) { OwnFont = SFont.System(14.2) }.Foreground(WA.TextColor);
        return ZStack(Alignment.BottomTrailing,
            text.FixedSize(horizontal: false, vertical: true),
            Meta(m).Offset(0, 3));
    }

    private static Control Meta(Message m) =>
        HStack(3,
                m.IsEdited ? Text("Đã chỉnh sửa") : null,
                Text(Fmt.Time(m.Date)),
                m.FromMe ? WhatsAppTicks.Make(m.Status) : null)
            .Font(11).Foreground(WA.Secondary);

    private static Control Reply(PlatformSession session, Message m, ReplyRef r)
    {
        var color = WA.NameColor(r.SenderName ?? r.Id);
        return HStack(0,
                Rectangle().Fill(color).Frame(width: 4),
                VStack(HAlign.Leading, 2,
                        Text(r.SenderName ?? "Bạn").Font(12.8, Weight.Semibold).Foreground(color),
                        Text(r.Text ?? "📷 Ảnh").Font(13).Foreground(WA.Secondary).LineLimit(2))
                    .PaddingH(8).PaddingV(5))
            .Frame(minWidth: 120, alignment: Alignment.Leading)
            .Background(SColor.Black.Opacity(0.05))
            .ClipRounded(6)
            .OnTap(() =>
            {
                session.ScrollTarget = session.ScrollTarget.Remove(m.ChatId);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => session.ScrollTarget = session.ScrollTarget.SetItem(m.ChatId, r.Id));
            });
    }
}

public static class WhatsAppComposer
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
                return HStack(0,
                        Rectangle().Fill(WA.Green).Frame(width: 4),
                        WAIcon.Make("Pencil", "pencil", 16).Foreground(WA.Green).PaddingLeading(10),
                        VStack(HAlign.Leading, 2,
                                Text("Chỉnh sửa tin nhắn").Font(12.8, Weight.Semibold).Foreground(WA.Green),
                                err != null
                                    ? Text(err).Font(13).Foreground(SColor.Red).LineLimit(2)
                                    : Text(e.Preview()).Font(13).Foreground(WA.Secondary).LineLimit(1))
                            .Padding(8),
                        Spacer(),
                        Button(() => session.CancelEdit(chat.Id), Image("xmark").Foreground(WA.Icon).Padding(10)).Help("Hủy chỉnh sửa (Esc)"))
                    .FixedSize(false, true)
                    .Background(RoundedRectangle(7).Fill(SColor.Hex(0xF0F2F5)))
                    .ClipRounded(7)
                    .PaddingH(60).PaddingTop(8);
            }
            if (session.ReplyingTo.GetValueOrDefault(chat.Id) is not { } r) return null;
            return HStack(0,
                    Rectangle().Fill(WA.NameColor(r.SenderId)).Frame(width: 4),
                    VStack(HAlign.Leading, 2,
                            Text(r.FromMe ? "Bạn" : (r.SenderName ?? chat.Title)).Font(12.8, Weight.Semibold).Foreground(WA.NameColor(r.SenderId)),
                            Text(r.Preview()).Font(13).Foreground(WA.Secondary).LineLimit(1))
                        .Padding(8),
                    Spacer(),
                    Button(() => session.ReplyingTo = session.ReplyingTo.Remove(chat.Id),
                        Image("xmark").Foreground(WA.Icon).Padding(10)))
                .FixedSize(false, true)
                .Background(RoundedRectangle(7).Fill(SColor.Hex(0xF0F2F5)))
                .ClipRounded(7)
                .PaddingH(60).PaddingTop(8);
        });

        var editor = Composer.Editor(chat.Id, draft, "Nhập tin nhắn",
            onSubmit: () => session.Send(session.Draft(chat.Id), chat.Id),
            font: SFont.System(15), textColor: WA.TextColor, placeholderColor: WA.Muted,
            maxHeight: compact ? 220 : 140,
            onTypingChange: on => session.SetTyping(chat.Id, on),
            mentionSession: session, mentionChatId: chat.IsGroup ? chat.Id : null,
            onFiles: files => ChatTools.SendPasted(files, session, chat.Id),
            onCommand: key => session.ComposerCommand(key, chat.Id));

        var trailing = View(() =>
        {
            if (session.Editing.ContainsKey(chat.Id))
                return Button(() => session.Send(session.Draft(chat.Id), chat.Id),
                        Image("checkmark").Font(17, Weight.Bold).Foreground(SColor.White).Frame(width: 40, height: 40)
                            .Background(Circle().Fill(WA.Green)))
                    .Help("Lưu");
            return session.Draft(chat.Id).Length == 0 && !session.Attachments.ContainsKey(chat.Id)
                ? VoiceTools.VoiceTool(() => WAIcon.Make("ChatBarMicrophone", "mic", 22, shared: true)
                    .Foreground(WA.Icon).Frame(width: 40, height: 40), session, chat.Id)
                : Button(() => session.Send(session.Draft(chat.Id), chat.Id),
                    WAIcon.Make("IconSend", "paperplane.fill", 20, shared: true)
                        .Foreground(SColor.White).Frame(width: 40, height: 40)
                        .Background(Circle().Fill(WA.Green)));
        });

        var input = HStack(VAlign.Bottom, 8,
                Button(() => ChatTools.Attach(session, chat.Id),
                    WAIcon.Make("ButtonAttachCatalyst", "plus", 24).Foreground(WA.Icon).Frame(width: 40, height: 40)),
                HStack(VAlign.Bottom, 10,
                        View(() => Collapsed() ? null
                            : WAIcon.Make("ChatBarIconEmoji", "face.smiling", 22, shared: true).Foreground(WA.Icon)
                                .Tool("Biểu tượng cảm xúc", Composer.Emoji, 30).PaddingBottom(4)),
                        OutgoingLanguageButton.Make(session, chat.Id, WA.Teal, WA.Icon, 17, 30, hideWhenIdle: Collapsed).PaddingBottom(4),
                        editor.PaddingV(7).ComposerTapArea())
                    .PaddingH(12)
                    .Background(RoundedRectangle(8).Fill(SColor.White)),
                trailing)
            .PaddingH(12).PaddingV(6);

        return VStack(0,
                bar,
                TranslationNotice.Make(session, chat.Id),
                MathSendPreview.Make(session, chat.Id, WA.Green),
                ComposerAttachments.Make(session, chat.Id, WA.Green),
                input)
            .Background(WA.Panel);
    }
}

// MARK: Login

public static class WhatsAppLogin
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        Control qr = session.QrImage is { } img
            ? ZStack(
                new BitmapView(img) { Smooth = false, Mode = ContentMode.Stretch }.Frame(width: 264, height: 264),
                Circle().Fill(SColor.White).Frame(width: 64, height: 64)
                    .Overlay(new PlatformIcon(Platform.Whatsapp, 48).ClipCircle()),
                QRExpiryOverlay.Make(session, WA.Teal).Frame(width: 264, height: 264))
            : ZStack(new ProgressView().Frame(width: 264, height: 264));

        var steps = new WAMaxWidth(420, VStack(HAlign.Leading, 22,
                Text("Các bước đăng nhập").Font(28, Weight.Light).Foreground(WA.Title),
                Step(1, "Mở WhatsApp trên điện thoại của bạn"),
                Step(2, "Trên Android, nhấn vào **Menu** ⋮ · Trên iPhone, nhấn vào **Cài đặt** ⚙︎"),
                Step(3, "Nhấn vào **Thiết bị đã liên kết**, sau đó nhấn vào **Liên kết thiết bị**"),
                Step(4, "Quét mã QR để xác nhận"),
                session.AuthError is { } e ? Text(e).Font(13).Foreground(SColor.Red) : null,
                Spacer().Frame(height: 30),
                HStack(4, Image("lock.fill"), Text("Tin nhắn cá nhân của bạn được mã hóa đầu cuối"))
                    .Font(13).Foreground(WA.Secondary)),
            HAlign.Leading);

        var card = new WAMaxWidth(900, HStack(VAlign.Top, 50,
                steps,
                qr.Padding(10).Background(SColor.White))
            .Padding(50)
            .Background(RoundedRectangle(24).Fill(SColor.White).Overlay(RoundedRectangle(24).Stroke(WA.Separator))));

        return VStack(0,
                HStack(8,
                        new PlatformIcon(Platform.Whatsapp, 30),
                        Text("WhatsApp").Font(16, Weight.Semibold).Foreground(WA.Green),
                        Spacer())
                    .PaddingH(40).PaddingTop(40).PaddingBottom(24),
                card,
                Spacer())
            .Fill()
            .Background(SColor.Hex(0xFCF5EB))
            .OnAppear(session.EnsureQR)
            .OnDisappear(session.StopWantingQR);
    });

    private static Control Step(int n, string s) =>
        HStack(VAlign.Top, 14,
            Text($"{n}").Font(14).Foreground(WA.TextColor)
                .Frame(width: 26, height: 26).Overlay(Circle().Stroke(WA.TextColor, 1)),
            LinkText.Markdown(s).Font(17).Foreground(SColor.Hex(0x3B4A54)));
}
