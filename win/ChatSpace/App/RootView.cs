using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ChatSpace.Core;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// Left rail + whatever it selects: the ChatSpace inbox, one platform's own full UI, or a custom group.
/// Only the selected view is mounted: hidden views would otherwise keep receiving clicks and stealing focus.
/// Selection, drafts and replies live in the sessions.
public sealed class RootView : Reactive
{
    public RootView() : base(Body) { }

    private static Control Body()
    {
        var model = AppModel.Shared;
        return HStack(0,
            new PlatformRail(),
            View(() => model.Rail.Kind switch
            {
                RailItem.RailKind.Inbox => new AggregateView(model.Rail),
                RailItem.RailKind.Group => new AggregateView(model.Rail),
                _ => PlatformView(model.Rail.Platform),
            }).Fill()
        ).Background(SColor.WindowBackground);
    }

    private static Control PlatformView(Platform p)
    {
        var s = AppModel.Shared.Session(p);
        return p switch
        {
            Platform.Zalo => new ZaloRootView(s),
            Platform.Messenger => new MessengerRootView(s),
            Platform.Whatsapp => new WhatsAppRootView(s),
            _ => new TelegramRootView(s),
        };
    }
}

/// Thin vertical switcher (like a dock): ChatSpace inbox, each platform's app icon, custom groups.
public sealed class PlatformRail : Reactive
{
    public const double RailWidth = 68;

    public PlatformRail() : base(Body) { }

    private static Control Body()
    {
        var model = AppModel.Shared;
        var ws = Workspace.Shared;
        var items = new List<Control?>();
        if (ws.ShowInbox)
        {
            items.Add(Item(RailItem.Inbox, "ChatSpace — tin chưa đọc (Ctrl+0)", model.InboxBadge, false, new ChatSpaceIcon(38)));
            items.Add(Divider30());
        }
        foreach (var p in Platforms.All)
        {
            var s = model.Session(p);
            items.Add(Item(RailItem.For(p), p.DisplayName(), s.UnreadTotal,
                s.AuthState != AuthState.LoggedIn && s.AuthState != AuthState.Connecting, new PlatformIcon(p, 38)));
        }
        items.Add(Divider30());
        foreach (var g in ws.Groups)
        {
            var item = Item(RailItem.Group(g.Id), $"{g.Name} — {string.Join(", ", g.Keywords)}", model.GroupBadge(g), false, new GroupIcon(g, 38));
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Sửa nhóm…" };
            edit.Click += (_, _) => GroupEditor.Present(g);
            var del = new MenuItem { Header = "Xoá nhóm" };
            del.Click += (_, _) => ws.Delete(g.Id);
            menu.Items.Add(edit);
            menu.Items.Add(del);
            item.ContextMenu = menu;
            items.Add(item);
        }
        items.Add(Button(() => GroupEditor.Present(new CustomGroup { Name = "", Keywords = [] }),
                RoundedRectangle(11, true).StrokeBorder(SColor.White.Opacity(0.28), 1.5, [4, 3])
                    .Frame(38, 38)
                    .Overlay(Image("plus").Font(16, Weight.Semibold).Foreground(SColor.White.Opacity(0.7)))
                    .Padding(5))
            .Help("Tạo nhóm tuỳ chỉnh: gom các đoạn chat có tên chứa từ khoá"));

        return VStack(0,
            Clear.Frame(height: 30), // traffic lights
            ScrollView(VStack(14, items.ToArray()).Frame(width: RailWidth).PaddingV(4), indicators: false).FillHeight(),
            Updater.Shared.ReadyVersion is { } v
                ? Button(Updater.Shared.RestartToUpdate,
                        Image("arrow.down.circle.fill").Font(22).Foreground(SColor.Hex(0x34C759)).Frame(44, 36))
                    .Help($"Đã tải ChatSpace {v} — bấm để khởi động lại và cập nhật (hoặc tự cập nhật khi thoát app)")
                    .PaddingTop(8)
                : null,
            Button(SettingsWindow.ShowShared,
                    Image("gearshape").Font(17).Foreground(SColor.White.Opacity(0.7)).Frame(44, 36))
                .Help("Cài đặt")
                .PaddingV(12)
        ).Frame(width: RailWidth).FillHeight().Background(SColor.Hex(0x1C1C1E));
    }

    private static Control Divider30() => Rectangle().Fill(SColor.White.Opacity(0.14)).Frame(30, 1);

    private static Control Item(RailItem r, string help, int badge, bool warn, Control icon)
    {
        var selected = AppModel.Shared.Rail == r;
        Control label = icon.Padding(5)
            .Background(RoundedRectangle(12, true).Fill(selected ? SColor.White.Opacity(0.14) : SColor.Clear));
        if (selected) label = label.Overlay(Capsule().Fill(SColor.White).Frame(3, 22).Offset(x: -10), Alignment.Leading);
        if (badge > 0) label = label.Overlay(new RailBadge(badge).Offset(4, -3), Alignment.TopTrailing);
        else if (warn) label = label.Overlay(Circle().Fill(SColor.Orange).Frame(9, 9).Offset(1, 1), Alignment.TopTrailing);
        return Button(() => AppModel.Shared.Rail = r, label).Help(help);
    }
}

public sealed class RailBadge : Reactive
{
    public RailBadge(int count) : base(() =>
        Text(count > 99 ? "99+" : count.ToString()).Font(10, Weight.Bold).Foreground(SColor.White)
            .PaddingH(5).Frame(minWidth: 17, minHeight: 17)
            .Background(Capsule().Fill(SColor.Hex(0xFF3B30))))
    { }
}

/// ChatSpace's own icon.
public sealed class ChatSpaceIcon : Reactive
{
    public ChatSpaceIcon(double size) : base(() =>
        Bitmap(NativeAssets.Shared.Image(size > 40 ? "icons/chatspace-256.png" : "icons/chatspace-128.png"), ContentMode.Fit)
            .Frame(size * 1.22, size * 1.22).Frame(size, size))
    { }
}

/// A custom group's icon: the picked image, or an SF Symbol on the group's colour.
public sealed class GroupIcon : Reactive
{
    public GroupIcon(CustomGroup group, double size) : base(() =>
    {
        if (group.ImagePath is { } p && File.Exists(p) && ImageLoader.Shared.LoadLocalNow(p, size * 3) is { } img)
            return Bitmap(img).Frame(size, size).ClipRounded(size * 0.23, true);
        var c = SColor.Hex(group.ColorHex);
        return RoundedRectangle(size * 0.23, true)
            .Fill(new LinearGradient([c.Opacity(0.85), c], LinearGradient.Top, LinearGradient.Bottom))
            .Overlay(Image(group.Symbol ?? "folder.fill").Font(size * 0.46, Weight.Medium).Foreground(SColor.White))
            .Frame(size, size);
    })
    { }
}

/// The official app's own icon, otherwise a drawn approximation.
public sealed class PlatformIcon : Reactive
{
    public PlatformIcon(Platform platform, double size) : base(() => Build(platform, size)) { }

    private static Control Build(Platform platform, double size)
    {
        if (NativeAssets.Shared.AppIcon(platform) is { } icon)
            // macOS icons carry ~10% transparent margin; enlarge so the tile fills `size`.
            return new OverflowingIcon(icon, size, 1.22);
        var r = size * 0.23;
        return platform switch
        {
            Platform.Zalo => RoundedRectangle(r, true).Fill(SColor.Hex(0x0068FF))
                .Overlay(Text("Zalo").Font(size * 0.3, Weight.Heavy).Foreground(SColor.White)
                    .Padding(size * 0.06, size * 0.1, size * 0.06, size * 0.1)
                    .Background(Capsule().Stroke(SColor.White, size * 0.045)))
                .Frame(size, size),
            Platform.Messenger => RoundedRectangle(r, true).Fill(SColor.White)
                .Overlay(MessengerLogo.Make(size * 0.7))
                .Frame(size, size),
            Platform.Whatsapp => RoundedRectangle(r, true).Fill(SColor.Hex(0x25D366))
                .Overlay(ResizableSymbol("phone.bubble.fill").Foreground(SColor.White).Frame(width: size * 0.62))
                .Frame(size, size),
            _ => RoundedRectangle(r, true)
                .Fill(new LinearGradient([SColor.Hex(0x37BBFE), SColor.Hex(0x007DBB)], LinearGradient.Top, LinearGradient.Bottom))
                .Overlay(ResizableSymbol("paperplane.fill").Foreground(SColor.White).Frame(width: size * 0.5).Offset(-size * 0.03, size * 0.02))
                .Frame(size, size),
        };
    }
}

/// `Image(nsImage:).resizable().frame(size * scale).frame(size)`: lays out at `size` but draws the bitmap `scale` times
/// larger around its centre, overflowing like SwiftUI (a Kit frame would clamp it to `size`).
public sealed class OverflowingIcon : Control
{
    private readonly Avalonia.Media.Imaging.Bitmap _icon;
    private readonly double _scale;

    public OverflowingIcon(Avalonia.Media.Imaging.Bitmap icon, double size, double scale)
    {
        _icon = icon;
        _scale = scale;
        Width = size;
        Height = size;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    public override void Render(DrawingContext ctx)
    {
        var full = Width * _scale;
        var o = (Width - full) / 2;
        ctx.DrawImage(_icon, new Rect(o, o, full, full));
    }
}

/// Messenger's gradient bubble with the white zigzag.
public static class MessengerLogo
{
    public static readonly LinearGradient Gradient = new(
        [SColor.Hex(0x0099FF), SColor.Hex(0xA033FF), SColor.Hex(0xFF5280), SColor.Hex(0xFF7061)],
        LinearGradient.BottomLeading, LinearGradient.TopTrailing);

    public static Control Make(double size) =>
        ZStack(PathShape(Bubble).Fill(Gradient), PathShape(Zigzag).Fill(SColor.White)).Frame(size, size);

    public static Geometry Bubble(Rect r)
    {
        double w = r.Width, h = r.Height;
        var g = new GeometryGroup { FillRule = FillRule.NonZero };
        g.Children.Add(new EllipseGeometry(new Rect(r.X, r.Y, w, h * 0.94)));
        var tail = new StreamGeometry();
        using (var c = tail.Open())
        {
            c.BeginFigure(new Point(r.X + w * 0.17, r.Y + h * 0.70), true);
            c.LineTo(new Point(r.X + w * 0.42, r.Y + h * 0.86));
            c.LineTo(new Point(r.X + w * 0.22, r.Y + h * 1.0));
            c.QuadraticBezierTo(new Point(r.X + w * 0.17, r.Y + h * 1.01), new Point(r.X + w * 0.17, r.Y + h * 0.96));
            c.EndFigure(true);
        }
        g.Children.Add(tail);
        return g;
    }

    public static Geometry Zigzag(Rect r)
    {
        Point P(double x, double y) => new(r.X + r.Width * x, r.Y + r.Height * y);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(0.18, 0.62), true);
            c.LineTo(P(0.42, 0.33)); c.LineTo(P(0.56, 0.45)); c.LineTo(P(0.82, 0.30)); c.LineTo(P(0.58, 0.60)); c.LineTo(P(0.44, 0.48));
            c.EndFigure(true);
        }
        return g;
    }
}
