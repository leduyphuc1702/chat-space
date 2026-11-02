#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ChatSpace.Core;

public sealed partial class Notifier
{
    /// Toast group (max 64 chars) → (platform, chat), so read chats can withdraw their banners.
    private readonly Dictionary<string, (Platform p, string chatId)> _groups = new();

    private static string Group(Platform p, string chatId)
    {
        var g = $"{p.Raw()}|{chatId}";
        return g.Length <= 64 ? g : $"{p.Raw()}|{chatId.StableHash():x}";
    }

    private bool PlatformInit()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                if (!args.TryGetValue("platform", out var raw) || Platforms.Parse(raw) is not { } p) return;
                if (!args.TryGetValue("chatId", out var chatId)) return;
                var action = args.TryGetValue("action", out var a) ? a : "open";
                var text = e.UserInput.TryGetValue("reply", out var v) ? v as string : null;
                Dispatcher.UIThread.Post(() => HandleAction(action, p, chatId, text));
            };
            return true;
        }
        catch { return false; }
    }

    private async void PlatformNotify(Platform p, Message m, string title, string body, string? avatar)
    {
        var labels = LabelsFor(p);
        var group = Group(p, m.ChatId);
        _groups[group] = (p, m.ChatId);
        string? logo = null;
        if (avatar is { Length: > 0 })
        {
            var load = AvatarFile(avatar, group);
            if (await Task.WhenAny(load, Task.Delay(1500)) == load) logo = load.Result;
        }
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddArgument("platform", p.Raw())
                .AddArgument("chatId", m.ChatId)
                .AddText(title)
                .AddText(body)
                .AddAttributionText(p.DisplayName())
                // The platform's own tone is played by ChatSpace; Windows can't play custom files for unpackaged apps.
                .AddAudio(new ToastAudio { Silent = true });
            if (logo != null) b.AddAppLogoOverride(new Uri(logo), ToastGenericAppLogoCrop.Circle);
            b.AddInputTextBox("reply", labels.Placeholder)
                .AddButton(new ToastButton()
                    .SetContent(labels.Send)
                    .SetTextBoxId("reply")
                    .AddArgument("action", "reply")
                    .AddArgument("platform", p.Raw())
                    .AddArgument("chatId", m.ChatId)
                    .SetBackgroundActivation())
                .AddButton(new ToastButton()
                    .SetContent(labels.MarkRead)
                    .AddArgument("action", "read")
                    .AddArgument("platform", p.Raw())
                    .AddArgument("chatId", m.ChatId)
                    .SetBackgroundActivation());
            var tag = m.Id.Length <= 64 ? m.Id : $"{m.Id.StableHash():x}";
            b.Show(t => { t.Tag = tag; t.Group = group; });
            NativeAssets.Shared.Play(p, SoundKind.Incoming);
        }
        catch { }
    }

    private void PlatformClear(Platform p, string chatId)
    {
        try { ToastNotificationManagerCompat.History.RemoveGroup(Group(p, chatId)); } catch { }
    }

    private void PlatformReconcile(Platform p, IReadOnlySet<string> unread)
    {
        foreach (var (g, (gp, chatId)) in _groups.ToList())
        {
            if (gp != p || unread.Contains(chatId)) continue;
            PlatformClear(p, chatId);
            _groups.Remove(g);
        }
    }

    /// Circular 128 px PNG of the avatar, as the official apps attach to their banners.
    private static async Task<string?> AvatarFile(string source, string id)
    {
        var img = await ImageLoader.Shared.Load(source, 256);
        if (img == null) return null;
        const int side = 128;
        using var rtb = new RenderTargetBitmap(new PixelSize(side, side));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var rect = new Rect(0, 0, side, side);
            var s = img.Size;
            var scale = Math.Max(side / s.Width, side / s.Height);
            var src = new Rect((s.Width - side / scale) / 2, (s.Height - side / scale) / 2, side / scale, side / scale);
            using (ctx.PushGeometryClip(new EllipseGeometry(rect)))
                ctx.DrawImage(img, src, rect);
        }
        var dir = Path.Combine(Path.GetTempPath(), "ChatSpaceNotifications");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id.StableHash():x}.png");
        rtb.Save(path);
        return path;
    }
}

public static partial class DockBadge
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    private class TaskbarList;

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private static ITaskbarList3? _taskbar;
    private static IntPtr _icon;
    private static string? _shown;

    /// A red pill with the count, drawn over the taskbar button (the Windows counterpart of the Dock badge).
    static partial void PlatformShow(int n)
    {
        var label = n <= 0 ? null : n > 99 ? "99+" : n.ToString();
        if (label == _shown) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var hwnd = App.MainWindowHandle;
                if (hwnd == IntPtr.Zero) return;
                if (_taskbar == null)
                {
                    _taskbar = (ITaskbarList3)new TaskbarList();
                    _taskbar.HrInit();
                }
                var old = _icon;
                _icon = label == null ? IntPtr.Zero : Icon(label);
                _taskbar.SetOverlayIcon(hwnd, _icon, label == null ? null : $"{label} tin nhắn chưa đọc");
                if (old != IntPtr.Zero) DestroyIcon(old);
                _shown = label;
            }
            catch { }
        });
    }

    private static IntPtr Icon(string label)
    {
        const int side = 32;
        using var rtb = new RenderTargetBitmap(new PixelSize(side, side));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyle.Normal, FontWeight.SemiBold), label.Length > 2 ? 13 : label.Length > 1 ? 16 : 19,
                Brushes.White);
            ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), null, new Rect(0, 0, side, side));
            ctx.DrawText(text, new Point((side - text.Width) / 2, (side - text.Height) / 2));
        }
        using var ms = new MemoryStream();
        rtb.Save(ms);
        var png = ms.ToArray();
        return CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, side, side, 0);
    }
}
#endif
