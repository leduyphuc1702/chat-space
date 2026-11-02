using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatSpace.Kit;

namespace ChatSpace.Core;

/// Settings → Dịch tin nhắn.
public sealed record TranslationSettings
{
    /// Google Translate code that messages are translated into, and the language the user writes in.
    public string Language { get; init; } = "vi";
    /// A reply goes out in the language of the message it answers.
    public bool MatchReplyLanguage { get; init; } = true;
    /// Outgoing language chosen per chat (`ChatRef.Id`); absent means `Language`, i.e. sent as typed.
    public ImmutableDictionary<string, string> ChatLanguages { get; init; } = ImmutableDictionary<string, string>.Empty;

    public bool Equals(TranslationSettings? o) => o is not null && Language == o.Language && MatchReplyLanguage == o.MatchReplyLanguage
        && ChatLanguages.Count == o.ChatLanguages.Count && ChatLanguages.All(p => o.ChatLanguages.TryGetValue(p.Key, out var v) && v == p.Value);
    public override int GetHashCode() => Language.GetHashCode();
}

public static class TranslationLanguages
{
    public static readonly (string Code, string Name)[] All =
    [
        ("vi", "Tiếng Việt"), ("en", "Tiếng Anh"), ("zh-CN", "Tiếng Trung (giản thể)"), ("zh-TW", "Tiếng Trung (phồn thể)"),
        ("ja", "Tiếng Nhật"), ("ko", "Tiếng Hàn"), ("th", "Tiếng Thái"), ("lo", "Tiếng Lào"), ("km", "Tiếng Khmer"),
        ("id", "Tiếng Indonesia"), ("ms", "Tiếng Mã Lai"), ("tl", "Tiếng Philippines"), ("hi", "Tiếng Hindi"),
        ("ru", "Tiếng Nga"), ("fr", "Tiếng Pháp"), ("de", "Tiếng Đức"), ("es", "Tiếng Tây Ban Nha"),
        ("pt", "Tiếng Bồ Đào Nha"), ("it", "Tiếng Ý"), ("ar", "Tiếng Ả Rập"),
    ];

    public static string Name(string code)
    {
        foreach (var (c, n) in All) if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return n;
        try
        {
            var ci = CultureInfo.GetCultureInfo(code);
            if (!string.IsNullOrEmpty(ci.EnglishName) && ci.EnglishName != code) return "Tiếng " + ci.NativeName;
        }
        catch { }
        return code.ToUpperInvariant();
    }

    /// Badge text on the composer's language button.
    public static string Short(string code) =>
        string.Equals(code, "zh-TW", StringComparison.OrdinalIgnoreCase) ? "TW" : code.Split('-')[0].ToUpperInvariant();

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
    public static TranslationException Http(int code) =>
        new(code == 429 ? "Google Dịch đang giới hạn tần suất, thử lại sau ít phút" : $"Google Dịch trả lỗi {code}");
    public static TranslationException BadResponse => new("Google Dịch trả dữ liệu không đọc được");
}

/// Message translation through Google Translate's public web endpoint (no API key).
public sealed class Translator : Observable
{
    public static readonly Translator Shared = new();

    public sealed record Entry
    {
        /// The message text this translation belongs to; an edited message is translated again.
        public string Original { get; init; } = "";
        public string Text { get; init; } = "";
        public string Source { get; init; } = "";
        public string Target { get; init; } = "";
        public bool Shown { get; init; }
        public DateTime At { get; init; }
    }

    private ImmutableDictionary<string, Entry> _entries = ImmutableDictionary<string, Entry>.Empty;
    public ImmutableDictionary<string, Entry> Entries { get => Get(ref _entries); private set => Set(ref _entries, value); }
    private ImmutableHashSet<string> _loading = [];
    public ImmutableHashSet<string> Loading { get => Get(ref _loading); private set => Set(ref _loading, value); }
    private ImmutableDictionary<string, string> _failed = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> Failed { get => Get(ref _failed); private set => Set(ref _failed, value); }
    /// Detected language of messages being replied to, by message key.
    private ImmutableDictionary<string, string> _replyLanguages = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> ReplyLanguages { get => Get(ref _replyLanguages); private set => Set(ref _replyLanguages, value); }
    /// Chats (`ChatRef.Id`) where the user overrode the automatic reply language for the current reply.
    private ImmutableHashSet<string> _replyAutoOff = [];
    public ImmutableHashSet<string> ReplyAutoOff { get => Get(ref _replyAutoOff); set => Set(ref _replyAutoOff, value); }
    /// Why the last send in a chat was held back.
    private ImmutableDictionary<string, string> _sendError = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> SendError { get => Get(ref _sendError); private set => Set(ref _sendError, value); }

    public void SetSendError(string chatRefId, string? error) =>
        SendError = error == null ? SendError.Remove(chatRefId) : SendError.SetItem(chatRefId, error);

    private readonly Dictionary<string, Task<string?>> _detecting = new();
    private CancellationTokenSource? _saveTask;

    private const int MaxEntries = 3000;
    private static string FilePath => Path.Combine(Platforms.BaseDirectory, "translations.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private Translator()
    {
        if (DemoData.Enabled) return;
        try
        {
            if (Json.Parse<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) is { } e) _entries = e.ToImmutableDictionary();
        }
        catch { }
    }

    public TranslationSettings Settings => Workspace.Shared.Translation;

    public static string Key(Message m) => m.ChatId + "|" + m.Id;

    /// Text worth translating: has letters, not just emoji, links or numbers.
    public static bool HasWords(string? text)
    {
        if (text == null) return false;
        var stripped = Regex.Replace(text, @"https?://\S+", "");
        return stripped.Any(char.IsLetter);
    }

    public static bool CanTranslate(Message m) => !m.IsDeleted && m.Kind != MessageKind.System && HasWords(m.Text);

    // MARK: Incoming

    /// The translation to show under a message, if the user asked for it and it is still current.
    public Entry? ShownEntry(Message m) => Entries.TryGetValue(Key(m), out var e) && e.Shown && e.Original == m.Text ? e : null;

    public bool IsShown(Message m) => ShownEntry(m) != null || Loading.Contains(Key(m)) || Failed.ContainsKey(Key(m));
    public bool IsLoading(Message m) => Loading.Contains(Key(m));
    public string? Failure(Message m) => Failed.TryGetValue(Key(m), out var f) ? f : null;

    public void Toggle(Message m)
    {
        var k = Key(m);
        if (IsShown(m))
        {
            if (Entries.TryGetValue(k, out var e)) Entries = Entries.SetItem(k, e with { Shown = false });
            Loading = Loading.Remove(k);
            Failed = Failed.Remove(k);
            ScheduleSave();
            return;
        }
        Translate(m);
    }

    public async void Translate(Message m)
    {
        var k = Key(m);
        if (m.Text is not { } text || !HasWords(text) || Loading.Contains(k)) return;
        var target = Settings.Language;
        Failed = Failed.Remove(k);
        if (Entries.TryGetValue(k, out var e) && e.Original == text && TranslationLanguages.Same(e.Target, target))
        {
            Entries = Entries.SetItem(k, e with { Shown = true });
            ScheduleSave();
            return;
        }
        Loading = Loading.Add(k);
        try
        {
            var r = await Translate(text, target);
            if (!Loading.Contains(k)) return;
            Loading = Loading.Remove(k);
            Entries = Entries.SetItem(k, new Entry { Original = text, Text = r.text, Source = r.source, Target = target, Shown = true, At = DateTime.Now });
            ScheduleSave();
        }
        catch (Exception ex)
        {
            if (!Loading.Contains(k)) return;
            Loading = Loading.Remove(k);
            Failed = Failed.SetItem(k, ex.Message);
        }
    }

    // MARK: Outgoing

    public string ChatLanguage(ChatRef r) => Settings.ChatLanguages.TryGetValue(r.Id, out var l) ? l : Settings.Language;

    public void SetChatLanguage(string code, ChatRef r)
    {
        var s = Workspace.Shared.Translation;
        Workspace.Shared.Translation = s with
        {
            ChatLanguages = TranslationLanguages.Same(code, s.Language) ? s.ChatLanguages.Remove(r.Id) : s.ChatLanguages.SetItem(r.Id, code),
        };
    }

    /// The language a message typed now would go out in; null while the replied-to message's language is still being detected.
    public string? OutgoingLanguage(ChatRef r, Message? reply)
    {
        if (reply == null || !Settings.MatchReplyLanguage || ReplyAutoOff.Contains(r.Id) || !HasWords(reply.Text)) return ChatLanguage(r);
        return KnownLanguage(reply);
    }

    /// A message's language, if a translation or a detection already found it.
    public string? KnownLanguage(Message m)
    {
        if (Entries.TryGetValue(Key(m), out var e) && e.Original == m.Text) return e.Source;
        return ReplyLanguages.TryGetValue(Key(m), out var l) ? l : null;
    }

    public void ClearCache()
    {
        Entries = ImmutableDictionary<string, Entry>.Empty;
        Failed = ImmutableDictionary<string, string>.Empty;
        ReplyLanguages = ImmutableDictionary<string, string>.Empty;
        ScheduleSave();
    }

    /// Same, waiting for detection; falls back to the chat's language when it can't tell.
    public async Task<string> ResolveOutgoing(ChatRef r, Message? reply)
    {
        if (OutgoingLanguage(r, reply) is { } l) return l;
        if (reply != null && await DetectLanguage(reply) is { } d) return d;
        return ChatLanguage(r);
    }

    public async Task<string?> DetectLanguage(Message m)
    {
        var k = Key(m);
        if (KnownLanguage(m) is { } known) return known;
        if (m.Text is not { } text || !HasWords(text)) return null;
        if (_detecting.TryGetValue(k, out var running)) return await running;
        var target = Settings.Language;
        var task = Detect();
        _detecting[k] = task;
        var l = await task;
        _detecting.Remove(k);
        if (l != null) ReplyLanguages = ReplyLanguages.SetItem(k, l);
        return l;

        async Task<string?> Detect()
        {
            try
            {
                var r = await Translate(text.Length > 600 ? text[..600] : text, target);
                if (r.source != "auto") return r.source;
            }
            catch { }
            return LocalGuess(text);
        }
    }

    /// Shows what the user typed under a message that went out translated.
    public void RememberSent(Message m, string typed, string language)
    {
        if (m.Text is not { } sent) return;
        Entries = Entries.SetItem(Key(m), new Entry { Original = sent, Text = typed, Source = language, Target = Settings.Language, Shown = true, At = DateTime.Now });
        ScheduleSave();
    }

    // MARK: Google Translate

    /// One request per ≤ 4,500 characters; the endpoint rejects much longer texts.
    public static async Task<(string text, string source)> Translate(string text, string target, string source = "auto")
    {
        var sb = new StringBuilder();
        var detected = source;
        foreach (var chunk in Chunks(text, 4500))
        {
            var r = await Request(chunk, source, target);
            sb.Append(r.text);
            if (detected == "auto") detected = r.source;
        }
        return (sb.ToString(), detected);
    }

    /// Translates around `@name` tokens so mentions keep pointing at the right people.
    public static async Task<string> Translate(string text, IReadOnlyList<string> keeping, string target)
    {
        var pieces = new List<(string, bool)>();
        var rest = text;
        var tokens = keeping.Where(t => t.Length > 0).ToList();
        while (true)
        {
            var best = -1;
            var bestToken = "";
            foreach (var t in tokens)
            {
                var at = rest.IndexOf(t, StringComparison.Ordinal);
                if (at >= 0 && (best < 0 || at < best)) { best = at; bestToken = t; }
            }
            if (best < 0) break;
            pieces.Add((rest[..best], true));
            pieces.Add((bestToken, false));
            rest = rest[(best + bestToken.Length)..];
        }
        pieces.Add((rest, true));
        var sb = new StringBuilder();
        foreach (var (piece, isText) in pieces)
        {
            if (!isText || !HasWords(piece)) { sb.Append(piece); continue; }
            var lead = piece[..(piece.Length - piece.TrimStart().Length)];
            var trail = piece[piece.TrimEnd().Length..];
            var translated = (await Translate(piece.Trim(), target)).text;
            sb.Append(lead).Append(translated).Append(trail);
        }
        return sb.ToString();
    }

    private static async Task<(string text, string source)> Request(string text, string source, string target)
    {
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={Uri.EscapeDataString(source)}&tl={Uri.EscapeDataString(target)}&dt=t";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("q=" + Uri.EscapeDataString(text), Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        using var resp = await Http.SendAsync(req);
        if ((int)resp.StatusCode != 200) throw TranslationException.Http((int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        // [[["translated","original",…],…],null,"detected source",…]
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            foreach (var s in root[0].EnumerateArray())
                if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 && s[0].ValueKind == JsonValueKind.String) sb.Append(s[0].GetString());
            if (sb.Length == 0) throw TranslationException.BadResponse;
            var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString()! : source;
            return (sb.ToString(), detected);
        }
        catch (TranslationException) { throw; }
        catch { throw TranslationException.BadResponse; }
    }

    private static List<string> Chunks(string text, int limit)
    {
        if (text.Length <= limit) return [text];
        var output = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            while (line.Length > limit)
            {
                if (current.Length > 0) { output.Add(current.ToString()); current.Clear(); }
                output.Add(line[..limit] + "\n");
                line = line[limit..];
            }
            if (current.Length + line.Length + 1 > limit) { output.Add(current.ToString()); current.Clear(); }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) output.Add(current.ToString());
        if (output.Count > 0 && output[^1].EndsWith('\n') && !text.EndsWith('\n')) output[^1] = output[^1][..^1];
        return output;
    }

    /// Script-based guess when Google can't be reached (macOS uses NLLanguageRecognizer here).
    private static string? LocalGuess(string text)
    {
        int Count(Func<char, bool> f) => text.Count(f);
        if (Count(c => c is >= '\u3040' and <= '\u30FF') > 0) return "ja";
        if (Count(c => c is >= '\uAC00' and <= '\uD7AF') > 0) return "ko";
        if (Count(c => c is >= '\u4E00' and <= '\u9FFF') > 0) return "zh-CN";
        if (Count(c => c is >= '\u0E00' and <= '\u0E7F') > 0) return "th";
        if (Count(c => c is >= '\u0400' and <= '\u04FF') > 0) return "ru";
        if (Count(c => c is >= '\u0600' and <= '\u06FF') > 0) return "ar";
        if (Count(c => "ăâđêôơưạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ".Contains(char.ToLowerInvariant(c))) > 0) return "vi";
        if (Count(char.IsLetter) > 0) return "en";
        return null;
    }

    // MARK: Persistence

    private async void ScheduleSave()
    {
        if (DemoData.Enabled) return;
        _saveTask?.Cancel();
        var cts = new CancellationTokenSource();
        _saveTask = cts;
        try { await Task.Delay(1000, cts.Token); } catch { return; }
        if (Entries.Count > MaxEntries)
            Entries = Entries.OrderByDescending(p => p.Value.At).Take(MaxEntries).ToImmutableDictionary();
        var snapshot = Entries;
        await Task.Run(() =>
        {
            try { File.WriteAllText(FilePath, Json.Encode(snapshot)); } catch { }
        });
    }
}
