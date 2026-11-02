using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatSpace.Core;

public enum Platform { Zalo, Messenger, Whatsapp, Telegram }

public static class Platforms
{
    public static readonly Platform[] All = [Platform.Zalo, Platform.Messenger, Platform.Whatsapp, Platform.Telegram];

    public static string Raw(this Platform p) => p switch
    {
        Platform.Zalo => "zalo", Platform.Messenger => "messenger", Platform.Whatsapp => "whatsapp", _ => "telegram",
    };

    public static Platform? Parse(string? raw) => raw switch
    {
        "zalo" => Platform.Zalo, "messenger" => Platform.Messenger, "whatsapp" => Platform.Whatsapp, "telegram" => Platform.Telegram, _ => null,
    };

    public static string DisplayName(this Platform p) => p switch
    {
        Platform.Zalo => "Zalo", Platform.Messenger => "Messenger", Platform.Whatsapp => "WhatsApp", _ => "Telegram",
    };

    /// %APPDATA%\ChatSpace on Windows (Application Support/ChatSpace on macOS).
    public static string BaseDirectory
    {
        get
        {
            var root = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(root, OperatingSystem.IsMacOS() ? "ChatSpace-Windows" : "ChatSpace");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DataDirectory(this Platform p)
    {
        var dir = Path.Combine(BaseDirectory, p.Raw());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// Executable + arguments for this platform's sidecar: `bridges\` next to ChatSpace.exe, else the repo (development).
    public static (string exe, string[] args) BridgeCommand(this Platform p)
    {
        string[] dataArgs = ["--data-dir", p.DataDirectory()];
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var res = Path.Combine(AppContext.BaseDirectory, "bridges");
        var node = Path.Combine(res, "chatspace-bridge" + ext);
        var meta = Path.Combine(res, "chatspace-messenger" + ext);
        if (p == Platform.Messenger && File.Exists(meta)) return (meta, dataArgs);
        if (p != Platform.Messenger && File.Exists(node)) return (node, ["--platform", p.Raw(), .. dataArgs]);
        if (BundledBridge(p == Platform.Messenger ? "chatspace-messenger.exe" : "chatspace-bridge.exe") is { } bundled)
            return p == Platform.Messenger ? (bundled, dataArgs) : (bundled, ["--platform", p.Raw(), .. dataArgs]);

        var root = RepoRoot() ?? throw new BridgeException("Không tìm thấy thư mục bridges. Đặt biến môi trường CHATSPACE_ROOT.");
        if (p == Platform.Messenger)
        {
            var bin = Path.Combine(root, "bridges", "messenger", "chatspace-messenger" + ext);
            if (!File.Exists(bin)) throw new BridgeException("Chưa build Messenger bridge: chạy `go build -o chatspace-messenger .` trong bridges/messenger");
            return (bin, dataArgs);
        }
        var compiled = Path.Combine(root, "bridges", "node", "dist", "chatspace-bridge" + ext);
        if (File.Exists(compiled) && Environment.GetEnvironmentVariable("CHATSPACE_DEV") == null)
            return (compiled, ["--platform", p.Raw(), .. dataArgs]);
        var bun = FindBun() ?? throw new BridgeException("Không tìm thấy Bun. Cài tại https://bun.sh hoặc build bridge bằng `bun run build`.");
        var main = Path.Combine(root, "bridges", "node", "src", "main.ts");
        return (bun, [main, "--platform", p.Raw(), .. dataArgs]);
    }

    private static readonly object ExtractLock = new();

    /// The single-file build carries the bridges as embedded resources; they are unpacked once per build
    /// into %LOCALAPPDATA%\ChatSpace\bridges\<build id>, since Windows can only run executables from disk.
    private static string? BundledBridge(string name)
    {
        var asm = typeof(Platforms).Assembly;
        using var src = asm.GetManifestResourceStream("bridges/" + name);
        if (src == null) return null;
        var build = asm.ManifestModule.ModuleVersionId.ToString("N")[..12];
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatSpace", "bridges", build);
        var path = Path.Combine(dir, name);
        lock (ExtractLock)
        {
            if (File.Exists(path) && new FileInfo(path).Length == src.Length) return path;
            Directory.CreateDirectory(dir);
            var tmp = path + ".part";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            File.Move(tmp, path, overwrite: true);
            // Older builds' copies are no longer used.
            foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(dir)!).Where(d => d != dir))
                try { Directory.Delete(old, true); } catch { }
        }
        return path;
    }

    public static string? RepoRoot()
    {
        if (Environment.GetEnvironmentVariable("CHATSPACE_ROOT") is { } env) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "bridges", "node", "src", "main.ts"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string? FindBun()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(home, ".bun", "bin", "bun.exe")]
            : [Path.Combine(home, ".bun", "bin", "bun"), "/opt/homebrew/bin/bun", "/usr/local/bin/bun"];
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// UserDefaults: a small JSON key-value file next to the platform data.
public static class Prefs
{
    private static readonly string FilePath = Path.Combine(Platforms.BaseDirectory, "settings.json");
    private static readonly JsonObject Store = Load();
    private static System.Threading.Timer? _saveTimer;

    private static JsonObject Load()
    {
        try { return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    public static string? GetString(string key) => Store.Str(key);
    public static bool? GetBool(string key) => Store.Bool(key);
    public static double? GetDouble(string key) => Store.Num(key);

    public static T? Get<T>(string key)
    {
        try { return Store[key].Decode<T>(); } catch { return default; }
    }

    public static void Set<T>(string key, T? value)
    {
        lock (Store)
        {
            if (value == null) Store.Remove(key);
            else Store[key] = Json.From(value);
        }
        ScheduleSave();
    }

    public static void Remove(string key) { lock (Store) Store.Remove(key); ScheduleSave(); }

    private static void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, 300, Timeout.Infinite);
    }

    /// Demo mode keeps its changes in memory: it must never overwrite the user's real settings.
    public static void Flush()
    {
        if (DemoData.Enabled) return;
        string text;
        lock (Store) text = Store.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
