namespace ChatSpace.Core;

/// Posts per-chat notifications that behave like the official desktop apps:
/// grouped by conversation, the platform's own tone, inline reply / mark-as-read actions,
/// silent in-app sound while ChatSpace is frontmost, and withdrawn once the chat is read
/// (here or on another device). On Windows these are toast notifications (Windows.UI.Notifications).
public sealed partial class Notifier
{
    public static readonly Notifier Shared = new();
    public bool Authorized { get; private set; }

    public static string ThreadId(Platform p, string chatId) => $"{p.Raw()}|{chatId}";

    public sealed record Labels(string Reply, string Send, string Placeholder, string MarkRead);

    public static Labels LabelsFor(Platform p) => p switch
    {
        Platform.Telegram => new("Reply", "Send", "Message", "Mark as Read"),
        Platform.Whatsapp => new("Trả lời", "Gửi", "Nhập tin nhắn", "Đánh dấu là đã đọc"),
        Platform.Messenger => new("Trả lời", "Gửi", "Aa", "Đánh dấu là đã đọc"),
        _ => new("Trả lời", "Gửi", "Nhập tin nhắn", "Đánh dấu đã đọc"),
    };

    public void RequestAuthorization()
    {
        Authorized = PlatformInit();
    }

    public string StatusDescription() => Authorized ? "Đã cho phép" : "Không khả dụng";

    public void Notify(Platform p, Message m, Chat? chat)
    {
        // While ChatSpace is frontmost the official apps only play their in-app tone.
        if (AppModel.MainWindowActive)
        {
            NativeAssets.Shared.Play(p, SoundKind.Incoming);
            return;
        }
        if (!Authorized || DemoData.Enabled && App.Headless) return;
        var sender = m.SenderName ?? "";
        var title = chat?.Title ?? (sender.Length == 0 ? p.DisplayName() : sender);
        var body = Body(p, m);
        // Messenger's media wording already names the sender ("An đã gửi một ảnh.").
        var namesSender = p == Platform.Messenger && m.Kind is not (MessageKind.Text or MessageKind.Link);
        if (chat?.IsGroup == true && sender.Length > 0 && !namesSender) body = $"{ShortName(sender, p)}: {body}";
        var avatar = chat?.Avatar ?? m.SenderAvatar;
        PlatformNotify(p, m, title, body, avatar);
    }

    /// Withdraws every banner of one conversation (it was read here or elsewhere).
    public void Clear(Platform p, string chatId) => PlatformClear(p, chatId);

    /// Withdraws banners of a platform whose chats are no longer unread.
    public void Reconcile(Platform p, IReadOnlySet<string> unread) => PlatformReconcile(p, unread);

    private static string ShortName(string name, Platform p) =>
        p is Platform.Zalo or Platform.Messenger ? name.Split(' ').Last() : name;

    /// Message body worded the way each app words its notifications.
    public static string Body(Platform p, Message m)
    {
        var t = m.Text?.Trim() ?? "";
        if (m.IsDeleted) return p == Platform.Telegram ? "Message deleted" : "Tin nhắn đã được thu hồi";
        switch (p)
        {
            case Platform.Zalo:
                return m.Kind switch
                {
                    MessageKind.Image => "[Hình ảnh]",
                    MessageKind.Video or MessageKind.Gif => "[Video]",
                    MessageKind.Sticker => "[Sticker]",
                    MessageKind.Voice or MessageKind.Audio => "[Tin nhắn thoại]",
                    MessageKind.File => "[File] " + (m.Media?.FileName ?? ""),
                    MessageKind.Link => "[Link] " + t,
                    MessageKind.Location => "[Vị trí]",
                    MessageKind.Contact => "[Danh thiếp]",
                    _ => t,
                };
            case Platform.Messenger:
                var who = m.SenderName ?? "Ai đó";
                return m.Kind switch
                {
                    MessageKind.Image => $"{who} đã gửi một ảnh.",
                    MessageKind.Video or MessageKind.Gif => $"{who} đã gửi một video.",
                    MessageKind.Sticker => $"{who} đã gửi một nhãn dán.",
                    MessageKind.Voice or MessageKind.Audio => $"{who} đã gửi một tin nhắn thoại.",
                    MessageKind.File => $"{who} đã gửi một file đính kèm.",
                    _ => t,
                };
            case Platform.Whatsapp:
                return m.Preview(photo: "📷 Ảnh", video: "🎥 Video", voice: "🎤 Tin nhắn thoại", sticker: "Nhãn dán",
                    file: "📄 Tài liệu", deleted: "Tin nhắn này đã bị xóa");
            default:
                return m.Kind switch
                {
                    MessageKind.Image => t.Length == 0 ? "🖼 Photo" : "🖼 " + t,
                    MessageKind.Video => t.Length == 0 ? "📹 Video" : "📹 " + t,
                    MessageKind.Gif => "GIF",
                    MessageKind.Sticker => (t.Length == 0 ? "" : t + " ") + "Sticker",
                    MessageKind.Voice => "🎤 Voice message",
                    MessageKind.Audio => "🎵 " + (m.Media?.FileName ?? "Audio"),
                    MessageKind.File => "📎 " + (m.Media?.FileName ?? "File"),
                    MessageKind.Location => "📍 Location",
                    MessageKind.Contact => "👤 Contact",
                    _ => t,
                };
        }
    }

    /// A notification action came back (toast button, inline reply, or a click on the toast body).
    public static void HandleAction(string action, Platform p, string chatId, string? text)
    {
        var s = AppModel.Shared.Session(p);
        switch (action)
        {
            case "reply":
                if (!string.IsNullOrWhiteSpace(text)) s.Send(text, chatId, checkMath: false);
                s.MarkRead(chatId);
                break;
            case "read":
                s.MarkRead(chatId);
                break;
            default:
                App.ActivateMainWindow();
                AppModel.Shared.Open(p, chatId);
                break;
        }
    }

#if !WINDOWS
    private bool PlatformInit() => false;
    private void PlatformNotify(Platform p, Message m, string title, string body, string? avatar) { }
    private void PlatformClear(Platform p, string chatId) { }
    private void PlatformReconcile(Platform p, IReadOnlySet<string> unread) { }
#endif
}

/// The taskbar's overlay badge (macOS Dock badge): the sum of every platform's unread count.
public static partial class DockBadge
{
    public static int Current { get; private set; }

    public static void Show(int n)
    {
        Current = n;
        PlatformShow(n);
    }

    static partial void PlatformShow(int n);
}
