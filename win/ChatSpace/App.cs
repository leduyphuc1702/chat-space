using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using ChatSpace.Core;

namespace ChatSpace;

public sealed class App : Application
{
    public static bool Headless;
    /// Set by "Thoát": the main window's close button only hides it, like closing the window on macOS.
    public static bool Quitting;
    public Window? MainWindow { get; private set; }
    public static App Current => (App)Application.Current!;

    /// Extra windows the headless screenshot mode can capture (`--window <name>`).
    public static readonly Dictionary<string, Func<TopLevel?>> ScreenshotTargets = new();
    public static TopLevel? ScreenshotWindow(string name) => ScreenshotTargets.TryGetValue(name, out var f) ? f() : null;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(MacStyles.Create());
        ScreenshotTargets["settings"] = SettingsWindow.ScreenshotTarget;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Start();
            desktop.MainWindow = MainWindow;
            desktop.ShutdownRequested += (_, _) => AppModel.Shared.StopAll();
            desktop.Exit += (_, _) => AppModel.Shared.StopAll();
            SetupTray();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void Start()
    {
        ScreenshotTargets["forward"] = () => Forwarder.Shared.ScreenshotWindow;
        ScreenshotTargets["bubble"] = () => ChatHeads.Shared.HeadWindowForScreenshot;
        ScreenshotTargets["bubble-panel"] = () => ChatHeads.Shared.ChatWindowForScreenshot;
        AppStartup.Run();
        MainWindow = new MainWindow();
        MainWindow.Show();
        MediaViewer.RegisterScreenshotTarget();
        DockBadge.Show(DockBadge.Current);
        AppStartup.AfterWindow();
    }

    public static IntPtr MainWindowHandle => Current.MainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// Brings the main window back (notification click, tray icon, second launch).
    public static void ActivateMainWindow()
    {
        var w = Current.MainWindow;
        if (w == null) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public static void Quit()
    {
        Quitting = true;
        AppModel.Shared.StopAll();
        if (Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    /// The Dock icon's role on Windows: ChatSpace keeps running with its window closed, reachable from the tray.
    private void SetupTray()
    {
        try
        {
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Mở ChatSpace");
            open.Click += (_, _) => ActivateMainWindow();
            var settings = new NativeMenuItem("Cài đặt…");
            settings.Click += (_, _) => SettingsWindow.ShowShared();
            var quit = new NativeMenuItem("Thoát ChatSpace");
            quit.Click += (_, _) => Quit();
            menu.Items.Add(open);
            menu.Items.Add(settings);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);
            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ChatSpace/Assets/AppIcon.ico"))),
                ToolTipText = "ChatSpace",
                Menu = menu,
            };
            tray.Clicked += (_, _) => ActivateMainWindow();
            TrayIcon.SetIcons(this, [tray]);
        }
        catch { }
    }
}
