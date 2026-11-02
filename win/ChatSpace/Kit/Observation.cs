using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace ChatSpace.Kit;

/// Swift's @Observable: property reads made while a view builds are recorded, and writes to any of
/// them rebuild that view. Models store immutable values (records, immutable collections) and replace
/// them on change, exactly like Swift value types assigned back to an observed property.
public abstract class Observable
{
    private Dictionary<string, List<WeakReference<IObserver>>>? _observers;

    protected T Get<T>(ref T field, [CallerMemberName] string name = "")
    {
        Tracker.Read(this, name);
        return field;
    }

    /// Writes and notifies even when equal for reference types the caller replaced; value-equal writes are skipped.
    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name);
    }

    /// Records a read of a computed property that depends on `name` (for properties without a backing field).
    protected void Track(string name) => Tracker.Read(this, name);

    public void Notify(string name)
    {
        if (_observers == null || !_observers.TryGetValue(name, out var list)) return;
        foreach (var w in list.ToArray())
        {
            if (w.TryGetTarget(out var o)) o.Invalidate();
            else list.Remove(w);
        }
    }

    internal void Subscribe(string name, IObserver o)
    {
        _observers ??= new();
        if (!_observers.TryGetValue(name, out var list)) _observers[name] = list = new();
        foreach (var w in list) if (w.TryGetTarget(out var e) && ReferenceEquals(e, o)) return;
        if (list.Count > 32) list.RemoveAll(w => !w.TryGetTarget(out _));
        list.Add(new WeakReference<IObserver>(o));
    }

    internal void Unsubscribe(string name, IObserver o)
    {
        if (_observers == null || !_observers.TryGetValue(name, out var list)) return;
        list.RemoveAll(w => !w.TryGetTarget(out var e) || ReferenceEquals(e, o));
    }
}

public interface IObserver
{
    void Invalidate();
}

/// Collects (object, property) reads on the current thread while a scope is open.
public static class Tracker
{
    [ThreadStatic] private static Stack<HashSet<(Observable, string)>>? _scopes;

    internal static void Read(Observable o, string name)
    {
        if (_scopes is { Count: > 0 } s) s.Peek().Add((o, name));
    }

    public static HashSet<(Observable, string)> Capture(Action body)
    {
        _scopes ??= new();
        var set = new HashSet<(Observable, string)>();
        _scopes.Push(set);
        try { body(); }
        finally { _scopes.Pop(); }
        return set;
    }

    /// Runs `body` without recording its reads (event handlers reading state).
    public static T Untracked<T>(Func<T> body)
    {
        _scopes ??= new();
        var saved = _scopes;
        _scopes = new();
        try { return body(); }
        finally { _scopes = saved; }
    }
}

/// `withObservationTracking`: calls `onChange` once (on the UI thread) after any value `body` read changes.
public sealed class Watch : IObserver
{
    private readonly Action _onChange;
    private HashSet<(Observable, string)> _deps = new();
    private bool _queued;
    private bool _disposed;

    private Watch(Action onChange) => _onChange = onChange;

    /// Re-runs `body` every time something it read changes, for as long as the returned watch is referenced.
    public static Watch Effect(Action body)
    {
        Watch? w = null;
        w = new Watch(() => w!.Run(body));
        w.Run(body);
        return w;
    }

    private void Run(Action body)
    {
        if (_disposed) return;
        var deps = Tracker.Capture(body);
        foreach (var (o, n) in _deps) if (!deps.Contains((o, n))) o.Unsubscribe(n, this);
        foreach (var (o, n) in deps) o.Subscribe(n, this);
        _deps = deps;
    }

    public void Invalidate()
    {
        if (_queued || _disposed) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; _onChange(); }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var (o, n) in _deps) o.Unsubscribe(n, this);
        _deps.Clear();
    }
}

/// SwiftUI @State / @Binding in one: a tracked box.
public sealed class State<T> : Observable
{
    private T _value;
    public State(T value) => _value = value;
    public T Value { get => Get(ref _value); set => Set(ref _value, value); }
    public static implicit operator T(State<T> s) => s.Value;
    public override string ToString() => _value?.ToString() ?? "";
}

/// Two-way access to some state, like SwiftUI's Binding.
public sealed class Binding<T>
{
    public Func<T> Get { get; }
    public Action<T> Set { get; }
    public Binding(Func<T> get, Action<T> set) { Get = get; Set = set; }
    public T Value { get => Get(); set => Set(value); }
    public static Binding<T> Constant(T v) => new(() => v, _ => { });
    public static implicit operator Binding<T>(State<T> s) => new(() => s.Value, v => s.Value = v);
}
