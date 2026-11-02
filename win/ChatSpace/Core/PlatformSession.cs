using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using ChatSpace.Kit;

namespace ChatSpace.Core;

using MessageMap = ImmutableDictionary<string, ImmutableList<Message>>;

/// Observable state for one platform, fed by its bridge process.
public sealed class PlatformSession : Observable
{
    public Platform Platform { get; }

    private AuthState _authState = AuthState.Connecting;
    public AuthState AuthState { get => Get(ref _authState); set => Set(ref _authState, value); }
    private SelfInfo? _selfInfo;
    public SelfInfo? SelfInfo { get => Get(ref _selfInfo); set => Set(ref _selfInfo, value); }
    private string? _authError;
    public string? AuthError { get => Get(ref _authError); set => Set(ref _authError, value); }
    private string? _qrPayload;
    public string? QrPayload { get => Get(ref _qrPayload); set => Set(ref _qrPayload, value); }
    private Bitmap? _qrImage;
    public Bitmap? QrImage { get => Get(ref _qrImage); set => Set(ref _qrImage, value); }
    /// When the current QR stops being scannable (ms epoch), if the bridge said.
    private double? _qrExpiresAt;
    public double? QrExpiresAt { get => Get(ref _qrExpiresAt); set => Set(ref _qrExpiresAt, value); }
    private bool _qrRefreshing;
    public bool QrRefreshing { get => Get(ref _qrRefreshing); set => Set(ref _qrRefreshing, value); }
    private AuthStep? _authStep;
    public AuthStep? AuthStep { get => Get(ref _authStep); set => Set(ref _authStep, value); }
    private bool _authBusy;
    public bool AuthBusy { get => Get(ref _authBusy); set => Set(ref _authBusy, value); }
    private string _connection = "connecting";
    public string Connection { get => Get(ref _connection); set => Set(ref _connection, value); }
    private string? _connectionDetail;
    public string? ConnectionDetail { get => Get(ref _connectionDetail); set => Set(ref _connectionDetail, value); }
    private ImmutableHashSet<string> _capabilities = [];
    public ImmutableHashSet<string> Capabilities { get => Get(ref _capabilities); set => Set(ref _capabilities, value); }
    private string? _bridgeError;
    public string? BridgeError { get => Get(ref _bridgeError); set => Set(ref _bridgeError, value); }

    private ImmutableList<Chat> _chats = [];
    public ImmutableList<Chat> Chats { get => Get(ref _chats); set => Set(ref _chats, value); }
    private ImmutableList<ChatFolder> _folders = [];
    public ImmutableList<ChatFolder> Folders { get => Get(ref _folders); set => Set(ref _folders, value); }
    /// Telegram folder shown in the chat list; null = All Chats.
    private string? _selectedFolderId;
    public string? SelectedFolderId { get => Get(ref _selectedFolderId); set => Set(ref _selectedFolderId, value); }
    private MessageMap _messages = MessageMap.Empty;
    public MessageMap Messages { get => Get(ref _messages); set => Set(ref _messages, value); }
    private string? _selectedChatId;
    public string? SelectedChatId { get => Get(ref _selectedChatId); set => Set(ref _selectedChatId, value); }
    private ImmutableDictionary<string, ImmutableList<TypingInfo>> _typing = ImmutableDictionary<string, ImmutableList<TypingInfo>>.Empty;
    public ImmutableDictionary<string, ImmutableList<TypingInfo>> Typing { get => Get(ref _typing); set => Set(ref _typing, value); }
    private ImmutableHashSet<string> _reachedStart = [];
    public ImmutableHashSet<string> ReachedStart { get => Get(ref _reachedStart); set => Set(ref _reachedStart, value); }
    private ImmutableHashSet<string> _loadingHistory = [];
    public ImmutableHashSet<string> LoadingHistory { get => Get(ref _loadingHistory); set => Set(ref _loadingHistory, value); }
    private ImmutableDictionary<string, string> _drafts = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> Drafts { get => Get(ref _drafts); set => Set(ref _drafts, value); }
    /// Images/files pasted or dropped into a composer, sent with the draft as caption.
    private ImmutableDictionary<string, ImmutableList<string>> _attachments = ImmutableDictionary<string, ImmutableList<string>>.Empty;
    public ImmutableDictionary<string, ImmutableList<string>> Attachments { get => Get(ref _attachments); set => Set(ref _attachments, value); }
    private ImmutableDictionary<string, Message> _replyingTo = ImmutableDictionary<string, Message>.Empty;
    public ImmutableDictionary<string, Message> ReplyingTo { get => Get(ref _replyingTo); set => Set(ref _replyingTo, value); }
    /// Message whose text the composer is editing, per chat; the draft holds the new text meanwhile.
    private ImmutableDictionary<string, Message> _editing = ImmutableDictionary<string, Message>.Empty;
    public ImmutableDictionary<string, Message> Editing { get => Get(ref _editing); set => Set(ref _editing, value); }
    /// Why the last edit in a chat was refused, shown in the edit bar until the next attempt.
    private ImmutableDictionary<string, string> _editError = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> EditError { get => Get(ref _editError); set => Set(ref _editError, value); }
    private readonly Dictionary<string, ComposerStash> _editStash = new();
    /// Group members for the "@" list, loaded on first use.
    private ImmutableDictionary<string, ImmutableList<ChatMember>> _members = ImmutableDictionary<string, ImmutableList<ChatMember>>.Empty;
    public ImmutableDictionary<string, ImmutableList<ChatMember>> Members { get => Get(ref _members); set => Set(ref _members, value); }
    private ImmutableDictionary<string, ImmutableList<MentionPick>> _mentionPicks = ImmutableDictionary<string, ImmutableList<MentionPick>>.Empty;
    public ImmutableDictionary<string, ImmutableList<MentionPick>> MentionPicks { get => Get(ref _mentionPicks); set => Set(ref _mentionPicks, value); }
    private ImmutableDictionary<string, MathPreview> _mathPreviews = ImmutableDictionary<string, MathPreview>.Empty;
    public ImmutableDictionary<string, MathPreview> MathPreviews { get => Get(ref _mathPreviews); set => Set(ref _mathPreviews, value); }
    private readonly HashSet<string> _loadingMembers = new();
    /// Messenger's PIN-protected backup of end-to-end encrypted chats (history.status).
    private HistoryBackup _historyBackup = HistoryBackup.Unknown;
    public HistoryBackup HistoryBackup { get => Get(ref _historyBackup); set => Set(ref _historyBackup, value); }
    /// Encrypted Messenger chats whose pre-encryption history is loading / fully loaded (history.loadOpen).
    private ImmutableHashSet<string> _loadingOpenHistory = [];
    public ImmutableHashSet<string> LoadingOpenHistory { get => Get(ref _loadingOpenHistory); set => Set(ref _loadingOpenHistory, value); }
    private ImmutableHashSet<string> _openHistoryDone = [];
    public ImmutableHashSet<string> OpenHistoryDone { get => Get(ref _openHistoryDone); set => Set(ref _openHistoryDone, value); }
    /// Zalo history transfer from the phone (history.phoneSync); null until the bridge reports it.
    private PhoneSyncProgress? _phoneSync;
    public PhoneSyncProgress? PhoneSync { get => Get(ref _phoneSync); set => Set(ref _phoneSync, value); }
    /// In-chat search jumps here (message id per chat).
    private ImmutableDictionary<string, string> _scrollTarget = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> ScrollTarget { get => Get(ref _scrollTarget); set => Set(ref _scrollTarget, value); }

    private BridgeProcess? _bridge;
    private readonly HashSet<string> _avatarRequested = new();
    private readonly HashSet<string> _downloadRequested = new();
    private int _restartAttempts;
    private readonly Dictionary<string, CancellationTokenSource> _typingExpiry = new();
    /// The user is on a QR screen and expects a scannable code at all times.
    private bool _qrWanted;
    private CancellationTokenSource? _qrWatchdog;
    private Task? _sendQueue;

    public PlatformSession(Platform platform) { Platform = platform; }

    public Chat? SelectedChat => Chats.FirstOrDefault(c => c.Id == SelectedChatId);

    /// The number each official app puts on its icon: Telegram counts unread messages, the others
    /// count unread conversations. Muted chats are excluded everywhere (the apps' default).
    public int UnreadTotal
    {
        get
        {
            var active = Chats.Where(c => c.HasUnread && !c.IsMuted);
            return Platform == Platform.Telegram ? active.Sum(c => Math.Max(c.Unread, 1)) : active.Count();
        }
    }

    /// The user is looking at this chat right now: app frontmost, this platform shown, chat open.
    public bool IsViewing(string chatId) => AppModel.Shared.IsDisplaying(new ChatRef(Platform, chatId));

    /// Chats in the selected Telegram folder (all chats when none is selected).
    public IReadOnlyList<Chat> VisibleChats
    {
        get
        {
            var id = SelectedFolderId;
            var f = id == null ? null : Folders.FirstOrDefault(x => x.Id == id);
            if (f == null) return Chats;
            var pinned = f.PinnedChatIds;
            var list = Chats.Where(f.Contains).ToList();
            if (pinned.Count == 0) return list;
            // Inside a folder Telegram orders by the folder's own pins, not the global ones.
            var top = pinned.Select(pid => list.FirstOrDefault(c => c.Id == pid)).Where(c => c != null).Select(c => c! with { Pinned = true });
            var rest = list.Where(c => !pinned.Contains(c.Id)).OrderByDescending(c => c.Timestamp).Select(c => c with { Pinned = false });
            return top.Concat(rest).ToList();
        }
    }

    /// Badge on a folder tab: Telegram counts unread chats, greyed when all of them are muted.
    public (int count, bool muted) FolderBadge(ChatFolder? f)
    {
        var list = (f == null ? Chats : Chats.Where(f.Contains)).Where(c => c.HasUnread).ToList();
        return (list.Count, list.Count > 0 && list.All(c => c.IsMuted));
    }

    public bool Can(string capability) => Capabilities.Contains(capability);

    private static async void Fire(Func<Task> work)
    {
        try { await work(); } catch { }
    }

    // MARK: Lifecycle

    public void Start()
    {
        if (_bridge != null) return;
        if (DemoData.Enabled)
        {
            DemoData.Load(this);
            return;
        }
        var b = new BridgeProcess(Platform);
        b.OnEvent = HandleEvent;
        b.OnExit = BridgeExited;
        try
        {
            b.Start();
            _bridge = b;
            BridgeError = null;
            Fire(Handshake);
        }
        catch (Exception e)
        {
            BridgeError = e.Message;
            AuthState = AuthState.Error;
        }
    }

    public void Stop()
    {
        if (_bridge != null) _bridge.OnExit = null;
        _bridge?.Stop();
        _bridge = null;
    }

    private async Task Handshake()
    {
        try
        {
            var hello = await Call("bridge.hello", new JsonObject { ["config"] = AppSettings.BridgeConfig(Platform) });
            if (hello.Get("capabilities") is JsonArray caps)
                Capabilities = caps.Select(c => c?.GetValue<string>()).Where(c => c != null).Select(c => c!).ToImmutableHashSet();
            ConfigureStorage();
            var status = await Call("auth.status");
            ApplyAuth(status);
            if (AuthState == AuthState.LoggedIn) await RefreshChats();
        }
        catch (Exception e)
        {
            BridgeError = e.Message;
        }
    }

    private void BridgeExited(int status)
    {
        _bridge = null;
        Connection = "offline";
        if (_restartAttempts >= 5)
        {
            BridgeError = $"Bridge {Platform.DisplayName()} dừng (mã {status}). Xem bridge.log.";
            AuthState = AuthState.Error;
            return;
        }
        _restartAttempts++;
        var delay = _restartAttempts * _restartAttempts * 1000;
        Fire(async () =>
        {
            await Task.Delay(delay);
            Start();
        });
    }

    public void Restart()
    {
        Stop();
        _restartAttempts = 0;
        Start();
    }

    public Task<JsonNode?> Call(string method, JsonObject? parameters = null)
    {
        if (_bridge == null) return Task.FromException<JsonNode?>(BridgeException.Exited);
        return _bridge.Call(method, parameters);
    }

    // MARK: Auth

    public void Login(string method = "qr", string? cookies = null, string? phone = null, string? username = null, string? password = null)
    {
        AuthError = null;
        AuthStep = null;
        _qrWanted = method == "qr";
        if (_qrWanted) QrRefreshing = QrImage != null;
        var p = new JsonObject { ["method"] = method };
        if (cookies != null) p["cookies"] = cookies;
        if (phone != null) p["phone"] = phone;
        if (username != null) p["username"] = username;
        if (password != null) p["password"] = password;
        AuthBusy = true;
        Fire(async () =>
        {
            try { await Call("auth.login", p); }
            catch (Exception e) { AuthError = e.Message; }
            finally { AuthBusy = false; }
        });
    }

    /// Answers the current `auth.step` of a multi-step native login.
    public void SubmitStep(IReadOnlyDictionary<string, string> inputs)
    {
        AuthError = null;
        AuthBusy = true;
        var obj = new JsonObject();
        foreach (var (k, v) in inputs) obj[k] = v;
        Fire(async () =>
        {
            try { await Call("auth.submit", new JsonObject { ["inputs"] = obj }); }
            catch (Exception e) { AuthError = e.Message; }
            finally { AuthBusy = false; }
        });
    }

    /// Leaves a native login flow locally; the bridge discards it on the next `auth.login`.
    public void AbandonStep()
    {
        AuthStep = null;
        AuthError = null;
        if (AuthState == AuthState.AwaitingInput) AuthState = AuthState.LoggedOut;
    }

    /// Called by QR screens: start a QR flow unless a live code is already showing.
    public void EnsureQR()
    {
        _qrWanted = true;
        if (QrImage == null || IsQRExpired || AuthState == AuthState.LoggedOut) RefreshQR();
    }

    /// QR screen went away (other login method, view closed).
    public void StopWantingQR()
    {
        _qrWanted = false;
        _qrWatchdog?.Cancel();
    }

    public bool IsQRExpired => QrExpiresAt is { } e && e < Time.NowMs;

    private readonly List<DateTime> _autoRefreshes = new();

    /// `auto`: at most 10 unattended refreshes per 10 minutes, 3 s apart, so a failing bridge can't spin.
    public void RefreshQR(bool auto = false)
    {
        if (AuthBusy || AuthState == AuthState.LoggedIn) return;
        if (auto)
        {
            var now = DateTime.UtcNow;
            _autoRefreshes.RemoveAll(d => (now - d).TotalSeconds > 600);
            if (_autoRefreshes.Count >= 10 || (_autoRefreshes.Count > 0 && (now - _autoRefreshes[^1]).TotalSeconds < 3))
            {
                QrRefreshing = false;
                return;
            }
            _autoRefreshes.Add(now);
        }
        QrRefreshing = true;
        Login("qr");
    }

    /// If a code expires and the bridge doesn't replace it within a few seconds, request a new flow.
    private void ArmQRWatchdog()
    {
        _qrWatchdog?.Cancel();
        if (QrExpiresAt is not { } exp) return;
        var wait = Math.Max(0, exp - Time.NowMs) + 6000;
        var cts = new CancellationTokenSource();
        _qrWatchdog = cts;
        Fire(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait), cts.Token);
            if (cts.IsCancellationRequested || !_qrWanted || AuthState == AuthState.LoggedIn || !IsQRExpired) return;
            RefreshQR(auto: true);
        });
    }

    public void CancelStep() => Fire(async () =>
    {
        try { await Call("auth.cancelStep"); } catch (Exception e) { AuthError = e.Message; }
    });

    public void Submit(string? password = null, string? code = null)
    {
        AuthError = null;
        var p = new JsonObject();
        if (password != null) p["password"] = password;
        if (code != null) p["code"] = code;
        Fire(async () =>
        {
            try { await Call("auth.submit", p); } catch (Exception e) { AuthError = e.Message; }
        });
    }

    public void Logout() => Fire(async () =>
    {
        try { await Call("auth.logout"); } catch { }
        Chats = [];
        Messages = MessageMap.Empty;
        SelectedChatId = null;
        AuthState = AuthState.LoggedOut;
        Notifier.Shared.Reconcile(Platform, new HashSet<string>());
    });

    private void ApplyAuth(JsonNode? v)
    {
        if (v.Str("state") is { } s && TryAuthState(s) is { } st)
        {
            var wasLoggedIn = AuthState == AuthState.LoggedIn;
            AuthState = st;
            if (st != AuthState.AwaitingInput) AuthStep = null;
            else if (v.Get("step") is { } step && step.TryDecode<AuthStep>() is { } decoded) AuthStep = decoded;
            if (st == AuthState.LoggedIn)
            {
                QrImage = null;
                QrPayload = null;
                QrExpiresAt = null;
                _qrWanted = false;
                _qrWatchdog?.Cancel();
                _restartAttempts = 0;
                if (!wasLoggedIn) Fire(RefreshChats);
            }
            else if (st == AuthState.LoggedOut)
            {
                HistoryBackup = HistoryBackup.Unknown;
            }
        }
        if (v.Get("self") is JsonObject selfV) SelfInfo = selfV.TryDecode<SelfInfo>();
        var e = v.Str("error");
        if (e != null && e != "qr_expired") AuthError = e;
        // The platform stopped issuing codes: start a fresh flow instead of leaving a dead QR on screen.
        if (_qrWanted && AuthState == AuthState.LoggedOut && (e == "qr_expired" || QrImage != null))
        {
            QrImage = null;
            RefreshQR(auto: true);
        }
    }

    private static AuthState? TryAuthState(string s) =>
        Enum.GetValues<AuthState>().Cast<AuthState?>().FirstOrDefault(a => Json.SnakeCase(a!.Value.ToString()) == s);

    // MARK: Data

    public async Task RefreshChats()
    {
        try
        {
            var r = await Call("chats.list", new JsonObject { ["limit"] = 200 });
            Chats = Sorted(r.Get("chats").Decode<List<Chat>>() ?? []);
            Notifier.Shared.Reconcile(Platform, Chats.Where(c => c.HasUnread).Select(c => c.Id).ToHashSet());
        }
        catch (Exception e)
        {
            BridgeError = e.Message;
        }
        if (Can("folders"))
        {
            try
            {
                var r = await Call("chats.folders");
                if (r.Get("folders").TryDecode<List<ChatFolder>>() is { } f) ApplyFolders(f);
            }
            catch { }
        }
    }

    public void ApplyFolders(IReadOnlyList<ChatFolder> f)
    {
        Folders = f.ToImmutableList();
        if (SelectedFolderId is { } id && !f.Any(x => x.Id == id)) SelectedFolderId = null;
    }

    // MARK: Storage

    /// Pushes Settings → Dữ liệu và bộ nhớ to the bridge (unknown methods are fine: Messenger has no cache).
    public void ConfigureStorage()
    {
        if (_bridge == null) return;
        var p = Workspace.Shared.Storage.BridgeParams;
        Fire(async () => { try { await Call("storage.configure", p); } catch { } });
    }

    public sealed record StorageUsage(double MediaBytes, int MediaFiles, double DatabaseBytes);

    public async Task<StorageUsage?> GetStorageUsage()
    {
        if (DemoData.Enabled)
        {
            double seed = Platform.Raw().Length;
            return new StorageUsage(seed * 61_000_000, (int)seed * 140, seed * 4_200_000);
        }
        try
        {
            var r = await Call("storage.usage");
            return new StorageUsage(r.Num("mediaBytes") ?? 0, (int)(r.Num("mediaFiles") ?? 0), r.Num("databaseBytes") ?? 0);
        }
        catch { return null; }
    }

    public async Task<double> ClearStorage(int? olderThanDays = null)
    {
        var p = new JsonObject();
        if (olderThanDays is { } d) p["olderThanDays"] = d;
        double freed;
        try
        {
            var r = await Call("storage.clear", p);
            if (r.Num("freedBytes") is not { } f) return 0;
            freed = f;
        }
        catch { return 0; }
        var map = Messages;
        foreach (var (k, list) in Messages)
        {
            var changed = list.Select(m => m.Media?.Path is { } path && !File.Exists(path) ? m.WithMediaPath(null) : m).ToImmutableList();
            map = map.SetItem(k, changed);
        }
        Messages = map;
        _downloadRequested.Clear();
        return freed;
    }

    public void Select(string chatId)
    {
        SelectedChatId = chatId;
        Prepare(chatId);
    }

    /// Opens a chat outside this platform's own list (ChatSpace inbox, custom group, chat bubble).
    public void Prepare(string chatId)
    {
        TrimMessages(chatId);
        if (!Messages.ContainsKey(chatId)) Fire(() => LoadMessages(chatId));
        MarkRead(chatId);
    }

    private const int KeptPerChat = 50;

    /// A chat scrolled far back holds hundreds of messages; once left, keep its newest page only
    /// (scrolling up reloads the rest from the bridge).
    private void TrimMessages(string except)
    {
        var bubble = ChatHeads.Shared.Expanded;
        var map = Messages;
        var reached = ReachedStart;
        foreach (var (id, list) in Messages)
        {
            if (id == except || list.Count <= KeptPerChat + 10) continue;
            if (bubble == new ChatRef(Platform, id)) continue;
            map = map.SetItem(id, list.GetRange(list.Count - KeptPerChat, KeptPerChat));
            reached = reached.Remove(id);
        }
        Messages = map;
        ReachedStart = reached;
    }

    public ImmutableList<Message> MessagesIn(string chatId) => Messages.TryGetValue(chatId, out var l) ? l : [];

    public async Task LoadMessages(string chatId)
    {
        if (LoadingHistory.Contains(chatId)) return;
        LoadingHistory = LoadingHistory.Add(chatId);
        try
        {
            var r = await Call("messages.list", new JsonObject { ["chatId"] = chatId, ["limit"] = 50 });
            var list = r.Get("messages").Decode<List<Message>>() ?? [];
            Messages = Messages.SetItem(chatId, Merge(MessagesIn(chatId), list));
            if (list.Count < 50) ReachedStart = ReachedStart.Add(chatId);
        }
        catch (Exception e)
        {
            BridgeError = e.Message;
        }
        finally
        {
            LoadingHistory = LoadingHistory.Remove(chatId);
        }
    }

    public void LoadOlder(string chatId)
    {
        if (ReachedStart.Contains(chatId) || LoadingHistory.Contains(chatId)) return;
        var first = MessagesIn(chatId).FirstOrDefault();
        if (first == null) return;
        LoadingHistory = LoadingHistory.Add(chatId);
        Fire(async () =>
        {
            try
            {
                var r = await Call("messages.list", new JsonObject { ["chatId"] = chatId, ["limit"] = 50, ["before"] = first.Id });
                var list = r.Get("messages").Decode<List<Message>>() ?? [];
                if (list.Count == 0) ReachedStart = ReachedStart.Add(chatId);
                Messages = Messages.SetItem(chatId, Merge(MessagesIn(chatId), list));
            }
            catch
            {
                ReachedStart = ReachedStart.Add(chatId);
            }
            finally
            {
                LoadingHistory = LoadingHistory.Remove(chatId);
            }
        });
    }

    public string Draft(string chatId) => Drafts.TryGetValue(chatId, out var d) ? d : "";
    public void SetDraft(string chatId, string text) => Drafts = Drafts.SetItem(chatId, text);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "gif", "heic", "heif", "webp", "bmp", "tif", "tiff" };

    public static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    /// Without an explicit file, the chat's staged attachments go out too, the text as the first one's caption.
    /// Text with arithmetic waits in `MathPreviews` unless `checkMath` is off; sending it again picks the computed version.
    public void Send(string text, string chatId, string? filePath = null, bool checkMath = true)
    {
        if (filePath == null && Editing.TryGetValue(chatId, out var target))
        {
            CommitEdit(target, text);
            return;
        }
        var trimmed = text.Trim();
        var files = filePath != null ? [filePath] : (Attachments.TryGetValue(chatId, out var a) ? a.ToList() : new List<string>());
        if (trimmed.Length == 0 && files.Count == 0) return;
        if (checkMath && Workspace.Shared.MathPreview && MessageMath.Annotate(trimmed) is { } pieces)
        {
            var preview = new MathPreview(trimmed, pieces, filePath);
            if (MathPreviews.TryGetValue(chatId, out var existing) && existing.Equals(preview)) ResolveMath(chatId, true);
            else MathPreviews = MathPreviews.SetItem(chatId, preview);
            return;
        }
        MathPreviews = MathPreviews.Remove(chatId);
        var staged = filePath == null && Attachments.TryGetValue(chatId, out var st) ? st : null;
        if (filePath == null) Attachments = Attachments.Remove(chatId);
        ReplyingTo.TryGetValue(chatId, out var reply);
        ReplyingTo = ReplyingTo.Remove(chatId);
        var picks = MentionPicks.TryGetValue(chatId, out var pk) ? pk : [];
        MentionPicks = MentionPicks.Remove(chatId);
        SetDraft(chatId, "");
        var cref = new ChatRef(Platform, chatId);
        var translator = Translator.Shared;
        translator.SetSendError(cref.Id, null);
        var previous = _sendQueue;
        _sendQueue = Run();

        async Task Run()
        {
            // Translation takes a round trip; queueing keeps quick successive sends in order.
            if (previous != null) { try { await previous; } catch { } }
            var outgoing = trimmed;
            var language = await translator.ResolveOutgoing(cref, reply);
            translator.ReplyAutoOff = translator.ReplyAutoOff.Remove(cref.Id);
            if (!TranslationLanguages.Same(language, translator.Settings.Language) && Translator.HasWords(trimmed))
            {
                try
                {
                    outgoing = await Translator.Translate(trimmed, picks.Select(p => "@" + p.Name).ToList(), language);
                }
                catch (Exception e)
                {
                    // The untranslated text would reach someone reading another language: hand it back instead.
                    if (Draft(chatId).Length == 0)
                    {
                        SetDraft(chatId, text);
                        if (reply != null && !ReplyingTo.ContainsKey(chatId)) ReplyingTo = ReplyingTo.SetItem(chatId, reply);
                        if (picks.Count > 0) MentionPicks = MentionPicks.SetItem(chatId, picks);
                        if (staged != null && !Attachments.ContainsKey(chatId)) Attachments = Attachments.SetItem(chatId, staged);
                    }
                    translator.SetSendError(cref.Id, $"Không dịch được sang {TranslationLanguages.Name(language)} ({e.Message}). Tin nhắn chưa được gửi.");
                    return;
                }
            }
            var mentions = Mentions.Resolve(picks, outgoing);
            // One after another, so several pasted images arrive in order.
            var list = files.Count == 0 ? new List<string?> { null } : files.Select(f => (string?)f).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var f = list[i];
                try
                {
                    MessageKind? kind = f != null && IsImagePath(f) ? MessageKind.Image : null;
                    var sent = await Post(i == 0 ? outgoing : "", chatId, f, kind, i == 0 ? reply : null, i == 0 ? mentions : []);
                    if (i == 0 && outgoing != trimmed && sent != null) translator.RememberSent(sent, trimmed, language);
                }
                catch (Exception e)
                {
                    BridgeError = e.Message;
                }
            }
        }
    }

    public void ResolveMath(string chatId, bool computed)
    {
        if (!MathPreviews.TryGetValue(chatId, out var p)) return;
        MathPreviews = MathPreviews.Remove(chatId);
        Send(computed ? p.Computed : p.Original, chatId, p.FilePath, checkMath: false);
    }

    public void Stage(IEnumerable<string> files, string chatId)
    {
        var have = Attachments.TryGetValue(chatId, out var a) ? a : [];
        Attachments = Attachments.SetItem(chatId, have.AddRange(files.Where(f => !have.Contains(f))));
    }

    public void Unstage(string file, string chatId)
    {
        if (!Attachments.TryGetValue(chatId, out var a)) return;
        a = a.Remove(file);
        Attachments = a.IsEmpty ? Attachments.Remove(chatId) : Attachments.SetItem(chatId, a);
    }

    private void UpdateMessage(string chatId, string id, Func<Message, Message> change)
    {
        var list = MessagesIn(chatId);
        var i = list.FindIndex(m => m.Id == id);
        if (i < 0) return;
        Messages = Messages.SetItem(chatId, list.SetItem(i, change(list[i])));
    }

    private void RemoveMessage(string chatId, string id)
    {
        if (!Messages.TryGetValue(chatId, out var list)) return;
        Messages = Messages.SetItem(chatId, list.RemoveAll(m => m.Id == id));
    }

    /// Sends without touching the chat's composer (draft, reply, mentions), showing it at once as pending
    /// and marking it failed if the bridge refuses. `kind` only shapes that pending bubble.
    public async Task<Message?> Post(string text, string chatId, string? filePath = null, MessageKind? kind = null,
        Message? reply = null, IReadOnlyList<JsonObject>? mentions = null)
    {
        var tempId = "pending-" + Guid.NewGuid();
        var optimistic = new Message
        {
            Id = tempId, ChatId = chatId, SenderId = SelfInfo?.Id ?? "me", SenderName = SelfInfo?.Name, FromMe = true,
            Timestamp = Time.NowMs, Kind = kind ?? (filePath == null ? MessageKind.Text : MessageKind.File),
            Text = text.Length == 0 ? null : text, Status = DeliveryStatus.Pending,
        };
        if (filePath != null) optimistic = optimistic with { Media = new Media { Path = filePath, FileName = Path.GetFileName(filePath) } };
        if (reply != null) optimistic = optimistic with { ReplyTo = new ReplyRef { Id = reply.Id, Text = reply.Text, SenderName = reply.SenderName, Kind = reply.Kind } };
        Upsert(optimistic);

        var p = new JsonObject { ["chatId"] = chatId };
        if (text.Length > 0) p["text"] = text;
        if (filePath != null) p["filePath"] = filePath;
        if (reply != null) p["replyTo"] = reply.Id;
        if (mentions is { Count: > 0 } && filePath == null) p["mentions"] = new JsonArray(mentions.Select(m => (JsonNode)m.DeepClone()).ToArray());
        if (DemoData.Enabled)
        {
            var sent = optimistic with { Id = "demo-" + Guid.NewGuid(), Status = DeliveryStatus.Sent };
            RemoveMessage(chatId, tempId);
            Upsert(sent);
            return sent;
        }
        try
        {
            var r = await Call("messages.send", p);
            var m = r.Get("message").Decode<Message>();
            if (m != null)
            {
                RemoveMessage(chatId, tempId);
                Upsert(m);
            }
            if (Platform is Platform.Whatsapp or Platform.Telegram) NativeAssets.Shared.Play(Platform, SoundKind.Sent);
            return m;
        }
        catch
        {
            UpdateMessage(chatId, tempId, m => m with { Status = DeliveryStatus.Failed });
            throw;
        }
    }

    /// The platform's own forward (capability `forward`): same message, "Forwarded" label, no re-upload.
    public async Task ForwardNatively(Message message, string chatId)
    {
        if (DemoData.Enabled)
        {
            Upsert(message with
            {
                Id = "demo-" + Guid.NewGuid(), ChatId = chatId, Timestamp = Time.NowMs, Forwarded = !message.FromMe, FromMe = true,
                SenderId = SelfInfo?.Id ?? "me", Reactions = null, ReplyTo = null, Status = DeliveryStatus.Sent,
            });
            return;
        }
        var r = await Call("messages.forward", new JsonObject { ["fromChatId"] = message.ChatId, ["messageId"] = message.Id, ["toChatId"] = chatId });
        if (r.Get("message").Decode<Message>() is { } m) Upsert(m);
    }

    public void LoadMembers(string chatId)
    {
        if (!Can("mentions") || _loadingMembers.Contains(chatId)) return;
        if (DemoData.Enabled)
        {
            Members = Members.SetItem(chatId, DemoData.Members(chatId).ToImmutableList());
            return;
        }
        _loadingMembers.Add(chatId);
        Fire(async () =>
        {
            try
            {
                var r = await Call("chats.members", new JsonObject { ["chatId"] = chatId });
                Members = Members.SetItem(chatId, (r.Get("members").Decode<List<ChatMember>>() ?? []).ToImmutableList());
            }
            catch
            {
                Members = Members.SetItem(chatId, []);
                await Task.Delay(20_000);
                if (Members.TryGetValue(chatId, out var m) && m.IsEmpty) Members = Members.Remove(chatId);
            }
            finally
            {
                _loadingMembers.Remove(chatId);
            }
        });
    }

    public async Task CheckHistoryBackup()
    {
        if (!Can("history_pin") || HistoryBackup != HistoryBackup.Unknown || DemoData.Enabled) return;
        for (var i = 0; i < 6; i++)
        {
            try
            {
                var r = await Call("history.status");
                HistoryBackup = r.Bool("restored") == true ? HistoryBackup.Restored
                    : r.Bool("available") == true ? HistoryBackup.Locked : HistoryBackup.None;
                return;
            }
            catch { }
            await Task.Delay(10_000);
        }
    }

    /// Unlocks the encrypted backup; on success every chat reloads with its restored history.
    public async Task<HistoryRestoreResult> RestoreHistory(string pin)
    {
        try
        {
            var r = await Call("history.restore", new JsonObject { ["pin"] = pin });
            if (r.Bool("restored") != true)
            {
                if (r.Bool("noBackup") == true) return new HistoryRestoreResult.NoBackup();
                if (r.Num("attemptsRemaining") is { } n) return new HistoryRestoreResult.WrongPin((int)n);
                return new HistoryRestoreResult.WrongPin(null);
            }
            HistoryBackup = HistoryBackup.Restored;
            Messages = Messages.Where(p => !p.Value.IsEmpty).ToImmutableDictionary();
            ReachedStart = [];
            if (SelectedChatId is { } id && !Messages.ContainsKey(id)) await LoadMessages(id);
            await RefreshChats();
            return new HistoryRestoreResult.Restored();
        }
        catch (Exception e)
        {
            return new HistoryRestoreResult.Failed(e.Message);
        }
    }

    /// Loads the next older page of an encrypted chat's pre-encryption history. Contacts Meta, so it
    /// runs only when the user asks for it.
    public void LoadOpenHistory(string chatId)
    {
        if (!Can("open_history") || LoadingOpenHistory.Contains(chatId) || OpenHistoryDone.Contains(chatId)) return;
        LoadingOpenHistory = LoadingOpenHistory.Add(chatId);
        Fire(async () =>
        {
            try
            {
                var r = await Call("history.loadOpen", new JsonObject { ["chatId"] = chatId });
                if (r.Bool("done") == true) OpenHistoryDone = OpenHistoryDone.Add(chatId);
                if (r.Num("added") is not { } added || added <= 0) return;
                var limit = MessagesIn(chatId).Count + added;
                var list = await Call("messages.list", new JsonObject { ["chatId"] = chatId, ["limit"] = limit });
                Messages = Messages.SetItem(chatId, Merge(MessagesIn(chatId), list.Get("messages").Decode<List<Message>>() ?? []));
            }
            catch (Exception e)
            {
                BridgeError = e.Message;
            }
            finally
            {
                LoadingOpenHistory = LoadingOpenHistory.Remove(chatId);
            }
        });
    }

    /// Asks the phone for the account's history (the user approves it in the Zalo app on the phone).
    public void StartPhoneSync()
    {
        if (!Can("phone_sync")) return;
        PhoneSync = new PhoneSyncProgress { State = PhoneSyncProgress.SyncState.Preparing };
        Fire(async () =>
        {
            try
            {
                var r = await Call("history.phoneSync", new JsonObject { ["action"] = "start" });
                if (r.TryDecode<PhoneSyncProgress>() is { } p && PhoneSync?.State == PhoneSyncProgress.SyncState.Preparing) PhoneSync = p;
            }
            catch (Exception e)
            {
                PhoneSync = new PhoneSyncProgress { State = PhoneSyncProgress.SyncState.Failed, Error = e.Message };
            }
        });
    }

    public void CancelPhoneSync() => Fire(async () =>
    {
        try { await Call("history.phoneSync", new JsonObject { ["action"] = "cancel" }); } catch { }
    });

    public async Task LoadPhoneSyncStatus()
    {
        if (!Can("phone_sync") || PhoneSync != null || DemoData.Enabled) return;
        try
        {
            var r = await Call("history.phoneSync", new JsonObject { ["action"] = "status" });
            PhoneSync = r.TryDecode<PhoneSyncProgress>();
        }
        catch { }
    }

    private void ApplyPhoneSync(PhoneSyncProgress p)
    {
        var ended = PhoneSync?.Running == true && !p.Running;
        PhoneSync = p;
        if (!ended) return;
        if (p.State == PhoneSyncProgress.SyncState.Done) Prefs.Set("zaloPhoneSyncedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        // Every chat may have gained older messages: drop the cached pages so they reload from the bridge.
        Messages = MessageMap.Empty;
        ReachedStart = [];
        Fire(async () =>
        {
            await RefreshChats();
            if (SelectedChatId is { } id) await LoadMessages(id);
            if (ChatHeads.Shared.Expanded is { } r && r.Platform == Platform) await LoadMessages(r.ChatId);
        });
    }

    private void UpdateChat(string chatId, Func<Chat, Chat> change)
    {
        var i = Chats.FindIndex(c => c.Id == chatId);
        if (i >= 0) Chats = Chats.SetItem(i, change(Chats[i]));
    }

    public void MarkRead(string chatId)
    {
        Notifier.Shared.Clear(Platform, chatId);
        var chat = Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat == null || !chat.HasUnread) return;
        UpdateChat(chatId, c => c with { Unread = 0, MarkedUnread = false });
        Fire(async () => { try { await Call("messages.markRead", new JsonObject { ["chatId"] = chatId }); } catch { } });
    }

    public void MarkAllRead()
    {
        foreach (var c in VisibleChats.Where(c => c.HasUnread).ToList()) MarkRead(c.Id);
    }

    public void React(Message message, string emoji)
    {
        if (!Can("reactions")) return;
        var mine = message.Reactions?.FirstOrDefault(r => r.FromMe)?.Emoji;
        var target = mine == emoji ? "" : emoji;
        if (DemoData.Enabled)
        {
            var rs = (message.Reactions ?? []).Select(r => !r.FromMe ? r : r.Count > 1 ? new Reaction(r.Emoji, r.Count - 1, false) : null)
                .Where(r => r != null).Select(r => r!).ToList();
            if (target.Length > 0)
            {
                var i = rs.FindIndex(r => r.Emoji == target.EmojiPresentation());
                if (i >= 0) rs[i] = rs[i] with { Count = rs[i].Count + 1, FromMe = true };
                else rs.Add(new Reaction(target, 1, true));
            }
            UpdateMessage(message.ChatId, message.Id, m => m with { Reactions = rs.Count == 0 ? null : rs });
            return;
        }
        Fire(async () =>
        {
            try { await Call("messages.react", new JsonObject { ["chatId"] = message.ChatId, ["messageId"] = message.Id, ["emoji"] = target }); } catch { }
        });
    }

    public void Delete(Message message, bool forEveryone) => Fire(async () =>
    {
        try
        {
            await Call("messages.delete", new JsonObject { ["chatId"] = message.ChatId, ["messageId"] = message.Id, ["forEveryone"] = forEveryone });
            if (!forEveryone) RemoveMessage(message.ChatId, message.Id);
        }
        catch (Exception e)
        {
            BridgeError = e.Message;
        }
    });

    // Editing

    /// Each official app's rule: your own messages only, with text (Telegram: also a media caption),
    /// within its time limit — Telegram 48 hours (none in Saved Messages and channels), WhatsApp and
    /// Messenger 15 minutes. Zalo has no editing.
    public bool CanEdit(Message m)
    {
        if (!Can("edit") || !m.FromMe || m.IsDeleted || m.Forwarded == true || m.Id.StartsWith("pending-")
            || m.Status is DeliveryStatus.Pending or DeliveryStatus.Failed) return false;
        var hasText = !string.IsNullOrWhiteSpace(m.Text);
        var age = DateTime.Now - m.Date;
        switch (Platform)
        {
            case Platform.Telegram:
                if (m.Kind is not (MessageKind.Text or MessageKind.Link or MessageKind.Image or MessageKind.Video or MessageKind.Gif
                    or MessageKind.File or MessageKind.Audio or MessageKind.Voice)) return false;
                if (m.Kind == MessageKind.Text && !hasText) return false;
                var kind = Chats.FirstOrDefault(c => c.Id == m.ChatId)?.Kind;
                return kind is ChatKind.Self or ChatKind.Channel || age < TimeSpan.FromHours(48);
            case Platform.Whatsapp or Platform.Messenger:
                return m.Kind is MessageKind.Text or MessageKind.Link && hasText && age < TimeSpan.FromMinutes(15);
            default:
                return false;
        }
    }

    /// Puts the message's text in the composer; whatever was being written comes back afterwards.
    public void BeginEdit(Message m, bool force = false)
    {
        if (!force && !CanEdit(m)) return;
        var id = m.ChatId;
        if (!Editing.ContainsKey(id))
            _editStash[id] = new ComposerStash(Draft(id), ReplyingTo.GetValueOrDefault(id), MentionPicks.GetValueOrDefault(id), Attachments.GetValueOrDefault(id));
        Editing = Editing.SetItem(id, m);
        EditError = EditError.Remove(id);
        ReplyingTo = ReplyingTo.Remove(id);
        Attachments = Attachments.Remove(id);
        MentionPicks = MentionPicks.Remove(id);
        MathPreviews = MathPreviews.Remove(id);
        SetDraft(id, m.Text ?? "");
        Avalonia.Threading.Dispatcher.UIThread.Post(ChatSpace.Composer.Focus);
    }

    public void CancelEdit(string chatId)
    {
        if (!Editing.ContainsKey(chatId)) return;
        Editing = Editing.Remove(chatId);
        EditError = EditError.Remove(chatId);
        _editStash.Remove(chatId, out var s);
        SetDraft(chatId, s?.Draft ?? "");
        if (!ReplyingTo.ContainsKey(chatId) && s?.Reply != null) ReplyingTo = ReplyingTo.SetItem(chatId, s.Reply);
        MentionPicks = s?.Picks != null ? MentionPicks.SetItem(chatId, s.Picks) : MentionPicks.Remove(chatId);
        Attachments = s?.Files != null ? Attachments.SetItem(chatId, s.Files) : Attachments.Remove(chatId);
    }

    /// Replying ends an edit in progress, as in the official apps.
    public void StartReply(Message m)
    {
        CancelEdit(m.ChatId);
        ReplyingTo = ReplyingTo.SetItem(m.ChatId, m);
    }

    /// Shows the new text at once; if the platform refuses, the old text returns and the editor reopens
    /// with what was typed and the reason.
    private void CommitEdit(Message target, string text)
    {
        var chatId = target.ChatId;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        if (!CanEdit(target))
        {
            EditError = EditError.SetItem(chatId, "Đã quá thời gian cho phép sửa tin nhắn này.");
            return;
        }
        var picks = MentionPicks.GetValueOrDefault(chatId) ?? [];
        CancelEdit(chatId);
        if (trimmed == (target.Text ?? "").Trim()) return;
        var before = MessagesIn(chatId).FirstOrDefault(x => x.Id == target.Id) ?? target;
        ReplaceLocally(before with { Text = trimmed, Edited = true });
        if (DemoData.Enabled) return;
        var p = new JsonObject { ["chatId"] = chatId, ["messageId"] = target.Id, ["text"] = trimmed };
        var mentions = Mentions.Resolve(picks, trimmed);
        if (mentions.Count > 0) p["mentions"] = new JsonArray(mentions.Select(x => (JsonNode)x.DeepClone()).ToArray());
        Fire(async () =>
        {
            try
            {
                var r = await Call("messages.edit", p);
                if (r.Get("message").Decode<Message>() is { } m) ReplaceLocally(m);
            }
            catch (Exception e)
            {
                if (MessagesIn(chatId).FirstOrDefault(x => x.Id == target.Id)?.Text == trimmed) ReplaceLocally(before);
                if (Editing.ContainsKey(chatId)) return;
                BeginEdit(before, force: true);
                SetDraft(chatId, trimmed);
                if (picks.Count > 0) MentionPicks = MentionPicks.SetItem(chatId, picks);
                EditError = EditError.SetItem(chatId, $"Không sửa được tin nhắn: {e.Message}");
            }
        });
    }

    /// Composer keys of the official apps: ↑ in an empty composer edits your last message, Esc stops editing.
    public bool ComposerCommand(Avalonia.Input.Key key, string chatId)
    {
        if (key == Avalonia.Input.Key.Escape && Editing.ContainsKey(chatId))
        {
            CancelEdit(chatId);
            return true;
        }
        if (key == Avalonia.Input.Key.Up && !Editing.ContainsKey(chatId) && Draft(chatId).Length == 0 && !Attachments.ContainsKey(chatId))
        {
            var m = MessagesIn(chatId).LastOrDefault(CanEdit);
            if (m == null) return false;
            BeginEdit(m);
            return true;
        }
        return false;
    }

    private void ReplaceLocally(Message m)
    {
        var list = MessagesIn(m.ChatId);
        var i = list.FindIndex(x => x.Id == m.Id);
        if (i >= 0)
        {
            var merged = m;
            if (merged.Media is { Path: null } media && list[i].Media?.Path is { } path) merged = merged with { Media = media with { Path = path } };
            Messages = Messages.SetItem(m.ChatId, list.SetItem(i, merged));
        }
        if (Chats.FirstOrDefault(c => c.Id == m.ChatId)?.LastMessage?.Id == m.Id) UpdateChat(m.ChatId, c => c with { LastMessage = m });
    }

    public void SetTyping(string chatId, bool typing)
    {
        if (!Can("typing")) return;
        Fire(async () => { try { await Call("typing.send", new JsonObject { ["chatId"] = chatId, ["typing"] = typing }); } catch { } });
    }

    /// Lazily resolves avatars the bridge didn't include in chats.list.
    public void RequestAvatar(Chat chat)
    {
        if (chat.Avatar != null || DemoData.Enabled || !_avatarRequested.Add(chat.Id)) return;
        Fire(async () =>
        {
            var r = await Call("chats.avatar", new JsonObject { ["chatId"] = chat.Id });
            if (r.Str("avatar") is { } a) UpdateChat(chat.Id, c => c with { Avatar = a });
        });
    }

    /// Downloads media that has no directly fetchable URL (encrypted WhatsApp/Telegram media).
    public void EnsureMedia(Message message)
    {
        if (message.Media?.Path != null || message.Media?.Url != null || DemoData.Enabled) return;
        var key = message.ChatId + "|" + message.Id;
        if (!_downloadRequested.Add(key)) return;
        Fire(async () =>
        {
            var r = await Call("media.download", new JsonObject { ["chatId"] = message.ChatId, ["messageId"] = message.Id });
            if (r.Str("path") is { } path) UpdateMessage(message.ChatId, message.Id, m => m.WithMediaPath(path));
        });
    }

    /// Photos and videos open in ChatSpace's own viewer; other files in their default app.
    public void OpenMedia(Message message)
    {
        if (MediaViewer.CanShow(message)) MediaViewer.Shared.Show(this, message);
        else OpenExternally(message);
    }

    /// Local path of the full-size media, downloading it through the bridge when the platform needs that.
    /// Falls back to the http URL for platforms that serve media straight from their CDN.
    public async Task<string?> MediaSource(Message message)
    {
        if (message.Media?.Path is { } p && File.Exists(p)) return p;
        if (message.Media?.Url is { } u && u.StartsWith("http") && !Can("media_download")) return u;
        try
        {
            var r = await Call("media.download", new JsonObject { ["chatId"] = message.ChatId, ["messageId"] = message.Id });
            if (r.Str("path") is { } path) return path;
        }
        catch { }
        return message.Media?.Url;
    }

    public void OpenExternally(Message message)
    {
        if (message.Media?.Path is { } p) Shell.Open(p);
        else if (message.Media?.Url is { } u && u.StartsWith("http") && !Can("media_download")) Shell.Open(u);
        else Fire(async () =>
        {
            var r = await Call("media.download", new JsonObject { ["chatId"] = message.ChatId, ["messageId"] = message.Id });
            if (r.Str("path") is { } path) Shell.Open(path);
        });
    }

    // MARK: Events

    /// Feeds a synthetic bridge event (demo mode only).
    public void InjectDemoEvent(string name, JsonNode? data)
    {
        if (!DemoData.Enabled) return;
        HandleEvent(name, data);
    }

    private void HandleEvent(string name, JsonNode? data)
    {
        switch (name)
        {
            case "auth.state":
                ApplyAuth(data);
                break;
            case "auth.step":
                AuthStep = data.TryDecode<AuthStep>();
                if (AuthStep?.Error is { Length: > 0 } err) AuthError = err;
                AuthState = AuthState.AwaitingInput;
                break;
            case "auth.qr":
                QrPayload = data.Str("qr");
                if (data.Str("image") is { } img && ImageLoader.DecodeDataUrl(img) is { } image) QrImage = image;
                else if (QrPayload is { } payload) QrImage = QRCode.Image(payload);
                QrExpiresAt = data.Num("expiresAt");
                QrRefreshing = false;
                _qrWanted = true;
                AuthState = AuthState.AwaitingQr;
                ArmQRWatchdog();
                break;
            case "folders.update":
                if (data.Get("folders").TryDecode<List<ChatFolder>>() is { } f) ApplyFolders(f);
                break;
            case "connection":
                Connection = data.Str("state") ?? Connection;
                ConnectionDetail = data.Str("detail");
                break;
            case "chat.upsert":
                if (data.Get("chat").TryDecode<Chat>() is { } c) UpsertChat(c);
                break;
            case "message.new":
                if (data.Get("message").TryDecode<Message>() is { } m)
                {
                    Upsert(m);
                    ClearTyping(m.ChatId, m.SenderId);
                    if (m.FromMe || data.Bool("live") != true || !FirstSighting(m)) return;
                    var chat = Chats.FirstOrDefault(x => x.Id == m.ChatId);
                    if (IsViewing(m.ChatId)) MarkRead(m.ChatId);
                    else if (chat != null && !chat.IsMuted)
                    {
                        Notifier.Shared.Notify(Platform, m, chat);
                        ChatHeads.Shared.Incoming(Platform, chat, m);
                    }
                    else if (chat == null) Notifier.Shared.Notify(Platform, m, null);
                }
                break;
            case "message.update":
                if (data.Get("message").TryDecode<Message>() is { } um) Upsert(um, isUpdate: true);
                break;
            case "message.delete":
                if (data.Str("chatId") is { } dc && data.Str("messageId") is { } did) UpdateMessage(dc, did, x => x with { Deleted = true });
                break;
            case "typing":
            {
                if (data.Str("chatId") is not { } tc || data.Str("userId") is not { } u) return;
                if (data.Bool("typing") == true)
                {
                    var list = Typing.TryGetValue(tc, out var l) ? l : [];
                    if (!list.Any(t => t.UserId == u)) list = list.Add(new TypingInfo(u, data.Str("name")));
                    Typing = Typing.SetItem(tc, list);
                    if (_typingExpiry.TryGetValue(tc + u, out var old)) old.Cancel();
                    var cts = new CancellationTokenSource();
                    _typingExpiry[tc + u] = cts;
                    Fire(async () =>
                    {
                        await Task.Delay(6000, cts.Token);
                        if (!cts.IsCancellationRequested) ClearTyping(tc, u);
                    });
                }
                else ClearTyping(tc, u);
                break;
            }
            case "history.phoneSync":
                if (data.TryDecode<PhoneSyncProgress>() is { } ps) ApplyPhoneSync(ps);
                break;
            case "presence":
                if (data.Str("chatId") is { } pc)
                {
                    var online = data.Bool("online");
                    var ls = data.Num("lastSeen");
                    UpdateChat(pc, x => x with { Online = online, LastSeen = ls ?? x.LastSeen });
                }
                break;
        }
    }

    private void ClearTyping(string chatId, string userId)
    {
        if (!Typing.TryGetValue(chatId, out var list) || !list.Any(t => t.UserId == userId)) return;
        Typing = Typing.SetItem(chatId, list.RemoveAll(t => t.UserId == userId));
    }

    private void UpsertChat(Chat c)
    {
        var i = Chats.FindIndex(x => x.Id == c.Id);
        if (i >= 0)
        {
            var old = Chats[i];
            c = c with
            {
                Avatar = c.Avatar ?? old.Avatar,
                LastMessage = c.LastMessage ?? old.LastMessage,
                // Read receipts and metadata updates must not move a chat down the list.
                Timestamp = Math.Max(c.Timestamp, old.Timestamp),
            };
            if (c.HasUnread && IsViewing(c.Id))
            {
                Chats = Chats.SetItem(i, c);
                MarkRead(c.Id);
                Chats = Sorted(Chats);
                return;
            }
            if (old.HasUnread && !c.HasUnread) Notifier.Shared.Clear(Platform, c.Id);
            Chats = Sorted(Chats.SetItem(i, c));
        }
        else Chats = Sorted(Chats.Add(c));
    }

    private readonly List<string> _seenLive = new();

    /// Bridges may repeat `message.new` for the same id; only the first live sighting alerts.
    private bool FirstSighting(Message m)
    {
        var key = m.ChatId + "|" + m.Id;
        if (_seenLive.Contains(key)) return false;
        _seenLive.Add(key);
        if (_seenLive.Count > 400) _seenLive.RemoveRange(0, 100);
        return true;
    }

    private void Upsert(Message m, bool isUpdate = false)
    {
        var had = Messages.TryGetValue(m.ChatId, out var existing);
        var list = existing ?? [];
        var i = list.FindIndex(x => x.Id == m.Id);
        if (i >= 0)
        {
            var merged = m;
            if (merged.Media != null && merged.Media.Path == null) merged = merged with { Media = merged.Media with { Path = list[i].Media?.Path } };
            list = list.SetItem(i, merged);
        }
        else if (!isUpdate || had)
        {
            if (m.FromMe)
            {
                var pi = list.FindIndex(x => x.Status == DeliveryStatus.Pending && x.Text == m.Text);
                if (pi >= 0) list = list.RemoveAt(pi);
            }
            list = list.Add(m).Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        if (had || m.ChatId == SelectedChatId) Messages = Messages.SetItem(m.ChatId, list);

        if (isUpdate) return;
        var ci = Chats.FindIndex(c => c.Id == m.ChatId);
        if (ci >= 0)
        {
            var c = Chats[ci];
            if ((c.LastMessage?.Timestamp ?? 0) <= m.Timestamp)
                Chats = Chats.SetItem(ci, c with { LastMessage = m, Timestamp = Math.Max(c.Timestamp, m.Timestamp) });
            Chats = Sorted(Chats);
        }
        else Fire(RefreshChats);
    }

    private static ImmutableList<Message> Merge(IEnumerable<Message> a, IEnumerable<Message> b)
    {
        var byId = new Dictionary<string, Message>();
        foreach (var m in a) byId[m.Id] = m;
        foreach (var m0 in b)
        {
            var m = m0;
            if (m.Media != null && m.Media.Path == null && byId.TryGetValue(m.Id, out var old))
                m = m with { Media = m.Media with { Path = old.Media?.Path } };
            byId[m.Id] = m;
        }
        return byId.Values.OrderBy(m => m.Timestamp).ToImmutableList();
    }

    private static ImmutableList<Chat> Sorted(IEnumerable<Chat> list) =>
        list.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.Timestamp).ToImmutableList();
}

/// What the composer held when an edit began, put back when it ends.
public sealed record ComposerStash(string Draft, Message? Reply, ImmutableList<MentionPick>? Picks, ImmutableList<string>? Files);
