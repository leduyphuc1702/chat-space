using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// The hover button beside each app's reaction button; `label` draws it in that app's style.
public static class TranslateToggle
{
    public static Control Make(Message message, Func<bool, Control> label) => View(() =>
    {
        var t = Translator.Shared;
        var on = t.IsShown(message);
        return Button(() => t.Toggle(message), label(on))
            .Help(on ? "Ẩn bản dịch" : $"Dịch sang {TranslationLanguages.Name(t.Settings.Language)}");
    });
}

/// The translation inside a bubble, as an italic quote under the message text.
public static class TranslationQuote
{
    public static Control Make(Message message, SColor bar, SColor color, double size = 14) => View(() =>
    {
        var t = Translator.Shared;
        Control? body = null;
        var font = SFont.System(size) with { Italic = true };
        if (t.ShownEntry(message) is { } e)
        {
            var text = TranslationLanguages.Same(e.Source, e.Target) && e.Text == e.Original
                ? $"Tin nhắn đã là {TranslationLanguages.Name(e.Target)}" : e.Text;
            body = new LinkText([new Span(text)]) { OwnFont = font };
        }
        else if (t.IsLoading(message))
            body = HStack(6, new ProgressView { Size = ControlSize.Mini }, Text("Đang dịch…"));
        else if (t.Failure(message) is { } error)
            body = HStack(6, Text("Không dịch được"),
                MacButton.Titled("Thử lại", () => t.Translate(message), ButtonKind.Link).Font(size - 1)).Help(error);
        if (body == null) return null;
        return body.Font(font).Foreground(color).LineLimit(null)
            .PaddingLeading(9)
            .Overlay(Alignment.Leading, RoundedRectangle(1).Fill(bar).Frame(width: 2))
            .PaddingV(1);
    });
}

/// Composer button choosing the language a message goes out in (`文A` plus the language when it isn't the user's own).
public static class OutgoingLanguageButton
{
    /// `hideWhenIdle`: hide while it would only mean "send as typed" (the compact composer collapses while typing).
    public static Control Make(PlatformSession session, string chatId, SColor tint, SColor idle, double iconSize = 17, double toolSize = 32,
        Func<bool>? hideWhenIdle = null) => View(() =>
    {
        var open = Remember(false);
        var t = Translator.Shared;
        var r = new ChatRef(session.Platform, chatId);
        var reply = session.ReplyingTo.GetValueOrDefault(chatId);
        var target = t.OutgoingLanguage(r, reply);
        var active = target != null && !TranslationLanguages.Same(target, t.Settings.Language);
        var key = reply != null ? Translator.Key(reply) : null;
        var detected = Remember<string?>(null);
        if (reply != null && t.Settings.MatchReplyLanguage && detected.Value != key)
        {
            detected.Value = key;
            _ = t.DetectLanguage(reply);
        }
        if (hideWhenIdle?.Invoke() == true && target != null && !active && !t.SendError.ContainsKey(r.Id) && !open.Value) return null;
        var label = HStack(2,
                Image("translate").Font(iconSize),
                target != null && active ? Text(TranslationLanguages.Short(target)).Font(10, Weight.Bold)
                    : target == null ? new ProgressView { Size = ControlSize.Mini } : null)
            .Foreground(t.SendError.ContainsKey(r.Id) ? SColor.Red : active ? tint : idle)
            .PaddingH(active ? 3 : 0);
        return label.ToolPopover(Help(target), open, done => Picker(session, r, reply, done), toolSize);
    });

    private static string Help(string? target)
    {
        if (target == null) return "Đang nhận diện ngôn ngữ tin nhắn được trả lời…";
        var own = Translator.Shared.Settings.Language;
        if (TranslationLanguages.Same(target, own)) return $"Gửi nguyên văn ({TranslationLanguages.Name(own)}) — bấm để chọn ngôn ngữ gửi";
        return $"Tự dịch sang {TranslationLanguages.Name(target)} trước khi gửi";
    }

    private static Control Picker(PlatformSession session, ChatRef r, Message? reply, Action done) => View(() =>
    {
        var t = Translator.Shared;
        var own = t.Settings.Language;
        var replyAuto = reply != null && t.Settings.MatchReplyLanguage && Translator.HasWords(reply.Text);
        var followingReply = replyAuto && !t.ReplyAutoOff.Contains(r.Id);
        var chosen = t.ChatLanguage(r);
        var rows = new List<Control?>
        {
            Text("Gửi tin nhắn bằng").Font(11.5, Weight.Semibold).Foreground(SColor.Secondary).Padding(4, 10, 2, 10),
        };
        if (replyAuto)
        {
            var detected = t.KnownLanguage(reply!);
            rows.Add(Row("Theo tin nhắn đang trả lời" + (detected != null ? " · " + TranslationLanguages.Name(detected) : "…"),
                "arrowshape.turn.up.left", followingReply, () => t.ReplyAutoOff = t.ReplyAutoOff.Remove(r.Id), done));
            rows.Add(Divider().PaddingV(3));
        }
        var langs = VStack(HAlign.Leading, 2, TranslationLanguages.All.Select(l =>
        {
            var isOwn = TranslationLanguages.Same(l.Code, own);
            return (Control?)Row(l.Name + (isOwn ? " (không dịch)" : ""), null,
                !followingReply && TranslationLanguages.Same(l.Code, chosen), () =>
                {
                    t.SetChatLanguage(l.Code, r);
                    if (replyAuto) t.ReplyAutoOff = t.ReplyAutoOff.Add(r.Id);
                }, done);
        }).ToArray());
        rows.Add(ScrollView(langs).Frame(maxHeight: 300).FixedSize(horizontal: false, vertical: true));
        if (t.SendError.GetValueOrDefault(r.Id) is { } e)
        {
            rows.Add(Divider().PaddingV(3));
            rows.Add(Text(e).Font(11.5).Foreground(SColor.Red).LineLimit(null).Padding(0, 10, 4, 10));
        }
        return VStack(HAlign.Leading, 2, rows.ToArray()).Padding(6).Frame(width: 290).Scheme(Scheme.Light);
    });

    private static Control Row(string title, string? icon, bool selected, Action action, Action done) =>
        Button(() => { action(); done(); }, new HoverBox(h =>
            HStack(8,
                    Image("checkmark").Font(11, Weight.Semibold).Opacity(selected ? 1 : 0),
                    icon != null ? Image(icon).Font(12).Foreground(SColor.Secondary) : null,
                    Text(title).Font(13),
                    Spacer(0))
                .PaddingH(10).Frame(height: 28).ContentShape()
                .Background(RoundedRectangle(5).Fill(SColor.Black.Opacity(h ? 0.06 : 0)))));
}

/// Shown above a composer when a message was held back because it could not be translated.
public static class TranslationNotice
{
    public static Control Make(PlatformSession session, string chatId) => View(() =>
    {
        var id = new ChatRef(session.Platform, chatId).Id;
        if (Translator.Shared.SendError.GetValueOrDefault(id) is not { } e) return null;
        return HStack(VAlign.Top, 8,
                Image("exclamationmark.triangle.fill").Foreground(SColor.Orange),
                Text(e).Font(12).Foreground(SColor.Hex(0x333333)).LineLimit(null),
                Spacer(4),
                Button(() => Translator.Shared.SetSendError(id, null), Image("xmark").Foreground(SColor.Secondary)))
            .Padding(8)
            .Background(RoundedRectangle(6).Fill(SColor.Hex(0xFFF4E5)))
            .Padding(8, 12, 0, 12);
    });
}

/// Shown above a composer when the message being sent contains arithmetic: send it with the results or as typed.
public static class MathSendPreview
{
    public static Control Make(PlatformSession session, string chatId, SColor accent) => View(() =>
    {
        var draft = session.Draft(chatId).Trim();
        if (session.MathPreviews.GetValueOrDefault(chatId) is not { } p || p.Original != draft) return null;
        var spans = p.Pieces.Select(piece => piece.IsResult
            ? new Span(piece.Text, accent, Weight.Bold)
            : new Span(piece.Text)).ToList();
        var text = new LinkText(spans) { OwnFont = SFont.System(13) }.Foreground(SColor.Hex(0x333333));
        return VStack(HAlign.Leading, 8,
                HStack(6,
                    Image("function").Foreground(accent),
                    Text("Tin nhắn có phép tính").Font(12, Weight.Semibold),
                    Spacer(4),
                    Button(() => session.MathPreviews = session.MathPreviews.Remove(chatId), Image("xmark").Foreground(SColor.Secondary))
                        .Help("Huỷ gửi để sửa tiếp (Esc)")),
                ScrollView(text.FillWidth(Alignment.Leading)).Frame(maxHeight: 132).FixedSize(horizontal: false, vertical: true)
                    .Padding(8).Background(RoundedRectangle(6).Fill(SColor.White)),
                HStack(8,
                    Button(() => session.ResolveMath(chatId, computed: true),
                            Text("Gửi bản có kết quả  ↩").Font(12.5, Weight.Semibold).Foreground(SColor.White)
                                .PaddingH(12).Frame(height: 28).Background(RoundedRectangle(6).Fill(accent)))
                        .Help("Gửi kèm kết quả. Nhấn Enter lần nữa trong khung soạn tin cũng gửi bản này."),
                    Button(() => session.ResolveMath(chatId, computed: false),
                            Text("Gửi bản gốc").Font(12.5).Foreground(SColor.Hex(0x333333))
                                .PaddingH(12).Frame(height: 28).Background(RoundedRectangle(6).StrokeBorder(SColor.Black.Opacity(0.15))))
                        .Help("Gửi đúng như đã gõ, không thêm kết quả"),
                    Spacer(0)))
            .Padding(10)
            .Background(RoundedRectangle(8).Fill(SColor.Hex(0xF2F4F7)))
            .Padding(8, 12, 0, 12);
    });
}

/// The member list that floats above a group composer while "@name" is being typed.
public static class MentionSuggestions
{
    public static Control? Make(PlatformSession session, string chatId, SColor accent)
    {
        var center = Mentions.Shared;
        if (center.Current is not { } q || q.Ref.ChatId != chatId || q.Ref.Platform != session.Platform) return null;
        var list = center.Candidates(session, chatId);
        Control body;
        if (list.Count == 0)
        {
            var loading = !session.Members.ContainsKey(chatId);
            body = HStack(8,
                    loading ? new ProgressView { Size = ControlSize.Small } : null,
                    Text(loading ? "Đang tải thành viên…" : "Không tìm thấy thành viên").Font(12.5).Foreground(SColor.Secondary))
                .Padding(10);
        }
        else
        {
            var rows = VStack(0, list.Select((m, i) => (Control?)Row(session, chatId, m, i == center.Index, accent)).ToArray());
            var sv = ScrollView(rows);
            body = sv.Frame(height: Math.Min(list.Count, 5) * 40);
            var idx = center.Index;
            sv.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var y = idx * 40.0;
                if (y < sv.Offset.Y) sv.Offset = new Vector(0, y);
                else if (y + 40 > sv.Offset.Y + 200) sv.Offset = new Vector(0, y + 40 - 200);
            });
        }
        return body.Frame(width: 280)
            .Background(RoundedRectangle(8).Fill(SColor.White).Shadow(SColor.Black.Opacity(0.18), 8, 0, 2))
            .Scheme(Scheme.Light)
            .Padding(0, 12, 6, 12);
    }

    private static Control Row(PlatformSession session, string chatId, ChatMember m, bool selected, SColor accent) =>
        Button(() => Mentions.Shared.Pick(m, session, chatId),
            HStack(10,
                    AvatarView.Make(m.Avatar, 28, () => Circle().Fill(SColor.Hsb(m.Id.StableHash() % 360 / 360.0, 0.45, 0.85))
                        .Overlay(Text(m.Name.Initials()).Font(11, Weight.Semibold).Foreground(SColor.White))),
                    Text(m.Name).Font(13.5).Foreground(SColor.Black.Opacity(0.85)).LineLimit(1),
                    Spacer(0))
                .PaddingH(10).Frame(height: 40)
                .Background(selected ? accent.Opacity(0.12) : SColor.Clear)
                .ContentShape());

    /// Floats the "@" member list above this composer for group chats, growing upward over the messages.
    public static Control WithMentionSuggestions(this Control composer, PlatformSession session, Chat chat, SColor? accent = null)
    {
        if (!chat.IsGroup) return composer;
        var list = View(() => session.Can("mentions") ? Make(session, chat.Id, accent ?? SColor.Accent)?.FixedSize() : null);
        return composer.Overlay(Alignment.TopLeading, new HangAbove(list));
    }
}

/// Lays its child out above its own top edge (`Color.clear.frame(height: 0).overlay(alignment: .bottomLeading)`).
public sealed class HangAbove : Decorator
{
    public HangAbove(Control child) { Child = child; ClipToBounds = false; }

    protected override Size MeasureOverride(Size available)
    {
        Child?.Measure(new Size(available.Width, double.PositiveInfinity));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Child is { } c) c.Arrange(new Rect(0, -c.DesiredSize.Height, c.DesiredSize.Width, c.DesiredSize.Height));
        return final;
    }
}

/// Right-click actions ChatSpace adds to chats in any list.
public static class ChatMenuItems
{
    private static readonly uint[] GroupColors = [0x5E5CE6, 0x0A84FF, 0x30D158, 0xFF9F0A, 0xFF375F, 0xBF5AF2];

    public static IEnumerable<MenuEntry> Build(ChatRef r, Chat chat, bool inInbox = false)
    {
        var ws = Workspace.Shared;
        var app = AppModel.Shared;
        var session = app.Session(r.Platform);
        var items = new List<MenuEntry> { MenuEntry.Item("Mở bong bóng chat", () => ChatHeads.Shared.Show(r, expand: true)) };
        if (chat.HasUnread) items.Add(MenuEntry.Item("Đánh dấu đã đọc", () => session.MarkRead(chat.Id)));
        items.Add(MenuEntry.Separator);
        if (ws.IsIgnored(r)) items.Add(MenuEntry.Item("Hiện lại trong mục Chưa đọc của ChatSpace", () => ws.Restore(r)));
        else
            items.Add(MenuEntry.Item(inInbox ? "Bỏ qua cho lần sau" : "Bỏ qua trong mục Chưa đọc của ChatSpace", () =>
            {
                ws.Ignore(r, chat.Title);
                if (app.AggregateSelection.TryGetValue(RailItem.Inbox, out var sel) && sel == r)
                    app.AggregateSelection = app.AggregateSelection.Remove(RailItem.Inbox);
            }));
        if (!inInbox || app.Rail != RailItem.For(r.Platform))
            items.Add(MenuEntry.Item($"Mở trong {r.Platform.DisplayName()}", () => app.Open(r.Platform, chat.Id)));
        var groupItems = ws.Groups.Select(g => MenuEntry.Item(g.Name, () =>
        {
            var updated = g.Matches(chat, r.Platform) ? g : g with { Keywords = g.Keywords.Append(chat.Title).ToList() };
            ws.Upsert(updated);
        })).ToList();
        groupItems.Add(MenuEntry.Separator);
        groupItems.Add(MenuEntry.Item("Nhóm mới từ tên này…", () =>
        {
            var words = System.Text.RegularExpressions.Regex.Split(chat.Title, @"[^\p{L}]+").Where(w => w.Length > 0).ToList();
            var name = words.FirstOrDefault() ?? chat.Title;
            var g = new CustomGroup { Name = name, Keywords = [name], Match = CustomGroup.MatchKind.Word,
                ColorHex = GroupColors[Random.Shared.Next(GroupColors.Length)] };
            ws.Upsert(g);
            app.Rail = RailItem.Group(g.Id);
        }));
        items.Add(MenuEntry.Sub("Thêm vào nhóm tuỳ chỉnh", groupItems));
        return items;
    }

    public static T ChatContextMenu<T>(this T c, Platform platform, Chat chat, bool inInbox = false) where T : Control =>
        c.ContextMenu(() => Build(new ChatRef(platform, chat.Id), chat, inInbox));
}
