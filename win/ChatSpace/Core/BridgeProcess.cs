using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace ChatSpace.Core;

public sealed class BridgeException : Exception
{
    public string Code { get; }
    public BridgeException(string message, string code = "launch") : base(message) { Code = code; }
    public static BridgeException Exited => new("Bridge process exited", "exited");
}

/// One sidecar process speaking newline-delimited JSON (docs/PROTOCOL.md).
public sealed class BridgeProcess
{
    public Platform Platform { get; }

    private readonly object _lock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private int _nextId = 1;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private FileStream? _log;

    /// Called on the UI thread.
    public Action<string, JsonNode?>? OnEvent;
    public Action<int>? OnExit;

    public BridgeProcess(Platform platform) { Platform = platform; }

    public bool IsRunning { get { lock (_lock) return _process is { HasExited: false }; } }

    public void Start()
    {
        var (exe, args) = Platform.BridgeCommand();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!OperatingSystem.IsWindows())
            psi.Environment["PATH"] = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin") + ":/opt/homebrew/bin:/usr/local/bin";

        var logPath = Path.Combine(Platform.DataDirectory(), "bridge.log");
        try { _log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite); } catch { _log = null; }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => Exited(p);
        if (!p.Start()) throw new BridgeException("Không chạy được bridge " + Platform.DisplayName());
        lock (_lock)
        {
            _process = p;
            _stdin = p.StandardInput;
            _stdin.AutoFlush = true;
            _stdin.NewLine = "\n";
        }
        var stdout = p.StandardOutput;
        var stderr = p.StandardError;
        new Thread(() => ReadLoop(stdout)) { IsBackground = true, Name = "bridge-" + Platform.Raw() }.Start();
        new Thread(() =>
        {
            var buf = new char[4096];
            int n;
            try
            {
                while ((n = stderr.Read(buf, 0, buf.Length)) > 0) WriteLog(Encoding.UTF8.GetBytes(buf, 0, n));
            }
            catch { }
        }) { IsBackground = true }.Start();
    }

    private void WriteLog(byte[] data)
    {
        lock (_lock)
        {
            try { _log?.Write(data); _log?.Flush(); } catch { }
        }
    }

    private void Exited(Process p)
    {
        List<TaskCompletionSource<JsonNode?>> waiting;
        lock (_lock)
        {
            waiting = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var w in waiting) w.TrySetException(BridgeException.Exited);
        var status = 0;
        try { status = p.ExitCode; } catch { }
        var cb = OnExit;
        Dispatcher.UIThread.Post(() => cb?.Invoke(status));
    }

    public void Stop()
    {
        Process? p;
        lock (_lock)
        {
            try { _stdin?.Close(); } catch { }
            _stdin = null;
            p = _process;
        }
        Task.Delay(2000).ContinueWith(_ =>
        {
            try { if (p is { HasExited: false }) p.Kill(true); } catch { }
        });
    }

    public Task<JsonNode?> Call(string method, JsonObject? parameters = null)
    {
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        StreamWriter? w;
        lock (_lock)
        {
            id = (_nextId++).ToString();
            _pending[id] = tcs;
            w = _stdin;
        }
        if (w == null)
        {
            lock (_lock) _pending.Remove(id);
            tcs.TrySetException(BridgeException.Exited);
            return tcs.Task;
        }
        var frame = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        try
        {
            var line = frame.ToJsonString(Json.Options);
            lock (w) w.WriteLine(line);
        }
        catch (Exception e)
        {
            lock (_lock) _pending.Remove(id);
            tcs.TrySetException(e);
        }
        return tcs.Task;
    }

    private void ReadLoop(StreamReader r)
    {
        try
        {
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                Handle(line);
            }
        }
        catch { }
    }

    private void Handle(string line)
    {
        JsonNode? frame;
        try { frame = JsonNode.Parse(line); }
        catch
        {
            WriteLog(Encoding.UTF8.GetBytes("[app] non-JSON stdout: " + line + "\n"));
            return;
        }
        if (frame.Str("event") is { } ev)
        {
            var data = frame.Get("data");
            var cb = OnEvent;
            Dispatcher.UIThread.Post(() => cb?.Invoke(ev, data));
            return;
        }
        if (frame.Str("id") is not { } id) return;
        TaskCompletionSource<JsonNode?>? tcs;
        lock (_lock) _pending.Remove(id, out tcs);
        if (frame.Get("error") is JsonObject err)
            tcs?.TrySetException(new BridgeException(err.Str("message") ?? "Unknown error", err.Str("code") ?? "internal"));
        else
            tcs?.TrySetResult(frame.Get("result"));
    }
}
