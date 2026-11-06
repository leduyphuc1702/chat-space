import { createHash, randomUUID } from "node:crypto";
import { createWriteStream, existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { basename, extname, join } from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import {
  AvatarSize,
  BoardType,
  CloseReason,
  FriendEventType,
  GroupEventType,
  GroupTopicType,
  LoginQRCallbackEventType,
  MuteAction,
  Reactions,
  ThreadType,
  Zalo,
  type API,
  type Credentials,
  type DeliveredMessage,
  type FriendEvent as ZFriendEvent,
  type GroupEvent as ZGroupEvent,
  type Message as ZMessage,
  type Reaction as ZReaction,
  type SeenMessage,
  type SendMessageQuote,
  type SendSeenEventMessageParams,
  type Typing as ZTyping,
  type Undo as ZUndo,
} from "zca-js";
import { Adapter, type AdapterContext } from "./adapter.ts";
import * as mediaCache from "./media-cache.ts";
import { searchLocal, type SearchParams } from "./search.ts";
import { sanitizeMentions, TtlCache } from "./mentions.ts";
import {
  BridgeError,
  isFresh,
  type Chat,
  type Member,
  type MentionSpan,
  type Media,
  type Message,
  type StorageConfig,
} from "./types.ts";
import {
  ALBUM_MAX_SPREAD_MS,
  albumOf,
  albumTimestamps,
  chatIdOf,
  computeReactions,
  emojiToIcon,
  fixMentionText,
  kindFromFile,
  mapContent,
  mimeFromName,
  parseChatId,
  parsePinTopics,
  pinToMessage,
  quoteToReply,
  readImageMeta,
  recallEvent,
  str,
  toMs,
  type PinTopic,
} from "./zalo-map.ts";
import { MAX_PER_CHAT, ZaloStore, type Entry, type ZMeta } from "./zalo-store.ts";
import { DEFAULT_ASAR as ZALO_ASAR, readWasmFromAsar } from "./zalo-sync2/asar.ts";
import { PhoneSyncClient, SyncError, type SyncPhase, type SyncRows } from "./zalo-sync2/client.ts";
import { syncToZaloMessage, type SyncMessage } from "./zalo-sync2/convert.ts";
import { partitionOf } from "./zalo-sync2/sync2.ts";
import { decodeBody, frameHeader, frameJson } from "./zalo-sync2/ws.ts";
import { Zproto } from "./zalo-sync2/zproto.ts";

/** Same default UA zca-js uses; Zalo ties the imei to it, so we persist it with the cookies. */
const DEFAULT_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";
const QR_LIFETIME_MS = 100_000; // zca-js fires QRCodeExpired 100 s after QRCodeGenerated (hard-coded in loginQR)
/** Auto-refreshes (≈100 s each, ≈17 min in total) before giving up with auth.state logged_out / qr_expired. */
const MAX_QR_REFRESH = 10;
/** Newest messages fetched per group by the post-login warm-up, and the pause between groups. */
const WARM_GROUP_COUNT = 15;
const WARM_GROUP_DELAY_MS = 700;
/** After an expiry the library must hand out a new QR within this long, else the flow is declared dead. */
const QR_REGEN_TIMEOUT_MS = 30_000;
/** Protocol error string the app reacts to by calling auth.login again while the QR screen is open. */
const QR_EXPIRED = "qr_expired";
const DIR_STALE_MS = 10 * 60_000;
/** zca-js has no push event for mute / "mark as unread" changes made on the phone: poll them. */
const NOTIFY_POLL_MS = 5 * 60_000;
/** Most messages one on-demand getGroupChatHistory asks for (it has no paging cursor; server cap not verified). */
const OLDER_HISTORY_MAX = 500;
/** msgTypes whose stored kind / text / media are derived again from the raw content on start (see remapStored). */
const REMAP_TYPES = new Set(["chat.recommended", "chat.link", "chat.list.action", "chat.webcontent", "chat.webcontent.v2", "chat.delete"]);
/** getMute `muteMode` of an entry that was switched off again (zca-js MuteAction.UNMUTE). */
const MUTE_MODE_OFF = 3;
/** A mute set through chats.mute is not overwritten by a getMute poll that started within this long before it (or after). */
const MUTE_SETTLE_MS = 15_000;
/** chats.members answers are reused this long per chat (typing "@" repeatedly must not hit Zalo each time). */
const MEMBERS_TTL_MS = 5 * 60_000;
/** chats.pins answers are reused this long per chat; pin events drop them sooner. */
const PINS_TTL_MS = 5 * 60_000;
/** Board items asked per chats.pins (getListBoard returns notes and polls too, in one list). */
const PINS_BOARD_COUNT = 50;
/** getGroupMembersInfo is asked for this many unknown member names per request. */
const MEMBER_INFO_CHUNK = 50;
/** Group events that add, remove or reorder pinned messages. */
const PIN_EVENTS = new Set<string>([
  GroupEventType.NEW_PIN_TOPIC,
  GroupEventType.UPDATE_PIN_TOPIC,
  GroupEventType.UNPIN_TOPIC,
  GroupEventType.REORDER_PIN_TOPIC,
]);
/** Board events for any kind of topic; they touch the pins only when the topic is a message. */
const BOARD_EVENTS = new Set<string>([
  GroupEventType.UPDATE_BOARD,
  GroupEventType.REMOVE_BOARD,
  GroupEventType.UPDATE_TOPIC,
  GroupEventType.REMOVE_TOPIC,
]);
/** Phone sync: conversations per page of the conversation list (Zalo PC's conversations_batch_size). */
const SYNC_CONV_PAGE = 1000;
/** Phone sync: conversations per message request (Zalo PC never puts more than 30 queries in one). */
const SYNC_QUERIES_PER_REQUEST = 30;
/** Phone sync: tries per message request before the run fails (the same key needs no new phone prompt). */
const SYNC_ATTEMPTS = 3;
/** Phone sync: global ids resolved per /api/gid/decrypt call. */
const SYNC_GID_CHUNK = 50;

interface PhoneSyncProgress {
  /** paused: Zalo went to the background on the phone, which stops sending until it is opened again */
  state: "idle" | "preparing" | "waiting" | "paused" | "chats" | "messages" | "done" | "failed" | "cancelled";
  /** conversations the phone listed / whose messages are in */
  chats?: number;
  chatsDone?: number;
  /** messages added to the store */
  messages?: number;
  /** conversations dropped because Zalo would not say who/which group they are */
  skipped?: number;
  /** SyncError code on failure: rejected | busy | timeout | disconnected | server | decode | unavailable */
  code?: string;
  error?: string;
  finishedAt?: number;
}

type ConnState = "connecting" | "online" | "offline";
type Status = NonNullable<Message["status"]>;
const STATUS_RANK: Record<Status, number> = { failed: 0, pending: 1, sent: 2, delivered: 3, read: 4 };

interface StoredCredentials extends Credentials {
  uid?: string;
  savedAt?: number;
}

interface QrFlow {
  cancelled: boolean;
  done: boolean;
  gotQr: boolean;
  reason?: string;
  abort?: () => unknown;
  /** armed while we wait for the replacement QR after an expiry */
  regen?: ReturnType<typeof setTimeout>;
  firstQr: Promise<void>;
  resolveFirst: () => void;
}

interface IngestOpts {
  /** message comes from the listener's old-message replay or from a history call */
  replay: boolean;
  /** store it but emit nothing and leave unread alone */
  silent?: boolean;
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

function withTimeout<T>(p: Promise<T>, ms: number, what: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`${what} (timeout ${ms}ms)`)), ms);
    p.then(
      (v) => {
        clearTimeout(t);
        resolve(v);
      },
      (e) => {
        clearTimeout(t);
        reject(e);
      },
    );
  });
}

const errMsg = (e: unknown) => String((e as any)?.message ?? e);

function isNetworkError(e: unknown): boolean {
  const code = String((e as any)?.code ?? "");
  if (/^(ECONN|ENOTFOUND|ETIMEDOUT|EAI_AGAIN|EPIPE|ENETUNREACH|EHOSTUNREACH|UND_ERR)/.test(code)) return true;
  if (e instanceof TypeError) return true; // fetch() network failures
  return /fetch failed|network|timeout|timed out|socket|ENOTFOUND|ECONN|Unable to connect|Bad Gateway|Service Unavailable|Gateway Time|Too Many Requests/i.test(
    errMsg(e),
  );
}

/** Deep-copy through JSON so BigNumber ids from zca-js become plain strings. */
function plain<T>(x: T): T | undefined {
  if (x === undefined) return undefined;
  try {
    return JSON.parse(JSON.stringify(x));
  } catch {
    return undefined;
  }
}

const nonEmpty = (s: unknown): string | undefined => (typeof s === "string" && s.trim() ? s : undefined);

/** Answer of profile/api/gid/decrypt as global id -> uid; the exact shape is not documented, so accept the plausible ones. */
function gidMap(res: unknown, wanted: Set<string>): Record<string, string> {
  const out: Record<string, string> = {};
  const uidOf = (x: any) => str(x?.uid ?? x?.userId ?? x?.id);
  const visit = (v: unknown, depth: number) => {
    if (!v || typeof v !== "object" || depth > 3) return;
    if (Array.isArray(v)) {
      for (const x of v) {
        const g = str((x as any)?.globalUid ?? (x as any)?.globalId ?? (x as any)?.gid);
        if (g && wanted.has(g) && uidOf(x)) out[g] = uidOf(x);
        else visit(x, depth + 1);
      }
      return;
    }
    for (const [k, x] of Object.entries(v)) {
      if (wanted.has(k) && (typeof x === "string" || typeof x === "number")) out[k] = String(x);
      else if (wanted.has(k) && x && typeof x === "object" && uidOf(x)) out[k] = uidOf(x);
      else visit(x, depth + 1);
    }
  };
  visit(res, 0);
  return out;
}

export class ZaloAdapter extends Adapter {
  readonly platform = "zalo";
  readonly capabilities = [
    "reactions",
    "reply",
    "delete",
    "typing",
    "read_receipts",
    "media_send",
    "mentions",
    "search",
    "mute",
    "pins",
    "direct_chat",
    // the transfer is decrypted with Zalo's own library, read from the installed Zalo PC
    ...(existsSync(ZALO_ASAR) ? ["phone_sync"] : []),
  ];

  private store: ZaloStore;
  private membersCache = new TtlCache<Member[]>(MEMBERS_TTL_MS);
  private pinsCache = new TtlCache<PinTopic[]>(PINS_TTL_MS);
  /** chatId -> bumped by every pin event, so a fetch that raced one can be repeated */
  private pinsVer = new Map<string, number>();
  /** chatId -> when chats.mute last changed it (see MUTE_SETTLE_MS) */
  private muteWrites = new Map<string, number>();
  private api?: API;
  private creds?: StoredCredentials;
  private selfId = "";

  /** Local storage policy; the app pushes the real one right after start (storage.configure). */
  private storage: StorageConfig = {
    keepMediaDays: 0,
    maxCacheMB: 0,
    compressImages: false,
    maxStoredMessagesPerChat: MAX_PER_CHAT,
  };
  /** media.download calls in progress, keyed by chat|message (concurrent callers share one download) */
  private downloads = new Map<string, Promise<string>>();
  /** groups whose older history is used up (the server returned nothing older than what we hold) */
  private historyDone = new Set<string>();
  /** a friend list (persisted or fresh) is available, so a missing friend flag really means "not a contact" */
  private friendsKnown = false;

  // lifecycle
  private stopping = false;
  /** bumped by logout/new login/shutdown; async continuations compare against it */
  private epoch = 0;
  /** bumped for each listener attachment; stale listener callbacks compare against it */
  private gen = 0;
  private qrFlow?: QrFlow;

  // connection / backoff
  private connState?: ConnState;
  private lastConnKey = "";
  private connectedAt = 0;
  private failCount = 0;
  private dupCount = 0;
  private authFails = 0;
  private reconnectTimer?: ReturnType<typeof setTimeout>;
  private connectWatch?: ReturnType<typeof setTimeout>;
  private stableTimer?: ReturnType<typeof setTimeout>;
  private heartbeat?: ReturnType<typeof setInterval>;
  private credsTimer?: ReturnType<typeof setInterval>;

  // processing
  private queue: Promise<void> = Promise.resolve();
  private upsertTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private typingTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private typingSentAt = new Map<string, number>();
  private stickerCache = new Map<number, Media | undefined>();
  private histFetched = new Set<string>();
  /** Groups the background warm-up already asked for (see warmGroups). */
  private warmed = new Set<string>();
  private warming = false;
  /** Zalo answered 404 for group history: the endpoint is gone for this account, stop asking. */
  private groupHistoryGone = false;
  /** Chats the app already knows about (sent in a chat.upsert or chats.list this process). */
  private announced = new Set<string>();
  private notifyTimer?: ReturnType<typeof setInterval>;

  // phone sync (Zalo "sync v2": the phone uploads history for this session)
  private cipherKey?: string;
  private rawWs?: { readyState: number; send(d: Uint8Array): void; off?(e: string, f: (...a: any[]) => void): void };
  private rawHandler?: (d: unknown) => void;
  /** Kept for the life of the process: its identity key is what the user approved on the phone. */
  private syncClient?: PhoneSyncClient;
  private syncRun?: { cancelled: boolean };
  private syncProgress: PhoneSyncProgress = { state: "idle" };

  // directory
  private dirPromise?: Promise<void>;
  private dirAt = 0;
  private dirTimer?: ReturnType<typeof setTimeout>;

  constructor(ctx: AdapterContext) {
    super(ctx);
    this.store = new ZaloStore(ctx.dataDir, (...a) => this.log(...a));
  }

  private log(...a: unknown[]) {
    this.ctx.log("[zalo]", ...a);
  }

  private get mediaDir() {
    return join(this.ctx.dataDir, "media");
  }
  private get credsPath() {
    return join(this.ctx.dataDir, "credentials.json");
  }

  // ===================================================================== lifecycle

  async start(_config: Record<string, unknown>): Promise<void> {
    this.store.load();
    this.remapStored();
    this.friendsKnown = Object.values(this.store.contacts).some((c) => c.fr);
    this.selfId = this.store.selfId ?? "";
    const creds = this.readCredentials();
    if (!creds) {
      this.setState("logged_out");
      return;
    }
    this.creds = creds;
    this.setState("connecting");
    await this.connectWithCreds("restore");
  }

  async login(params: { method?: string; cookies?: string; phone?: string }): Promise<void> {
    const method = params.method ?? "qr";
    if (method === "phone") {
      throw new BridgeError("unsupported", "Zalo web login supports QR code only");
    }
    if (method === "reconnect" || (method === "cookies" && !params.cookies)) {
      const creds = this.creds ?? this.readCredentials();
      if (!creds) throw new BridgeError("not_logged_in", "No saved Zalo session; log in with a QR code");
      this.creds = creds;
      this.cancelReconnect();
      this.failCount = 0;
      this.dupCount = 0;
      const mode = this.state === "logged_in" && this.api ? "reconnect" : "restore";
      if (mode === "restore") this.setState("connecting");
      void this.connectWithCreds(mode);
      return;
    }
    if (method === "cookies") {
      await this.loginWithCookies(params.cookies!);
      return;
    }
    if (method !== "qr") throw new BridgeError("invalid_params", `unknown login method "${method}"`);
    if (this.state === "logged_in") return;
    await this.startQr();
  }

  async logout(): Promise<void> {
    this.epoch++;
    this.abortPhoneSync();
    this.syncClient = undefined;
    this.syncProgress = { state: "idle" };
    this.cancelQr();
    this.teardown();
    this.api = undefined;
    this.creds = undefined;
    rmSync(this.credsPath, { force: true });
    this.store.reset();
    this.stickerCache.clear();
    this.membersCache.clear();
    this.pinsCache.clear();
    this.pinsVer.clear();
    this.muteWrites.clear();
    this.histFetched.clear();
    this.historyDone.clear();
    this.friendsKnown = false;
    this.announced.clear();
    this.dirPromise = undefined;
    this.dirAt = 0;
    this.selfId = "";
    this.self = undefined;
    this.emitConn("offline", "Logged out");
    this.setState("logged_out");
  }

  async shutdown(): Promise<void> {
    this.stopping = true;
    this.epoch++;
    this.abortPhoneSync();
    this.cancelQr();
    this.teardown();
    if (this.api) this.saveCredentials();
    this.store.flush();
  }

  // ===================================================================== credentials

  private readCredentials(): StoredCredentials | undefined {
    try {
      const raw = JSON.parse(readFileSync(this.credsPath, "utf8"));
      if (raw && raw.imei && raw.userAgent && raw.cookie) return raw as StoredCredentials;
    } catch {}
    return undefined;
  }

  private saveCredentials() {
    const api = this.api;
    if (!api) return;
    try {
      const c = api.getContext();
      const cookie = api.getCookie().toJSON()?.cookies;
      if (!cookie?.length) return;
      const creds: StoredCredentials = {
        imei: c.imei,
        userAgent: c.userAgent,
        language: c.language,
        cookie: cookie as StoredCredentials["cookie"],
        uid: c.uid,
        savedAt: Date.now(),
      };
      this.creds = creds;
      const tmp = this.credsPath + ".tmp";
      writeFileSync(tmp, JSON.stringify(creds), { mode: 0o600 });
      renameSync(tmp, this.credsPath);
    } catch (e) {
      this.log("could not save credentials", e);
    }
  }

  private newZalo() {
    return new Zalo({
      selfListen: true, // we want our own messages sent from other devices
      checkUpdate: false,
      logging: false, // zca-js logs via console.log; keep it silent
      imageMetadataGetter: readImageMeta,
    });
  }

  // ===================================================================== login flows

  private async loginWithCookies(raw: string) {
    let parsed: any;
    try {
      parsed = JSON.parse(raw);
    } catch {
      throw new BridgeError("invalid_params", "cookies must be JSON: {cookie, imei, userAgent} or a cookie array");
    }
    const userAgent: string = parsed?.userAgent ?? DEFAULT_UA;
    const creds: StoredCredentials = Array.isArray(parsed)
      ? { cookie: parsed, imei: randomUUID() + "-" + createHash("md5").update(userAgent).digest("hex"), userAgent }
      : { ...parsed, userAgent, imei: parsed?.imei ?? randomUUID() + "-" + createHash("md5").update(userAgent).digest("hex") };
    if (!creds.cookie) throw new BridgeError("invalid_params", "no cookies found");
    this.cancelQr();
    this.teardown();
    const epoch = ++this.epoch;
    this.creds = creds;
    this.setState("connecting");
    try {
      const api = await withTimeout(this.newZalo().login(creds), 40_000, "login");
      if (epoch !== this.epoch) return;
      await this.onLoggedIn(api);
    } catch (e) {
      if (epoch !== this.epoch) return;
      this.creds = undefined;
      this.setState("logged_out", { error: `Đăng nhập bằng cookie thất bại: ${errMsg(e)}` });
    }
  }

  /** Stop the running QR flow (new auth.login, logout, shutdown). Its late library callbacks abort themselves. */
  private cancelQr() {
    const f = this.qrFlow;
    if (!f) return;
    f.cancelled = true;
    clearTimeout(f.regen);
    f.regen = undefined;
    this.qrFlow = undefined;
    f.resolveFirst();
    try {
      f.abort?.();
    } catch {}
  }

  private async startQr() {
    this.cancelQr();
    this.teardown();
    const epoch = ++this.epoch;
    let resolveFirst!: () => void;
    const flow: QrFlow = {
      cancelled: false,
      done: false,
      gotQr: false,
      firstQr: new Promise<void>((r) => (resolveFirst = r)),
      resolveFirst: () => resolveFirst(),
    };
    this.qrFlow = flow;
    this.setState("connecting");
    void this.runQr(flow, epoch);
    // Report back once the first QR is out (or after 20 s), so `auth.login` can return awaiting_qr.
    await Promise.race([flow.firstQr, sleep(20_000)]);
  }

  private async runQr(flow: QrFlow, epoch: number) {
    let loginInfo: StoredCredentials | undefined;
    let refreshes = 0;

    /**
     * End the flow with auth.state logged_out. The flow is marked cancelled so that whatever the library still does
     * later (a stuck request finally returning, the rejection caused by our abort) is ignored and aborted.
     */
    const fail = (msg: string, stopLib = true) => {
      if (flow.done || flow.cancelled || epoch !== this.epoch) return;
      flow.done = true;
      flow.cancelled = true;
      if (this.qrFlow === flow) this.qrFlow = undefined;
      clearTimeout(watchdog);
      clearTimeout(flow.regen);
      flow.regen = undefined;
      if (stopLib) {
        try {
          flow.abort?.();
        } catch {}
      }
      flow.resolveFirst();
      this.setState("logged_out", { error: msg });
    };
    // loginQR has no network timeout of its own.
    const watchdog = setTimeout(() => {
      if (!flow.gotQr) fail("Không tạo được mã QR (hết thời gian chờ). Kiểm tra kết nối mạng.");
    }, 45_000);

    try {
      const api = await this.newZalo().loginQR({ userAgent: DEFAULT_UA, language: "vi" }, (ev) => {
        if (flow.cancelled || epoch !== this.epoch) {
          // Stale flow: make sure the library stops polling Zalo.
          if (ev.actions && "abort" in ev.actions) ev.actions.abort();
          return;
        }
        switch (ev.type) {
          case LoginQRCallbackEventType.QRCodeGenerated: {
            flow.abort = ev.actions.abort;
            flow.gotQr = true;
            clearTimeout(flow.regen); // the replacement arrived in time
            flow.regen = undefined;
            const raw = ev.data.image;
            const image = raw.startsWith("data:") ? raw : `data:image/png;base64,${raw}`;
            if (this.state !== "awaiting_qr") this.setState("awaiting_qr");
            // The QR image encodes http://zaloapp.com/qr/l?tk=<token> (verified by decoding the PNG);
            // `code` is only the polling id, so it is a last-resort fallback.
            const payload = ev.data.token ? `http://zaloapp.com/qr/l?tk=${ev.data.token}` : ev.data.code || image;
            // The library's 100 s expiry timer starts right after this callback returns.
            this.ctx.emit("auth.qr", {
              qr: payload,
              image,
              expiresAt: Date.now() + QR_LIFETIME_MS,
            });
            flow.resolveFirst();
            break;
          }
          case LoginQRCallbackEventType.QRCodeScanned:
            flow.abort = ev.actions.abort;
            this.ctx.emit("log", {
              level: "info",
              message: `Đã quét mã QR (${ev.data.display_name}). Hãy xác nhận đăng nhập trên điện thoại.`,
            });
            break;
          case LoginQRCallbackEventType.QRCodeExpired:
            flow.abort = ev.actions.abort;
            if (++refreshes > MAX_QR_REFRESH) {
              // Nobody scanned the last codes: give up, the app restarts the flow if its QR screen is still open.
              flow.reason = QR_EXPIRED;
              this.log(`QR expired ${refreshes - 1} times, giving up`);
              ev.actions.abort();
            } else {
              this.log(`QR expired, requesting a new one (${refreshes}/${MAX_QR_REFRESH})`);
              // retry() regenerates and fires QRCodeGenerated again. Its requests have no timeout, so if no new
              // code shows up soon the flow must not sit in awaiting_qr showing a dead one.
              clearTimeout(flow.regen);
              flow.regen = setTimeout(() => {
                flow.regen = undefined;
                this.log("no new QR after expiry, giving up");
                fail(QR_EXPIRED);
              }, QR_REGEN_TIMEOUT_MS);
              ev.actions.retry();
            }
            break;
          case LoginQRCallbackEventType.QRCodeDeclined:
            flow.reason = "Đăng nhập bị từ chối trên điện thoại.";
            ev.actions.abort();
            break;
          case LoginQRCallbackEventType.GotLoginInfo:
            loginInfo = { cookie: ev.data.cookie, imei: ev.data.imei, userAgent: ev.data.userAgent, language: "vi" };
            break;
        }
      });
      clearTimeout(watchdog);
      clearTimeout(flow.regen);
      if (flow.cancelled || epoch !== this.epoch) return;
      flow.done = true;
      this.qrFlow = undefined;
      if (loginInfo) this.creds = loginInfo;
      await this.onLoggedIn(api);
    } catch (e) {
      clearTimeout(watchdog);
      if (flow.cancelled || epoch !== this.epoch) return;
      this.log("QR login failed", errMsg(e));
      fail(flow.reason ?? `Đăng nhập QR thất bại: ${errMsg(e)}`, false); // the library has already stopped
    }
  }

  // ===================================================================== session

  /** Common tail of every successful login (QR, cookies, restore). */
  private async onLoggedIn(api: API) {
    const uid = api.getOwnId();
    if (this.store.selfId && this.store.selfId !== uid) {
      this.log("different account than the stored one; resetting local store");
      this.store.reset();
    }
    this.selfId = uid;
    this.store.selfId = uid;
    if (this.store.meta.activeTs === undefined) this.store.meta.activeTs = Date.now();

    let profile: any;
    try {
      profile = (await withTimeout(api.fetchAccountInfo(), 15_000, "fetchAccountInfo")).profile;
    } catch (e) {
      this.log("fetchAccountInfo failed", errMsg(e));
    }
    this.self = {
      id: uid,
      name: nonEmpty(profile?.displayName) ?? nonEmpty(profile?.zaloName) ?? this.self?.name ?? uid,
      avatar: nonEmpty(profile?.avatar),
      phone: nonEmpty(profile?.phoneNumber),
    };

    this.authFails = 0;
    this.attach(api);
    this.saveCredentials();
    this.setState("logged_in");
    this.startDirectory();
    this.credsTimer ??= setInterval(() => this.saveCredentials(), 30 * 60_000);
  }

  /**
   * (Re)create the session from the saved credentials.
   *  restore   – we are not logged in yet (startup / explicit reconnect while logged out)
   *  reconnect – we are logged in and only the realtime listener was lost
   */
  private async connectWithCreds(mode: "restore" | "reconnect"): Promise<void> {
    const creds = this.creds ?? this.readCredentials();
    if (!creds) {
      if (mode === "restore") this.setState("logged_out");
      return;
    }
    this.creds = creds;
    const epoch = this.epoch;
    this.emitConn("connecting");
    let api: API;
    try {
      api = await withTimeout(this.newZalo().login(creds), 40_000, "Zalo login");
    } catch (e) {
      if (epoch !== this.epoch || this.stopping) return;
      this.onConnectFailure(mode, e);
      return;
    }
    if (epoch !== this.epoch || this.stopping) return;
    try {
      if (mode === "restore") await this.onLoggedIn(api);
      else {
        this.authFails = 0;
        this.attach(api);
      }
    } catch (e) {
      this.log("post-login setup failed", e);
      this.onConnectFailure(mode, e);
    }
  }

  private onConnectFailure(mode: "restore" | "reconnect", e: unknown) {
    const msg = errMsg(e);
    this.log(`${mode} failed:`, msg);
    if (isNetworkError(e)) {
      this.failCount++;
      this.emitConn("offline", "Không có kết nối mạng tới Zalo");
      if (mode === "restore" && this.state !== "error") {
        this.setState("error", { error: `Không kết nối được tới Zalo: ${msg}` });
      }
      this.scheduleReconnect("other", mode);
      return;
    }
    // Zalo answered but refused the session.
    this.authFails++;
    if (mode === "restore" || this.authFails >= 3) {
      this.teardown();
      this.api = undefined;
      this.emitConn("offline", "Phiên đăng nhập không còn hiệu lực");
      this.setState("logged_out", { error: `Không khôi phục được phiên đăng nhập Zalo: ${msg}` });
      return;
    }
    this.failCount++;
    this.scheduleReconnect("other", mode);
  }

  // ===================================================================== realtime listener

  private emitConn(state: ConnState, detail?: string) {
    this.connState = state;
    const key = `${state}|${detail ?? ""}`;
    if (key === this.lastConnKey) return;
    this.lastConnKey = key;
    this.ctx.emit("connection", detail ? { state, detail } : { state });
  }

  private detachListener(api: API | undefined) {
    if (!api) return;
    try {
      const l = api.listener;
      l.removeAllListeners();
      l.on("error", () => {}); // an 'error' event without a listener would crash the process
      l.stop();
    } catch {}
  }

  private attach(api: API) {
    if (this.api && this.api !== api) this.detachListener(this.api);
    this.api = api;
    this.clearConnTimers();
    const gen = ++this.gen;
    const live = () => gen === this.gen && !this.stopping;
    const l = api.listener;

    l.on("connected", () => {
      if (!live()) return;
      this.hookRawSocket(l, gen);
      this.connectedAt = Date.now();
      this.pinsCache.clear(); // pin events missed while offline
      if (this.connectWatch) clearTimeout(this.connectWatch);
      this.connectWatch = undefined;
      this.emitConn("online");
      this.stableTimer = setTimeout(() => {
        this.failCount = 0;
        this.dupCount = 0;
      }, 60_000);
      this.heartbeat ??= setInterval(() => {
        // Kept in memory only; it is flushed with the next real store write / on shutdown.
        if (this.connState === "online") this.store.meta.activeTs = Date.now();
      }, 30_000);
      this.notifyTimer ??= setInterval(() => {
        if (this.connState === "online") void this.refreshNotifyState();
      }, NOTIFY_POLL_MS);
      this.saveCredentials();
    });
    l.on("cipher_key", (key: string) => {
      if (!live()) return;
      this.cipherKey = key;
      void this.requestReplay(api, gen);
    });
    l.on("closed", (code: number, reason: string) => {
      if (!live()) return;
      this.dropRawSocket();
      this.onClosed(code, reason);
    });
    l.on("error", (e: unknown) => {
      if (live()) this.log("listener error", errMsg((e as any)?.error ?? e));
    });
    l.on("message", (m) => live() && this.enqueue(() => this.ingestZMessage(m, { replay: false })));
    l.on("old_messages", (msgs, type) => {
      if (!live()) return;
      this.enqueue(async () => {
        for (const m of msgs) await this.ingestZMessage(m, { replay: true });
        this.trackLastMsgId(msgs, type);
        this.log(`replay: ${msgs.length} ${type === ThreadType.Group ? "group" : "user"} messages`);
      });
    });
    l.on("reaction", (r) => live() && this.enqueue(() => this.applyReaction(r)));
    l.on("old_reactions", (rs) => {
      if (!live()) return;
      this.enqueue(() => {
        for (const r of rs) this.applyReaction(r);
      });
    });
    l.on("undo", (u) => live() && this.enqueue(() => this.applyUndo(u)));
    l.on("typing", (t) => live() && this.applyTyping(t));
    l.on("seen_messages", (s) => live() && this.enqueue(() => this.applySeen(s)));
    l.on("delivered_messages", (d) => live() && this.enqueue(() => this.applyDelivered(d)));
    l.on("group_event", (ev) => live() && this.enqueue(() => this.applyGroupEvent(ev)));
    l.on("friend_event", (ev) => live() && this.applyFriendEvent(ev));

    this.emitConn("connecting");
    this.connectWatch = setTimeout(() => {
      if (!live() || this.connState === "online") return;
      this.log("listener did not connect within 30s");
      this.gen++; // invalidate this attachment's callbacks
      this.detachListener(api);
      this.onClosed(1006, "connect timeout");
    }, 30_000);
    try {
      // We do our own backoff (retryOnClose=false) so a "session opened elsewhere" close can't spin.
      l.start({ retryOnClose: false });
    } catch (e) {
      this.gen++;
      this.onClosed(1006, errMsg(e));
    }
  }

  private onClosed(code: number, reason: string) {
    if (this.stopping) return;
    this.clearConnTimers();
    const uptime = this.connectedAt ? Date.now() - this.connectedAt : 0;
    this.connectedAt = 0;
    if (uptime > 60_000) {
      this.failCount = 0;
      this.dupCount = 0;
    }
    const dup = code === CloseReason.DuplicateConnection;
    const kicked = code === CloseReason.KickConnection;
    let detail: string;
    if (dup) detail = "Zalo session opened elsewhere";
    else if (kicked) detail = "Zalo session was closed by the server";
    else detail = `Connection lost (${code}${reason ? `: ${reason}` : ""})`;
    this.log("listener closed:", detail);
    this.emitConn("offline", detail);
    if (dup || kicked) this.dupCount++;
    else this.failCount++;
    this.scheduleReconnect(dup || kicked ? "dup" : "other", "reconnect");
  }

  private scheduleReconnect(kind: "dup" | "other", mode: "restore" | "reconnect") {
    this.cancelReconnect();
    const n = Math.max(1, kind === "dup" ? this.dupCount : this.failCount);
    // "Opened elsewhere": reconnecting kicks the other session in turn, so back off hard.
    const base = kind === "dup" ? 30_000 : 2_000;
    const cap = kind === "dup" ? 600_000 : 60_000;
    const delay = Math.round(Math.min(cap, base * 2 ** (n - 1)) * (0.9 + Math.random() * 0.2));
    this.log(`reconnecting in ${Math.round(delay / 1000)}s (${kind}, attempt ${n})`);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      if (this.stopping) return;
      void this.connectWithCreds(mode);
    }, delay);
  }

  private cancelReconnect() {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = undefined;
  }

  private clearConnTimers() {
    for (const t of [this.connectWatch, this.stableTimer]) if (t) clearTimeout(t);
    this.connectWatch = undefined;
    this.stableTimer = undefined;
  }

  /**
   * connection.resume: after sleep or a network change. Zalo's socket pings on an interval and the OS may only
   * report the dead connection minutes later; a socket that is not OPEN, or a reconnect already waiting out its
   * backoff, is dealt with now. The reconnect replays what was missed (requestOldMessages), as always.
   */
  async resume(): Promise<void> {
    if (this.stopping || this.state !== "logged_in") return;
    if (this.reconnectTimer) {
      this.log("resume: reconnecting now instead of after the backoff");
      this.cancelReconnect();
      this.failCount = 0;
      void this.connectWithCreds("reconnect");
      return;
    }
    if (this.connState !== "online") return; // a connection attempt is in progress
    const ws = (this.api?.listener as unknown as { ws?: { readyState?: number; ping?: (cb?: () => void) => void; terminate?: () => void; once?: (e: string, f: () => void) => void } } | undefined)?.ws;
    if (!ws) return;
    if (ws.readyState !== 1) {
      this.log("resume: socket is not open, reconnecting");
      this.failCount = 0;
      this.detachListener(this.api);
      this.onClosed(1006, "resume");
      return;
    }
    if (typeof ws.ping !== "function" || typeof ws.once !== "function") return;
    // A ping the server does not answer within a few seconds means the connection died while we slept.
    const pong = new Promise<boolean>((resolve) => {
      ws.once!("pong", () => resolve(true));
      setTimeout(() => resolve(false), 5000).unref?.();
    });
    try {
      ws.ping();
    } catch {
      return;
    }
    if (await pong) return;
    if (this.stopping || ws !== (this.api?.listener as unknown as { ws?: unknown } | undefined)?.ws) return;
    this.log("resume: no pong after 5s, reconnecting");
    this.failCount = 0;
    ws.terminate?.(); // the listener's onclose fires -> onClosed -> scheduleReconnect (first attempt: ~2 s)
  }

  /** Stop the listener and every timer; session data stays (see logout() for that). */
  private teardown() {
    this.gen++;
    this.dropRawSocket();
    this.cancelReconnect();
    this.clearConnTimers();
    if (this.heartbeat) clearInterval(this.heartbeat);
    if (this.credsTimer) clearInterval(this.credsTimer);
    if (this.notifyTimer) clearInterval(this.notifyTimer);
    this.heartbeat = undefined;
    this.credsTimer = undefined;
    this.notifyTimer = undefined;
    if (this.dirTimer) clearTimeout(this.dirTimer);
    this.dirTimer = undefined;
    for (const t of [...this.upsertTimers.values(), ...this.typingTimers.values()]) clearTimeout(t);
    this.upsertTimers.clear();
    this.typingTimers.clear();
    this.detachListener(this.api);
    this.connState = undefined;
    this.lastConnKey = "";
    this.connectedAt = 0;
  }

  /** Ask Zalo to replay recent messages/reactions (also catches up after a reconnect). */
  private async requestReplay(api: API, gen: number) {
    const l = api.listener;
    const run = async (fn: () => void) => {
      if (gen !== this.gen) return;
      try {
        fn();
      } catch (e) {
        this.log("replay request failed", errMsg(e));
      }
      await sleep(250);
    };
    await run(() => l.requestOldMessages(ThreadType.User, null));
    await run(() => l.requestOldMessages(ThreadType.Group, null));
    const last = this.store.meta.lastMsgId;
    if (last?.u) await run(() => l.requestOldMessages(ThreadType.User, last.u!));
    if (last?.g) await run(() => l.requestOldMessages(ThreadType.Group, last.g!));
    await run(() => l.requestOldReactions(ThreadType.User, null));
    await run(() => l.requestOldReactions(ThreadType.Group, null));
  }

  private trackLastMsgId(msgs: ZMessage[], type: ThreadType) {
    let best: bigint | undefined;
    for (const m of msgs) {
      try {
        const v = BigInt(str((m.data as any).msgId));
        if (best === undefined || v > best) best = v;
      } catch {}
    }
    if (best === undefined) return;
    const key = type === ThreadType.Group ? "g" : "u";
    const cur = this.store.meta.lastMsgId ?? (this.store.meta.lastMsgId = {});
    try {
      if (!cur[key] || BigInt(cur[key]!) < best) {
        cur[key] = best.toString();
        this.store.touch();
      }
    } catch {}
  }

  private enqueue(fn: () => Promise<void> | void) {
    this.queue = this.queue.then(fn).catch((e) => this.log("event handler failed", (e as any)?.stack ?? e));
  }

  // ===================================================================== phone sync (history from the phone)

  /**
   * Zalo's history transfer, as Zalo PC/Web do it: we ask the phone over the session's socket (cmd 590), the user
   * approves once on the phone, the phone uploads encrypted batches and announces them with cmd 601 controls.
   * zca-js ignores those controls, so we read the same socket ourselves.
   */
  private hookRawSocket(l: API["listener"], gen: number) {
    this.dropRawSocket();
    const ws = (l as any).ws;
    if (!ws || typeof ws.on !== "function") return;
    const handler = (d: unknown) => {
      if (gen !== this.gen || !this.syncClient?.active) return;
      void this.onRawFrame(d);
    };
    ws.on("message", handler);
    this.rawWs = ws;
    this.rawHandler = handler;
  }

  private dropRawSocket() {
    if (this.rawWs && this.rawHandler) this.rawWs.off?.("message", this.rawHandler);
    this.rawWs = undefined;
    this.rawHandler = undefined;
    this.cipherKey = undefined;
    this.syncClient?.abortAll(new SyncError("disconnected", "the Zalo connection dropped"));
  }

  private async onRawFrame(d: unknown) {
    const buf = Buffer.isBuffer(d) ? d : d instanceof ArrayBuffer ? Buffer.from(d) : undefined;
    const h = buf && frameHeader(buf);
    if (!h || (h.cmd !== 601 && h.cmd !== 590)) return;
    try {
      const body = await decodeBody(frameJson(buf), this.cipherKey);
      if (h.cmd === 590) {
        this.syncClient?.handleReply(body);
        return;
      }
      for (const c of body?.data?.controls ?? body?.controls ?? []) this.syncClient?.handleControl(c?.content);
    } catch (e) {
      this.log(`phone sync: frame ${h.cmd} not decodable`, errMsg(e));
    }
  }

  private sendRaw(frame: Uint8Array): boolean {
    const ws = this.rawWs;
    if (!ws || ws.readyState !== 1) return false;
    ws.send(frame);
    return true;
  }

  async phoneSync(action: "start" | "cancel" | "status"): Promise<PhoneSyncProgress> {
    if (action === "cancel") {
      this.abortPhoneSync();
      return this.syncProgress;
    }
    if (action === "status") return this.syncProgress;
    this.requireLogin();
    if (!this.capabilities.includes("phone_sync")) {
      throw new BridgeError("unsupported", "Zalo PC must be installed: its library decrypts the transfer");
    }
    if (!this.syncRun) void this.runPhoneSync();
    return this.syncProgress;
  }

  /** After a reconnect the raw socket is hooked again; wait for it (and a short pause) before retrying. */
  private async waitRawSocket(ms: number) {
    const until = Date.now() + ms;
    await sleep(3000);
    while ((!this.rawWs || !this.cipherKey) && Date.now() < until) await sleep(1000);
  }

  private abortPhoneSync() {
    if (!this.syncRun) return;
    this.syncRun.cancelled = true;
    this.syncClient?.abortAll(new SyncError("cancelled", "cancelled"));
  }

  private setSync(p: PhoneSyncProgress) {
    this.syncProgress = p;
    this.ctx.emit("history.phoneSync", p);
  }

  private async runPhoneSync() {
    const run = { cancelled: false };
    this.syncRun = run;
    const epoch = this.epoch;
    const check = () => {
      if (run.cancelled || epoch !== this.epoch) throw new SyncError("cancelled", "cancelled");
    };
    let progress: PhoneSyncProgress = { state: "preparing" };
    const update = (p: Partial<PhoneSyncProgress>) => this.setSync((progress = { ...progress, ...p }));
    update({});
    try {
      const api = this.api;
      if (!api || !this.rawWs || !this.cipherKey) throw new SyncError("disconnected", "Zalo is not connected");
      if (!this.syncClient) {
        let z: Zproto;
        try {
          z = await Zproto.load(readWasmFromAsar().bytes);
        } catch (e) {
          throw Object.assign(new SyncError("server", `cannot load Zalo PC's library: ${errMsg(e)}`), { code: "unavailable" });
        }
        this.syncClient = new PhoneSyncClient(z, { send: (f) => this.sendRaw(f), log: (...a) => this.log("phone sync:", ...a) });
      }
      const client = this.syncClient;
      let stage: "chats" | "messages" = "chats";
      const onPhase = (p: SyncPhase) => {
        const state = p === "confirmed" ? stage : p;
        if (state !== progress.state) {
          if (p !== "confirmed") this.log(`phone sync: ${p}`);
          update({ state });
        }
      };

      // 1. Conversation list, newest first, paged by lastTs.
      const convs = new Map<string, { convId: string; convType: number; lastTs: number }>();
      let to = Number.MAX_SAFE_INTEGER;
      for (let page = 0; page < 50; page++) {
        const rows = await client.request("conversation", [{ partition: "", from: 0, to, limit: SYNC_CONV_PAGE }], 0, onPhase);
        check();
        const list = rows.flatMap((r) => r.data) as any[];
        for (const c of list) if (c?.convId) convs.set(String(c.convId), { convId: String(c.convId), convType: Number(c.convType), lastTs: Number(c.lastTs) || 0 });
        const oldest = Math.min(...list.map((c) => Number(c.lastTs)).filter((n) => n > 0));
        if (list.length < SYNC_CONV_PAGE || !Number.isFinite(oldest)) break;
        to = oldest - 1;
      }
      this.log(`phone sync: ${convs.size} conversations`);

      // 2. Global ids -> Zalo ids (friend list and group info carry them; the rest are asked from Zalo).
      if (!Object.keys(this.store.meta.gids ?? {}).length) {
        this.dirPromise = this.refreshDirectory().catch((e) => this.log("directory refresh failed", errMsg(e)));
      }
      await this.waitDirectory(90_000);
      check();
      await this.resolveSelfGid(api);
      await this.resolveGids(
        api,
        [...convs.values()].filter((c) => c.convType !== 2).map((c) => c.convId),
      );
      check();
      const targets: Array<{ convId: string; isGroup: boolean; chatId: string }> = [];
      for (const c of [...convs.values()].sort((a, b) => b.lastTs - a.lastTs)) {
        const isGroup = c.convType === 2;
        const chatId = this.store.meta.gids?.[c.convId];
        if (chatId && chatId.startsWith(isGroup ? "g:" : "u:")) targets.push({ convId: c.convId, isGroup, chatId });
      }
      const skipped = convs.size - targets.length;
      if (skipped) this.log(`phone sync: ${skipped} conversations have no known id and are skipped`);
      await this.fillUnknownPeers(api, targets.filter((t) => !t.isGroup).map((t) => parseChatId(t.chatId).id));
      check();

      // 3. Messages, up to the per-chat storage limit, 30 conversations per request. Each decoded batch is
      // stored as it arrives; finished conversations are remembered so a failed or cancelled run resumes.
      stage = "messages";
      const max = this.storage.maxStoredMessagesPerChat;
      const limit = max > 0 ? max : 2_147_483_647;
      const done = new Set(this.store.meta.syncDone ?? []);
      const todo = targets.filter((t) => !done.has(t.convId));
      let chatsDone = targets.length - todo.length;
      let added = 0;
      if (chatsDone) this.log(`phone sync: resuming, ${chatsDone} conversations already done`);
      update({ state: "messages", chats: targets.length, chatsDone, messages: 0, skipped });
      for (let i = 0; i < todo.length; i += SYNC_QUERIES_PER_REQUEST) {
        const batch = todo.slice(i, i + SYNC_QUERIES_PER_REQUEST);
        const byPartition = new Map(batch.map((t) => [partitionOf(t.convId, t.isGroup), t]));
        const sink = async (rows: SyncRows[]) => {
          check();
          const senders = new Set<string>();
          let n = 0;
          for (const r of rows) {
            const t = byPartition.get(r.partition);
            n += r.data.length;
            for (const m of r.data) {
              if (!m?.senderId) continue;
              senders.add(String(m.senderId));
              // A 1:1 partition holds only the peer (its id is the conversation id) and us: learn our own global id.
              if (!this.selfGid && t && !t.isGroup && String(m.senderId) !== t.convId) this.selfGid = String(m.senderId);
            }
          }
          await this.resolveGids(api, [...senders]);
          check();
          for (const r of rows) {
            const t = byPartition.get(r.partition);
            if (!t || !r.data.length) continue;
            added += await this.importSynced(t.chatId, t.convId, t.isGroup, r.data as SyncMessage[]);
            this.scheduleUpsert(t.chatId);
          }
          this.log(`phone sync: batch of ${n} messages (${chatsDone}/${targets.length} conversations, ${added} added)`);
          update({ state: "messages", messages: added });
        };
        for (let attempt = 1; ; attempt++) {
          try {
            await client.request(
              "message",
              batch.map((t) => ({ partition: partitionOf(t.convId, t.isGroup), from: 0, to: Number.MAX_SAFE_INTEGER, limit })),
              2,
              onPhase,
              sink,
            );
            break;
          } catch (e) {
            check();
            const code = e instanceof SyncError ? e.code : "server";
            if (attempt >= SYNC_ATTEMPTS || !["timeout", "server", "decode", "disconnected"].includes(code)) throw e;
            this.log(`phone sync: request failed (${errMsg(e)}), retrying (${attempt}/${SYNC_ATTEMPTS - 1})`);
            await this.waitRawSocket(60_000);
            check();
          }
        }
        for (const t of batch) done.add(t.convId);
        this.store.meta.syncDone = [...done];
        this.store.touch();
        chatsDone += batch.length;
        update({ state: "messages", chatsDone, messages: added });
      }
      delete this.store.meta.syncDone;
      this.store.touch();
      this.log(`phone sync: done, ${added} messages added from ${targets.length} conversations`);
      update({ state: "done", messages: added, finishedAt: Date.now() });
    } catch (e) {
      const se = e instanceof SyncError ? e : undefined;
      if (se?.code === "cancelled") {
        update({ state: "cancelled" });
      } else {
        this.log("phone sync failed:", errMsg(e));
        update({ state: "failed", code: (e as any)?.code ?? "server", error: errMsg(e) });
      }
    } finally {
      if (this.syncRun === run) this.syncRun = undefined;
    }
  }

  private rememberGid(globalId: unknown, chatId: string) {
    const g = str(globalId);
    if (!g || g === "0") return;
    const map = (this.store.meta.gids ??= {});
    if (map[g] === chatId) return;
    map[g] = chatId;
    this.store.touch();
  }

  /** The user's own global id: their messages come back with it as senderId. */
  private selfGid?: string;
  private async resolveSelfGid(api: API) {
    if (this.selfGid || !this.selfId) return;
    try {
      const res = await withTimeout(api.getUserInfo(this.selfId), 20_000, "getUserInfo");
      const p: any = res.changed_profiles?.[this.selfId] ?? res.changed_profiles?.[`${this.selfId}_0`];
      if (p?.globalId) this.selfGid = str(p.globalId);
    } catch (e) {
      this.log("phone sync: own global id unavailable", errMsg(e));
    }
  }

  /** Asks Zalo which users the unknown global ids are (Zalo PC: GET profile/api/gid/decrypt). */
  private async resolveGids(api: API, gids: string[]) {
    const known = this.store.meta.gids ?? {};
    const todo = [...new Set(gids)].filter((g) => g && !known[g] && g !== this.selfGid);
    if (!todo.length) return;
    const a = api as any;
    if (typeof a.csGidDecrypt !== "function") {
      api.custom("csGidDecrypt", async ({ utils, props }: any) => {
        const params = utils.encodeAES(JSON.stringify({ globalUids: JSON.stringify(props.gids) }));
        const url = utils.makeURL(`${api.zpwServiceMap.profile[0]}/api/gid/decrypt`, { params });
        return utils.resolve(await utils.request(url, { method: "GET" }));
      });
    }
    for (let i = 0; i < todo.length; i += SYNC_GID_CHUNK) {
      const chunk = todo.slice(i, i + SYNC_GID_CHUNK);
      try {
        const res = await withTimeout<any>(a.csGidDecrypt({ gids: chunk }), 20_000, "gid/decrypt");
        const map = gidMap(res, new Set(chunk));
        let found = 0;
        for (const g of chunk) {
          const uid = map[g];
          if (uid && /^\d+$/.test(uid)) {
            this.rememberGid(g, `u:${uid}`);
            found++;
          }
        }
        if (found < chunk.length) {
          this.log(`phone sync: gid/decrypt resolved ${found}/${chunk.length} (answer keys: ${Object.keys(res ?? {}).slice(0, 5)})`);
        }
      } catch (e) {
        this.log("phone sync: gid/decrypt failed", errMsg(e));
        return;
      }
    }
  }

  /** Names and avatars of people we have never seen (non-friends), so their chats get a title. */
  private async fillUnknownPeers(api: API, uids: string[]) {
    const todo = uids.filter((u) => !this.store.contacts[u]);
    for (let i = 0; i < todo.length; i += MEMBER_INFO_CHUNK) {
      try {
        const res = await withTimeout(api.getUserInfo(todo.slice(i, i + MEMBER_INFO_CHUNK)), 20_000, "getUserInfo");
        for (const [key, p] of Object.entries<any>(res.changed_profiles ?? {})) {
          const uid = str(p?.userId) || key.split("_")[0]!;
          this.remember(uid, nonEmpty(p?.displayName) ?? nonEmpty(p?.zaloName), nonEmpty(p?.avatar));
        }
      } catch (e) {
        this.log("phone sync: getUserInfo failed", errMsg(e));
      }
    }
  }

  /** Stores one conversation's synced messages silently (they are history); returns how many were new. */
  private async importSynced(chatId: string, convId: string, isGroup: boolean, msgs: SyncMessage[]): Promise<number> {
    const threadId = parseChatId(chatId).id;
    const gids = this.store.meta.gids ?? {};
    const have = new Set(this.store.list(chatId).map((e) => e.z.cliMsgId));
    let added = 0;
    for (const sm of msgs) {
      const sender = str(sm.senderId);
      // In a 1:1 partition every message is from the peer (whose global id is the conversation id) or from us.
      const peer = gids[sender];
      const fromMe = isGroup ? (!!this.selfGid && sender === this.selfGid) || peer === `u:${this.selfId}` : sender !== convId;
      const senderUid = fromMe ? this.selfId : !isGroup ? threadId : peer?.startsWith("u:") ? peer.slice(2) : "";
      const data = syncToZaloMessage(sm, { isGroup, threadId, senderUid, fromMe, selfId: this.selfId });
      if (!data) continue;
      const gid = str(data.msgId);
      const cid = str(data.cliMsgId);
      const known = (gid ? this.store.find(chatId, gid) : undefined) ?? (have.has(cid) ? this.store.findByCli(chatId, cid) : undefined);
      if (known || have.has(cid)) {
        if (known && data.recalled && !known.m.deleted) this.markDeleted(known);
        continue;
      }
      const zm = { type: isGroup ? ThreadType.Group : ThreadType.User, threadId, isSelf: data.uidFrom === this.selfId, data } as unknown as ZMessage;
      try {
        const entry = await this.toEntry(zm);
        if (this.store.find(chatId, entry.m.id)) continue;
        if (data.recalled) this.markDeleted(entry);
        this.ingestEntry(entry, { replay: true, silent: true });
        have.add(entry.z.cliMsgId);
        added++;
      } catch (e) {
        this.log("phone sync: message skipped", errMsg(e));
      }
    }
    return added;
  }

  // ===================================================================== directory (friends / groups)

  private startDirectory() {
    this.dirPromise = this.refreshDirectory().catch((e) => this.log("directory refresh failed", errMsg(e)));
  }

  private scheduleDirRefresh() {
    if (this.dirTimer) return;
    this.dirTimer = setTimeout(() => {
      this.dirTimer = undefined;
      this.startDirectory();
    }, 5000);
  }

  private async waitDirectory(ms: number) {
    if (this.dirPromise) await Promise.race([this.dirPromise, sleep(ms)]);
  }

  private remember(uid: string, name?: string, avatar?: string) {
    if (!uid || uid === this.selfId) return;
    const c = this.store.contacts[uid];
    if (!c) {
      if (name) {
        this.store.contacts[uid] = { name, avatar };
        this.store.touch();
      }
      return;
    }
    if (!c.fr && name && c.name !== name) {
      c.name = name;
      this.store.touch();
    }
    if (avatar && !c.avatar) {
      c.avatar = avatar;
      this.store.touch();
    }
  }

  private nameOf(uid: string): string | undefined {
    if (uid === this.selfId) return this.self?.name;
    return this.store.contacts[uid]?.name;
  }

  private async refreshDirectory() {
    const api = this.api;
    if (!api) return;
    const results = await Promise.allSettled([
      this.loadFriends(api),
      this.loadGroups(api),
      this.loadPinned(api),
      this.loadMuted(api),
      this.loadUnreadMarks(api),
    ]);
    results.forEach((r, i) => {
      if (r.status === "rejected") {
        this.log(`directory part ${["friends", "groups", "pinned", "muted", "unread marks"][i]} failed`, errMsg(r.reason));
      }
    });
    this.dirAt = Date.now();
    this.store.touch();
    this.upsertChanged(results.slice(3));
    void this.warmGroups(api);
  }

  /**
   * After a new login Zalo replays nothing (1:1 history lives on the phone), so every chat would lack a preview and
   * the "Ưu tiên" tab would be empty. Group history is kept server-side: fetch the newest few messages of each group
   * that has none yet, one at a time and slowly so it looks like someone opening groups, newest-relevant first.
   */
  private async warmGroups(api: API) {
    if (this.warming || this.groupHistoryGone) return;
    this.warming = true;
    const gen = this.gen;
    try {
      const sc = (gid: string) => this.store.chats[chatIdOf(ThreadType.Group, gid)];
      const rank = (gid: string) => (sc(gid)?.pinned ? 0 : sc(gid)?.unread || sc(gid)?.markedUnread ? 1 : sc(gid)?.muted ? 3 : 2);
      const todo = Object.keys(this.store.groups)
        .filter((gid) => !this.warmed.has(gid) && this.store.list(chatIdOf(ThreadType.Group, gid)).length === 0)
        .sort((a, b) => rank(a) - rank(b));
      if (!todo.length) return;
      this.log(`warming ${todo.length} groups`);
      let failures = 0;
      let done = 0;
      for (const gid of todo) {
        if (gen !== this.gen || this.stopping || this.api !== api) return;
        this.warmed.add(gid);
        const chatId = chatIdOf(ThreadType.Group, gid);
        try {
          const res = await withTimeout(api.getGroupChatHistory(gid, WARM_GROUP_COUNT), 15_000, "getGroupChatHistory");
          await new Promise<void>((resolve) =>
            this.enqueue(async () => {
              for (const gm of res.groupMsgs ?? []) await this.ingestZMessage(gm, { replay: true, silent: true });
              if (this.store.list(chatId).length) this.scheduleUpsert(chatId);
              resolve();
            }),
          );
          failures = 0;
          done++;
        } catch (e) {
          this.warmed.delete(gid);
          if (/\b404\b/.test(errMsg(e))) {
            this.groupHistoryGone = true;
            this.log("group history unavailable (404), warm-up off", errMsg(e));
            return;
          }
          if (++failures >= 3) {
            this.log("group warm-up paused after repeated failures", errMsg(e));
            return;
          }
          await sleep(5_000);
        }
        await sleep(WARM_GROUP_DELAY_MS);
      }
      this.log(`warmed ${done} groups`);
    } finally {
      this.warming = false;
    }
  }

  /** Mute / "mark as unread" are set on the phone without any push event: re-read them now and then. */
  private async refreshNotifyState() {
    const api = this.api;
    if (!api) return;
    const results = await Promise.allSettled([this.loadMuted(api), this.loadUnreadMarks(api)]);
    results.forEach((r, i) => {
      if (r.status === "rejected") this.log(`${["mute", "unread marks"][i]} refresh failed`, errMsg(r.reason));
    });
    this.store.touch();
    this.upsertChanged(results);
  }

  private upsertChanged(results: PromiseSettledResult<string[] | void>[]) {
    for (const r of results) {
      if (r.status === "fulfilled" && Array.isArray(r.value)) for (const id of r.value) this.scheduleUpsert(id);
    }
  }

  private async loadFriends(api: API) {
    const friends = await withTimeout(api.getAllFriends(20000, 1, AvatarSize.Small), 60_000, "getAllFriends");
    const seen = new Set<string>();
    for (const f of friends) {
      const uid = str(f.userId);
      if (!uid) continue;
      seen.add(uid);
      const name = nonEmpty(f.displayName) ?? nonEmpty(f.zaloName) ?? nonEmpty(f.username) ?? uid;
      this.store.contacts[uid] = { name, avatar: nonEmpty(f.avatar), fr: 1 };
      this.rememberGid(f.globalId, `u:${uid}`);
    }
    // Drop the friend flag from people who are no longer friends (keep names for chat history).
    for (const [uid, c] of Object.entries(this.store.contacts)) if (c.fr && !seen.has(uid)) delete c.fr;
    this.friendsKnown = true;
    this.log(`friends: ${seen.size}`);
  }

  private async loadGroups(api: API) {
    const { gridVerMap } = await withTimeout(api.getAllGroups(), 30_000, "getAllGroups");
    const ids = Object.keys(gridVerMap ?? {});
    const groups: Record<string, { name: string; avatar?: string; memberCount?: number }> = {};
    for (let i = 0; i < ids.length; i += 40) {
      const res = await withTimeout(api.getGroupInfo(ids.slice(i, i + 40)), 30_000, "getGroupInfo");
      for (const [gid, info] of Object.entries(res.gridInfoMap ?? {})) {
        groups[gid] = {
          name: nonEmpty(info.name) ?? `Nhóm ${gid}`,
          avatar: nonEmpty(info.avt) ?? nonEmpty(info.fullAvt),
          memberCount: info.totalMember ?? info.memberIds?.length,
        };
        this.rememberGid((info as any).globalId, `g:${gid}`);
        for (const m of info.currentMems ?? []) this.remember(str(m.id), nonEmpty(m.dName) ?? nonEmpty(m.zaloName), nonEmpty(m.avatar));
      }
    }
    this.store.groups = groups;
    this.log(`groups: ${ids.length}`);
  }

  /** Zalo prefixes conversation ids with u/g in some APIs; accept both forms. */
  private convToChatId(raw: string): string | undefined {
    const m = /^([ug])(\d+)$/.exec(raw);
    if (m) return `${m[1]}:${m[2]}`;
    if (/^\d+$/.test(raw)) return this.store.groups[raw] ? `g:${raw}` : `u:${raw}`;
    return undefined;
  }

  private async loadPinned(api: API) {
    const res = await withTimeout(api.getPinConversations(), 15_000, "getPinConversations");
    for (const c of Object.values(this.store.chats)) c.pinned = false;
    for (const raw of res.conversations ?? []) {
      const id = this.convToChatId(str(raw));
      if (id) this.store.chat(id).pinned = true;
    }
  }

  /** getMute -> chat.muted / mutedUntil. Returns the ids of chats whose mute state changed. */
  private async loadMuted(api: API): Promise<string[]> {
    const startedAt = Date.now();
    const res = await withTimeout(api.getMute(), 15_000, "getMute");
    const now = Date.now();
    const next = new Map<string, number>(); // chatId -> mutedUntil (ms epoch, -1 = forever)
    const collect = (entries: typeof res.chatEntries | undefined, prefix: "u" | "g") => {
      for (const e of entries ?? []) {
        if (!e.id || e.muteMode === MUTE_MODE_OFF) continue;
        if (e.duration === -1) {
          next.set(`${prefix}:${e.id}`, -1);
          continue;
        }
        // startTime/duration/currentTime are all seconds on Zalo's clock: the remaining time is skew-free.
        const left = e.startTime + e.duration - (e.currentTime || Math.floor(now / 1000));
        if (left > 0) next.set(`${prefix}:${e.id}`, now + left * 1000);
      }
    };
    collect(res.chatEntries, "u");
    collect(res.groupChatEntries, "g");

    const changed: string[] = [];
    const ids = new Set<string>(next.keys());
    for (const [id, c] of Object.entries(this.store.chats)) if (c.muted || c.mutedUntil !== undefined) ids.add(id);
    for (const id of ids) {
      // This answer may predate (or not yet include) a mute the user just set: keep that one, the next poll settles it.
      if ((this.muteWrites.get(id) ?? 0) > startedAt - MUTE_SETTLE_MS) continue;
      const sc = this.store.chat(id);
      const was = sc.mutedUntil ?? (sc.muted ? -1 : undefined); // a bare legacy `muted` meant forever
      const to = next.get(id);
      if (to === undefined) {
        if (was !== undefined) {
          sc.muted = false;
          sc.mutedUntil = undefined;
          changed.push(id);
        }
        continue;
      }
      // Estimates wobble by a few seconds between polls; only a real difference counts as a change.
      const same = was !== undefined && (was === to || (was !== -1 && to !== -1 && Math.abs(was - to) < 60_000));
      sc.muted = true;
      if (!same) {
        sc.mutedUntil = to;
        changed.push(id);
      }
    }
    return changed;
  }

  /**
   * getUnreadMark -> chat.markedUnread (Zalo's manual "mark as unread"). Returns the ids that changed.
   * zca-js JSON.parse()s this response, so ids beyond 2^53 are already rounded: those are skipped, and only ids
   * of chats we know are accepted.
   */
  private async loadUnreadMarks(api: API): Promise<string[]> {
    const res = await withTimeout(api.getUnreadMark(), 15_000, "getUnreadMark");
    const marked = new Set<string>();
    const collect = (list: { id: number }[] | undefined, prefix: "u" | "g") => {
      for (const e of list ?? []) {
        if (typeof e?.id !== "number" || !Number.isSafeInteger(e.id)) continue;
        const id = `${prefix}:${e.id}`;
        const known = !!this.store.chats[id] || (prefix === "u" ? !!this.store.contacts[String(e.id)] : !!this.store.groups[String(e.id)]);
        if (known) marked.add(id);
      }
    };
    collect(res.data?.convsUser, "u");
    collect(res.data?.convsGroup, "g");

    const changed: string[] = [];
    for (const id of marked) {
      const sc = this.store.chat(id);
      if (!sc.markedUnread) {
        sc.markedUnread = true;
        changed.push(id);
      }
    }
    for (const [id, sc] of Object.entries(this.store.chats)) {
      if (sc.markedUnread && !marked.has(id)) {
        sc.markedUnread = undefined;
        changed.push(id);
      }
    }
    return changed;
  }

  // ===================================================================== chats

  private buildChat(chatId: string): Chat {
    const { type, id } = parseChatId(chatId);
    const sc = this.store.chats[chatId];
    const list = this.store.list(chatId);
    const last = list[list.length - 1]?.m;
    let title: string;
    let avatar: string | undefined;
    let kind: Chat["kind"];
    let memberCount: number | undefined;
    if (type === ThreadType.Group) {
      const g = this.store.groups[id];
      title = g?.name ?? sc?.title ?? `Nhóm ${id}`;
      avatar = g?.avatar ?? sc?.avatar;
      memberCount = g?.memberCount;
      kind = "group";
    } else if (id === this.selfId) {
      title = "Cloud của tôi";
      avatar = this.self?.avatar;
      kind = "self";
    } else {
      const c = this.store.contacts[id];
      title = c?.name ?? sc?.title ?? id;
      avatar = c?.avatar ?? sc?.avatar;
      kind = "direct";
    }
    // Mute: `mutedUntil` (ms, -1 forever) when known; a bare legacy `muted` flag means forever.
    const until = sc?.mutedUntil ?? (sc?.muted ? -1 : undefined);
    const muted = until !== undefined && (until === -1 || until > Date.now());
    // Friend list = contact list. "Not a contact" is only claimed once a friend list has been loaded.
    const isContact = kind === "direct" ? (this.store.contacts[id]?.fr ? true : this.friendsKnown ? false : undefined) : undefined;
    return {
      id: chatId,
      title,
      avatar,
      kind,
      isContact,
      unread: sc?.unread ?? 0,
      markedUnread: sc?.markedUnread || undefined,
      pinned: sc?.pinned || undefined,
      muted: muted || undefined,
      mutedUntil: muted ? until : undefined,
      timestamp: Math.max(last?.timestamp ?? 0, sc?.timestamp ?? 0),
      lastMessage: last,
      memberCount,
    };
  }

  private scheduleUpsert(chatId: string) {
    if (this.upsertTimers.has(chatId)) return;
    this.upsertTimers.set(
      chatId,
      setTimeout(() => {
        this.upsertTimers.delete(chatId);
        this.emitChat(chatId);
      }, 150),
    );
  }

  /** chat.upsert right now (no debounce); a pending debounced one for the same chat is dropped. */
  private emitChatNow(chatId: string) {
    const t = this.upsertTimers.get(chatId);
    if (t) clearTimeout(t);
    this.upsertTimers.delete(chatId);
    this.emitChat(chatId);
  }

  private emitChat(chatId: string) {
    this.ctx.emit("chat.upsert", { chat: this.buildChat(chatId) });
    this.announced.add(chatId);
  }

  async listChats(limit: number): Promise<Chat[]> {
    this.requireLogin();
    if (!Object.keys(this.store.contacts).length && !Object.keys(this.store.groups).length) {
      await this.waitDirectory(20_000);
    } else if (Date.now() - this.dirAt > DIR_STALE_MS && this.api) {
      this.startDirectory(); // refresh in the background, serve the cache now
    }
    const ids = new Set<string>();
    for (const [uid, c] of Object.entries(this.store.contacts)) if (c.fr) ids.add(`u:${uid}`);
    for (const gid of Object.keys(this.store.groups)) ids.add(`g:${gid}`);
    for (const id of this.store.chatIds()) ids.add(id);
    for (const [id, c] of Object.entries(this.store.chats)) if (c.pinned || c.unread) ids.add(id);
    const chats: Chat[] = [];
    for (const id of ids) {
      try {
        chats.push(this.buildChat(id));
        this.announced.add(id);
      } catch {}
    }
    chats.sort(
      (a, b) =>
        Number(!!b.pinned) - Number(!!a.pinned) || b.timestamp - a.timestamp || a.title.localeCompare(b.title, "vi"),
    );
    return chats.slice(0, Math.max(1, limit));
  }

  async chatAvatar(chatId: string): Promise<string | undefined> {
    this.requireLogin();
    const { type, id } = parseChatId(chatId);
    const known = this.buildChat(chatId).avatar;
    if (known) return known;
    const api = this.api!;
    try {
      if (type === ThreadType.Group) {
        const info = (await api.getGroupInfo(id)).gridInfoMap?.[id];
        const avatar = nonEmpty(info?.avt) ?? nonEmpty(info?.fullAvt);
        if (avatar) {
          const g = (this.store.groups[id] ??= { name: nonEmpty(info?.name) ?? `Nhóm ${id}` });
          g.avatar = avatar;
          this.store.touch();
        }
        return avatar;
      }
      const res = await api.getUserInfo(id);
      const p = res.changed_profiles?.[id] ?? Object.entries(res.changed_profiles ?? {}).find(([k]) => k.startsWith(id))?.[1];
      const avatar = nonEmpty(p?.avatar);
      this.remember(id, nonEmpty(p?.displayName) ?? nonEmpty(p?.zaloName), avatar);
      return avatar;
    } catch (e) {
      this.log("chatAvatar failed", errMsg(e));
      return undefined;
    }
  }

  /**
   * chats.direct (capability "direct_chat"): the one-to-one chat with a group member, `userId` being a group
   * `Message.senderId` (a Zalo uid). The id is the one messages.send / messages.list take for a user thread
   * ("u:<uid>"; the Cloud chat for our own uid). Nothing is sent to the person and nothing is stored for a chat that
   * has no message yet: the answer is the stored chat when there is one, else the same chat built from what is known.
   * Zalo lets us write to a non-friend, so there is no check here; a refusal comes back from messages.send.
   *
   * The title is a name we already hold (contacts, the stored chat, the name a group message came with); only when
   * there is none does one getUserInfo run, shared and cached for a few minutes with chats.members.
   */
  async directChat(userId: string): Promise<Chat> {
    this.requireLogin();
    const uid = str(userId).trim();
    if (!/^\d+$/.test(uid)) {
      throw new BridgeError("invalid_params", `bad userId "${uid}" (expected a Zalo uid: the senderId of a group message)`);
    }
    const chatId = chatIdOf(ThreadType.User, uid);
    const named = uid === this.selfId || !!this.nameOf(uid) || !!nonEmpty(this.store.chats[chatId]?.title);
    if (!named) {
      const seen = this.knownSender(uid);
      if (seen) this.remember(uid, seen.name, seen.avatar);
      else if (this.api) await this.chatMembers(chatId);
    }
    return this.buildChat(chatId);
  }

  /** The name (and picture) a stored group message of `uid` came with, newest first; undefined when none was kept. */
  private knownSender(uid: string): { name: string; avatar?: string } | undefined {
    for (const chatId of this.store.chatIds()) {
      if (!chatId.startsWith("g:")) continue;
      const list = this.store.list(chatId);
      for (let i = list.length - 1; i >= 0; i--) {
        const m = list[i]!.m;
        if (m.senderId !== uid || m.fromMe) continue;
        const name = nonEmpty(m.senderName);
        if (name) return { name, avatar: nonEmpty(m.senderAvatar) };
      }
    }
    return undefined;
  }

  /**
   * Members for the @mention picker. Group: getGroupInfo gives the ids (memVerList = "uid_version", plus memberIds and
   * the partial currentMems); names come from the contact store, then currentMems, then getGroupMembersInfo for the
   * rest (and are remembered, so the next group that shares them needs no request). 1:1: the other participant.
   */
  async chatMembers(chatId: string): Promise<Member[]> {
    this.requireLogin();
    const { type, id } = parseChatId(chatId);
    const api = this.api!;
    const epoch = this.epoch;
    const members = await this.membersCache.getOrLoad(chatId, async () => {
      if (type === ThreadType.User) return [await this.directMember(id)];
      return this.groupMembers(api, id);
    });
    // A logout while the request was in flight must not leave the previous account's members behind.
    if (epoch !== this.epoch) this.membersCache.clear();
    return members;
  }

  private async directMember(uid: string): Promise<Member> {
    let name = this.nameOf(uid) ?? nonEmpty(this.store.chats[chatIdOf(ThreadType.User, uid)]?.title);
    let avatar = uid === this.selfId ? this.self?.avatar : this.store.contacts[uid]?.avatar;
    if (!name && this.api) {
      try {
        const res = await withTimeout(this.api.getUserInfo(uid), 15_000, "getUserInfo");
        const p = res.changed_profiles?.[uid] ?? Object.entries(res.changed_profiles ?? {}).find(([k]) => k.startsWith(uid))?.[1];
        name = nonEmpty(p?.displayName) ?? nonEmpty(p?.zaloName);
        avatar ??= nonEmpty(p?.avatar);
        this.remember(uid, name, nonEmpty(p?.avatar));
      } catch (e) {
        this.log("chatMembers: getUserInfo failed", errMsg(e));
      }
    }
    return { id: uid, name: name ?? uid, ...(avatar ? { avatar } : {}) };
  }

  private async groupMembers(api: API, gid: string): Promise<Member[]> {
    const info = (await withTimeout(api.getGroupInfo(gid), 30_000, "getGroupInfo")).gridInfoMap?.[gid];
    if (!info) throw new BridgeError("not_found", `Zalo group ${gid} not found`);

    const ids: string[] = [];
    const seen = new Set<string>();
    const add = (raw: unknown) => {
      const uid = str(raw).replace(/_\d+$/, ""); // memVerList entries look like "uid_version"
      if (uid && !seen.has(uid)) {
        seen.add(uid);
        ids.push(uid);
      }
    };
    for (const v of info.memVerList ?? []) add(v);
    for (const v of info.memberIds ?? []) add(v);
    const partial = new Map<string, { name?: string; avatar?: string }>();
    for (const m of info.currentMems ?? []) {
      const uid = str(m.id);
      add(uid);
      partial.set(uid, { name: nonEmpty(m.dName) ?? nonEmpty(m.zaloName), avatar: nonEmpty(m.avatar) });
    }

    const profile = (uid: string) => {
      const c = this.store.contacts[uid];
      const p = partial.get(uid);
      return { name: nonEmpty(c?.name) ?? p?.name, avatar: c?.avatar ?? p?.avatar };
    };
    const unknown = ids.filter((uid) => uid !== this.selfId && !profile(uid).name);
    for (let i = 0; i < unknown.length; i += MEMBER_INFO_CHUNK) {
      try {
        const res = await withTimeout(api.getGroupMembersInfo(unknown.slice(i, i + MEMBER_INFO_CHUNK)), 20_000, "getGroupMembersInfo");
        for (const [key, p] of Object.entries(res.profiles ?? {})) {
          const uid = str(p.id) || key.replace(/_\d+$/, "");
          partial.set(uid, { name: nonEmpty(p.displayName) ?? nonEmpty(p.zaloName), avatar: nonEmpty(p.avatar) });
        }
      } catch (e) {
        this.log("chatMembers: getGroupMembersInfo failed", errMsg(e));
      }
    }

    const out: Member[] = [];
    for (const uid of ids) {
      if (uid === this.selfId) continue;
      const { name, avatar } = profile(uid);
      this.remember(uid, name, avatar);
      out.push({ id: uid, name: name ?? uid, ...(avatar ? { avatar } : {}) });
    }
    out.sort((a, b) => a.name.localeCompare(b.name, "vi"));
    return out;
  }

  // ===================================================================== incoming messages

  private resolveStickerLater(chatId: string, msgId: string, stickerId: number) {
    void this.resolveSticker(stickerId)
      .then((media) => {
        if (!media) return;
        const e = this.store.find(chatId, msgId);
        if (!e || e.m.media?.url) return;
        e.m.media = { ...media, path: e.m.media?.path };
        e.m.text = undefined;
        this.store.touch();
        this.ctx.emit("message.update", { message: e.m });
        this.scheduleUpsert(chatId);
      })
      .catch(() => {});
  }

  private async resolveSticker(id: number): Promise<Media | undefined> {
    if (this.stickerCache.has(id)) return this.stickerCache.get(id);
    const api = this.api;
    if (!api) return undefined;
    try {
      const res = await withTimeout(api.getStickersDetail(id), 5000, "getStickersDetail");
      const s = res[0];
      const url = nonEmpty(s?.stickerWebpUrl) ?? nonEmpty(s?.stickerUrl);
      // Cloud-first: the app loads the (animated) sticker straight from the CDN; the static image is its preview.
      const still = nonEmpty(s?.stickerUrl);
      const media: Media | undefined = url
        ? { url, mime: mimeFromName(url) ?? "image/png", thumbnail: still && still !== url ? still : undefined }
        : undefined;
      this.stickerCache.set(id, media);
      return media;
    } catch (e) {
      this.log(`sticker ${id} lookup failed`, errMsg(e));
      return undefined;
    }
  }

  private async toEntry(zm: ZMessage): Promise<Entry> {
    const d = zm.data as unknown as Record<string, any>;
    const chatId = chatIdOf(zm.type, str(zm.threadId));
    const uidFrom = str(d.uidFrom);
    const fromMe = zm.isSelf || (!!this.selfId && uidFrom === this.selfId);
    const msgType = str(d.msgType);
    const mapped = mapContent(msgType, d.content, { fromMe });
    let { text, media } = mapped;
    // Group messages carry the mention spans beside the text; they rewrite a span that holds an id instead of a name.
    if (text && Array.isArray(d.mentions)) text = fixMentionText(text, d.mentions, (uid) => this.nameOf(uid));
    const gid = str(d.msgId);
    const id = gid && gid !== "0" ? gid : str(d.cliMsgId);
    if (mapped.sticker) {
      if (this.stickerCache.has(mapped.sticker.id)) {
        media = this.stickerCache.get(mapped.sticker.id);
      } else {
        // Sticker art is fetched off the ingest queue: a slow sticker API (up to 5 s) must not hold back the
        // messages behind this one. The message shows up now and gets its image with a message.update.
        media = undefined;
        this.resolveStickerLater(chatId, id, mapped.sticker.id);
      }
      text = media?.url ? undefined : "[Sticker]";
    }
    if (!fromMe) this.remember(uidFrom, nonEmpty(d.dName));
    const senderName = fromMe ? this.self?.name : (this.nameOf(uidFrom) ?? nonEmpty(d.dName));
    const m: Message = {
      id,
      chatId,
      senderId: fromMe ? this.selfId || uidFrom : uidFrom,
      senderName,
      senderAvatar: fromMe ? this.self?.avatar : this.store.contacts[uidFrom]?.avatar,
      fromMe,
      timestamp: toMs(d.ts),
      kind: mapped.kind,
      text,
      media,
      replyTo: quoteToReply(d.quote),
      status: fromMe ? "sent" : undefined,
      groupId: albumOf(msgType, d.content)?.groupId,
    };
    const z: ZMeta = {
      t: zm.type === ThreadType.Group ? 1 : 0,
      msgId: gid,
      cliMsgId: str(d.cliMsgId),
      uidFrom,
      idTo: str(d.idTo),
      msgType,
      ts: str(d.ts),
      ttl: Number(d.ttl) || 0,
      st: Number(d.st) || 0,
      at: Number(d.at) || 0,
      cmd: Number(d.cmd) || 0,
      content: plain(d.content),
      propertyExt: plain(d.propertyExt),
      sticker: mapped.sticker,
    };
    return { m, z };
  }

  private async ingestZMessage(zm: ZMessage, opts: IngestOpts) {
    this.ingestEntry(await this.toEntry(zm), opts);
  }

  private ingestEntry(entry: Entry, opts: IngestOpts) {
    const m = entry.m;
    const chatId = m.chatId;
    // A recall event in a history listing only names the message to recall; it is not a message to show.
    const recall = recallEvent(entry.z.msgType, entry.z.content);
    if (recall) {
      this.recallStored(chatId, recall, !opts.silent);
      return;
    }
    const existing = this.store.find(chatId, m.id);
    if (existing) {
      this.mergeExisting(existing, entry, opts);
      return;
    }
    const sc = this.store.chat(chatId);
    const prevTs = sc.timestamp;
    const lastOwnTs = this.lastOwnTs(chatId); // before the insert
    this.store.insert(chatId, entry);
    // Album members arrive in upload order; the grid must follow the order the sender picked (see albumTimestamps).
    const reordered = m.groupId ? this.settleAlbum(chatId, entry) : [];
    const newest = m.timestamp >= prevTs;
    if (newest) sc.timestamp = m.timestamp;
    if (!m.fromMe && m.kind !== "system" && m.senderName && chatId.startsWith("u:") && !sc.title) {
      sc.title = m.senderName;
    }
    if (!opts.silent) {
      if (m.fromMe) {
        // Writing in a chat (from the phone or any other device) means everything before it was read.
        this.readUpTo(chatId, m.timestamp, true);
      } else if (m.kind !== "system") {
        // zca-js exposes no server unread counter, so this is counted locally: live messages always count;
        // replayed ones only if they arrived while we were away, and never if we answered after them (read).
        const active = this.store.meta.activeTs;
        const arrivedWhileAway = !opts.replay || (active !== undefined && m.timestamp > active);
        if (arrivedWhileAway && m.timestamp > lastOwnTs) sc.unread++;
      }
    }
    if (!opts.replay) this.store.meta.activeTs = Date.now();
    this.store.touch();
    if (opts.silent) return;
    // Siblings that got a new place in their album (the app re-sorts its list the next time it fetches or gets a message).
    for (const e of reordered) if (e !== entry && !e.m.deleted) this.ctx.emit("message.update", { message: e.m });
    // A replayed message older than what we already show is backfill: it is served by messages.list.
    if (!opts.replay || newest) {
      // Real time = straight from the listener's "message" event (not a replay / old_messages / history call)
      // and not stale; our own and service messages never notify.
      const live = !opts.replay && !m.fromMe && m.kind !== "system" && isFresh(m.timestamp);
      // The app needs the chat (title, avatar, mute) before the first message.new of a chat it has not seen.
      if (!this.announced.has(chatId)) this.emitChatNow(chatId);
      if (live) {
        this.stopTyping(chatId, m.senderId);
        this.log(`live message ${m.id} in ${chatId} delivered ${Date.now() - m.timestamp} ms after its send time`);
      }
      this.ctx.emit("message.new", { message: m, live });
      // Updated unread right behind every live message (the notification reads it from here).
      if (live) {
        this.emitChatNow(chatId);
        return;
      }
    }
    this.scheduleUpsert(chatId);
  }

  /** Timestamp (ms) of our newest own message in the chat, 0 if none. */
  private lastOwnTs(chatId: string): number {
    const list = this.store.list(chatId);
    for (let i = list.length - 1; i >= 0; i--) if (list[i]!.m.fromMe) return list[i]!.m.timestamp;
    return 0;
  }

  /**
   * The chat was read up to `tsMs` (we wrote after it, or another device saw it): only incoming messages
   * newer than that can still be unread. `clearMark` also drops a manual "marked as unread" flag.
   */
  private readUpTo(chatId: string, tsMs: number, clearMark = false) {
    const sc = this.store.chats[chatId];
    if (!sc || (!sc.unread && !sc.markedUnread)) return;
    const list = this.store.list(chatId); // ascending
    let after = 0;
    for (let i = list.length - 1; i >= 0 && list[i]!.m.timestamp > tsMs; i--) {
      const x = list[i]!.m;
      if (!x.fromMe && x.kind !== "system") after++;
    }
    const unread = Math.min(sc.unread, after);
    const dropMark = clearMark && unread === 0 && !!sc.markedUnread;
    if (unread === sc.unread && !dropMark) return;
    sc.unread = unread;
    if (dropMark) sc.markedUnread = undefined;
    this.store.touch();
    this.scheduleUpsert(chatId);
  }

  /** Stop a "typing" indicator we are still showing for this sender (their message just arrived). */
  private stopTyping(chatId: string, userId: string) {
    const key = `${chatId}|${userId}`;
    const t = this.typingTimers.get(key);
    if (!t) return;
    clearTimeout(t);
    this.typingTimers.delete(key);
    this.ctx.emit("typing", { chatId, userId, name: this.nameOf(userId), typing: false });
  }

  /** Same message seen again (replay, or the echo of something we just sent). */
  private mergeExisting(old: Entry, incoming: Entry, opts: IngestOpts) {
    let changed = false;
    const oz = old.z;
    const nz = incoming.z;
    if (!oz.cliMsgId && nz.cliMsgId) oz.cliMsgId = nz.cliMsgId;
    if (!oz.msgType || oz.msgType !== nz.msgType) oz.msgType = nz.msgType || oz.msgType;
    if (oz.content === undefined && nz.content !== undefined) oz.content = nz.content;
    if (oz.propertyExt === undefined && nz.propertyExt !== undefined) oz.propertyExt = nz.propertyExt;
    if (!oz.st && nz.st) oz.st = nz.st;
    if (!oz.at && nz.at) oz.at = nz.at;
    if (!oz.cmd && nz.cmd) oz.cmd = nz.cmd;
    if (nz.ts) oz.ts = nz.ts;
    if (nz.idTo) oz.idTo = nz.idTo;
    if (incoming.m.media?.url && !old.m.media?.url) {
      old.m.media = { ...old.m.media, ...incoming.m.media, path: old.m.media?.path };
      changed = true;
    }
    if (!old.m.text && incoming.m.text) {
      old.m.text = incoming.m.text;
      changed = true;
    }
    if (!old.m.replyTo && incoming.m.replyTo) {
      old.m.replyTo = incoming.m.replyTo;
      changed = true;
    }
    this.store.touch();
    if (changed && !opts.silent && !old.m.deleted) this.ctx.emit("message.update", { message: old.m });
  }

  private markDeleted(e: Entry) {
    e.m.deleted = true;
    e.m.kind = "text";
    e.m.text = undefined;
    e.m.media = undefined;
    e.m.reactions = undefined;
    e.z.reacts = undefined;
    this.store.touch();
  }

  /** Mark the message a recall event names as deleted. False when we do not have it. */
  private recallStored(chatId: string, ev: { msgId: string; cliMsgId: string }, notify: boolean): boolean {
    const e = (ev.msgId ? this.store.find(chatId, ev.msgId) : undefined) ?? this.store.findByCli(chatId, ev.cliMsgId);
    if (!e) return false;
    if (!e.m.deleted) {
      this.markDeleted(e);
      if (notify) {
        this.ctx.emit("message.update", { message: e.m });
        this.scheduleUpsert(chatId);
      }
    }
    return true;
  }

  /**
   * Give the members of one album (same sender and group id) their timestamps back in the order the sender picked.
   * Returns the entries that moved; the caller re-sorts the chat (store.resort) once it has handled every album.
   */
  private retimeAlbum(members: Entry[]): Entry[] {
    const changes = albumTimestamps(
      members.map((e) => ({
        id: e.m.id,
        ts: e.m.timestamp,
        cli: Number(e.z.cliMsgId) || 0,
        index: albumOf(e.z.msgType, e.z.content)?.index ?? 0,
      })),
    );
    if (!changes.size) return [];
    const moved = members.filter((e) => changes.has(e.m.id));
    for (const e of moved) e.m.timestamp = changes.get(e.m.id)!;
    return moved;
  }

  /** A new album member was stored: re-time it together with the members already there (they are close in time). */
  private settleAlbum(chatId: string, entry: Entry): Entry[] {
    const list = this.store.list(chatId);
    const from = entry.m.timestamp - ALBUM_MAX_SPREAD_MS;
    const members: Entry[] = [];
    for (let i = list.length - 1; i >= 0; i--) {
      const e = list[i]!;
      if (e.m.timestamp < from) break;
      if (e.m.groupId === entry.m.groupId && e.z.uidFrom === entry.z.uidFrom) members.push(e);
    }
    const moved = this.retimeAlbum(members);
    if (moved.length) this.store.resort(chatId);
    return moved;
  }

  /**
   * Bring what an earlier version stored up to the current mapping, from the raw content kept in every entry: call
   * logs, link / contact cards, zinstant cards and notices (they read "sendBubbleMessage Cuộc gọi" or "Tin nhắn chưa
   * được hỗ trợ" before), album ids and album order, recall events listed as messages. Runs on every start; it only
   * writes what differs, so a second run changes nothing.
   */
  private remapStored(): number {
    let changed = 0;
    for (const chatId of this.store.chatIds()) {
      const albums = new Map<string, Entry[]>();
      for (const e of [...this.store.list(chatId)]) {
        const { m, z } = e;
        const recall = recallEvent(z.msgType, z.content);
        if (recall) {
          this.recallStored(chatId, recall, false);
          this.store.remove(chatId, m.id);
          changed++;
          continue;
        }
        if (m.deleted) continue;
        if (REMAP_TYPES.has(z.msgType) && z.content !== undefined && z.content !== null) {
          const mapped = mapContent(z.msgType, z.content, { fromMe: m.fromMe });
          if (
            mapped.kind !== m.kind ||
            mapped.text !== m.text ||
            mapped.media?.url !== m.media?.url ||
            mapped.media?.thumbnail !== m.media?.thumbnail
          ) {
            m.kind = mapped.kind;
            m.text = mapped.text;
            m.media = mapped.media ? { ...mapped.media, path: m.media?.path } : undefined;
            changed++;
          }
        }
        const album = albumOf(z.msgType, z.content);
        if (album) {
          if (m.groupId !== album.groupId) {
            m.groupId = album.groupId;
            changed++;
          }
          const key = `${album.groupId}|${z.uidFrom}`;
          albums.set(key, [...(albums.get(key) ?? []), e]);
        }
      }
      let moved = false;
      for (const members of albums.values()) {
        const n = this.retimeAlbum(members).length;
        if (n) {
          moved = true;
          changed += n;
        }
      }
      if (moved) this.store.resort(chatId);
    }
    if (changed) {
      this.log(`remapped ${changed} stored message(s) to the current mapping`);
      this.store.touch();
    }
    return changed;
  }

  private applyUndo(u: ZUndo) {
    const chatId = chatIdOf(u.isGroup ? ThreadType.Group : ThreadType.User, str(u.threadId));
    const content = (u.data as any).content ?? {};
    const gid = str(content.globalMsgId);
    const cid = str(content.cliMsgId);
    const e = (gid ? this.store.find(chatId, gid) : undefined) ?? this.store.findByCli(chatId, cid);
    if (e) {
      if (!e.m.deleted) {
        this.markDeleted(e);
        this.ctx.emit("message.update", { message: e.m });
        this.scheduleUpsert(chatId);
      }
    } else if (gid) {
      this.ctx.emit("message.delete", { chatId, messageId: gid });
    }
  }

  private applyReaction(r: ZReaction) {
    const d = r.data as any;
    let content = d.content;
    if (typeof content === "string") {
      try {
        content = JSON.parse(content);
      } catch {
        return;
      }
    }
    if (!content || typeof content !== "object") return;
    const rIcon = str(content.rIcon);
    const reactor = str(d.uidFrom);
    const chatId = chatIdOf(r.isGroup ? ThreadType.Group : ThreadType.User, str(r.threadId));
    for (const rm of (content.rMsg ?? []) as any[]) {
      const e =
        (str(rm.gMsgID) ? this.store.find(chatId, str(rm.gMsgID)) : undefined) ??
        this.store.findByCli(chatId, str(rm.cMsgID));
      if (!e || e.m.deleted) continue;
      this.setReaction(e, reactor, rIcon);
    }
  }

  /** Set/remove one user's reaction on a message and emit message.update if anything changed. */
  private setReaction(e: Entry, uid: string, icon: string) {
    const before = JSON.stringify(e.z.reacts ?? {});
    const reacts = (e.z.reacts ??= {});
    if (icon) reacts[uid] = icon;
    else delete reacts[uid];
    if (JSON.stringify(reacts) === before) return;
    if (!Object.keys(reacts).length) e.z.reacts = undefined;
    e.m.reactions = computeReactions(e.z.reacts, this.selfId);
    this.store.touch();
    this.ctx.emit("message.update", { message: e.m });
  }

  private applyTyping(t: ZTyping) {
    const chatId = chatIdOf(t.type, str(t.threadId));
    const uid = str((t.data as any).uid);
    const key = `${chatId}|${uid}`;
    const name = this.nameOf(uid);
    this.ctx.emit("typing", { chatId, userId: uid, name, typing: true });
    const prev = this.typingTimers.get(key);
    if (prev) clearTimeout(prev);
    // Zalo only tells us when typing starts; stop it ourselves.
    this.typingTimers.set(
      key,
      setTimeout(() => {
        this.typingTimers.delete(key);
        this.ctx.emit("typing", { chatId, userId: uid, name, typing: false });
      }, 6000),
    );
  }

  private raiseStatus(chatId: string, msgId: string, status: Status, upTo: boolean) {
    const target = msgId ? this.store.find(chatId, msgId) : undefined;
    if (!target) return;
    const list = upTo ? this.store.list(chatId) : [target];
    for (const e of list) {
      if (!e.m.fromMe || e.m.timestamp > target.m.timestamp) continue;
      const cur = e.m.status ?? "sent";
      if (STATUS_RANK[status] <= STATUS_RANK[cur]) continue;
      e.m.status = status;
      this.ctx.emit("message.update", { message: e.m });
    }
    this.store.touch();
  }

  private applySeen(list: SeenMessage[]) {
    for (const s of list) {
      const chatId = chatIdOf(s.type, str(s.threadId));
      const d = s.data as any;
      if (s.type === ThreadType.Group && s.isSelf) {
        // We read this group on another device (zca-js only reports this for groups; selfListen is on).
        const seen = this.store.find(chatId, str(d.msgId));
        if (seen) this.readUpTo(chatId, seen.m.timestamp, true);
        else this.clearUnread(chatId);
        continue;
      }
      if (s.type === ThreadType.User && this.selfId && str(d.idTo) === this.selfId) {
        // Heuristic: zca-js hard-codes isSelf=false for direct chats, but a "seen" whose recipient is us can only
        // mean we saw incoming messages on another device. The payload has no thread, so find it by message id.
        for (const mid of [str(d.msgId), str(d.realMsgId)]) {
          if (!mid) continue;
          const hit = this.findIncomingEntry(mid);
          if (hit) {
            this.readUpTo(hit.chatId, hit.entry.m.timestamp, true);
            break;
          }
        }
        continue;
      }
      this.raiseStatus(chatId, str(d.msgId), "read", true);
      if (d.realMsgId) this.raiseStatus(chatId, str(d.realMsgId), "read", true);
    }
  }

  private clearUnread(chatId: string) {
    const sc = this.store.chats[chatId];
    if (!sc || (!sc.unread && !sc.markedUnread)) return;
    sc.unread = 0;
    sc.markedUnread = undefined;
    this.store.touch();
    this.scheduleUpsert(chatId);
  }

  /** Find a stored incoming message by its global msgId in any chat. */
  private findIncomingEntry(msgId: string): { chatId: string; entry: Entry } | undefined {
    for (const chatId of this.store.chatIds()) {
      const entry = this.store.find(chatId, msgId);
      if (entry && !entry.m.fromMe) return { chatId, entry };
    }
    return undefined;
  }

  private applyDelivered(list: DeliveredMessage[]) {
    for (const dm of list) {
      const chatId = chatIdOf(dm.type, str(dm.threadId));
      const d = dm.data as any;
      this.raiseStatus(chatId, str(d.msgId), "delivered", false);
    }
  }

  private applyFriendEvent(ev: ZFriendEvent) {
    if (ev.type === FriendEventType.ADD || ev.type === FriendEventType.REMOVE) this.scheduleDirRefresh();
    if (ev.type === FriendEventType.PIN_CREATE || ev.type === FriendEventType.PIN_UNPIN) {
      // Whether `conversationId` is the peer when the peer pinned is not documented: cover the actor's chat too.
      const threadId = str(ev.threadId);
      const actorId = str(ev.data.actorId);
      const chats = new Set<string>();
      for (const uid of [threadId, actorId]) {
        const id = uid && (uid !== this.selfId || uid === threadId) ? this.convToChatId(uid) : undefined;
        if (id) chats.add(id);
      }
      for (const id of chats) this.pinsChanged(id);
    }
  }

  private applyGroupEvent(ev: ZGroupEvent) {
    const gid = str(ev.threadId);
    const chatId = chatIdOf(ThreadType.Group, gid);
    const d = ev.data as any;
    if (PIN_EVENTS.has(ev.type)) {
      this.pinsChanged(chatId);
      return;
    }
    if (BOARD_EVENTS.has(ev.type)) {
      const topicType = Number(d.groupTopic?.type);
      if (Number.isNaN(topicType) || topicType === GroupTopicType.Message) this.pinsChanged(chatId);
      return;
    }
    const members: any[] = Array.isArray(d.updateMembers) ? d.updateMembers : [];
    for (const mem of members) {
      if (mem && typeof mem === "object" && mem.id) this.remember(str(mem.id), nonEmpty(mem.dName), nonEmpty(mem.avatar));
    }
    const who = (id: string) => (id === this.selfId ? "Bạn" : (this.nameOf(id) ?? "Một thành viên"));
    const source = str(d.sourceId);
    const src = source ? who(source) : "Quản trị viên";
    const memberIds = members.map((x) => str(typeof x === "object" ? x?.id : x)).filter(Boolean);
    const names = memberIds.map(who).join(", ") || "Một thành viên";
    const selfOnly = memberIds.length === 1 && memberIds[0] === source;

    let text: string | undefined;
    switch (ev.type) {
      case GroupEventType.JOIN:
        text = !source || selfOnly || memberIds.includes(source) ? `${names} đã tham gia nhóm` : `${src} đã thêm ${names} vào nhóm`;
        break;
      case GroupEventType.LEAVE:
        text = `${names} đã rời khỏi nhóm`;
        break;
      case GroupEventType.REMOVE_MEMBER:
        text = `${src} đã xóa ${names} khỏi nhóm`;
        break;
      case GroupEventType.BLOCK_MEMBER:
        text = `${src} đã chặn ${names} khỏi nhóm`;
        break;
      case GroupEventType.ADD_ADMIN:
        text = `${names} đã được ${src} bổ nhiệm làm phó nhóm`;
        break;
      case GroupEventType.REMOVE_ADMIN:
        text = `${names} đã bị ${src} gỡ quyền phó nhóm`;
        break;
      case GroupEventType.UPDATE_AVATAR:
        text = `${src} đã thay đổi ảnh đại diện nhóm`;
        if (nonEmpty(d.avt) && this.store.groups[gid]) this.store.groups[gid]!.avatar = d.avt;
        break;
      case GroupEventType.UPDATE: {
        const newName = nonEmpty(d.groupName);
        const g = this.store.groups[gid];
        if (newName && g && g.name !== newName) {
          text = `${src} đã đổi tên nhóm thành "${newName}"`;
          g.name = newName;
        } else if (newName && !g) {
          this.store.groups[gid] = { name: newName };
        }
        break;
      }
      default:
        break;
    }
    if (ev.type === GroupEventType.JOIN || ev.type === GroupEventType.LEAVE || ev.type === GroupEventType.REMOVE_MEMBER) {
      const g = this.store.groups[gid];
      if (g?.memberCount !== undefined) {
        const delta = memberIds.length * (ev.type === GroupEventType.JOIN ? 1 : -1);
        g.memberCount = Math.max(0, g.memberCount + delta);
      }
    }
    this.store.touch();
    if (!text) return;

    const timestamp = toMs(d.time);
    const hash = createHash("md5").update(`${ev.act}|${source}|${memberIds.join(",")}|${text}`).digest("hex").slice(0, 8);
    const m: Message = {
      id: `sys:${gid}:${ev.act}:${timestamp}:${hash}`,
      chatId,
      senderId: source,
      senderName: source ? this.nameOf(source) : undefined,
      fromMe: source === this.selfId,
      timestamp,
      kind: "system",
      text,
    };
    const z: ZMeta = {
      t: 1,
      msgId: "",
      cliMsgId: "",
      uidFrom: source,
      idTo: gid,
      msgType: "system",
      ts: String(timestamp),
      ttl: 0,
      st: 0,
      at: 0,
      cmd: 0,
    };
    this.ingestEntry({ m, z }, { replay: false });
  }

  // ===================================================================== messages (API)

  private async fetchGroupHistory(gid: string) {
    const api = this.api;
    if (!api) return;
    try {
      const res = await withTimeout(api.getGroupChatHistory(gid, 50), 15_000, "getGroupChatHistory");
      const msgs = res.groupMsgs ?? [];
      for (const gm of msgs) await this.ingestZMessage(gm, { replay: true, silent: true });
      this.log(`group ${gid}: history ${msgs.length} messages`);
    } catch (e) {
      this.histFetched.delete(gid);
      this.log(`group ${gid}: history failed`, errMsg(e));
    }
  }

  /**
   * The user scrolled past the oldest message we hold (never fetched, or trimmed by maxStoredMessagesPerChat).
   * getGroupChatHistory has no cursor, only a count of the newest messages, so ask for "what we hold + want".
   * Returns how many messages were new. Direct chats have no such call: Zalo only replays them via the realtime
   * socket, so what is trimmed there cannot be fetched again.
   */
  private async fetchOlderGroup(gid: string, want: number): Promise<number> {
    const api = this.api;
    if (!api || this.historyDone.has(gid)) return 0;
    const chatId = chatIdOf(ThreadType.Group, gid);
    const held = this.store.list(chatId).length;
    const count = Math.min(OLDER_HISTORY_MAX, held + Math.max(want, 50));
    if (count <= held) {
      this.historyDone.add(gid);
      return 0;
    }
    try {
      this.store.raiseCap(chatId, count - held); // otherwise the cap would trim the fetched messages right back
      const res = await withTimeout(api.getGroupChatHistory(gid, count), 20_000, "getGroupChatHistory");
      for (const gm of res.groupMsgs ?? []) await this.ingestZMessage(gm, { replay: true, silent: true });
      const added = this.store.list(chatId).length - held;
      if (added <= 0 || res.more === 0) this.historyDone.add(gid);
      this.log(`group ${gid}: older history asked ${count}, got ${res.groupMsgs?.length ?? 0}, ${Math.max(added, 0)} new`);
      return Math.max(added, 0);
    } catch (e) {
      this.log(`group ${gid}: older history failed`, errMsg(e));
      return 0;
    }
  }

  /** A message as the app should see it: a cached file that was pruned in the meantime is no longer referenced. */
  private outbound(m: Message): Message {
    const p = m.media?.path;
    return p && !existsSync(p) ? { ...m, media: { ...m.media, path: undefined } } : m;
  }

  async listMessages(chatId: string, limit: number, before?: string): Promise<Message[]> {
    this.requireLogin();
    const { type, id } = parseChatId(chatId);
    if (type === ThreadType.Group && !before && !this.histFetched.has(id)) {
      this.histFetched.add(id);
      // Serialise with live handlers so ordering/dedupe stays consistent.
      await new Promise<void>((resolve) => this.enqueue(() => this.fetchGroupHistory(id).finally(resolve)));
    }
    limit = Math.max(1, limit);
    const list = this.store.list(chatId);
    let end = list.length;
    if (before) {
      const i = list.findIndex((e) => e.m.id === before);
      if (i < 0) return [];
      end = i;
      if (end < limit && type === ThreadType.Group && !this.historyDone.has(id)) {
        const added = await new Promise<number>((resolve) =>
          this.enqueue(() => this.fetchOlderGroup(id, limit - end).then(resolve, () => resolve(0))),
        );
        if (added > 0) end = list.findIndex((e) => e.m.id === before);
        if (end < 0) return [];
      }
    }
    return list.slice(Math.max(0, end - limit), end).map((e) => this.outbound(e.m));
  }

  async searchMessages(p: SearchParams): Promise<Message[]> {
    this.requireLogin();
    const ids = p.chatId ? [p.chatId] : this.store.chatIds();
    return searchLocal(
      ids.map((id) => [id, this.store.list(id)] as [string, Entry[]]),
      p,
      (e) => this.outbound(e.m),
      (e) => [e.m.text, e.m.deleted, e.m.media?.fileName, e.m.kind, e.m.timestamp],
    );
  }

  private entryOrThrow(chatId: string, messageId: string): Entry {
    const e = this.store.find(chatId, messageId);
    if (!e) throw new BridgeError("not_found", "message not found in the local store");
    return e;
  }

  /** Record a message we just sent so the listener's echo merges instead of duplicating. */
  private recordSent(chatId: string, e: Entry): Message {
    const existing = this.store.find(chatId, e.m.id);
    if (existing) {
      this.mergeExisting(existing, e, { replay: false });
      return existing.m;
    }
    this.store.insert(chatId, e);
    const sc = this.store.chat(chatId);
    if (e.m.timestamp >= sc.timestamp) {
      sc.timestamp = e.m.timestamp;
      sc.unread = 0;
      sc.markedUnread = undefined;
    }
    this.store.touch();
    this.scheduleUpsert(chatId);
    return e.m;
  }

  async sendMessage(p: {
    chatId: string;
    text?: string;
    replyTo?: string;
    filePath?: string;
    mentions?: MentionSpan[];
  }): Promise<Message> {
    this.requireLogin();
    const api = this.api!;
    const { type, id } = parseChatId(p.chatId);
    const text = p.text ?? "";
    // Zalo only honours mentions in groups (zca-js drops them for 1:1 anyway); its pos/len are UTF-16 like ours.
    const mentions =
      type === ThreadType.Group
        ? sanitizeMentions(text, p.mentions).map((m) => ({ pos: m.offset, uid: m.userId, len: m.length }))
        : [];

    let quote: SendMessageQuote | undefined;
    if (p.replyTo) {
      const q = this.entryOrThrow(p.chatId, p.replyTo).z;
      if (q.content === undefined || q.content === null) {
        throw new BridgeError("unsupported", "Cannot reply to this message (its content is not available yet)");
      }
      quote = {
        content: q.content as SendMessageQuote["content"],
        msgType: q.msgType,
        propertyExt: q.propertyExt as SendMessageQuote["propertyExt"],
        uidFrom: q.uidFrom,
        msgId: q.msgId,
        cliMsgId: q.cliMsgId || "0",
        ts: q.ts,
        ttl: q.ttl,
      };
    }
    if (p.filePath && !existsSync(p.filePath)) throw new BridgeError("invalid_params", `file not found: ${p.filePath}`);

    const res = await api.sendMessage(
      {
        msg: text,
        quote,
        mentions: mentions.length ? mentions : undefined,
        attachments: p.filePath ? [p.filePath] : undefined,
      },
      id,
      type,
    );

    const now = Date.now();
    const base = (msgId: unknown, msgType: string, kind: Message["kind"], body?: string, media?: Media, content?: unknown): Entry => {
      const gid = str(msgId);
      const m: Message = {
        id: gid,
        chatId: p.chatId,
        senderId: this.selfId,
        senderName: this.self?.name,
        senderAvatar: this.self?.avatar,
        fromMe: true,
        timestamp: now,
        kind,
        text: body,
        media,
        replyTo: undefined,
        status: "sent",
      };
      const z: ZMeta = {
        t: type === ThreadType.Group ? 1 : 0,
        msgId: gid,
        cliMsgId: "",
        uidFrom: this.selfId,
        idTo: id,
        msgType,
        ts: String(now),
        ttl: 0,
        st: 0,
        at: 0,
        cmd: 0,
        content,
      };
      return { m, z };
    };

    let result: Message | undefined;
    if (res.message) {
      const e = base(res.message.msgId, "webchat", "text", text, undefined, text);
      if (p.replyTo && quote) {
        const q = this.store.find(p.chatId, p.replyTo)!;
        e.m.replyTo = { id: q.m.id, text: q.m.text, senderName: q.m.senderName, kind: q.m.kind };
      }
      result = this.recordSent(p.chatId, e);
    }
    if (p.filePath) {
      const kind = kindFromFile(p.filePath);
      const fileName = basename(p.filePath);
      for (const a of res.attachment ?? []) {
        const media: Media = { path: p.filePath, fileName, mime: mimeFromName(p.filePath) };
        const caption = !res.message && text ? text : kind === "file" ? fileName : undefined;
        const msgType = kind === "image" ? "chat.photo" : kind === "gif" ? "chat.gif" : kind === "video" ? "chat.video.msg" : "share.file";
        result = this.recordSent(p.chatId, base(a.msgId, msgType, kind, caption, media));
      }
    }
    if (!result) throw new BridgeError("internal", "Zalo accepted the request but returned no message id");
    return result;
  }

  async markRead(chatId: string): Promise<void> {
    this.requireLogin();
    const { type, id } = parseChatId(chatId);
    const sc = this.store.chat(chatId);
    const hadMark = !!sc.markedUnread;
    if (sc.unread || hadMark) {
      sc.unread = 0;
      sc.markedUnread = undefined;
      this.store.touch();
      this.emitChatNow(chatId);
    }
    if (hadMark) {
      // Opening a chat marked unread clears the flag on the account too (best effort, polled back otherwise).
      this.api!.removeUnreadMark(id, type).catch((e) => this.log("removeUnreadMark failed", errMsg(e)));
    }
    const incoming = this.store
      .list(chatId)
      .filter((e) => !e.m.fromMe && e.m.kind !== "system" && e.z.msgId && e.m.timestamp > (sc.seenSent ?? 0))
      .slice(-5);
    if (!incoming.length) return;
    const msgs: SendSeenEventMessageParams[] = incoming.map(({ z }) => ({
      msgId: z.msgId,
      cliMsgId: z.cliMsgId || "0",
      uidFrom: z.uidFrom,
      idTo: z.idTo,
      msgType: z.msgType,
      st: z.st,
      at: z.at,
      cmd: z.cmd,
      ts: z.ts,
    }));
    try {
      await this.api!.sendSeenEvent(msgs, type);
      sc.seenSent = incoming[incoming.length - 1]!.m.timestamp;
      this.store.touch();
    } catch (e) {
      // Local unread is already cleared; the next markRead retries the receipt.
      this.log("sendSeenEvent failed", errMsg(e));
    }
  }

  async muteChat(chatId: string, until: number): Promise<Chat> {
    this.requireLogin();
    const { type, id } = parseChatId(chatId);
    const now = Date.now();
    const off = until === 0 || (until > 0 && until <= now);
    // Zalo wants the remaining time in seconds (-1 = forever).
    const seconds = until === -1 ? -1 : Math.ceil((until - now) / 1000);
    const params = off ? { action: MuteAction.UNMUTE } : { duration: seconds, action: MuteAction.MUTE };
    await withTimeout(this.api!.setMute(params, id, type), 15_000, "setMute");
    const sc = this.store.chat(chatId);
    sc.muted = !off;
    sc.mutedUntil = off ? undefined : seconds === -1 ? -1 : now + seconds * 1000;
    this.muteWrites.set(chatId, Date.now());
    this.store.touch();
    this.emitChatNow(chatId);
    return this.buildChat(chatId);
  }

  async pinnedMessages(chatId: string): Promise<Message[]> {
    this.requireLogin();
    parseChatId(chatId);
    const pins = await this.pinsCache.getOrLoad(chatId, () => this.loadPins(chatId));
    const out: Message[] = [];
    for (const pin of pins) out.push(await this.pinMessage(chatId, pin));
    return out;
  }

  /** Group: the pinned-message items of the group board. 1:1: the friend board. */
  private async loadPins(chatId: string): Promise<PinTopic[]> {
    const api = this.api!;
    const { type, id } = parseChatId(chatId);
    for (let tries = 0; ; tries++) {
      const ver = this.pinsVer.get(chatId) ?? 0;
      let items: unknown[];
      if (type === ThreadType.Group) {
        const board = await withTimeout(api.getListBoard({ count: PINS_BOARD_COUNT }, id), 15_000, "getListBoard");
        items = (board.items ?? []).filter((it) => it.boardType === BoardType.PinnedMessage).map((it) => it.data);
      } else {
        items = (await withTimeout(api.getFriendBoardList(id), 15_000, "getFriendBoardList")).data ?? [];
      }
      // A pin event that came in while Zalo was answering may not be in this answer: ask once more.
      if ((this.pinsVer.get(chatId) ?? 0) === ver || tries >= 1) return parsePinTopics(items);
    }
  }

  /** The stored original when we have it (same id as messages.list), else a message built from the pin itself. */
  private async pinMessage(chatId: string, pin: PinTopic): Promise<Message> {
    const sender = pin.senderUid === "0" ? this.selfId : pin.senderUid;
    const byCli = this.store.findByCli(chatId, pin.cliMsgId);
    const stored =
      (pin.msgId ? this.store.find(chatId, pin.msgId) : undefined) ??
      (byCli && (!sender || byCli.m.senderId === sender) ? byCli : undefined); // cliMsgIds repeat across senders
    if (stored) return this.outbound(stored.m);

    const m = pinToMessage(pin, chatId, this.selfId);
    if (pin.mapped.sticker) {
      m.media = (await this.resolveSticker(pin.mapped.sticker.id)) ?? m.media;
      m.text = m.media?.url ? undefined : "[Sticker]";
    }
    m.senderName = this.nameOf(m.senderId) ?? m.senderName;
    m.senderAvatar = m.fromMe ? this.self?.avatar : this.store.contacts[m.senderId]?.avatar;
    return m;
  }

  /** The pins of a chat changed (event from Zalo): forget what we know and tell the app. */
  private pinsChanged(chatId: string) {
    this.pinsVer.set(chatId, (this.pinsVer.get(chatId) ?? 0) + 1);
    this.pinsCache.delete(chatId);
    this.ctx.emit("pins.update", { chatId });
  }

  async react(chatId: string, messageId: string, emoji: string): Promise<void> {
    this.requireLogin();
    const { type, id } = parseChatId(chatId);
    const e = this.entryOrThrow(chatId, messageId);
    const icon = emojiToIcon(emoji);
    if (icon === undefined) {
      throw new BridgeError("invalid_params", `"${emoji}" is not a Zalo reaction (try ❤️ 👍 😆 😮 😭 😡)`);
    }
    await this.api!.addReaction(icon, {
      data: { msgId: e.z.msgId, cliMsgId: e.z.cliMsgId || "0" },
      threadId: id,
      type,
    });
    this.setReaction(e, this.selfId, icon === Reactions.NONE ? "" : icon);
  }

  async deleteMessage(chatId: string, messageId: string, forEveryone: boolean): Promise<void> {
    this.requireLogin();
    const { type, id } = parseChatId(chatId);
    const e = this.entryOrThrow(chatId, messageId);
    const api = this.api!;
    const cliMsgId = e.z.cliMsgId || "0";

    if (forEveryone) {
      if (e.m.fromMe) {
        await api.undo({ msgId: e.z.msgId, cliMsgId }, id, type);
        this.markDeleted(e);
        this.ctx.emit("message.update", { message: e.m });
        this.scheduleUpsert(chatId);
        return;
      }
      if (type !== ThreadType.Group) {
        throw new BridgeError("unsupported", "Zalo cannot delete someone else's message for everyone in a private chat");
      }
      // Group admins may remove other members' messages.
      await api.deleteMessage({ data: { cliMsgId, msgId: e.z.msgId, uidFrom: e.z.uidFrom }, threadId: id, type }, false);
    } else {
      await api.deleteMessage({ data: { cliMsgId, msgId: e.z.msgId, uidFrom: e.z.uidFrom }, threadId: id, type }, true);
    }
    this.store.remove(chatId, messageId);
    this.ctx.emit("message.delete", { chatId, messageId });
    this.scheduleUpsert(chatId);
  }

  // ===================================================================== storage (Settings -> Data and Storage)

  async configureStorage(config: StorageConfig): Promise<void> {
    this.storage = { ...config };
    this.store.setMaxPerChat(config.maxStoredMessagesPerChat);
    this.forgetPaths(mediaCache.prune(this.mediaDir, this.storage).deleted);
  }

  async storageUsage() {
    return {
      ...mediaCache.usage(this.mediaDir),
      databaseBytes: this.store.diskBytes + mediaCache.treeSize(this.credsPath),
    };
  }

  async clearStorage(olderThanDays?: number): Promise<number> {
    const r = mediaCache.clear(this.mediaDir, olderThanDays);
    this.forgetPaths(r.deleted);
    return r.freedBytes;
  }

  /** Cached files were deleted: messages must not point at them any more (the app falls back to the cloud url). */
  private forgetPaths(deleted: string[]) {
    if (!deleted.length) return;
    const gone = new Set(deleted);
    let n = 0;
    for (const chatId of this.store.chatIds()) {
      for (const e of this.store.list(chatId)) {
        const m = e.m.media;
        if (m?.path && gone.has(m.path)) {
          m.path = undefined;
          n++;
        }
      }
    }
    if (n) this.store.touch();
  }

  /**
   * media.download. Zalo media normally never needs this: `media.url` is a CDN link the app loads itself. This is for
   * "save to disk"/open-in-app requests, so it is cached under DIR/media and subject to StorageConfig.
   */
  async downloadMedia(chatId: string, messageId: string): Promise<string> {
    this.requireLogin();
    return mediaCache.inflight(this.downloads, `${chatId}|${messageId}`, () => this.fetchMedia(chatId, messageId));
  }

  private async fetchMedia(chatId: string, messageId: string): Promise<string> {
    const e = this.entryOrThrow(chatId, messageId);
    const media = e.m.media;
    if (media?.path && existsSync(media.path)) {
      mediaCache.touch(media.path);
      return media.path;
    }
    const url = media?.url;
    if (!media || !url || e.m.kind === "link" || e.m.kind === "location" || e.m.kind === "contact") {
      throw new BridgeError("unsupported", "this message has no downloadable media");
    }
    mkdirSync(this.mediaDir, { recursive: true });
    // Make room first (age + size cap), leaving space for this file when its size is known.
    this.forgetPaths(mediaCache.prune(this.mediaDir, this.storage, media.size ?? 0).deleted);

    const headers: Record<string, string> = { "user-agent": this.creds?.userAgent ?? DEFAULT_UA, referer: "https://chat.zalo.me/" };
    let host = "";
    try {
      host = new URL(url).hostname;
    } catch {
      throw new BridgeError("invalid_params", "invalid media url");
    }
    // Only hand the Zalo session cookie to Zalo's own hosts.
    if (this.api && /(^|\.)(zalo\.me|zdn\.vn|zadn\.vn)$/.test(host)) {
      const cookie = this.api.getCookie().getCookieStringSync(`https://${host}`);
      if (cookie) headers.cookie = cookie;
    }
    const res = await fetch(url, { headers, signal: AbortSignal.timeout(10 * 60_000) });
    if (!res.ok || !res.body) throw new BridgeError("internal", `download failed: HTTP ${res.status}`);

    const ctype = res.headers.get("content-type")?.split(";")[0]?.trim();
    const extFromType = Object.entries(MIME_EXT).find(([, m]) => m === ctype)?.[0];
    let ext = media.fileName ? extname(media.fileName) : "";
    if (!ext) ext = extname(new URL(url).pathname);
    if (!ext && extFromType) ext = "." + extFromType;
    if (!ext) ext = ".bin";
    const safeId = messageId.replace(/[^\w.-]/g, "_");
    const safeName = media.fileName ? "_" + basename(media.fileName).replace(/[^\w.\- ]/g, "_") : "";
    const out = join(this.mediaDir, `${safeId}${safeName.endsWith(ext) ? safeName : safeName + ext}`);
    const tmp = out + ".part";
    try {
      await pipeline(Readable.fromWeb(res.body as any), createWriteStream(tmp));
      renameSync(tmp, out);
    } catch (err) {
      rmSync(tmp, { force: true });
      throw new BridgeError("internal", `download failed: ${errMsg(err)}`);
    }

    // Photos only (not gif/sticker/video): HEIC <= 1600 px when it is really smaller. Failure keeps the original.
    const final = e.m.kind === "image" ? await mediaCache.compressImage(out, this.storage) : out;
    if (final !== out) media.mime = "image/heic";
    media.path = final;
    this.store.touch();
    return final;
  }

  async sendTyping(chatId: string, typing: boolean): Promise<void> {
    if (!typing || this.state !== "logged_in" || !this.api) return; // Zalo has no "stopped typing" event
    const { type, id } = parseChatId(chatId);
    const last = this.typingSentAt.get(chatId) ?? 0;
    if (Date.now() - last < 4000) return;
    this.typingSentAt.set(chatId, Date.now());
    try {
      await this.api.sendTypingEvent(id, type);
    } catch (e) {
      this.log("sendTypingEvent failed", errMsg(e));
    }
  }
}

/** content-type -> extension for downloads whose url has none */
const MIME_EXT: Record<string, string> = {
  jpg: "image/jpeg",
  png: "image/png",
  gif: "image/gif",
  webp: "image/webp",
  mp4: "video/mp4",
  m4a: "audio/mp4",
  aac: "audio/aac",
  mp3: "audio/mpeg",
  pdf: "application/pdf",
};
