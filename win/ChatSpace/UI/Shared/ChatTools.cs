using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

public static class ChatTools
{
    internal static Window? ActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l)
            return l.Windows.FirstOrDefault(w => w.IsActive) ?? l.MainWindow;
        return null;
    }

    /// Calls, stickers, contact cards… need the official client; offer to open it.
    public static async void NativeOnly(Platform platform, string feature)
    {
        var app = NativeAssets.Shared.AppLaunch(platform);
        var name = platform.DisplayName();
        var message = app != null
            ? $"Tính năng này chỉ dùng được trong app {name} chính thức. Mở {name} ngay?"
            : $"Tính năng này chỉ dùng được trong app {name} chính thức (chưa cài trên máy này).";
        var buttons = app != null
            ? new List<Alert.Choice> { new($"Mở {name}", true), new("Huỷ") }
            : new List<Alert.Choice> { new("OK", true) };
        var choice = await Alert.Run($"{feature} chưa có trong ChatSpace", message, buttons, ActiveWindow());
        if (choice == 0 && app != null) KitShell.Open(app);
    }

    public static void Call(Platform platform, bool video) => NativeOnly(platform, video ? "Gọi video" : "Gọi thoại");

    /// Zalo's "Chụp màn hình": interactive capture, then send the image to the chat.
    public static async void Screenshot(PlatformSession session, string chatId)
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatspace-{Guid.NewGuid()}.png");
        if (OperatingSystem.IsMacOS())
        {
            var p = System.Diagnostics.Process.Start("/usr/sbin/screencapture", ["-i", "-x", path]);
            if (p == null) return;
            await p.WaitForExitAsync();
        }
        else if (OperatingSystem.IsWindows())
        {
            // The Snipping Tool overlay puts the capture on the clipboard.
            if (ActiveWindow()?.Clipboard is not { } clip) return;
            await clip.ClearAsync();
            KitShell.Open("ms-screenclip:");
            for (var i = 0; i < 240; i++)
            {
                await Task.Delay(500);
                if (await Composer.ClipboardAttachments(clip) is { Count: > 0 } files) { path = files[0]; break; }
            }
        }
        if (File.Exists(path)) session.Send(session.Draft(chatId), chatId, path);
    }

    public static async Task<string?> PickFile(string? title = null)
    {
        if (ActiveWindow()?.StorageProvider is not { } sp) return null;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = title });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async void Attach(PlatformSession session, string chatId)
    {
        if (await PickFile() is { } f) session.Send(session.Draft(chatId), chatId, f);
    }

    /// Pasted or dropped images/files wait above the composer, so a caption can be typed before Return sends them together.
    public static void SendPasted(IReadOnlyList<string> files, PlatformSession session, string chatId)
    {
        if (files.Count == 0) return;
        session.Stage(files, chatId);
        ActiveWindow()?.Activate();
        Composer.Focus();
    }
}

/// Thumbnails of the chat's pasted images/files above the composer; × removes one.
public static class ComposerAttachments
{
    public static Control Make(PlatformSession session, string chatId, SColor? accent = null) => View(() =>
    {
        if (session.Attachments.GetValueOrDefault(chatId) is not { Count: > 0 } files) return null;
        var row = HStack(8, files.Select(f => Thumb(f, accent ?? SColor.Accent, () => session.Unstage(f, chatId))).ToArray());
        return ScrollView(row.Padding(10, 16, 2, 16), vertical: false, horizontal: true, indicators: false);
    });

    private static Control Thumb(string path, SColor accent, Action remove)
    {
        Control body = PlatformSession.IsImagePath(path) && ImageLoader.Shared.LoadLocalNow(path, 144) is { } img
            ? Bitmap(img)
            : VStack(4,
                Image("doc.fill").Font(22).Foreground(accent),
                Text(Path.GetFileName(path)).Font(10).LineLimit(2).MultilineAlignment(TextAlignment.Center).Foreground(SColor.Secondary))
                .Padding(4);
        return body.Frame(width: 72, height: 72)
            .Background(SColor.Black.Opacity(0.06))
            .ClipRounded(8)
            .Overlay(RoundedRectangle(8).Stroke(SColor.Black.Opacity(0.1)))
            .Overlay(Alignment.TopTrailing,
                Button(remove, ResizableSymbol("xmark.circle.fill").Frame(width: 16, height: 16)
                        .Foreground(SColor.Black.Opacity(0.6))
                        .Background(Circle().Fill(SColor.White).Padding(3)))
                    .Padding(3).Help("Bỏ tệp này"))
            .Help(Path.GetFileName(path));
    }
}

/// Records a voice note; the second click sends it as a file.
public sealed class VoiceRecorder : Observable
{
    public static readonly VoiceRecorder Shared = new();

    private ChatRef? _recordingChat;
    public ChatRef? RecordingChat { get => Get(ref _recordingChat); private set => Set(ref _recordingChat, value); }
    private DateTime? _startedAt;
    public DateTime? StartedAt { get => Get(ref _startedAt); private set => Set(ref _startedAt, value); }
    private string? _path;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? ret, int retLength, IntPtr callback);

    public bool IsRecording(ChatRef r) => RecordingChat == r;

    public void Toggle(ChatRef r)
    {
        if (RecordingChat == r) Stop(send: true); else Start(r);
    }

    public void Start(ChatRef r)
    {
        Stop(send: false);
        if (!OperatingSystem.IsWindows())
        {
            _ = Alert.Info("Chưa ghi âm được trên máy này", "Ghi âm tin nhắn thoại dùng micro của Windows.");
            return;
        }
        mciSendString("open new type waveaudio alias csvoice", null, 0, IntPtr.Zero);
        mciSendString("set csvoice bitspersample 16 channels 1 samplespersec 24000", null, 0, IntPtr.Zero);
        if (mciSendString("record csvoice", null, 0, IntPtr.Zero) != 0)
        {
            mciSendString("close csvoice", null, 0, IntPtr.Zero);
            _ = Alert.Info("ChatSpace chưa có quyền dùng micro", "Bật trong Cài đặt → Quyền riêng tư & bảo mật → Micrô.");
            return;
        }
        _path = Path.Combine(Path.GetTempPath(), $"voice-{DateTimeOffset.Now.ToUnixTimeSeconds()}.wav");
        RecordingChat = r;
        StartedAt = DateTime.Now;
    }

    public void Stop(bool send)
    {
        if (RecordingChat is not { } r || _path is not { } path || StartedAt is not { } start) return;
        var longEnough = (DateTime.Now - start).TotalSeconds >= 0.7;
        mciSendString("stop csvoice", null, 0, IntPtr.Zero);
        if (send && longEnough) mciSendString($"save csvoice \"{path}\"", null, 0, IntPtr.Zero);
        mciSendString("close csvoice", null, 0, IntPtr.Zero);
        RecordingChat = null;
        StartedAt = null;
        _path = null;
        if (send && longEnough && File.Exists(path)) AppModel.Shared.Session(r.Platform).Send("", r.ChatId, path);
        else try { File.Delete(path); } catch { }
    }
}

public static class VoiceTools
{
    /// Click: start/stop-and-send a voice note. Right-click while recording: cancel.
    public static Control VoiceTool(Func<Control> icon, PlatformSession session, string chatId)
    {
        var r = new ChatRef(session.Platform, chatId);
        return View(() =>
        {
            var rec = VoiceRecorder.Shared;
            Control label;
            if (rec.IsRecording(r) && rec.StartedAt is { } start)
                label = Timeline(TimeSpan.FromSeconds(1), now =>
                {
                    var s = (int)(now - start).TotalSeconds;
                    return HStack(5,
                        Circle().Fill(SColor.Red).Frame(width: 8, height: 8),
                        Text($"{s / 60}:{s % 60:00}").Font(SFont.System(12, Weight.Semibold).Monospaced()),
                        Image("arrow.up.circle.fill").Font(18).Foreground(SColor.Red)).Foreground(SColor.Red);
                });
            else label = icon();
            return label.Tool(rec.IsRecording(r) ? "Bấm để gửi tin nhắn thoại" : "Ghi âm tin nhắn thoại", () => rec.Toggle(r))
                .ContextMenu(() => rec.IsRecording(r) ? [MenuEntry.Item("Huỷ ghi âm", () => rec.Stop(send: false))] : []);
        });
    }
}

/// Search the loaded history of one chat; picking a result scrolls the conversation to it.
public static class InChatSearch
{
    public static Control Make(PlatformSession session, string chatId, Action dismiss, string placeholder = "Tìm tin nhắn") =>
        View(() =>
        {
            var q = Remember("");
            var field = Keep("q", () =>
            {
                var f = new Field(placeholder, q, FieldStyle.RoundedBorder);
                f.FocusField();
                return f;
            });
            var needle = CustomGroup.Normalize(q.Value);
            var all = session.MessagesIn(chatId);
            var hits = needle.Length == 0 ? [] : all.Reverse().Where(m => CustomGroup.Normalize(m.Text ?? m.Media?.FileName ?? "").Contains(needle)).ToList();
            var results = VStack(HAlign.Leading, 2, hits.Take(60).Select(m => (Control?)Button(() =>
            {
                session.ScrollTarget = session.ScrollTarget.SetItem(chatId, m.Id);
                dismiss();
            }, VStack(HAlign.Leading, 2,
                    HStack(Text(m.FromMe ? "Bạn" : m.SenderName ?? "").Font(11, Weight.Semibold), Spacer(),
                        Text(m.Date.ToString("d MMM, HH:mm", new System.Globalization.CultureInfo("vi-VN"))).Font(10).Foreground(SColor.Secondary)),
                    Text(m.Text ?? m.Media?.FileName ?? "").Font(12).LineLimit(2))
                .Padding(6).FillWidth(Alignment.Leading).ContentShape())).ToArray());
            return VStack(HAlign.Leading, 8,
                    field,
                    needle.Length == 0 ? null
                        : Text(hits.Count == 0 ? $"Không tìm thấy trong {all.Count} tin đã tải" : $"{hits.Count} kết quả").Font(SFont.Caption).Foreground(SColor.Secondary),
                    needle.Length == 0 ? null : ScrollView(results).Frame(height: 260),
                    !session.ReachedStart.Contains(chatId) && needle.Length > 0
                        ? MacButton.Titled("Tải thêm tin cũ để tìm", () => session.LoadOlder(chatId), size: ControlSize.Small)
                        : null)
                .Padding(12).Frame(width: 320);
        });
}

/// Conversation info: who/what this chat is, plus ChatSpace actions.
public static class ChatInfoPanel
{
    public static Control Make(PlatformSession session, Chat chat) => View(() =>
    {
        var r = new ChatRef(session.Platform, chat.Id);
        var groups = Workspace.Shared.Groups.Where(g => g.Matches(chat, session.Platform)).ToList();
        Control Info(string k, string v) => HStack(VAlign.Top, null, Text(k).Foreground(SColor.Secondary), Spacer(),
            Text(v).MultilineAlignment(TextAlignment.Right)).Font(12);
        var kind = chat.Kind switch
        {
            ChatKind.Direct => "Trò chuyện cá nhân",
            ChatKind.Group => chat.MemberCount is { } n ? $"Nhóm · {n} thành viên" : "Nhóm",
            ChatKind.Channel => "Kênh",
            ChatKind.Bot => "Bot",
            _ => "Tin nhắn đã lưu",
        };
        return VStack(10,
                AvatarView.Make(chat.Avatar, 72, () => Circle().Fill(SColor.Gray.Opacity(0.3))
                    .Overlay(Text(chat.Title.Length > 0 ? chat.Title[..1] : "").Font(28, Weight.Semibold).Foreground(SColor.White))),
                Text(chat.Title).Font(16, Weight.Semibold).MultilineAlignment(TextAlignment.Center),
                HStack(6, new PlatformIcon(session.Platform, 14), Text(kind).Font(SFont.Caption).Foreground(SColor.Secondary)),
                Divider(),
                VStack(HAlign.Leading, 6,
                    Info("Thông báo", chat.IsMuted ? "Đã tắt" : "Đang bật"),
                    chat.HasUnread ? Info("Chưa đọc", chat.Unread > 0 ? $"{chat.Unread} tin" : "Đánh dấu chưa đọc") : null,
                    Info("Tin đã tải", $"{session.MessagesIn(chat.Id).Count}"),
                    groups.Count > 0 ? Info("Nhóm tuỳ chỉnh", string.Join(", ", groups.Select(g => g.Name))) : null),
                Divider(),
                VStack(6,
                    MacButton.Titled("Mở bong bóng chat", () => ChatHeads.Shared.Show(r, expand: true), ButtonKind.Link),
                    Workspace.Shared.IsIgnored(r)
                        ? MacButton.Titled("Hiện lại trong Chưa đọc của ChatSpace", () => Workspace.Shared.Restore(r), ButtonKind.Link)
                        : MacButton.Titled("Bỏ qua trong Chưa đọc của ChatSpace", () => Workspace.Shared.Ignore(r, chat.Title), ButtonKind.Link)))
            .Padding(16).Frame(width: 260);
    });
}
