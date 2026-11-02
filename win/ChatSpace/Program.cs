using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ChatSpace;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var shot = Array.IndexOf(args, "--screenshot");
        if (shot >= 0 && shot + 1 < args.Length) return Screenshot(args[shot + 1], args);
        // One ChatSpace per user, as on macOS: a second launch brings the running window forward instead of
        // starting a second set of bridges on the same accounts.
        using var mutex = new Mutex(true, @"Local\ChatSpace.SingleInstance", out var first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ChatSpace.Activate");
        if (!first)
        {
            activate.Set();
            return 0;
        }
        new Thread(() =>
        {
            while (activate.WaitOne()) Dispatcher.UIThread.Post(App.ActivateMainWindow);
        }) { IsBackground = true }.Start();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "avares://ChatSpace/Assets/Fonts#SF Pro 17" })
            .LogToTrace();

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// Renders the main window (or `--window settings|forward|viewer|bubble`) offscreen to a PNG at `--scale`
    /// (default 2, like a Retina capture of the macOS build). Environment variables select the demo state.
    private static int Screenshot(string output, string[] args)
    {
        var width = double.Parse(Arg(args, "--width") ?? "1280");
        var height = double.Parse(Arg(args, "--height") ?? "820");
        var scale = double.Parse(Arg(args, "--scale") ?? "2");
        var wait = int.Parse(Arg(args, "--wait") ?? "1500");
        var which = Arg(args, "--window") ?? "main";
        Environment.SetEnvironmentVariable("CHATSPACE_DEMO", Environment.GetEnvironmentVariable("CHATSPACE_DEMO") ?? "1");

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = "avares://ChatSpace/Assets/Fonts#SF Pro 17" })
            .SetupWithoutStarting();

        App.Headless = true;
        var app = (App)Application.Current!;
        app.Start();

        Dispatcher.UIThread.RunJobs();
        if (which == "main") Core.AppModel.Shared.MarkVisibleRead();
        var deadline = DateTime.UtcNow.AddMilliseconds(wait);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }

        TopLevel? target = which switch
        {
            "main" => app.MainWindow,
            _ => App.ScreenshotWindow(which),
        };
        if (target == null) { Console.Error.WriteLine("no window " + which); return 2; }
        if (which == "main" && target is Window w) { w.Width = width; w.Height = height; }
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }

        var size = target.ClientSize;
        using var rtb = new RenderTargetBitmap(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        rtb.Render(target);
        rtb.Save(output);
        Console.WriteLine($"{output} {size.Width}x{size.Height}@{scale}");
        return 0;
    }
}
