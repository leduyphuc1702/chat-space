using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// `Text(...).foregroundStyle(gradient)`: SwiftUI spreads the gradient over the whole text frame, while
/// Avalonia restarts a relative brush on every line, and anchors an absolute brush at each glyph run.
/// So once the text is laid out, it is split into one run per line, and every run gets the brush
/// shifted by the position of its line inside the text frame.
internal sealed class MessengerGradientText : SText
{
    private readonly LinearGradient _paint;
    private readonly string _full;
    private string _applied = "";

    public MessengerGradientText(string text, LinearGradient paint) : base(text) { _paint = paint; _full = text; }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = base.ArrangeOverride(finalSize);
        var lines = new List<(int Start, int Length, double Dx, double Dy)>();
        double y = 0;
        foreach (var l in TextLayout.TextLines)
        {
            lines.Add((l.FirstTextSourceIndex, l.Length, l.Start, y));
            y += l.Height;
        }
        // Replacing the runs invalidates the layout, so only do it when the lines or the frame changed.
        var key = $"{s.Width:0.#}x{s.Height:0.#}|" + string.Join(";", lines.Select(l => $"{l.Start},{l.Length},{l.Dx:0.#},{l.Dy:0.#}"));
        if (key == _applied || lines.Count == 0) return s;
        _applied = key;

        var runs = new Avalonia.Controls.Documents.InlineCollection();
        foreach (var l in lines)
        {
            var len = Math.Min(l.Length, _full.Length - l.Start);
            if (len <= 0) continue;
            runs.Add(new Avalonia.Controls.Documents.Run(_full.Substring(l.Start, len)) { Foreground = Gradient(s, l.Dx, l.Dy) });
        }
        Inlines = runs;
        return s;
    }

    private IBrush Gradient(Size frame, double dx, double dy)
    {
        var stops = new GradientStops();
        for (var i = 0; i < _paint.Colors.Length; i++)
            stops.Add(new GradientStop(_paint.Colors[i].Resolve(Scheme.Light), _paint.Colors.Length == 1 ? 0 : (double)i / (_paint.Colors.Length - 1)));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(_paint.Start.X * frame.Width - dx, _paint.Start.Y * frame.Height - dy, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(_paint.End.X * frame.Width - dx, _paint.End.Y * frame.Height - dy, RelativeUnit.Absolute),
            GradientStops = stops,
        };
    }
}

/// Text with a fixed tracking. The Kit looks 22pt system text up as -0.26pt per character, but macOS lays
/// the Messenger 22pt headings and the large code field out with about +0.08pt (measured 3% wider).
internal sealed class MessengerTrackedText : SText
{
    public const double Tracking22 = 0.08;
    private readonly double _tracking;

    public MessengerTrackedText(string text, double tracking) : base(text) { _tracking = tracking; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LetterSpacing = _tracking;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LetterSpacingProperty && _tracking != 0 && LetterSpacing != _tracking) LetterSpacing = _tracking;
    }
}

/// `ProgressView().controlSize(.regular)` as macOS draws it: eight round spokes in a 32pt circle,
/// the leading one at 55% black fading by 7% per step. (The Kit's spinner is a smaller 12-spoke one.)
internal sealed class MessengerSpinner : Control
{
    private readonly Avalonia.Threading.DispatcherTimer _timer;
    private int _phase;

    public MessengerSpinner()
    {
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 8) };
        _timer.Tick += (_, _) => { _phase = (_phase + 1) % 8; InvalidateVisual(); };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

    protected override Size MeasureOverride(Size availableSize) => new(32, 32);

    public override void Render(DrawingContext ctx)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        for (var i = 0; i < 8; i++)
        {
            var age = (_phase - i + 8) % 8;
            var a = i * Math.PI / 4;
            var dir = new Vector(Math.Sin(a), -Math.Cos(a));
            var pen = new Pen(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round((0.549 - 0.07 * age) * 255), 0, 0, 0)), 4, lineCap: PenLineCap.Round);
            ctx.DrawLine(pen, c + dir * 8, c + dir * 14);
        }
    }
}

/// Messenger desktop look (messenger.com / Messenger for Mac), Vietnamese strings.
public static class MS
{
    public static readonly SColor Blue = SColor.Hex(0x0A7CFF);
    public static readonly SColor Brand = SColor.Hex(0x0866FF);
    public static readonly SColor TextColor = SColor.Hex(0x050505);
    public static readonly SColor Secondary = SColor.Hex(0x65676B);
    public static readonly SColor InBubble = SColor.Hex(0xF0F0F0);
    public static readonly SColor FieldBg = SColor.Hex(0xF0F2F5);
    public static readonly SColor Hover = SColor.Hex(0xF2F2F2);
    public static readonly SColor Selected = SColor.Hex(0xEBF5FF);
    public static readonly SColor Online = SColor.Hex(0x31A24C);
    public static readonly SColor Grey = SColor.Hex(0xBCC0C4);
    public static readonly SColor Error = SColor.Hex(0xE41E3F);
    public static readonly SColor Muted = SColor.Hex(0x8A8D91);
    /// Placeholder grey of a plain macOS text field on a light background.
    public static readonly SColor Placeholder = SColor.Black.Opacity(0.445);

    public static string Relative(DateTime d)
    {
        var s = (DateTime.Now - d).TotalSeconds;
        if (s < 60) return "Vừa xong";
        if (s < 3600) return $"{(int)(s / 60)} phút";
        if (s < 86400) return $"{(int)(s / 3600)} giờ";
        if (s < 7 * 86400) return $"{(int)(s / 86400)} ngày";
        if (s < 52 * 7 * 86400) return $"{(int)(s / (7 * 86400))} tuần";
        return $"{(int)(s / (365 * 86400))} năm";
    }

    private static readonly CultureInfo Vi = new("vi-VN");

    public static string GapTitle(DateTime d)
    {
        if (d.Date == DateTime.Today) return Fmt.Time(d);
        var days = (int)(DateTime.Now - d).TotalDays;
        return days < 7 ? d.ToString("ddd HH:mm", Vi) : d.ToString("HH:mm, dd 'Tháng' M, yyyy", Vi);
    }

    public static Control Avatar(string? source, double size = 56, bool online = false)
    {
        var a = AvatarView.Make(source, size, () =>
            Circle().Fill(SColor.Hex(0xE4E6EB))
                .Overlay(Image("person.fill").Font(size * 0.55).Foreground(Grey).Offset(y: size * 0.1))
                .ClipCircle());
        if (!online) return a;
        return a.Overlay(Alignment.BottomTrailing,
            Circle().Fill(Online).Frame(width: size * 0.26, height: size * 0.26)
                .Overlay(Circle().Stroke(SColor.White, size * 0.05))
                .Offset(-size * 0.02, -size * 0.02));
    }

    public static Control Link(string title, Action action, double size, SColor color, Weight weight = Weight.Regular) =>
        Button(action, Text(title).Font(size, weight).Foreground(color));
}

public sealed class MessengerRootView : Reactive
{
    public MessengerRootView(PlatformSession s) : base(() => Build(s)) { }

    private static Control Build(PlatformSession session)
    {
        Control body;
        if (session.AuthState == AuthState.LoggedIn)
        {
            var conversation = View(() => session.SelectedChatId is { } id && Tracker.Untracked(() => session.SelectedChat) != null
                ? Keep(("conv", id), () => new MessengerConversation(session, id))
                : Keep("empty", Empty));
            body = HStack(0,
                MessengerNavBar.Make(session),
                MessengerChatList.Make(session).Frame(width: 360),
                Rectangle().Fill(SColor.Black.Opacity(0.08)).Frame(width: 1),
                conversation);
        }
        else if (session.AuthState == AuthState.Connecting || (session.BridgeError != null && session.AuthState != AuthState.Error))
            body = BridgeStatusView.Make(session, MS.Blue);
        else
            body = MessengerLogin.Make(session);
        return body.Background(SColor.White).Scheme(Scheme.Light);
    }

    private static Control Empty() =>
        VStack(10,
                Image("bubble.left.and.bubble.right").Font(54, Weight.Thin).Foreground(MS.Secondary),
                Text("Chưa chọn đoạn chat nào").Font(20, Weight.Bold).Foreground(MS.TextColor))
            .Fill();
}

public static class MessengerNavBar
{
    public static Control Make(PlatformSession session) => View(() =>
        VStack(8,
                Clear.Frame(height: 34),
                Icon("message.fill", true),
                Icon("storefront"),
                Icon("bubble.left.and.text.bubble.right"),
                Icon("archivebox"),
                Spacer(),
                MS.Avatar(session.SelfInfo?.Avatar, 32).PaddingBottom(16))
            .Frame(width: 60)
            .Background(SColor.White)
            .Overlay(Alignment.Trailing, Rectangle().Fill(SColor.Black.Opacity(0.06)).Frame(width: 1)));

    private static Control Icon(string name, bool selected = false) =>
        Image(name).Font(18).Foreground(selected ? MS.TextColor : MS.Secondary)
            .Frame(width: 44, height: 44)
            .Background(RoundedRectangle(8).Fill(selected ? SColor.Black.Opacity(0.05) : SColor.Clear));
}

/// A text field kept across rebuilds with a placeholder of its own (the Kit's is lighter than AppKit's),
/// optional centered text and an input filter.
public sealed class MessengerTextInput : Decorator, IFlexible
{
    public readonly Field Inner;
    public readonly State<bool> Focused = new(false);
    public Func<string, string>? Filter { get; set; }
    public Action? OnSubmit { get => Inner.OnSubmit; set => Inner.OnSubmit = value; }

    public MessengerTextInput(string placeholder, Binding<string> text, SFont font, bool secure = false, bool centered = false,
        bool multiline = false)
    {
        Inner = new Field("", text, secure: secure, multiline: multiline, font: font);
        if (centered) Inner.Box.TextAlignment = TextAlignment.Center;
        if (multiline) Inner.Box.VerticalContentAlignment = VerticalAlignment.Top;
        Inner.Box.GotFocus += (_, _) => Focused.Value = true;
        Inner.Box.LostFocus += (_, _) => Focused.Value = false;
        Inner.Box.TextChanged += (_, _) =>
        {
            if (Filter is not { } f) return;
            var now = Inner.Box.Text ?? "";
            var fixedText = f(now);
            if (fixedText != now) Inner.Box.Text = fixedText;
        };
        var large = font.Size == 22;
        if (large) Inner.Box.LetterSpacing = MessengerTrackedText.Tracking22;
        var ph = View(() =>
            {
                if (text.Value.Length != 0) return null;
                SText label = large ? new MessengerTrackedText(placeholder, MessengerTrackedText.Tracking22) : Text(placeholder);
                return label.Font(font).Foreground(MS.Placeholder).LineLimit(1);
            })
            .AllowsHitTesting(false);
        Child = ZStack(centered ? Alignment.Center : Alignment.Leading, Inner, ph);
    }

    public bool IsFlexible(Orientation axis) => axis == Orientation.Horizontal;

    /// Search-style field without chrome.
    public static Control Plain(object key, string placeholder, Binding<string> text, double size)
    {
        var input = Memo(("msi", key), () => new MessengerTextInput(placeholder, text, SFont.System(size)));
        return new KeepHost(input);
    }

    /// `MessengerField`: 50 pt rounded field, brand ring while focused; `large` is the centered one-time-code style.
    public static Control Boxed(object key, string placeholder, Binding<string> text, bool secure = false, bool large = false,
        Action? onSubmit = null, Func<string, string>? filter = null)
    {
        var font = SFont.System(large ? 22 : 16, large ? Weight.Semibold : Weight.Regular);
        var input = Memo(("msf", key), () => new MessengerTextInput(placeholder, text, font, secure, large));
        input.OnSubmit = onSubmit;
        input.Filter = filter;
        return new KeepHost(input)
            .PaddingH(16).Frame(height: 50)
            .Background(RoundedRectangle(12).Fill(MS.FieldBg))
            .Overlay(View(() => RoundedRectangle(12).Stroke(input.Focused.Value ? MS.Brand : SColor.Clear, 1.5)).AllowsHitTesting(false));
    }
}

public static class MessengerPrimaryButton
{
    public static Control Make(string title, Action action, bool busy = false, bool disabled = false)
    {
        // SwiftUI dims each layer of a disabled control by half.
        var dim = disabled || busy ? 0.5 : 1;
        return Button(action,
                ZStack(
                        RoundedRectangle(22).Fill(MS.Brand.Opacity((disabled ? 0.45 : 1) * dim)),
                        busy
                            ? new ProgressView { Size = ControlSize.Small }.Tint(SColor.White)
                            : Text(title).Font(16, Weight.Semibold).Foreground(SColor.White.Opacity(disabled ? 1 : 1)))
                    .Frame(height: 44))
            .Disabled(disabled || busy);
    }
}

// MARK: Chat list

public static class MessengerChatList
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        var query = Remember("");
        var tab = Remember(0);

        Control CircleIcon(string name) =>
            Image(name).Font(15, Weight.Semibold).Foreground(MS.TextColor)
                .Frame(width: 36, height: 36).Background(Circle().Fill(MS.FieldBg)).PaddingLeading(6);

        var header = HStack(
                Text("Đoạn chat").Font(24, Weight.Bold).Foreground(MS.TextColor),
                Spacer(),
                CircleIcon("ellipsis").Tool("Đánh dấu tất cả là đã đọc", session.MarkAllRead, 36),
                CircleIcon("square.and.pencil").Tool("Tin nhắn mới", () => ChatTools.NativeOnly(Platform.Messenger, "Tin nhắn mới"), 36))
            .Padding(34, 16, 10, 16);

        var search = HStack(8,
                Image("magnifyingglass").Font(14).Foreground(MS.Secondary),
                MessengerTextInput.Plain("search", "Tìm kiếm trên Messenger", query, 15))
            .PaddingH(12).Frame(height: 36)
            .Background(Capsule().Fill(MS.FieldBg))
            .PaddingH(16);

        var tabs = HStack(6,
                TabButton("Hộp thư", 0, tab),
                TabButton("Cộng đồng", 1, tab),
                Spacer())
            .PaddingH(12).PaddingV(10);

        Control? offline = session.Connection == "offline"
            ? Text(session.ConnectionDetail ?? "Đang kết nối lại…").Font(13).Foreground(MS.Secondary).PaddingBottom(6)
            : null;
        Control? banner = session.HistoryBackup == HistoryBackup.Locked
            ? MessengerHistoryBanner.Make(session).PaddingH(16).PaddingBottom(8)
            : null;

        var list = ScrollView(ForEach(
            () => Filtered(session, query.Value, tab.Value).Select(c => (c, c.Id == session.SelectedChatId)).ToList(),
            x => x.c.Id,
            x => MessengerChatRow.Make(session, x.c, x.Item2)
                .OnTap(() => session.Select(x.c.Id))
                .ChatContextMenu(session.Platform, x.c)).PaddingH(8));

        return VStack(0, header, search, tabs, offline, banner, list.FillHeight(Alignment.Top))
            .OnAppear(() => _ = session.CheckHistoryBackup());
    });

    private static Control TabButton(string title, int i, State<int> tab)
    {
        var on = tab.Value == i;
        return Text(title).Font(15, Weight.Semibold).Foreground(on ? MS.Brand : MS.Secondary)
            .PaddingH(12).PaddingV(7)
            .Background(Capsule().Fill(on ? MS.Selected : SColor.Clear))
            .OnTap(() => tab.Value = i);
    }

    private static List<Chat> Filtered(PlatformSession session, string query, int tab) =>
        session.Chats
            .Where(c => query.Length == 0 || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Where(c => tab == 0 ? c.Kind != ChatKind.Channel : c.Kind == ChatKind.Channel)
            .ToList();
}

/// Offers the secure storage PIN while encrypted chats have no history on this device.
public static class MessengerHistoryBanner
{
    public static Control Make(PlatformSession session) =>
        HStack(VAlign.Top, 10,
                Image("lock.fill").Font(15).Foreground(MS.Brand)
                    .Frame(width: 32, height: 32).Background(Circle().Fill(MS.Selected)),
                VStack(HAlign.Leading, 4,
                    Text("Khôi phục đoạn chat được mã hóa").Font(14, Weight.Semibold).Foreground(MS.TextColor),
                    Text("Nhập mã PIN Messenger để xem tin nhắn cũ trong các đoạn chat mã hóa đầu cuối.")
                        .Font(12.5).Foreground(MS.Secondary),
                    MessengerPINSheet.Link(session).PaddingTop(2)),
                Spacer(0))
            .Padding(12)
            .Background(RoundedRectangle(12).Fill(MS.FieldBg));
}

public static class MessengerPINSheet
{
    /// "Nhập mã PIN" text button that opens the sheet over its window.
    public static Control Link(PlatformSession session)
    {
        Pressable? b = null;
        b = Button(() => Present(b!, session), Text("Nhập mã PIN").Font(13, Weight.Semibold).Foreground(MS.Brand));
        return b;
    }

    public static void Present(Control from, PlatformSession session) => Sheets.Present(from, done => Make(session, done));

    private static Control Make(PlatformSession session, Action dismiss)
    {
        var pin = new State<string>("");
        var busy = new State<bool>(false);
        var error = new State<string?>(null);
        var noBackup = new State<bool>(false);

        async void Submit()
        {
            if (pin.Value.Length != 6 || busy.Value) return;
            busy.Value = true;
            error.Value = null;
            var result = await session.RestoreHistory(pin.Value);
            busy.Value = false;
            switch (result)
            {
                case HistoryRestoreResult.Restored:
                    dismiss();
                    break;
                case HistoryRestoreResult.WrongPin w:
                    pin.Value = "";
                    error.Value = w.AttemptsLeft switch
                    {
                        0 => "Mã PIN không đúng và đã hết lượt thử. Hãy đặt lại mã PIN trong ứng dụng Messenger.",
                        { } n => $"Mã PIN không đúng. Còn {n} lần thử.",
                        _ => "Mã PIN không đúng.",
                    };
                    break;
                case HistoryRestoreResult.NoBackup:
                    noBackup.Value = true;
                    error.Value = "Tài khoản này chưa bật kho lưu trữ an toàn trong Messenger nên không có tin nhắn cũ để khôi phục.";
                    break;
                case HistoryRestoreResult.Failed f:
                    error.Value = $"Không khôi phục được: {f.Message}";
                    break;
            }
        }

        return View(() =>
                VStack(14,
                    Image("lock.shield.fill").Font(34).Foreground(MS.Brand).PaddingTop(6),
                    Text("Nhập mã PIN của bạn").Font(20, Weight.Bold).Foreground(MS.TextColor),
                    Text("Tin nhắn cũ trong đoạn chat mã hóa đầu cuối nằm trong kho lưu trữ an toàn của Messenger. Nhập mã PIN 6 số bạn đã tạo cho kho lưu trữ này để khôi phục chúng trên máy này.")
                        .Font(13).Foreground(MS.Secondary).MultilineAlignment(TextAlignment.Center),
                    MessengerTextInput.Boxed("pin", "Mã PIN", pin, secure: true, large: true, onSubmit: Submit,
                        filter: v => new string(v.Where(char.IsAsciiDigit).Take(6).ToArray())),
                    error.Value is { } e
                        ? Text(e).Font(13).Foreground(MS.Error).MultilineAlignment(TextAlignment.Center)
                        : null,
                    Text("Messenger chỉ cho nhập sai một số lần. Mã PIN không được lưu trên máy.")
                        .Font(11.5).Foreground(MS.Secondary).MultilineAlignment(TextAlignment.Center),
                    MessengerPrimaryButton.Make("Khôi phục", Submit, busy.Value, pin.Value.Length != 6),
                    Button(dismiss, Text("Để sau").Font(14, Weight.Semibold).Foreground(MS.Brand)).Disabled(busy.Value))
                .Padding(24).Frame(width: 380)
                .Background(SColor.White).Scheme(Scheme.Light))
            .OnDisappear(() => { if (noBackup.Value) session.HistoryBackup = HistoryBackup.None; });
    }
}

public static class MessengerChatRow
{
    public static Control Make(PlatformSession session, Chat chat, bool selected)
    {
        var unread = chat.HasUnread;
        var row = HStack(12,
                MS.Avatar(chat.Avatar, 56, chat.Online == true),
                VStack(HAlign.Leading, 3,
                    Text(chat.Title).Font(15, unread ? Weight.Semibold : Weight.Regular).Foreground(MS.TextColor).LineLimit(1),
                    HStack(0,
                            Text(Preview(session, chat)).LineLimit(1),
                            Text(" · " + MS.Relative(chat.Date)).FixedSize())
                        .Font(13, unread ? Weight.Semibold : Weight.Regular)
                        .Foreground(unread ? MS.TextColor : MS.Secondary)),
                Spacer(4),
                chat.IsMuted ? Image("bell.slash.fill").Font(12).Foreground(MS.Grey) : null,
                unread
                    ? Circle().Fill(chat.IsMuted ? MS.Grey : MS.Brand).Frame(width: 12, height: 12)
                    : chat.LastMessage is { FromMe: true } m ? MessengerStatusIcon.Make(m.Status, chat.Avatar) : null)
            .Padding(8);
        return (selected
                ? row.Background(RoundedRectangle(8).Fill(MS.Selected))
                : row.HoverBackground(SColor.Clear, MS.Hover, 8))
            .ContentShape()
            .OnAppear(() => session.RequestAvatar(chat));
    }

    private static string Preview(PlatformSession session, Chat chat)
    {
        if (session.Typing.GetValueOrDefault(chat.Id) is { Count: > 0 } t)
            return chat.IsGroup ? $"{t[0].Name ?? ""} đang nhập…" : "Đang nhập…";
        if (chat.LastMessage is not { } m) return chat.Encrypted == true ? "Tin nhắn được mã hóa" : "";
        if (m.IsDeleted) return m.FromMe ? "Bạn đã thu hồi một tin nhắn" : $"{m.SenderName ?? "Ai đó"} đã thu hồi một tin nhắn";
        var who = m.FromMe ? "Bạn: " : chat.IsGroup ? (m.SenderName?.Split(' ').Last() ?? "") + ": " : "";
        var name = m.SenderName ?? "";
        switch (m.Kind)
        {
            case MessageKind.Image: return m.FromMe ? "Bạn đã gửi một ảnh." : $"{name} đã gửi một ảnh.";
            case MessageKind.Video: return m.FromMe ? "Bạn đã gửi một video." : $"{name} đã gửi một video.";
            case MessageKind.Sticker: return m.FromMe ? "Bạn đã gửi một nhãn dán." : $"{name} đã gửi một nhãn dán.";
            case MessageKind.Voice or MessageKind.Audio: return m.FromMe ? "Bạn đã gửi một tin nhắn thoại." : $"{name} đã gửi một tin nhắn thoại.";
            case MessageKind.File: return m.FromMe ? "Bạn đã gửi một file đính kèm." : $"{name} đã gửi một file đính kèm.";
            default: return who + (m.Text ?? "");
        }
    }
}

public static class MessengerStatusIcon
{
    public static Control Make(DeliveryStatus? status, string? avatar) => status switch
    {
        DeliveryStatus.Read => MS.Avatar(avatar, 14),
        DeliveryStatus.Delivered => Image("checkmark.circle.fill").Font(13).Foreground(MS.Grey),
        DeliveryStatus.Pending => Image("circle").Font(13).Foreground(MS.Grey),
        DeliveryStatus.Failed => Image("exclamationmark.circle.fill").Font(13).Foreground(SColor.Red),
        _ => Image("checkmark.circle").Font(13).Foreground(MS.Grey),
    };
}

// MARK: Conversation

public sealed class MessengerConversation : Decorator, IFlexible
{
    public MessengerConversation(PlatformSession session, string chatId)
    {
        Chat? Current() => session.Chats.FirstOrDefault(c => c.Id == chatId);
        Child = new CompactAware(compact =>
        {
            var initial = Tracker.Untracked(Current) ?? new Chat { Id = chatId };
            var scroll = new ChatScroll(session, chatId, row =>
                {
                    var chat = Tracker.Untracked(Current) ?? initial;
                    var gap = row.ShowTimeGap
                        ? Text(MS.GapTitle(row.Message.Date)).Font(12, Weight.Medium).Foreground(MS.Secondary).FillWidth().PaddingV(12)
                        : null;
                    return VStack(0,
                        gap,
                        MessengerMessageRow.Make(session, chat, row, row.IsLastOutgoing, compact)
                            .PaddingTop(row.IsFirstInGroup && !row.ShowTimeGap ? 8 : 1),
                        row.Id == chat.EncryptedAfter ? EncryptionDivider(session) : null);
                }, 0, new Thickness(0, 12, 0, 8),
                header: () => session.ReachedStart.Contains(chatId) ? Intro(session, Current() ?? initial) : null,
                footer: () => TypingBubble(session, Current() ?? initial));
            return VStack(0,
                    View(() => Header(session, Current() ?? initial, compact)).ZIndex(1),
                    ZStack(ColorView(SColor.White), scroll).FillHeight(),
                    View(() => MessengerComposer.Make(session, Current() ?? initial, compact)
                        .WithMentionSuggestions(session, Current() ?? initial, MS.Blue)))
                .Background(SColor.White);
        });
    }

    public bool IsFlexible(Orientation axis) => true;

    private static Control Header(PlatformSession session, Chat chat, bool compact)
    {
        var showSearch = Remember(false);
        var showInfo = Remember(false);
        return HStack(10,
                MS.Avatar(chat.Avatar, 40, chat.Online == true),
                VStack(HAlign.Leading, 1,
                    Text(chat.Title).Font(15, Weight.Semibold).Foreground(MS.TextColor).LineLimit(1),
                    Text(Status(chat)).Font(13).Foreground(MS.Secondary).LineLimit(1)),
                Spacer(),
                HStack(8,
                        Image("phone.fill").Font(18).Foreground(MS.Blue)
                            .Tool("Bắt đầu gọi thoại", () => ChatTools.Call(Platform.Messenger, false)),
                        Image("video.fill").Font(18).Foreground(MS.Blue)
                            .Tool("Bắt đầu gọi video", () => ChatTools.Call(Platform.Messenger, true)),
                        Image("magnifyingglass").Font(18).Foreground(MS.Blue)
                            .ToolPopover("Tìm kiếm trong cuộc trò chuyện", showSearch, done => InChatSearch.Make(session, chat.Id, done)),
                        Image("info.circle.fill").Font(18).Foreground(MS.Blue)
                            .ToolPopover("Thông tin về cuộc trò chuyện", showInfo, _ => ChatInfoPanel.Make(session, chat)))
                    .Font(18).Foreground(MS.Blue))
            .PaddingH(compact ? 12 : 16).Frame(height: compact ? 54 : 64).PaddingTop(compact ? 0 : 6)
            .Background(ColorView(SColor.White).Shadow(SColor.Black.Opacity(0.08), 1, 0, 1));
    }

    private static string Status(Chat chat)
    {
        if (chat.Online == true) return "Đang hoạt động";
        if (chat.LastSeen is { } ls) return $"Hoạt động {MS.Relative(Time.Local(ls))} trước";
        if (chat.IsGroup && chat.MemberCount is { } n) return $"{n} thành viên";
        return chat.Kind == ChatKind.Direct ? "Messenger" : "";
    }

    private static Control Intro(PlatformSession session, Chat chat)
    {
        Control? locked = session.HistoryBackup == HistoryBackup.Locked
            ? VStack(6,
                    Text("Tin nhắn cũ của đoạn chat mã hóa đầu cuối cần mã PIN Messenger để hiển thị.")
                        .Font(12.5).Foreground(MS.Secondary).MultilineAlignment(TextAlignment.Center),
                    MessengerPINSheet.Link(session))
                .Frame(maxWidth: 300).PaddingTop(8)
            : null;

        Control? encrypted = null;
        if (chat.Encrypted == true)
        {
            Control? action = null;
            if (!chat.IsGroup && session.Can("open_history") && !session.OpenHistoryDone.Contains(chat.Id))
                action = session.LoadingOpenHistory.Contains(chat.Id)
                    ? new ProgressView { Size = ControlSize.Small }
                    : Button(() => session.LoadOpenHistory(chat.Id),
                        Text("Tải tin nhắn từ trước khi mã hóa").Font(13, Weight.Semibold).Foreground(MS.Brand));
            encrypted = VStack(6,
                    HStack(4, Image("lock.fill"), Text("Đoạn chat được mã hóa đầu cuối"))
                        .Font(12.5, Weight.Medium).Foreground(MS.Secondary),
                    session.HistoryBackup != HistoryBackup.Restored
                        ? Text("Tin nhắn mã hóa gửi trước khi ChatSpace đăng nhập không có trên thiết bị này.")
                            .Font(12.5).Foreground(MS.Secondary).MultilineAlignment(TextAlignment.Center)
                        : null,
                    action)
                .Frame(maxWidth: 300).PaddingTop(8);
        }

        return VStack(6,
                MS.Avatar(chat.Avatar, 60),
                Text(chat.Title).Font(17, Weight.Semibold).Foreground(MS.TextColor),
                Text(chat.IsGroup ? "Nhóm trên Messenger" : "Facebook").Font(13).Foreground(MS.Secondary),
                locked,
                encrypted)
            .FillWidth().PaddingV(24);
    }

    private static Control EncryptionDivider(PlatformSession session) =>
        VStack(4,
                HStack(4, Image("lock.fill"), Text("Đoạn chat đã chuyển sang mã hóa đầu cuối")).Font(12, Weight.Medium),
                session.HistoryBackup != HistoryBackup.Restored
                    ? Text("Tin nhắn mã hóa gửi trước khi ChatSpace đăng nhập không có trên thiết bị này.")
                        .Font(12).MultilineAlignment(TextAlignment.Center)
                    : null)
            .Foreground(MS.Secondary).Frame(maxWidth: 340).FillWidth().PaddingV(14);

    private static Control? TypingBubble(PlatformSession session, Chat chat)
    {
        if (session.Typing.GetValueOrDefault(chat.Id) is not { Count: > 0 }) return null;
        return HStack(VAlign.Bottom, 8,
                MS.Avatar(chat.Avatar, 28),
                HStack(4, Enumerable.Range(0, 3).Select(_ => (Control?)Circle().Fill(SColor.Hex(0x90949C)).Frame(width: 7, height: 7)).ToArray())
                    .PaddingH(14).PaddingV(12)
                    .Background(Capsule().Fill(MS.InBubble)),
                Spacer())
            .PaddingH(12).PaddingTop(6);
    }
}

public static class MessengerMessageRow
{
    public static Control Make(PlatformSession session, Chat chat, MessageRow row, bool showStatus, bool compact)
    {
        var m = row.Message;
        if (m.Kind == MessageKind.System)
            return Text(m.Text ?? "").Font(12).Foreground(MS.Secondary).MultilineAlignment(TextAlignment.Center)
                .FillWidth().PaddingV(6);

        var hovered = Remember(false);
        var picking = Remember(false);
        bool Active() => hovered.Value || picking.Value;

        Control Actions()
        {
            Control? react = null;
            if (session.Can("reactions") && !m.IsDeleted)
            {
                var b = Button(() => picking.Value = true, Image("face.smiling").Frame(width: 24, height: 24).ContentShape())
                    .Help("Bày tỏ cảm xúc");
                react = m.HasReactions ? b : b.WithReactionPicker(picking, session, m, ReactionSets.Messenger);
            }
            Control? translate = Translator.CanTranslate(m)
                ? TranslateToggle.Make(m, on => Image("translate").Foreground(on ? MS.Blue : MS.Muted)
                    .Frame(width: 24, height: 24).ContentShape())
                : null;
            var reply = Button(() => session.StartReply(m),
                    Image("arrowshape.turn.up.left.fill").Frame(width: 24, height: 24).ContentShape())
                .Help("Trả lời");
            return HStack(6, react, translate, reply).Font(14).Foreground(MS.Muted).PaddingBottom(8).VisibleWhen(Active);
        }

        var bubble = MessengerBubble.Make(session, row, picking)
            .MessageContextMenu(session, m, MenuLabels.Vietnamese, ReactionSets.Messenger);
        var line = HStack(VAlign.Bottom, 8,
            m.FromMe ? Spacer(ChatMetrics.Gap(100, compact)) : null,
            !m.FromMe
                ? row.IsLastInGroup
                    ? MS.Avatar(m.SenderAvatar ?? (chat.IsGroup ? null : chat.Avatar), 28)
                    : Clear.Frame(width: 28, height: 1)
                : null,
            m.FromMe ? Actions() : null,
            bubble,
            !m.FromMe ? Actions() : null,
            !m.FromMe ? Spacer(ChatMetrics.Gap(100, compact)) : null,
            m.FromMe
                ? (showStatus ? MessengerStatusIcon.Make(m.Status, chat.Avatar) : Clear).Frame(width: 14, height: 14)
                : null);

        return VStack(m.FromMe ? HAlign.Trailing : HAlign.Leading, 2,
                !m.FromMe && chat.IsGroup && row.IsFirstInGroup
                    ? Text(m.SenderName ?? "").Font(12).Foreground(MS.Secondary).PaddingLeading(48)
                    : null,
                m.ReplyTo is { } r ? ReplyHeader(chat, m, r) : null,
                line)
            .PaddingH(12)
            .ContentShape()
            .TrackHover(hovered);
    }

    private static Control ReplyHeader(Chat chat, Message m, ReplyRef r)
    {
        var label = m.FromMe
            ? $"Bạn đã trả lời {r.SenderName ?? chat.Title}"
            : $"{m.SenderName ?? chat.Title} đã trả lời {r.SenderName ?? "bạn"}";
        var v = VStack(m.FromMe ? HAlign.Trailing : HAlign.Leading, 2,
            HStack(4, Image("arrowshape.turn.up.left.fill"), Text(label)).Font(12).Foreground(MS.Secondary),
            Text(r.Text ?? "File đính kèm").Font(14).Foreground(MS.Secondary).LineLimit(2)
                .Padding(8, 12, 14, 12)
                .Background(RoundedRectangle(18).Fill(MS.FieldBg))
                .PaddingBottom(-10)
                .Frame(maxWidth: 300, alignment: m.FromMe ? Alignment.Trailing : Alignment.Leading));
        return m.FromMe ? v.PaddingTrailing(22) : v.PaddingLeading(48);
    }
}

/// Clips its child to a path computed from its final size (per-corner radii).
public sealed class MessengerClip : Decorator, IFlexible
{
    private readonly Func<Rect, Geometry> _path;
    public MessengerClip(Control child, Func<Rect, Geometry> path) { Child = child; _path = path; }
    public bool IsFlexible(Orientation axis) => Flex.Of(Child, axis);

    protected override Size ArrangeOverride(Size final)
    {
        var s = base.ArrangeOverride(final);
        Clip = _path(new Rect(s));
        return s;
    }
}

public static class MessengerBubble
{
    public static Control Make(PlatformSession session, MessageRow row, State<bool> picking)
    {
        var m = row.Message;
        var isSticker = m.Kind == MessageKind.Sticker || (m.Kind == MessageKind.Text && Jumbo.IsJumboEmoji(m.Text));
        var fill = m.FromMe ? MS.Blue : MS.InBubble;

        Control body;
        if (m.IsDeleted)
            body = Text(m.FromMe ? "Bạn đã thu hồi một tin nhắn" : "Tin nhắn đã bị thu hồi")
                .Font(15).Foreground(MS.Secondary)
                .Padding(12, 8)
                .Overlay(Shape(row).Stroke(SColor.Hex(0xCED0D4), 1));
        else if (isSticker)
            body = m.Kind == MessageKind.Sticker
                ? MediaContent.Make(session, m, maxWidth: 120)
                : JumboText(m.Text ?? "");
        else
            switch (m.Kind)
            {
                case MessageKind.Image or MessageKind.Video or MessageKind.Gif:
                    body = Clipped(row,
                        VStack(HAlign.Leading, 0,
                                MediaContent.Make(session, m, maxWidth: 300, maxHeight: 340),
                                m.Text is { Length: > 0 } t ? TextBody(m, t).Padding(12, 8) : null)
                            .Background(fill));
                    break;
                case MessageKind.File or MessageKind.Audio or MessageKind.Voice:
                    body = FileContent.Make(session, m, m.FromMe ? SColor.White.Opacity(0.25) : MS.Grey, titleColor: m.FromMe ? SColor.White : MS.TextColor,
                            subtitleColor: m.FromMe ? SColor.White.Opacity(0.8) : MS.Secondary)
                        .Padding(12, 8)
                        .Background(Shape(row).Fill(fill));
                    break;
                default:
                    body = TextBody(m, m.Text ?? "").Padding(12, 8).Background(Shape(row).Fill(fill));
                    break;
            }

        body = body.Help(MS.GapTitle(m.Date) + (m.IsEdited ? " · Đã chỉnh sửa" : ""));

        Control? reaction = null;
        if (m.Reactions is { Count: > 0 } rs)
        {
            var total = rs.Sum(x => x.Count);
            var chips = rs.Take(3).Select(x => (Control?)Text(x.Emoji).Font(12)).ToList();
            if (total > 1) chips.Add(Text($"{total}").Font(11).Foreground(MS.Secondary).PaddingLeading(2));
            reaction = Button(() => { if (session.Can("reactions")) picking.Value = true; },
                    HStack(1, chips.ToArray()).PaddingH(4).Frame(height: 20)
                        .Background(Capsule().Fill(SColor.White).Shadow(SColor.Black.Opacity(0.2), 1.5, 0, 1))
                        .ContentShape())
                .WithReactionPicker(picking, session, m, ReactionSets.Messenger)
                .Offset(m.FromMe ? 4 : -4, 14);
        }
        return new CapWidth(460,
            body.Overlay(m.FromMe ? Alignment.BottomLeading : Alignment.BottomTrailing, reaction)
                .PaddingBottom(m.HasReactions ? 14 : 0));
    }

    /// Emoji-only message at 40 pt; the line is AppKit's 48 pt, Skia's emoji line is taller.
    private static Control JumboText(string text)
    {
        var t = Text(text).Font(40);
        t.LineHeight = 48;
        return t;
    }

    private static Control TextBody(Message m, string t)
    {
        var ink = m.FromMe ? SColor.White : MS.TextColor;
        // The 6 pt gap belongs to the quote: an empty view must not take a spacing slot.
        var col = VStack(HAlign.Leading, 0,
            // SwiftUI sets 15 pt Vietnamese text on a 19 pt line; Avalonia's natural line is a point shorter.
            new LinkText(LinkSpans.Spans(t, ink, underline: true)) { OwnFont = SFont.System(15), LineHeight = 19 }.Foreground(ink),
            View(() => Translator.Shared.IsShown(m)
                ? TranslationQuote.Make(m, m.FromMe ? SColor.White.Opacity(0.7) : MS.Secondary,
                    m.FromMe ? SColor.White.Opacity(0.9) : MS.TextColor.Opacity(0.75)).PaddingTop(6)
                : null));
        return col.Overlay(Alignment.BottomTrailing,
            m.IsEdited ? Text("Đã chỉnh sửa").Font(11).Foreground(MS.Secondary).Offset(y: 22) : null);
    }

    /// 18 pt pill, with 4 pt corners where it touches neighbours from the same sender.
    private static (double tl, double tr, double br, double bl) Radii(MessageRow row)
    {
        const double big = 18, small = 4;
        var top = row.IsFirstInGroup ? big : small;
        var bottom = row.IsLastInGroup ? big : small;
        return row.Message.FromMe ? (big, top, bottom, big) : (top, big, big, bottom);
    }

    private static ShapeView Shape(MessageRow row)
    {
        var (tl, tr, br, bl) = Radii(row);
        return Shapes.Bubble(tl, tr, br, bl);
    }

    private static Control Clipped(MessageRow row, Control c)
    {
        var (tl, tr, br, bl) = Radii(row);
        return new MessengerClip(c, r => Shapes.RoundedRect(r, tl, tr, br, bl));
    }
}

public static class MessengerComposer
{
    public static Control Make(PlatformSession session, Chat chat, bool compact = false)
    {
        var showActions = Memo(("ms.showActions", chat.Id), () => new State<bool>(false));
        var stored = Composer.Draft(session, chat.Id);
        // `.onChange(of: draft)`: typing more, or clearing the field, folds the actions back.
        var draft = new Binding<string>(() => stored.Value, v =>
        {
            var old = session.Draft(chat.Id);
            if (v.Length > old.Length || v.Length == 0) showActions.Value = false;
            stored.Value = v;
        });
        // Like messenger.com: once typing starts, the actions fold into a chevron so the field gets the width.
        bool Collapsed() => compact && session.Draft(chat.Id).Length > 0 && !showActions.Value;

        var bar = View(() =>
        {
            if (session.Editing.GetValueOrDefault(chat.Id) is { } e)
            {
                var err = session.EditError.GetValueOrDefault(chat.Id);
                return HStack(
                        VStack(HAlign.Leading, 2,
                            Text("Đang chỉnh sửa tin nhắn").Font(13, Weight.Semibold).Foreground(MS.TextColor),
                            err != null
                                ? Text(err).Font(12).Foreground(SColor.Red).LineLimit(2)
                                : Text(e.Preview()).Font(12).Foreground(MS.Secondary).LineLimit(1)),
                        Spacer(),
                        Button(() => session.CancelEdit(chat.Id), Image("xmark").Foreground(MS.Secondary)).Help("Hủy chỉnh sửa (Esc)"))
                    .PaddingH(16).PaddingV(8)
                    .Overlay(Alignment.Top, Rectangle().Fill(SColor.Black.Opacity(0.08)).Frame(height: 1));
            }
            if (session.ReplyingTo.GetValueOrDefault(chat.Id) is not { } r) return null;
            return HStack(
                    VStack(HAlign.Leading, 2,
                        Text($"Đang trả lời {(r.FromMe ? "chính mình" : r.SenderName ?? chat.Title)}")
                            .Font(13, Weight.Semibold).Foreground(MS.TextColor),
                        Text(r.Preview()).Font(12).Foreground(MS.Secondary).LineLimit(1)),
                    Spacer(),
                    Button(() => session.ReplyingTo = session.ReplyingTo.Remove(chat.Id), Image("xmark").Foreground(MS.Secondary)))
                .PaddingH(16).PaddingV(8)
                .Overlay(Alignment.Top, Rectangle().Fill(SColor.Black.Opacity(0.08)).Frame(height: 1));
        });

        var spacing = compact ? 8 : 12;

        var actions = View(() =>
        {
            var collapsed = Collapsed();
            Control Slot(Control c) => c.Font(20).Foreground(MS.Blue).PaddingBottom(collapsed ? 2 : 8);
            if (collapsed)
                return Slot(Image("chevron.right").Font(17, Weight.Semibold).Tool("Hiện các hành động", () => showActions.Value = true, 28));
            return HStack(VAlign.Bottom, spacing,
                Slot(Image("plus.circle.fill").Tool("Mở hành động khác", () => ChatTools.Attach(session, chat.Id), 28)),
                Slot(Button(async () =>
                {
                    if (await ChatTools.PickFile() is { } f) session.Send(session.Draft(chat.Id), chat.Id, f);
                }, Image("photo.on.rectangle.angled"))),
                Slot(Image("note.text").Tool("Chọn nhãn dán", Composer.Emoji, 28)),
                Slot(Text("GIF").Font(11, Weight.Heavy).PaddingH(3)
                    .Overlay(RoundedRectangle(3).Stroke(MS.Blue, 1.6))));
        });

        var editor = Composer.Editor(chat.Id, draft, "Aa",
            onSubmit: () => session.Send(session.Draft(chat.Id), chat.Id),
            font: SFont.System(15), textColor: MS.TextColor, placeholderColor: MS.Secondary,
            maxHeight: compact ? 220 : 140,
            onTypingChange: on => session.SetTyping(chat.Id, on),
            mentionSession: session, mentionChatId: chat.IsGroup ? chat.Id : null,
            onFiles: files => ChatTools.SendPasted(files, session, chat.Id),
            onCommand: key => session.ComposerCommand(key, chat.Id));

        var input = HStack(VAlign.Bottom, 8,
                editor.PaddingV(5).ComposerTapArea(),
                OutgoingLanguageButton.Make(session, chat.Id, MS.Blue, MS.Secondary, 16, 28, hideWhenIdle: Collapsed).PaddingBottom(2),
                View(() => Collapsed() ? null : Image("face.smiling.inverse").Font(19).Foreground(MS.Blue)
                    .Tool("Chọn biểu tượng cảm xúc", Composer.Emoji, 28).PaddingBottom(2)))
            .PaddingH(12)
            .Background(RoundedRectangle(18).Fill(MS.FieldBg));

        var send = View(() =>
        {
            var editing = session.Editing.ContainsKey(chat.Id);
            var thumbsUp = !editing && session.Draft(chat.Id).Length == 0 && !session.Attachments.ContainsKey(chat.Id);
            return Button(() => session.Send(thumbsUp ? "👍" : session.Draft(chat.Id), chat.Id),
                    Image(editing ? "checkmark.circle.fill" : thumbsUp ? "hand.thumbsup.fill" : "paperplane.fill").Font(21).Foreground(MS.Blue))
                .PaddingBottom(7);
        });

        return VStack(0,
            bar,
            TranslationNotice.Make(session, chat.Id),
            MathSendPreview.Make(session, chat.Id, MS.Blue),
            ComposerAttachments.Make(session, chat.Id, MS.Blue),
            HStack(VAlign.Bottom, spacing, actions, input, send).PaddingH(12).PaddingV(10));
    }
}

// MARK: Login

/// messenger.com-style onboarding: email/phone + password, then whatever extra steps Meta asks for
/// (2FA code, captcha, choice, "approve on another device") as `auth.step` from the bridge.
/// Session cookies remain available as a fallback.
public static class MessengerLogin
{
    public static Control Make(PlatformSession session) => View(() =>
    {
        var username = Remember("");
        var password = Remember("");
        var useCookies = Remember(false);

        Control group;
        if (session.AuthStep is { } step && session.AuthState == AuthState.AwaitingInput)
            group = VStack(10,
                MessengerAuthStepView.Make(session, step),
                Divider().PaddingV(8),
                MS.Link("Đăng nhập bằng cookie phiên", () => { session.AbandonStep(); useCookies.Value = true; }, 13, MS.Secondary));
        else if (useCookies.Value || !session.Can("password_login"))
            group = MessengerCookieLogin.Make(session, session.Can("password_login"), () => useCookies.Value = false);
        else
            group = Credentials(session, username, password, useCookies);

        var failure = session.AuthError ?? session.BridgeError;
        return VStack(0,
                Spacer(30),
                MessengerLogo.Make(72).PaddingBottom(26),
                group.Frame(width: 360),
                failure != null && session.AuthStep?.Error == null
                    ? Text(failure).Font(13).Foreground(MS.Error).MultilineAlignment(TextAlignment.Center).Frame(maxWidth: 360).PaddingTop(14)
                    : null,
                Spacer(30),
                Text("Thông tin đăng nhập chỉ được gửi tới Meta và lưu cục bộ trên máy này.")
                    .Font(12).Foreground(MS.Secondary).PaddingBottom(22))
            .Fill()
            .Background(SColor.White);
    });

    private static Control Credentials(PlatformSession session, State<string> username, State<string> password, State<bool> useCookies)
    {
        void Submit()
        {
            if (username.Value.Length == 0 || password.Value.Length == 0 || session.AuthBusy) return;
            session.Login("password", username: username.Value.Trim(), password: password.Value);
        }

        return VStack(12,
            new MessengerGradientText("Kết nối với những người bạn yêu quý.", MessengerLogo.Gradient).Font(32, Weight.Bold)
                .MultilineAlignment(TextAlignment.Center).PaddingBottom(16),
            MessengerTextInput.Boxed("user", "Email hoặc số điện thoại", username),
            MessengerTextInput.Boxed("pass", "Mật khẩu", password, secure: true, onSubmit: Submit),
            MessengerPrimaryButton.Make("Tiếp tục", Submit, session.AuthBusy,
                username.Value.Trim().Length == 0 || password.Value.Length == 0).PaddingTop(6),
            MS.Link("Bạn quên mật khẩu?", () => KitShell.OpenUrl("https://www.facebook.com/login/identify"), 14, MS.Brand).PaddingTop(8),
            Divider().PaddingV(10),
            MS.Link("Đăng nhập bằng cookie phiên", () => useCookies.Value = true, 13, MS.Secondary));
    }
}

/// One step of Meta's native login flow, rendered from the bridge's `auth.step`.
public static class MessengerAuthStepView
{
    /// Kept per step id, so typed values survive rebuilds of the login screen (`.id(step.stepId)`).
    public static Control Make(PlatformSession session, AuthStep step) =>
        Keep(("step", step.StepId), () => new Reactive(() => Body(session, session.AuthStep is { } s && s.StepId == step.StepId ? s : step)));

    private static Control Body(PlatformSession session, AuthStep step)
    {
        var values = new Dictionary<string, State<string>>();
        foreach (var f in step.Fields ?? [])
            values[f.Id] = Remember(f.Default ?? (f.Type == "select" ? f.Options?.FirstOrDefault() : null) ?? "", ("val", f.Id));

        bool Complete() => (step.Fields ?? []).All(f => values[f.Id].Value.Trim().Length > 0);
        void Submit()
        {
            if (!Complete() || session.AuthBusy) return;
            session.SubmitStep(values.ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        }

        var children = new List<Control?>();
        if (step.Instructions is { Length: > 0 } t)
        {
            // SwiftUI's line box for 15pt text is 19pt; Avalonia's default is about 17.5.
            var instructions = Text(t);
            instructions.LineHeight = 19;
            children.Add(instructions.Font(15).Foreground(MS.TextColor).MultilineAlignment(TextAlignment.Center));
        }
        foreach (var src in step.Images ?? [])
            if (ImageLoader.DecodeDataUrl(src) is { } img)
                children.Add(Bitmap(img, ContentMode.Fit).AspectRatio(img.PixelSize.Width / (double)img.PixelSize.Height)
                    .Frame(maxWidth: 300, maxHeight: 120).ClipRounded(8));
        if (step.Kind == "wait")
        {
            children.Add(new MessengerSpinner().PaddingV(8));
            children.Add(Text("Đang chờ bạn xác nhận…").Font(13).Foreground(MS.Secondary));
        }
        else
        {
            foreach (var f in step.Fields ?? []) children.Add(FieldView(step, f, values[f.Id], Submit));
            children.Add(MessengerPrimaryButton.Make("Tiếp tục", Submit, session.AuthBusy, !Complete()).PaddingTop(4));
        }
        if (step.Error is { Length: > 0 } e)
            children.Add(Text(e).Font(13).Foreground(MS.Error).MultilineAlignment(TextAlignment.Center));
        if (step.CanCancel ?? false)
            children.Add(MS.Link("Quay lại", session.CancelStep, 14, MS.Brand, Weight.Semibold).PaddingTop(4));
        else if (step.Fields?.Any(f => f.Type == "password") != true)
            children.Add(MS.Link("Dùng tài khoản khác", session.AbandonStep, 14, MS.Brand, Weight.Semibold).PaddingTop(4));
        return VStack(14, children.ToArray());
    }

    private static Control FieldView(AuthStep step, AuthStep.Field f, State<string> value, Action submit)
    {
        var key = (step.StepId, f.Id);
        switch (f.Type)
        {
            case "select":
                return VStack(HAlign.Leading, 6,
                    Text(f.Name).Font(13, Weight.Semibold).Foreground(MS.Secondary),
                    VStack(0, (f.Options ?? []).Select(opt =>
                        {
                            var on = value.Value == opt;
                            return (Control?)Button(() => value.Value = opt,
                                HStack(
                                        Text(opt).Font(15).Foreground(MS.TextColor),
                                        Spacer(),
                                        Image(on ? "largecircle.fill.circle" : "circle").Font(18).Foreground(on ? MS.Brand : MS.Grey))
                                    .PaddingH(14).Frame(height: 44).ContentShape());
                        }).ToArray())
                        .Background(RoundedRectangle(12).Fill(MS.FieldBg)));
            case "code":
                return MessengerTextInput.Boxed(key, f.Name, value, large: true, onSubmit: submit);
            case "password":
                return MessengerTextInput.Boxed(key, f.Name, value, secure: true, onSubmit: submit);
            default:
                return MessengerTextInput.Boxed(key, f.Name, value, onSubmit: submit);
        }
    }
}

public static class MessengerCookieLogin
{
    public static Control Make(PlatformSession session, bool canUsePassword, Action back)
    {
        var cookies = Remember("");
        var editor = Memo("cookies-editor", () => new MessengerTextInput("", cookies, SFont.System(12, Weight.Regular, ChatSpace.Kit.Design.Monospaced), multiline: true));
        var hint = new LinkText([
            new Span("Đăng nhập messenger.com trên trình duyệt rồi dán cookie phiên (c_user, xs, datr, fr) — dạng "),
            new Span("c_user=…; xs=…", Mono: true),
            new Span(" hoặc JSON xuất từ tiện ích cookie."),
        ]) { Selectable = false, OwnFont = SFont.System(13), LineHeight = 16 }.Foreground(MS.Secondary).MultilineAlignment(TextAlignment.Center);
        return VStack(12,
            new MessengerTrackedText("Đăng nhập bằng cookie", MessengerTrackedText.Tracking22).Font(22, Weight.Bold).Foreground(MS.TextColor),
            hint,
            new KeepHost(editor).Padding(8).Frame(height: 110).Background(RoundedRectangle(12).Fill(MS.FieldBg)),
            MessengerPrimaryButton.Make("Tiếp tục", () => session.Login("cookies", cookies: cookies.Value), session.AuthBusy,
                cookies.Value.Trim().Length == 0),
            canUsePassword
                ? MS.Link("Đăng nhập bằng email và mật khẩu", back, 14, MS.Brand, Weight.Semibold).PaddingTop(4)
                : null);
    }
}
