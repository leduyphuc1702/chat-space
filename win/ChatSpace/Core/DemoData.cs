using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChatSpace.Kit;

namespace ChatSpace.Core;

/// Sample data for `CHATSPACE_DEMO=1`: renders every platform UI without accounts or bridges.
public static class DemoData
{
    public static bool Enabled => Environment.GetEnvironmentVariable("CHATSPACE_DEMO") != null;

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    /// `CHATSPACE_DEMO_CURRENCY=GBP|EUR` switches the VietFlow sample PO and its invoice to that currency.
    public static string Currency => Environment.GetEnvironmentVariable("CHATSPACE_DEMO_CURRENCY") ?? "USD";

    /// `CHATSPACE_DEMO_INVOICE_CURRENCY` makes the invoice differ from the PO, e.g. a USD PO paid in £.
    public static string InvoiceCurrency => Environment.GetEnvironmentVariable("CHATSPACE_DEMO_INVOICE_CURRENCY") ?? Currency;

    /// How the sample invoice writes an amount: "$14.24", "£14.24", "14,24 €".
    public static string InvoiceMoney(double v)
    {
        var n = Math.Abs(v).ToString("F2", Inv.C);
        var s = InvoiceCurrency switch
        {
            "GBP" => "£" + n,
            "EUR" => n.Replace(".", ",") + " €",
            _ => "$" + n,
        };
        return v < 0 ? "-" + s : s;
    }

    private static string RenderSample(string name, int width, int height, Color background, IEnumerable<(string text, double x, double y, double size, bool bold, Color color, bool alignRight)> lines)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        if (File.Exists(path)) return path;
        try
        {
            using var rt = new RenderTargetBitmap(new PixelSize(width, height));
            using (var ctx = rt.CreateDrawingContext())
            {
                ctx.FillRectangle(new SolidColorBrush(background), new Rect(0, 0, width, height));
                foreach (var l in lines)
                {
                    var font = SFont.System(l.size, l.bold ? Weight.Bold : Weight.Regular);
                    var ft = new FormattedText(l.text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(font.AvaloniaFamily, FontStyle.Normal, font.AvaloniaWeight), l.size, new SolidColorBrush(l.color));
                    ctx.DrawText(ft, new Point(l.alignRight ? l.x - ft.Width : l.x, l.y));
                }
            }
            rt.Save(path);
        }
        catch { }
        return path;
    }

    /// A photo of a note with text in it, to exercise the viewer's Live Text (Windows OCR here).
    private static string SampleImage()
    {
        string[] lines = ["Góp ý thiết kế trang chủ", "", "1. Tăng khoảng cách giữa các khối lên 24px",
            "2. Màu chủ đạo #0068FF, nút phụ màu xám", "3. Logo đặt bên trái thanh điều hướng", "Hạn chót: thứ Sáu 15/11"];
        var ink = Color.FromRgb(38, 38, 38);
        return RenderSample("chatspace-demo-note.png", 1200, 800, Color.FromRgb(250, 245, 224),
            lines.Select((l, i) => (l, 80.0, 90.0 + i * 100 - (i == 0 ? 10 : 0), i == 0 ? 64.0 : 44.0, i == 0, ink, false)));
    }

    /// A Vinted-style purchase receipt, for the VietFlow cost form.
    private static string SampleReceipt()
    {
        (string, string, bool)[] rows =
        [
            ("Your Vinted purchase receipt:", "", true), ("", "", false),
            ("Seller", "perfecttimingcloset", true), ("Order", "Victoria's Secret Ivory Satin Nightgown L", true),
            ("Paid", InvoiceMoney(14.24), true), ("Item", InvoiceMoney(11), false), ("Shipping", InvoiceMoney(7.45), false),
            ("Vinted fee", InvoiceMoney(1.25), false), ("Sales tax", InvoiceMoney(0), false), ("Discount", InvoiceMoney(-5.46), false),
            ("Payment method", "MasterCard, **********8440", true), ("Payment date", "10/30/2026 02:10 AM", true),
            ("Transaction ID", "23728847703", true),
        ];
        var items = new List<(string, double, double, double, bool, Color, bool)>();
        for (var i = 0; i < rows.Length; i++)
        {
            var y = 70 + i * 74.0;
            items.Add((rows[i].Item1, 60, y, 30, rows[i].Item3, rows[i].Item3 ? Color.FromRgb(26, 26, 26) : Color.FromRgb(115, 115, 115), false));
            items.Add((rows[i].Item2, 840, y, 30, false, Color.FromRgb(26, 26, 26), true));
        }
        return RenderSample($"chatspace-demo-receipt-{InvoiceCurrency}.png", 900, 1100, Colors.White, items);
    }

    public static void Load(PlatformSession s)
    {
        var now = Time.NowMs;
        const double min = 60_000;
        var caps = new HashSet<string> { "reactions", "reply", "delete", "typing", "read_receipts", "media_send", "mentions", "edit" };
        if (s.Platform is Platform.Whatsapp or Platform.Telegram) caps.Add("forward");
        s.SelfInfo = new SelfInfo { Id = "me", Name = "Minh" };
        s.Connection = "online";

        Message Msg(string id, string chat, string? from, string? name, double ago, string text, MessageKind kind = MessageKind.Text,
            DeliveryStatus? status = null, IReadOnlyList<Reaction>? reactions = null, ReplyRef? reply = null, Media? media = null) =>
            new()
            {
                Id = id, ChatId = chat, SenderId = from ?? "me", SenderName = name, FromMe = from == null, Timestamp = now - ago * min,
                Kind = kind, Text = text, Media = media, ReplyTo = reply, Reactions = reactions,
                Status = from == null ? status ?? DeliveryStatus.Read : null,
            };

        const string direct = "c1", group = "c2";
        var conv = new List<Message>
        {
            Msg("m1", direct, "u1", "Lan Anh", 60 * 26, "Chào anh, mai mình họp lúc mấy giờ ạ?"),
            Msg("m2", direct, null, null, 60 * 26 - 2, "9h sáng nhé em"),
            Msg("m3", direct, null, null, 60 * 26 - 2.5, "Phòng họp tầng 3"),
            Msg("m4", direct, "u1", "Lan Anh", 60 * 25, "Dạ vâng 👍", reactions: [new Reaction("❤️", 1, true)]),
            Msg("m5", direct, "u1", "Lan Anh", 34, "Anh ơi, em gửi bản thiết kế mới nhé. Anh xem giúp em phần màu sắc với bố cục trang chủ có ổn không ạ?"),
            Msg("m6", direct, "u1", "Lan Anh", 33, "", kind: MessageKind.File, media: new Media { Mime = "application/pdf", FileName = "ThietKe_TrangChu_v3.pdf", Size = 2_480_000 }),
            Msg("m6b", direct, "u1", "Lan Anh", 32, "", kind: MessageKind.Image, reactions: [new Reaction("😆", 1, false)],
                media: new Media { Mime = "image/png", Path = SampleImage(), Width = 1200, Height = 800 }),
            Msg("m7", direct, null, null, 12, "Ok em, để anh xem rồi góp ý. Nhìn chung bố cục ổn rồi, chỉ cần chỉnh lại khoảng cách giữa các khối.",
                reply: new ReplyRef { Id = "m5", Text = "Anh ơi, em gửi bản thiết kế mới nhé…", SenderName = "Lan Anh" }),
            Msg("m8", direct, null, null, 11, "https://chatspace.vn/design-review", status: DeliveryStatus.Delivered, reactions: [new Reaction("\u2764", 1, false)]),
            Msg("m9", direct, "u1", "Lan Anh", 2, "😂"),
        };
        var groupConv = new List<Message>
        {
            new() { Id = "g0", ChatId = group, SenderId = "sys", FromMe = false, Timestamp = now - 90 * min, Kind = MessageKind.System, Text = "Tuấn đã thêm Hà vào nhóm" },
            Msg("g1", group, "u2", "Tuấn Nguyễn", 80, "Cuối tuần này team đi cafe không mọi người?"),
            Msg("g2", group, "u3", "Hà Phạm", 78, "Đi đi! Mình rảnh chiều thứ 7", reactions: [new Reaction("👍", 3, true)]),
            Msg("g3", group, "u3", "Hà Phạm", 77.5, "Quán cũ ở Phan Xích Long nhé"),
            Msg("g4", group, null, null, 70, "Mình ok, 3h chiều nha"),
            Msg("g5", group, "u2", "Tuấn Nguyễn", 5, "Chốt 3h thứ 7 ☕️", reactions: [new Reaction("❤️", 2, false), new Reaction("😆", 1, false)]),
        };

        var messages = new Dictionary<string, ImmutableList<Message>> { [direct] = conv.ToImmutableList(), [group] = groupConv.ToImmutableList() };
        var reached = new HashSet<string> { direct, group };
        var chats = new List<Chat>
        {
            new() { Id = direct, Title = "Lan Anh", Kind = ChatKind.Direct, Unread = 1, Timestamp = now - 2 * min, LastMessage = conv[^1], Online = true },
            new() { Id = group, Title = "Team Thiết Kế", Kind = ChatKind.Group, Unread = 3, Timestamp = now - 5 * min, LastMessage = groupConv[^1], MemberCount = 8 },
            new() { Id = "c3", Title = "Hoàng Long", Kind = ChatKind.Direct, Unread = 0, Pinned = true, Timestamp = now - 60 * 3 * min,
                LastMessage = Msg("x1", "c3", null, null, 180, "Cảm ơn bạn nhiều nhé!"), LastSeen = now - 40 * min },
            new() { Id = "c4", Title = "Gia đình 🏡", Kind = ChatKind.Group, Unread = 12, MutedUntil = -1, Timestamp = now - 60 * 20 * min,
                LastMessage = Msg("x2", "c4", "u5", "Mẹ", 1200, "", kind: MessageKind.Image), MemberCount = 5 },
            new() { Id = "c5", Title = "Phương Thảo", Kind = ChatKind.Direct, Unread = 0, MarkedUnread = true, Timestamp = now - 60 * 50 * min,
                LastMessage = Msg("x3", "c5", "u6", "Phương Thảo", 3000, "Hẹn gặp lại nha") },
            new() { Id = "c6", Title = "Kênh Tin Công Nghệ", Kind = ChatKind.Channel, Unread = 0, Timestamp = now - 60 * 72 * min,
                LastMessage = Msg("x4", "c6", "u7", null, 4320, "Apple ra mắt macOS mới với nhiều tính năng AI") },
        };
        for (var i = 0; i < chats.Count; i++)
            if (chats[i].Kind == ChatKind.Direct) chats[i] = chats[i] with { IsContact = chats[i].Id != "c5" };

        // Per-platform chats, so the ChatSpace inbox and the "VF" custom group have something to merge.
        (string id, string title, ChatKind kind, int unread, double ago, string text, string? sender)[] extra = s.Platform switch
        {
            Platform.Zalo => [("z1", "VF 1 - Kho Hà Nội", ChatKind.Group, 4, 7, "Đã nhập 120 thùng hàng", "Minh Quân"),
                              ("z2", "VF-2 Giao hàng", ChatKind.Group, 0, 300, "Xe đã xuất bến", "Tài xế Hùng")],
            Platform.Messenger => [("m1", "VF3 Marketing", ChatKind.Group, 2, 15, "Banner mới đây nha", "Thu Trang"),
                                   ("m2", "Ngọc Ánh", ChatKind.Direct, 1, 45, "Tối nay đi ăn không?", null)],
            Platform.Whatsapp => [("w1", "VF Logistics", ChatKind.Group, 0, 600, "Shipment ETA Monday", "David"),
                                  ("w2", "Anh Tuấn (Úc)", ChatKind.Direct, 3, 25, "Gọi lại cho anh nhé", null)],
            _ => [("t1", "VF-5 Dev", ChatKind.Group, 7, 3, "Build #482 passed ✅", "CI Bot"),
                  ("t2", "BotFather", ChatKind.Bot, 0, 900, "Done! Congratulations on your new bot.", null),
                  ("t3", "Crypto Signals", ChatKind.Channel, 25, 9, "BTC breaks 120k", null)],
        };
        foreach (var (id, title, kind, unread, ago, text, sender) in extra)
        {
            var them = sender == null ? "p" + id : "s" + id;
            var last = Msg("l" + id, id, them, sender ?? title, ago, text);
            var c = new Chat
            {
                Id = id, Title = title, Kind = kind, Unread = unread, Timestamp = now - ago * min, LastMessage = last,
                MemberCount = kind == ChatKind.Group ? 12 : null, IsContact = kind == ChatKind.Direct,
            };
            if (s.Platform == Platform.Telegram && id == "t3") c = c with { MutedUntil = -1 };
            chats.Add(c);
            messages[id] = ImmutableList.Create(
                Msg("a" + id, id, them, sender ?? title, ago + 40, "Chào cả nhà 👋"),
                Msg("b" + id, id, null, null, ago + 30, "Ok, mình đang theo dõi nhé"),
                Msg("c" + id, id, them, sender ?? title, ago + 10, "Cập nhật tiến độ trong hôm nay"),
                last);
            reached.Add(id);
        }
        if (s.Platform == Platform.Telegram)
        {
            const string vf = "t4";
            var po = Msg("v1", vf, "u8", "Anh Tran", 70, $"POVFMANNHI0082 Total: {InvoiceMoney(14.24)} https://www.vinted.com/items/10577605590-victorias-secret-ivory-satin-lace-trim-slip-nightgown-l\nChị mua giúp em ạ @Minh Ly");
            var vfConv = ImmutableList.Create(
                po,
                Msg("v2", vf, "u9", "Minh Ly Dang", 64, "", kind: MessageKind.Image,
                    reply: new ReplyRef { Id = po.Id, Text = po.Text, SenderName = po.SenderName },
                    media: new Media { Mime = "image/png", Path = SampleReceipt(), Width = 900, Height = 1100 }),
                Msg("v3", vf, "u9", "Minh Ly Dang", 63, "Usdt 26350"));
            messages[vf] = vfConv;
            reached.Add(vf);
            chats.Add(new Chat { Id = vf, Title = "VF - Đơn hàng ngày", Kind = ChatKind.Group, Unread = 0, Timestamp = now - 63 * min, LastMessage = vfConv[^1], MemberCount = 11 });
        }
        s.Messages = messages.ToImmutableDictionary();
        s.ReachedStart = reached.ToImmutableHashSet();
        s.Chats = chats.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.Timestamp).ToImmutableList();

        if (s.Platform == Platform.Telegram)
        {
            caps.Add("folders");
            s.Folders =
            [
                new ChatFolder { Id = "1", Title = "Công việc", Emoticon = "💼", IncludeChatIds = ["c2", "t1"] },
                new ChatFolder { Id = "2", Title = "Cá nhân", Emoticon = "👤", ExcludeChatIds = ["c5"], Contacts = true, NonContacts = true },
                new ChatFolder { Id = "3", Title = "Nhóm", Groups = true },
                new ChatFolder { Id = "4", Title = "Kênh", Channels = true },
                new ChatFolder { Id = "5", Title = "Bot", Bots = true },
                new ChatFolder { Id = "6", Title = "Chưa đọc", Contacts = true, NonContacts = true, Groups = true, Channels = true, Bots = true, ExcludeRead = true },
            ];
            if (Env("CHATSPACE_DEMO_FOLDER") is { Length: > 0 } f) s.SelectedFolderId = f;
        }

        s.SelectedChatId = Environment.GetEnvironmentVariable("CHATSPACE_DEMO_EMPTY") != null ? null
            : s.Platform is Platform.Telegram or Platform.Whatsapp ? group : direct;
        s.Typing = s.Platform == Platform.Zalo
            ? ImmutableDictionary<string, ImmutableList<TypingInfo>>.Empty.Add("c3", [new TypingInfo("u4", "Hoàng Long")])
            : ImmutableDictionary<string, ImmutableList<TypingInfo>>.Empty;
        s.AuthState = AuthState.LoggedIn;

        if (double.TryParse(Env("CHATSPACE_DEMO_NOTIFY"), NumberStyles.Any, Inv.C, out var delay)
            && s.Platform == (Platforms.Parse(Env("CHATSPACE_DEMO_NOTIFY_PLATFORM")) ?? Platform.Whatsapp))
        {
            // CHATSPACE_DEMO_NOTIFY_COUNT=n repeats it n times, `delay` seconds apart.
            var count = int.TryParse(Env("CHATSPACE_DEMO_NOTIFY_COUNT"), out var n) ? n : 1;
            var target = Env("CHATSPACE_DEMO_NOTIFY_CHAT") is { Length: > 0 } t ? t : "c3";
            string[] texts = ["Tối nay rảnh không? Đi ăn lẩu nhé 🍲", "7 giờ ở quán cũ nha", "Nhớ rủ cả Minh với Lan luôn"];
            for (var i = 0; i < Math.Max(1, count); i++)
            {
                var idx = i;
                DispatcherTimer.RunOnce(() =>
                {
                    var m = new Message
                    {
                        Id = $"live-{idx}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ChatId = target, SenderId = "u4", SenderName = "Hoàng Long",
                        FromMe = false, Timestamp = Time.NowMs, Kind = MessageKind.Text, Text = texts[idx % texts.Length],
                    };
                    s.InjectDemoEvent("message.new", new JsonObject { ["message"] = Json.From(m), ["live"] = true });
                    if (s.Chats.FirstOrDefault(c => c.Id == target) is { } chat)
                        s.InjectDemoEvent("chat.upsert", new JsonObject { ["chat"] = Json.From(chat with { Unread = chat.Unread + 1, Timestamp = m.Timestamp, LastMessage = m }) });
                }, TimeSpan.FromSeconds(delay * (idx + 1)));
            }
        }
        if (s.Platform == Platform.Messenger && Environment.GetEnvironmentVariable("CHATSPACE_DEMO_HISTORY_PIN") != null)
        {
            caps.Add("history_pin");
            s.HistoryBackup = HistoryBackup.Locked;
        }
        if (s.Platform == Platform.Zalo && Environment.GetEnvironmentVariable("CHATSPACE_DEMO_PHONE_SYNC") is { } raw)
        {
            caps.Add("phone_sync");
            if (Enum.GetValues<PhoneSyncProgress.SyncState>().FirstOrDefault(v => v.ToString().Equals(raw, StringComparison.OrdinalIgnoreCase)) is var state
                && state.ToString().Equals(raw, StringComparison.OrdinalIgnoreCase) && state != PhoneSyncProgress.SyncState.Idle)
            {
                var done = state == PhoneSyncProgress.SyncState.Done;
                s.PhoneSync = new PhoneSyncProgress
                {
                    State = state, Chats = 87, ChatsDone = 30, Messages = done ? 12_480 : 4_215, Skipped = done ? 2 : null,
                    Code = state == PhoneSyncProgress.SyncState.Failed ? "rejected" : null,
                };
            }
        }
        s.Capabilities = caps.ToImmutableHashSet();
        if (s.Platform == Platform.Messenger && Environment.GetEnvironmentVariable("CHATSPACE_DEMO_LOGIN") is { } screen) LoginScreen(screen, s);
    }

    /// `CHATSPACE_DEMO_LOGIN=form|cookie|code|select|wait` shows Messenger's onboarding at that step;
    /// `CHATSPACE_DEMO_LOGIN_ERROR` adds an error under it.
    private static void LoginScreen(string screen, PlatformSession s)
    {
        if (screen != "cookie") s.Capabilities = s.Capabilities.Add("password_login");
        s.AuthState = AuthState.LoggedOut;
        s.AuthError = Environment.GetEnvironmentVariable("CHATSPACE_DEMO_LOGIN_ERROR");
        var steps = new Dictionary<string, AuthStep>
        {
            ["code"] = new()
            {
                StepId = "demo-2fa", Kind = "input", CanCancel = true,
                Instructions = "Nhập mã gồm 6 chữ số từ ứng dụng xác thực hoặc tin nhắn SMS gửi tới số điện thoại của bạn.",
                Fields = [new AuthStep.Field { Id = "code", Name = "Mã đăng nhập", Type = "code" }],
            },
            ["select"] = new()
            {
                StepId = "demo-select", Kind = "input", Instructions = "Chọn cách xác nhận đây là bạn", CanCancel = true,
                Fields = [new AuthStep.Field { Id = "method", Name = "Phương thức", Type = "select", Options = ["Ứng dụng xác thực", "Tin nhắn SMS", "Phê duyệt trên thiết bị khác"] }],
            },
            ["wait"] = new()
            {
                StepId = "demo-wait", Kind = "wait", CanCancel = true,
                Instructions = "Kiểm tra thông báo trên điện thoại hoặc máy tính khác đang đăng nhập Facebook và chọn \"Đúng, là tôi\".",
            },
        };
        if (steps.TryGetValue(screen, out var step))
        {
            s.AuthStep = step;
            s.AuthState = AuthState.AwaitingInput;
        }
    }

    public static IReadOnlyList<ChatMember> Members(string chatId)
    {
        if (chatId != "c2") return [new ChatMember { Id = "u1", Name = "Lan Anh" }];
        return
        [
            new() { Id = "u2", Name = "Tuấn Nguyễn" }, new() { Id = "u3", Name = "Hà Phạm" },
            new() { Id = "u4", Name = "Đức Trần" }, new() { Id = "u5", Name = "Hương Lê" },
            new() { Id = "u6", Name = "Quân Võ" }, new() { Id = "u7", Name = "Thảo Phương" },
            new() { Id = "me", Name = "Minh" },
        ];
    }
}
