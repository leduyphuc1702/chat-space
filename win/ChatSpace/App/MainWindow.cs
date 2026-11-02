using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using ChatSpace.Kit;
using static ChatSpace.Kit.UI;

namespace ChatSpace;

/// The main window: macOS `.hiddenTitleBar` look on Windows — no system caption, content up to the top edge,
/// traffic-light buttons over the rail and the top 28 pt draggable like a title bar.
public sealed class MainWindow : Window
{
    public const double TitleBarHeight = 28;
    private readonly Panel _sheets = new();

    public void PresentSheet(Func<Action, Control> content) => Sheets.Present(_sheets, content, Scheme.Light);

    public MainWindow()
    {
        Title = "ChatSpace";
        Width = 1280;
        Height = 820;
        MinWidth = 980;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        Background = new SolidColorBrush(SColor.WindowBackground.Resolve(Scheme.Light));
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ChatSpace/Assets/AppIcon.ico"))); } catch { }
        SetValue(Env.FontProperty, SFont.Body);

        var root = new Panel();
        root.Children.Add(new RootView());
        var lights = new TrafficLightsView(this) { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        root.Children.Add(lights);
        root.Children.Add(_sheets);
        Content = root;

        Activated += (_, _) => { lights.InvalidateVisual(); Core.AppModel.Shared.MarkVisibleRead(); };
        Deactivated += (_, _) => lights.InvalidateVisual();
        AddHandler(PointerPressedEvent, OnTitleBarPress, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: false);
        Closing += (_, e) =>
        {
            // Like the macOS app: closing the window keeps ChatSpace running (bridges, notifications, bubbles).
            if (App.Quitting) return;
            e.Cancel = true;
            Hide();
        };
    }

    private void OnTitleBarPress(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p.Y > TitleBarHeight || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
    }
}
