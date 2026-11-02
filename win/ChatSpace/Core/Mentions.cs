using System.Text.Json.Nodes;
using Avalonia.Controls;
using ChatSpace.Kit;

namespace ChatSpace.Core;

/// The "@name" being typed in a group composer and the list offered for it.
public sealed class Mentions : Observable
{
    public static readonly Mentions Shared = new();

    public sealed record Query(ChatRef Ref, string Text, int Location, int Length);

    private Query? _query;
    public Query? Current { get => Get(ref _query); private set => Set(ref _query, value); }
    private int _index;
    public int Index { get => Get(ref _index); set => Set(ref _index, value); }

    private readonly Dictionary<ChatRef, WeakReference<TextBox>> _editors = new();

    public void Register(TextBox tb, ChatRef r) => _editors[r] = new WeakReference<TextBox>(tb);

    public IReadOnlyList<ChatMember> Candidates(PlatformSession session, string chatId) => Candidates(session, chatId, Current);

    private static IReadOnlyList<ChatMember> Candidates(PlatformSession session, string chatId, Query? q)
    {
        if (q == null || q.Ref.Platform != session.Platform || q.Ref.ChatId != chatId) return [];
        var selfId = session.SelfInfo?.Id;
        var all = (session.Members.GetValueOrDefault(chatId) ?? []).Where(m => m.Id != selfId).ToList();
        var needle = CustomGroup.Normalize(q.Text);
        if (needle.Length == 0) return all.Take(50).ToList();
        return all.Where(m =>
        {
            var name = CustomGroup.Normalize(m.Name);
            return name.StartsWith(needle, StringComparison.Ordinal)
                   || name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(needle, StringComparison.Ordinal))
                   || name.Contains(needle, StringComparison.Ordinal);
        }).Take(50).ToList();
    }

    /// Recomputes the "@query" before the caret; called on every edit and caret move.
    public void Update(TextBox tb, PlatformSession session, string chatId)
    {
        var r = new ChatRef(session.Platform, chatId);
        var s = tb.Text ?? "";
        var caret = tb.CaretIndex;
        if (tb.SelectionStart != tb.SelectionEnd || caret > s.Length) { Clear(r); return; }
        var i = caret;
        var floor = Math.Max(0, caret - 40);
        while (i > floor)
        {
            i--;
            var c = s[i];
            if (c is '\n' or '\r') { Clear(r); return; }
            if (c != '@') continue;
            if (i > 0 && !char.IsWhiteSpace(s[i - 1])) { Clear(r); return; }
            var typed = s.Substring(i + 1, caret - i - 1);
            if (typed.StartsWith(' ') || typed.Contains("  ") || typed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) { Clear(r); return; }
            // A finished pick: the caret has moved past "@Name".
            var picks = session.MentionPicks.GetValueOrDefault(chatId) ?? [];
            if (picks.Any(p => typed.StartsWith(p.Name, StringComparison.Ordinal) && typed.Length > p.Name.Length)) { Clear(r); return; }
            var q = new Query(r, typed, i, caret - i);
            // Words typed after an "@" that matches nobody are ordinary text, not a name.
            if (typed.Contains(' ') && session.Members.ContainsKey(chatId) && Candidates(session, chatId, q).Count == 0) { Clear(r); return; }
            if (Current != q)
            {
                if (Current?.Ref != r || Current?.Text != q.Text) Index = 0;
                Current = q;
            }
            if (!session.Members.ContainsKey(chatId)) session.LoadMembers(chatId);
            return;
        }
        Clear(r);
    }

    public void Clear(ChatRef r)
    {
        if (Current?.Ref == r) Current = null;
    }

    public enum Command { Up, Down, Pick, Cancel }

    /// Arrow/Return/Tab/Esc while the list is open. Returns true when handled.
    public bool Handle(Command command, PlatformSession session, string chatId)
    {
        var list = Candidates(session, chatId);
        if (Current is not { } q || list.Count == 0) return false;
        switch (command)
        {
            case Command.Up: Index = (Index - 1 + list.Count) % list.Count; break;
            case Command.Down: Index = (Index + 1) % list.Count; break;
            case Command.Pick: Pick(list[Math.Min(Index, list.Count - 1)], session, chatId); break;
            case Command.Cancel: Clear(q.Ref); break;
        }
        return true;
    }

    public void Pick(ChatMember m, PlatformSession session, string chatId)
    {
        var r = new ChatRef(session.Platform, chatId);
        if (Current is not { } q || q.Ref != r || !_editors.TryGetValue(r, out var w) || !w.TryGetTarget(out var tb)) return;
        var name = m.Name.Trim();
        var text = tb.Text ?? "";
        var insert = "@" + name + " ";
        var loc = Math.Min(q.Location, text.Length);
        var len = Math.Min(q.Length, text.Length - loc);
        tb.Text = text[..loc] + insert + text[(loc + len)..];
        tb.CaretIndex = loc + insert.Length;
        tb.Focus();
        var picks = session.MentionPicks.GetValueOrDefault(chatId) ?? [];
        session.MentionPicks = session.MentionPicks.SetItem(chatId, picks.Add(new MentionPick(m.Id, name)));
        Current = null;
    }

    /// The picks still present in `text`, as protocol mentions (UTF-16 offsets, span includes "@").
    public static IReadOnlyList<JsonObject> Resolve(IReadOnlyList<MentionPick> picks, string text)
    {
        var from = 0;
        var outList = new List<(int offset, JsonObject o)>();
        var used = new HashSet<int>();
        foreach (var p in picks)
        {
            var token = "@" + p.Name;
            var at = from <= text.Length ? text.IndexOf(token, from, StringComparison.Ordinal) : -1;
            if (at < 0) at = text.IndexOf(token, StringComparison.Ordinal);
            if (at < 0 || !used.Add(at)) continue;
            from = at + token.Length;
            outList.Add((at, new JsonObject { ["userId"] = p.UserId, ["offset"] = at, ["length"] = token.Length }));
        }
        return outList.OrderBy(x => x.offset).Select(x => x.o).ToList();
    }
}
