using Avalonia.Threading;
using ChatSpace.Core;

namespace ChatSpace;

/// `applicationDidFinishLaunching` + the demo screenshot script of the macOS AppDelegate.
public static class AppStartup
{
    public static void Run()
    {
        Notifier.Shared.RequestAuthorization();
        AppModel.Shared.StartAll();
    }

    public static void AfterWindow()
    {
        if (DemoData.Enabled) RunDemoScript();
    }

    private static void Later(Action a, double seconds = 1) =>
        DispatcherTimer.RunOnce(a, TimeSpan.FromSeconds(App.Headless ? Math.Min(seconds, 0.3) : seconds));

    /// Screenshot helpers: CHATSPACE_DEMO_BUBBLE=<platform>:<chatId>[:open], CHATSPACE_DEMO_SETTINGS=<section>,
    /// CHATSPACE_DEMO_SELECT=<platform>:<chatId> (chat opened in the inbox / group),
    /// CHATSPACE_DEMO_FORWARD=<platform>:<chatId>:<messageId> (forward picker for that message),
    /// CHATSPACE_DEMO_COST=<platform>:<chatId>:<messageId> (image viewer with the VietFlow cost form),
    /// CHATSPACE_DEMO_EDIT=<platform>:<chatId> (editing the last own message; CHATSPACE_DEMO_EDIT_ERROR adds a refusal),
    /// CHATSPACE_DEMO_DRAFT=<platform>:<chatId>:<text> (a typed draft, e.g. for the collapsed compact composer).
    private static void RunDemoScript()
    {
        string? Env(string k) => Environment.GetEnvironmentVariable(k);
        if (Env("CHATSPACE_DEMO_BUBBLE") is { } b)
        {
            var parts = b.Split(':');
            if (parts.Length >= 2 && Platforms.Parse(parts[0]) is { } p)
                Later(() =>
                {
                    ChatHeads.Shared.Show(new ChatRef(Platform.Zalo, "z1"), expand: false);
                    ChatHeads.Shared.Show(new ChatRef(Platform.Messenger, "m2"), expand: false);
                    ChatHeads.Shared.Show(new ChatRef(p, parts[1]), expand: parts.Length > 2);
                });
        }
        if (Env("CHATSPACE_DEMO_SETTINGS") is { } sec)
        {
            SettingsWindow.InitialSection = sec;
            Later(SettingsWindow.ShowShared);
        }
        if (Env("CHATSPACE_DEMO_SELECT") is { } sel)
        {
            var parts = sel.Split(':');
            if (parts.Length == 2 && Platforms.Parse(parts[0]) is { } p)
            {
                var r = new ChatRef(p, parts[1]);
                var m = AppModel.Shared;
                m.AggregateSelection = m.AggregateSelection.SetItem(m.Rail, r);
                m.Session(p).Prepare(r.ChatId);
            }
        }
        if (Env("CHATSPACE_DEMO_FORWARD") is { } fw)
        {
            var parts = fw.Split(':');
            if (parts.Length == 3 && Platforms.Parse(parts[0]) is { } p)
                Later(() =>
                {
                    var s = AppModel.Shared.Session(p);
                    if (s.Messages.GetValueOrDefault(parts[1])?.FirstOrDefault(x => x.Id == parts[2]) is { } msg) Forwarder.Shared.Show(msg, s);
                });
        }
        if (Env("CHATSPACE_DEMO_COST") is { } cost)
        {
            var parts = cost.Split(':');
            if (parts.Length == 3 && Platforms.Parse(parts[0]) is { } p)
                Later(() =>
                {
                    var s = AppModel.Shared.Session(p);
                    if (s.Messages.GetValueOrDefault(parts[1])?.FirstOrDefault(x => x.Id == parts[2]) is { } msg) MediaViewer.Shared.Show(s, msg, cost: true);
                });
        }
        if (Env("CHATSPACE_DEMO_EDIT") is { } ed)
        {
            var parts = ed.Split(':');
            if (parts.Length == 2 && Platforms.Parse(parts[0]) is { } p)
            {
                var s = AppModel.Shared.Session(p);
                s.Prepare(parts[1]);
                void Try(int attempts)
                {
                    if (s.MessagesIn(parts[1]).LastOrDefault(x => x.FromMe && !string.IsNullOrEmpty(x.Text)) is not { } msg)
                    {
                        if (attempts > 0) Later(() => Try(attempts - 1), 0.1);
                        return;
                    }
                    s.BeginEdit(msg, force: true);
                    if (Env("CHATSPACE_DEMO_EDIT_ERROR") is { } err) s.EditError = s.EditError.SetItem(parts[1], err);
                }
                Later(() => Try(20), 0.1);
            }
        }
        if (Env("CHATSPACE_DEMO_DRAFT") is { } dr)
        {
            var parts = dr.Split(':', 3);
            if (parts.Length == 3 && Platforms.Parse(parts[0]) is { } p) AppModel.Shared.Session(p).SetDraft(parts[1], parts[2]);
        }
    }
}
