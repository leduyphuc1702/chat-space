using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using ChatSpace.Kit;

namespace ChatSpace.Core;

/// Self-update from the GitHub releases of the public repo: the latest release's `ChatSpace-win-x64.exe` is
/// downloaded in the background next to the running exe, checked, and swapped in when the user restarts from
/// the rail button, or when ChatSpace quits. Windows lets a running exe be renamed, so the swap is two renames.
public sealed class Updater : Observable
{
    public static readonly Updater Shared = new();

    public const string Repo = "leduyphuc1702/chat-space";
    public const string AssetName = "ChatSpace-win-x64.exe";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private static readonly HttpClient Http = CreateClient();

    public abstract record UpdateState
    {
        public sealed record Idle : UpdateState;
        public sealed record Checking : UpdateState;
        public sealed record UpToDate : UpdateState;
        public sealed record Downloading(string Version) : UpdateState;
        public sealed record Ready(string Version) : UpdateState;
        public sealed record Failed(string Message) : UpdateState;
    }

    private UpdateState _state = new UpdateState.Idle();
    public UpdateState State { get => Get(ref _state); private set => Set(ref _state, value); }
    private DateTime? _lastCheck;
    public DateTime? LastCheck { get => Get(ref _lastCheck); private set => Set(ref _lastCheck, value); }
    private bool _automatic = Prefs.GetBool("updates.automatic") ?? true;
    public bool Automatic
    {
        get => Get(ref _automatic);
        set { Set(ref _automatic, value); Prefs.Set("updates.automatic", value); }
    }

    public string CurrentVersion { get; } = CurrentAppVersion();
    /// Only the published single-file exe updates itself: not `dotnet run`, the demo or screenshot modes.
    /// A single-file publish has no assembly location; `dotnet run` and plain builds do.
    public bool Supported { get; } = OperatingSystem.IsWindows() && !DemoData.Enabled && !App.Headless
        && string.IsNullOrEmpty(typeof(Updater).Assembly.Location)
        && Environment.ProcessPath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true;

    private string? _staged;
    private DispatcherTimer? _timer;

    public string? ReadyVersion => State is UpdateState.Ready r ? r.Version : null;

    private static string ExePath => Environment.ProcessPath!;
    private static string StagedPath => ExePath + ".new";
    private static string OldPath => ExePath + ".old";

    public void Start()
    {
        if (!Supported) return;
        try { File.Delete(OldPath); } catch { }
        try { File.Delete(StagedPath); } catch { }
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => { if (Automatic) _ = Check(); };
        _timer.Start();
        DispatcherTimer.RunOnce(() => { if (Automatic) _ = Check(); }, TimeSpan.FromSeconds(15));
    }

    public async Task Check()
    {
        if (!Supported || State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Ready) return;
        State = new UpdateState.Checking();
        try
        {
            var (tag, assets) = await LatestRelease();
            LastCheck = DateTime.Now;
            var version = tag.TrimStart('v', 'V');
            if (!IsNewer(version, CurrentVersion)) { State = new UpdateState.UpToDate(); return; }
            var asset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            // A release published before its Windows build was attached.
            if (asset.ValueKind == JsonValueKind.Undefined) { State = new UpdateState.UpToDate(); return; }
            State = new UpdateState.Downloading(version);
            await Stage(asset, version);
            _staged = StagedPath;
            State = new UpdateState.Ready(version);
        }
        catch (Exception e)
        {
            LastCheck = DateTime.Now;
            State = new UpdateState.Failed(e is UpdateException ? e.Message : $"Không kiểm tra được bản mới: {e.Message}");
        }
    }

    /// Quit, then start the new exe once this process (and its single-instance mutex) is gone.
    public void RestartToUpdate()
    {
        if (!Swap()) return;
        var pid = Environment.ProcessId;
        var exe = ExePath.Replace("'", "''");
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Process -FilePath '{exe}'\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch { }
        App.Quit();
    }

    /// From the app's exit path: a downloaded update is put in place on the way out.
    public void InstallOnQuit() => Swap();

    private bool Swap()
    {
        if (_staged == null || !File.Exists(_staged)) return false;
        try
        {
            File.Move(ExePath, OldPath, overwrite: true);
            try { File.Move(_staged, ExePath); }
            catch { File.Move(OldPath, ExePath); throw; }
            _staged = null;
            return true;
        }
        catch (Exception e)
        {
            State = new UpdateState.Failed($"Không thay được file ChatSpace: {e.Message}");
            return false;
        }
    }

    private static async Task<(string tag, JsonElement[] assets)> LatestRelease()
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if ((int)resp.StatusCode == 404) throw new UpdateException("Chưa có bản phát hành nào");
        if (!resp.IsSuccessStatusCode) throw new UpdateException($"GitHub trả về mã {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement.Clone();
        return (root.GetProperty("tag_name").GetString() ?? "", root.GetProperty("assets").EnumerateArray().ToArray());
    }

    private static async Task Stage(JsonElement asset, string version)
    {
        var dir = Path.GetDirectoryName(ExePath)!;
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        try
        {
            await using (var src = await Http.GetStreamAsync(url))
            await using (var dst = File.Create(StagedPath))
                await src.CopyToAsync(dst);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UpdateException($"Không có quyền ghi vào {dir} — hãy chép ChatSpace ra thư mục của bạn (ví dụ Desktop)");
        }
        try
        {
            if (new FileInfo(StagedPath).Length != size) throw new UpdateException("File tải về không đủ dung lượng");
            if (digest is not null && digest.StartsWith("sha256:"))
            {
                await using var f = File.OpenRead(StagedPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
                if (hash != digest["sha256:".Length..].ToLowerInvariant()) throw new UpdateException("Sai mã SHA-256 của bản cập nhật");
            }
            var head = new byte[2];
            await using (var f = File.OpenRead(StagedPath)) await f.ReadExactlyAsync(head);
            if (head[0] != 'M' || head[1] != 'Z') throw new UpdateException($"Gói cập nhật không đúng ChatSpace {version}");
        }
        catch
        {
            try { File.Delete(StagedPath); } catch { }
            throw;
        }
    }

    private static string CurrentAppVersion()
    {
        var v = typeof(Updater).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        return v?.Split('+')[0] ?? "0";
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ChatSpace-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static bool IsNewer(string a, string b)
    {
        int[] Parts(string s) => s.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var (x, y) = (Parts(a), Parts(b));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var (l, r) = (i < x.Length ? x[i] : 0, i < y.Length ? y[i] : 0);
            if (l != r) return l > r;
        }
        return false;
    }
}

public sealed class UpdateException(string message) : Exception(message);
