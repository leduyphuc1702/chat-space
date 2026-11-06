// Telegram adapter (MTProto via GramJS). See docs/PROTOCOL.md for the wire contract.
import { execFile } from "node:child_process";
import {
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readFileSync,
  readSync,
  renameSync,
  rmSync,
  statSync,
  utimesSync,
  writeFileSync,
  writeSync,
  type Stats,
} from "node:fs";
import { lstat, readdir, unlink } from "node:fs/promises";
import { extname, join } from "node:path";
import { promisify } from "node:util";
import { toDataURL } from "qrcode";
import { Api, TelegramClient, helpers, utils } from "telegram";
import { NewMessage, Raw } from "telegram/events";
import type { NewMessageEvent } from "telegram/events";
import { Logger, LogLevel } from "telegram/extensions/Logger";
import { UpdateConnectionState } from "telegram/network";
import { StringSession } from "telegram/sessions";
import { Adapter, type AdapterContext } from "./adapter.ts";
import { Matcher, newestFirst, type SearchParams } from "./search.ts";
import { channelPts, commonPts, judgePts, parseSavedState, type SavedUpdateState, type UpdateState } from "./telegram-updates.ts";
import { sanitizeMentions, TtlCache } from "./mentions.ts";
import {
  BridgeError,
  isFresh,
  LIVE_MAX_AGE_MS,
  type ButtonAnswer,
  type Chat,
  type Folder,
  type Member,
  type MentionSpan,
  type Message,
  type SelfInfo,
  type StorageConfig,
} from "./types.ts";
import {
  AlbumClock,
  buildChat,
  buildMentionEntities,
  entityName,
  hasProfilePhoto,
  isMinEntity,
  isPinNotice,
  mapFolders,
  mapMessage,
  mapReactions,
  MENTION_REJECTED,
  muteInfo,
  muteUntilSeconds,
  notifyScopeOf,
  peerKey,
  reactionEmoticonForTelegram,
  ReplyResolver,
  searchFilters,
  userPresence,
  type Entity,
  type MapEnv,
  type NotifyScope,
} from "./telegram-map.ts";

const MISSING_CREDS = "Telegram API ID/hash missing \u2014 get them at https://my.telegram.org";
const AVATAR_TTL_MS = 3 * 24 * 3600 * 1000;
const TYPING_TTL_MS = 6000;
const VOICE_EXT = new Set([".ogg", ".oga", ".opus"]);

/**
 * GramJS re-exports the login token every 30 s. We show at most this many unscanned codes (~5 min) before giving
 * up with auth.state logged_out / "qr_expired" (the app then restarts the flow while the QR screen is visible).
 */
const QR_MAX_TOKENS = 10;
/** How long past a token's expiry we wait for its replacement before declaring the flow stuck. */
const QR_GRACE_MS = 20_000;
/** After the QR was scanned, the login must finish (DC migration, key import) within this time. */
const QR_SCANNED_TIMEOUT_MS = 60_000;
/** Archived chats are listed in addition to the requested number of main-list chats, up to this many. */
const ARCHIVE_LIST_LIMIT = 200;
const DAY_MS = 86_400_000;
/** chats.members answers are reused this long per chat. */
const MEMBERS_TTL_MS = 5 * 60_000;
/** Participants fetched per chat for the @mention picker (GramJS pages 200 at a time). */
const MEMBERS_LIMIT = 200;
/** Pinned messages fetched per chat. */
const PINS_LIMIT = 100;
/** Telegram answers at most this many messages per messages.search / searchGlobal request. */
const SEARCH_PAGE_LIMIT = 100;
/** Server pages read for one search request and filter when results are narrowed afterwards. */
const SEARCH_MAX_PAGES = 5;
/**
 * updates.getDifference is asked to give up (DifferenceTooLong) past this many pending updates; the chat list is
 * then re-read instead, like the official apps do after a long absence. Below it every missed message is delivered
 * one by one (message.new), which keeps open chats current and notifies for the recent ones.
 */
const DIFFERENCE_TOTAL_LIMIT = 500;
/** Pages of updates.getChannelDifference (100 messages each) per catch-up of one channel. */
const CHANNEL_DIFFERENCE_PAGES = 5;
/** How long an out-of-order update may take to fill a pts gap before getDifference is asked to. */
const GAP_GRACE_MS = 600;
/** connection.resume: a request that is not answered within this time means the socket died while the computer slept. */
const RESUME_PROBE_MS = 4000;
/** The update state file is written at most this often while the stream is busy (and always on shutdown). */
const STATE_SAVE_DELAY_MS = 10_000;
const execFileP = promisify(execFile);

// ------------------------------------------------------------------ small utils

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

function deferred<T = void>() {
  let resolve!: (v: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

function safeStat(path: string) {
  try {
    return statSync(path);
  } catch {
    return undefined;
  }
}

function rmSafe(path: string) {
  try {
    rmSync(path, { force: true });
  } catch {
    /* ignore */
  }
}

function atomicWrite(path: string, data: string | Uint8Array, mode: number) {
  const tmp = `${path}.${process.pid}.tmp`;
  writeFileSync(tmp, data, { mode });
  renameSync(tmp, path);
}

const clamp = (n: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, Math.floor(n) || lo));

// ------------------------------------------------------------------ media cache helpers

interface MediaEntry {
  size: number;
  /** Last use in ms: newest of atime/mtime (atime is touched on every cache hit, and macOS may not update it). */
  used: number;
}

/** In-flight downloads and compression leave `.part` / `.tmp` files behind; they are never cache entries. */
const isTempName = (path: string) => /\.(part|tmp)(\.|$)/.test(path);

/** `/a/b/1_2.jpg` -> `/a/b/1_2.heic` */
const withExt = (path: string, ext: string) => path.slice(0, path.length - extname(path).length) + ext;

/** Every regular file below `dir` (symlinks skipped). A missing directory is simply empty. */
async function walkFiles(dir: string): Promise<Array<{ path: string; size: number; atimeMs: number; mtimeMs: number }>> {
  const out: Array<{ path: string; size: number; atimeMs: number; mtimeMs: number }> = [];
  let entries;
  try {
    entries = await readdir(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    const p = join(dir, e.name);
    if (e.isDirectory()) {
      out.push(...(await walkFiles(p)));
    } else if (e.isFile()) {
      try {
        const st = await lstat(p);
        out.push({ path: p, size: st.size, atimeMs: st.atimeMs, mtimeMs: st.mtimeMs });
      } catch {
        /* vanished while scanning */
      }
    }
  }
  return out;
}

/** What `sips` can usefully re-encode, judged by magic bytes. PNGs with an alpha channel are left alone. */
function sniffCompressible(path: string): "jpeg" | "png" | undefined {
  let fd: number | undefined;
  try {
    fd = openSync(path, "r");
    const b = Buffer.alloc(26);
    const n = readSync(fd, b, 0, 26, 0);
    if (n >= 3 && b[0] === 0xff && b[1] === 0xd8) return "jpeg";
    if (n >= 26 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47) {
      const colorType = b[25]; // IHDR colour type: 4 = grey+alpha, 6 = RGBA
      return colorType === 4 || colorType === 6 ? undefined : "png";
    }
  } catch {
    /* unreadable: leave it alone */
  } finally {
    if (fd !== undefined) closeSync(fd);
  }
  return undefined;
}

/** Best guess of the download size, used to make room in the cache before fetching. 0 = unknown. */
function mediaSizeHint(raw: Api.Message): number {
  const m = raw.media;
  if (m instanceof Api.MessageMediaDocument && m.document instanceof Api.Document) {
    return Number(String(m.document.size)) || 0;
  }
  if (m instanceof Api.MessageMediaPhoto && m.photo instanceof Api.Photo) {
    let best = 0;
    for (const s of m.photo.sizes) {
      if (s instanceof Api.PhotoSize) best = Math.max(best, s.size);
      else if (s instanceof Api.PhotoSizeProgressive) best = Math.max(best, ...s.sizes);
    }
    return best;
  }
  return 0;
}

/** Photos, and documents that are plain JPEG/PNG images, may be re-encoded when `compressImages` is on. */
function isCompressibleMedia(raw: Api.Message): boolean {
  const m = raw.media;
  if (m instanceof Api.MessageMediaPhoto) return true;
  return (
    m instanceof Api.MessageMediaDocument &&
    m.document instanceof Api.Document &&
    /^image\/(jpeg|png)$/i.test(m.document.mimeType)
  );
}

function parseMsgId(v: string): number {
  const n = Number(v);
  if (!Number.isInteger(n) || n <= 0) throw new BridgeError("invalid_params", `invalid message id: ${v}`);
  return n;
}

/** Map that evicts its oldest entry once `max` is exceeded. */
class BoundedMap<K, V> extends Map<K, V> {
  constructor(private readonly max: number) {
    super();
  }
  override set(key: K, value: V): this {
    if (super.has(key)) super.delete(key);
    super.set(key, value);
    if (this.size > this.max) {
      const oldest = this.keys().next();
      if (!oldest.done) super.delete(oldest.value);
    }
    return this;
  }
}

class Limiter {
  private active = 0;
  private queue: Array<() => void> = [];
  constructor(private readonly max: number) {}
  async run<T>(fn: () => Promise<T>): Promise<T> {
    if (this.active >= this.max) await new Promise<void>((r) => this.queue.push(r));
    this.active++;
    try {
      return await fn();
    } finally {
      this.active--;
      this.queue.shift()?.();
    }
  }
}

/** GramJS logs through console.log by default; force everything to the bridge's stderr log. */
class StderrLogger extends Logger {
  constructor(
    level: LogLevel,
    private readonly sink: (...args: unknown[]) => void,
  ) {
    super(level);
  }
  override log(level: LogLevel, message: string, _color: string): void {
    this.sink(`[gramjs:${level}] ${message}`);
  }
}

// ------------------------------------------------------------------ error helpers

const AUTH_ERRORS = new Set([
  "AUTH_KEY_UNREGISTERED",
  "AUTH_KEY_INVALID",
  "AUTH_KEY_PERM_EMPTY",
  "SESSION_REVOKED",
  "SESSION_EXPIRED",
  "USER_DEACTIVATED",
  "USER_DEACTIVATED_BAN",
]);

const NOT_FOUND_ERRORS = new Set([
  "PEER_ID_INVALID",
  "CHANNEL_INVALID",
  "CHANNEL_PRIVATE",
  "CHAT_ID_INVALID",
  "USER_ID_INVALID",
  "MESSAGE_ID_INVALID",
  "MSG_ID_INVALID",
  "INPUT_USER_DEACTIVATED",
  "USERNAME_NOT_OCCUPIED",
]);

const INVALID_ERRORS = new Set([
  "MESSAGE_EMPTY",
  "MESSAGE_TOO_LONG",
  "REACTION_INVALID",
  "REACTION_EMPTY",
  "MEDIA_CAPTION_TOO_LONG",
  "MEDIA_EMPTY",
  "FILE_PARTS_INVALID",
  "IMAGE_PROCESS_FAILED",
  "PHOTO_INVALID_DIMENSIONS",
  "PHONE_NUMBER_INVALID",
  "PHONE_NUMBER_EMPTY",
  "PHONE_NUMBER_BANNED",
  "API_ID_INVALID",
  "MESSAGE_EDIT_TIME_EXPIRED",
  "MESSAGE_AUTHOR_REQUIRED",
  "MESSAGE_NOT_MODIFIED",
]);

const FRIENDLY: Record<string, string> = {
  PHONE_NUMBER_INVALID: "The phone number is invalid.",
  PHONE_NUMBER_BANNED: "This phone number is banned from Telegram.",
  PHONE_NUMBER_FLOOD: "Too many login attempts with this number. Try again later.",
  API_ID_INVALID: "The Telegram API ID/hash is invalid \u2014 check them at https://my.telegram.org",
  API_ID_PUBLISHED_FLOOD:
    "This API ID has been rate-limited by Telegram. Use your own API ID from https://my.telegram.org",
  CHAT_WRITE_FORBIDDEN: "You can't send messages in this chat.",
  USER_IS_BLOCKED: "This user has blocked you.",
  MESSAGE_TOO_LONG: "The message is too long (4096 characters max).",
  MEDIA_CAPTION_TOO_LONG: "The caption is too long (1024 characters max).",
  REACTION_INVALID: "This reaction is not available in this chat.",
  MESSAGE_EDIT_TIME_EXPIRED: "This message can no longer be edited (48 hours have passed).",
  MESSAGE_AUTHOR_REQUIRED: "You can only edit your own messages.",
  MESSAGE_NOT_MODIFIED: "The message is unchanged.",
};

function rpcMessage(e: unknown): string {
  const m = (e as { errorMessage?: unknown } | null)?.errorMessage;
  return typeof m === "string" ? m : "";
}

function floodSeconds(e: unknown): number | undefined {
  const s = (e as { seconds?: unknown } | null)?.seconds;
  return typeof s === "number" && s > 0 ? s : undefined;
}

function isAuthError(e: unknown): boolean {
  const m = rpcMessage(e);
  if (m === "SESSION_PASSWORD_NEEDED") return false;
  return AUTH_ERRORS.has(m) || (e as { code?: unknown } | null)?.code === 401;
}

function describeError(e: unknown): string {
  const wait = floodSeconds(e);
  if (wait !== undefined) return `Telegram rate limit: try again in ${wait}s.`;
  const m = rpcMessage(e);
  if (m && FRIENDLY[m]) return FRIENDLY[m];
  if (m) return m;
  return (e as { message?: string } | null)?.message ?? String(e);
}

// ------------------------------------------------------------------ login state

interface Creds {
  apiId: number;
  apiHash: string;
}

/** One in-flight login attempt (QR or phone). */
class LoginRun {
  cancelled = false;
  passwordPhase = false;
  pending?: { kind: "code" | "password"; resolve: (v: string) => void };
  queuedPassword?: string;
  promptError?: string;
  fatal?: string;
  failure?: BridgeError;
  /** QR flow: tokens emitted so far, whether the user scanned one, and the "stuck / gave up" watchdog. */
  qrCount = 0;
  scanned = false;
  qrTimer?: ReturnType<typeof setTimeout>;
  /** Resolves on the first thing the caller can act on (QR shown, code requested) or on failure. */
  readonly first = deferred<void>();
  private waiters: Array<() => void> = [];

  stopQrTimer() {
    if (this.qrTimer) clearTimeout(this.qrTimer);
    this.qrTimer = undefined;
  }

  takePromptError(): string | undefined {
    const e = this.promptError;
    this.promptError = undefined;
    return e;
  }
  waitTransition(): Promise<void> {
    return new Promise((r) => this.waiters.push(r));
  }
  fire() {
    const w = this.waiters;
    this.waiters = [];
    for (const f of w) f();
  }
}

type RawMessage = Api.TypeMessage;
type RawUpdate = { _entities?: Map<string, Entity> };

// ------------------------------------------------------------------ adapter

export class TelegramAdapter extends Adapter {
  readonly platform = "telegram";
  // "edit" is deliberately absent: the protocol has no edit method yet.
  readonly capabilities = ["reactions", "reply", "delete", "edit", "typing", "read_receipts", "media_send", "voice", "folders", "mentions", "forward", "search", "mute", "pins", "bot_buttons", "direct_chat"];

  private creds?: Creds;
  private client?: TelegramClient;
  private clientFresh = false;
  private clientDirty = false;
  private bootId = 0;
  /** Bumped by every auth.login; a call that finds it changed was superseded and must not touch shared state. */
  private loginTicket = 0;
  private stopped = false;
  private run?: LoginRun;
  private connState?: string;
  private lastSavedSession = "";
  private saveTimer?: ReturnType<typeof setInterval>;

  private readonly entities = new Map<string, Entity>();
  private readonly inputPeers = new Map<string, Api.TypeInputPeer>();
  private readonly failedEntities = new Set<string>();
  private readonly chats = new Map<string, Chat>();
  /** Per-peer notify settings as last reported by the server (mute can be inherited from the scope default). */
  private readonly notifyOwn = new Map<string, Api.TypePeerNotifySettings>();
  /** Account-wide notify defaults: all private chats / all groups / all channels. */
  private readonly notifyScope = new Map<NotifyScope, Api.TypePeerNotifySettings>();
  private readonly chatScope = new Map<string, NotifyScope>();
  /** Incoming messages are handled strictly one at a time: unread counting and event order depend on it. */
  private newQueue: Promise<void> = Promise.resolve();
  private readonly readOutbox = new Map<string, number>();
  /** Recently seen messages keyed "chatId:messageId" (replies, reactions, read receipts). */
  private readonly recent = new BoundedMap<string, Message>(5000);
  /** Non-channel message ids are account-global, so deletions can be attributed to a chat. */
  private readonly idToChat = new BoundedMap<number, string>(50000);
  private readonly sentKeys = new BoundedMap<string, true>(500);
  /** Keeps the items of an album in sequence (they share one `date` in whole seconds). */
  private readonly albums = new AlbumClock();
  /** Fills `replyTo` of mapped messages: from the page, from `recent`, from past lookups, else one batched fetch. */
  private readonly replies = new ReplyResolver({
    recent: (chatId, id) => this.recent.get(`${chatId}:${id}`),
    fetch: (chatId, ids) => this.fetchReplyTargets(chatId, ids),
    log: (m) => this.ctx.log(m),
  });
  private readonly typingTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly avatarInflight = new Map<string, Promise<string | undefined>>();
  private readonly avatarSlots = new Limiter(4);
  private readonly downloadInflight = new Map<string, Promise<string>>();
  private readonly membersCache = new TtlCache<Member[]>(MEMBERS_TTL_MS);
  /** chats.pins answers per chat, kept until a pin change (or an edit/delete of a pinned message) drops them. */
  private readonly pinsCache = new BoundedMap<string, Message[]>(100);
  /** Bumped by every invalidation, so a fetch that started before one does not cache its stale answer. */
  private pinsEpoch = 0;

  /** Chat folders as last fetched, the JSON last emitted (change detection) and the refetch debounce. */
  private folders: Folder[] = [];
  private foldersJson = "";
  private foldersTimer?: ReturnType<typeof setTimeout>;
  private foldersForce = false;
  /** Re-announce folders once the first chats.list of a session has loaded the dialogs. */
  private foldersAfterChats = false;

  /**
   * Position in Telegram's update stream (updates.getState), kept in step with every live update and persisted, so a
   * reconnect or restart can ask updates.getDifference for exactly what was missed instead of waiting for the user
   * to open a chat. Channels each have their own pts (`chanPts`, by chat id) and updates.getChannelDifference.
   */
  private updState?: UpdateState;
  private readonly chanPts = new Map<string, number>();
  /** When the stream last delivered something: messages after that moment arrived while we were cut off. */
  private streamAliveAt = 0;
  private stateDirty = false;
  private stateTimer?: ReturnType<typeof setTimeout>;
  /** One getDifference run at a time; a trigger during a run makes it go round once more. */
  private catchUpJob?: Promise<void>;
  private catchUpAgain = false;
  private gapTimer?: ReturnType<typeof setTimeout>;
  private gapTarget = 0;
  private readonly channelJobs = new Map<string, Promise<void>>();
  private readonly channelAgain = new Set<string>();
  private readonly channelGapTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly channelGapTargets = new Map<string, number>();

  /** Storage policy pushed by the app. Until it arrives nothing is pruned and nothing is re-encoded. */
  private storage: StorageConfig = { keepMediaDays: 0, maxCacheMB: 0, compressImages: false, maxStoredMessagesPerChat: 0 };
  /** In-memory view of DIR/media (path -> size, last use); rebuilt from disk by every full scan. */
  private mediaIndex?: Map<string, MediaEntry>;
  /** Files an in-flight download may still write; the pruner must not delete them. */
  private readonly busyMedia = new Set<string>();
  private storageChain: Promise<unknown> = Promise.resolve();
  private sipsMissing = false;

  constructor(ctx: AdapterContext) {
    super(ctx);
  }

  // -------------------------------------------------------------- paths

  private get sessionFile() {
    return join(this.ctx.dataDir, "session.txt");
  }
  private get apiFile() {
    return join(this.ctx.dataDir, "api.json");
  }
  private get avatarDir() {
    return join(this.ctx.dataDir, "avatars");
  }
  private get mediaDir() {
    return join(this.ctx.dataDir, "media");
  }

  // -------------------------------------------------------------- credentials & session files

  private resolveCreds(config?: Record<string, unknown>): Creds | undefined {
    const parse = (id: unknown, hash: unknown): Creds | undefined => {
      const n =
        typeof id === "number" ? id : typeof id === "string" && /^\d+$/.test(id.trim()) ? Number(id.trim()) : NaN;
      const h = typeof hash === "string" ? hash.trim() : "";
      return Number.isInteger(n) && n > 0 && h ? { apiId: n, apiHash: h } : undefined;
    };

    const fromConfig = parse(config?.apiId, config?.apiHash);
    if (fromConfig) {
      this.saveCreds(fromConfig);
      return fromConfig;
    }
    const fromEnv = parse(process.env.TELEGRAM_API_ID, process.env.TELEGRAM_API_HASH);
    if (fromEnv) return fromEnv;
    try {
      const j = JSON.parse(readFileSync(this.apiFile, "utf8")) as Record<string, unknown>;
      return parse(j.apiId, j.apiHash);
    } catch {
      return undefined;
    }
  }

  private saveCreds(c: Creds) {
    try {
      const cur = JSON.parse(readFileSync(this.apiFile, "utf8")) as Record<string, unknown>;
      if (Number(cur.apiId) === c.apiId && cur.apiHash === c.apiHash) return;
    } catch {
      /* no file yet */
    }
    try {
      atomicWrite(this.apiFile, JSON.stringify({ apiId: c.apiId, apiHash: c.apiHash }), 0o600);
    } catch (e) {
      this.ctx.log("could not save api.json", e);
    }
  }

  private loadSession(): string {
    try {
      return readFileSync(this.sessionFile, "utf8").trim();
    } catch {
      return "";
    }
  }

  /** Persist the (possibly changed) auth key / DC. Only ever called for an authorized client. */
  private saveSession() {
    if (!this.client || !this.self) return;
    try {
      const s = (this.client.session as StringSession).save();
      if (!s || s === this.lastSavedSession) return;
      atomicWrite(this.sessionFile, s, 0o600);
      this.lastSavedSession = s;
    } catch (e) {
      this.ctx.log("could not save session", e);
    }
  }

  // -------------------------------------------------------------- client lifecycle

  private createClient(sessionString: string): TelegramClient {
    const creds = this.creds;
    if (!creds) throw new BridgeError("invalid_params", MISSING_CREDS);

    let session: StringSession;
    try {
      session = new StringSession(sessionString);
    } catch {
      this.ctx.log("saved Telegram session is corrupt; ignoring it");
      rmSafe(this.sessionFile);
      sessionString = "";
      session = new StringSession("");
    }

    const client = new TelegramClient(session, creds.apiId, creds.apiHash, {
      connectionRetries: 5,
      retryDelay: 1000,
      autoReconnect: true,
      deviceModel: "ChatSpace (macOS)",
      appVersion: "0.1.0",
      langCode: "en",
      systemLangCode: "en",
      baseLogger: new StderrLogger(LogLevel.ERROR, this.ctx.log),
    });
    client.setLogLevel(LogLevel.ERROR);
    // GramJS parses outgoing text as Markdown by default; users expect their text sent verbatim.
    client.setParseMode(undefined);
    // Updates that come back as the result of a request (our own sends, forwards, reads) are never pushed again on
    // the stream, yet they move the account's pts: GramJS does not hand them to update handlers, so look at every
    // result here. Otherwise each message we send would look like a gap to fill.
    const invoke = client.invoke.bind(client);
    client.invoke = (async (request: Api.AnyRequest, dcId?: number) => {
      const result = await invoke(request as never, dcId);
      this.noteResultUpdates(result);
      return result;
    }) as typeof client.invoke;

    client.addEventHandler(
      (e: NewMessageEvent) => this.safely("new message", () => this.onNewMessage(e)),
      new NewMessage({}),
    );
    client.addEventHandler((u: unknown) => this.safely("update", () => this.onUpdate(u)), new Raw({}));

    this.clientFresh = !sessionString;
    this.clientDirty = false;
    return client;
  }

  private async destroyClient() {
    const c = this.client;
    this.client = undefined;
    this.clientDirty = false;
    if (!c) return;
    try {
      await Promise.race([c.destroy(), sleep(3000)]);
    } catch {
      /* already gone */
    }
  }

  private safely(label: string, fn: () => Promise<void>) {
    fn().catch((e) => this.ctx.log(`telegram ${label} handler failed: ${describeError(e)}`, (e as Error)?.stack ?? ""));
  }

  private emitConnection(state: "connecting" | "online" | "offline", detail?: string) {
    if (this.connState === state && !detail) return;
    this.connState = state;
    this.ctx.emit("connection", detail ? { state, detail } : { state });
  }

  private resetCaches() {
    this.entities.clear();
    this.inputPeers.clear();
    this.failedEntities.clear();
    this.chats.clear();
    this.notifyOwn.clear();
    this.notifyScope.clear();
    this.chatScope.clear();
    this.readOutbox.clear();
    this.recent.clear();
    this.idToChat.clear();
    this.sentKeys.clear();
    this.replies.clear();
    this.membersCache.clear();
    this.pinsCache.clear();
    this.pinsEpoch++;
    for (const t of this.typingTimers.values()) clearTimeout(t);
    this.typingTimers.clear();
    this.avatarInflight.clear();
    if (this.foldersTimer) clearTimeout(this.foldersTimer);
    this.foldersTimer = undefined;
    this.foldersForce = false;
    this.foldersAfterChats = false;
    this.folders = [];
    this.foldersJson = "";
    this.lastSavedSession = "";
    this.updState = undefined;
    this.chanPts.clear();
    this.stateDirty = false;
    if (this.stateTimer) clearTimeout(this.stateTimer);
    this.stateTimer = undefined;
    if (this.gapTimer) clearTimeout(this.gapTimer);
    this.gapTimer = undefined;
    this.gapTarget = 0;
    this.catchUpAgain = false;
    for (const t of this.channelGapTimers.values()) clearTimeout(t);
    this.channelGapTimers.clear();
    this.channelGapTargets.clear();
    this.channelAgain.clear();
  }

  // -------------------------------------------------------------- start / restore

  async start(config: Record<string, unknown>): Promise<void> {
    // Set before yielding: an auth.status answered in between must not say logged_out, or the app shows the QR
    // screen, whose auth.login aborts the session restore.
    this.state = "connecting";
    // Let the bridge.hello response go out before we emit anything.
    await sleep(0);
    for (const d of [this.avatarDir, this.mediaDir]) mkdirSync(d, { recursive: true });
    void this.sweepTempMedia(); // leftovers of a crashed download/compression; nothing can be in flight yet

    const creds = this.resolveCreds(config);
    if (!creds) {
      this.setState("error", { error: MISSING_CREDS });
      return;
    }
    this.creds = creds;
    await this.restore();
  }

  /** Connect with the saved session (if any). Retries while the network is down. */
  private async restore(): Promise<void> {
    const boot = ++this.bootId;
    this.setState("connecting");
    const saved = this.loadSession();

    for (let attempt = 0; !this.stopped && boot === this.bootId; attempt++) {
      try {
        const client = this.createClient(saved);
        this.client = client;
        this.emitConnection("connecting");
        await client.connect();
        if (boot !== this.bootId) return; // login()/logout() took over and owns the client now
        this.emitConnection("online");

        let me: Api.User | undefined;
        try {
          me = await client.getMe();
        } catch (e) {
          if (!isAuthError(e)) throw e;
        }
        if (boot !== this.bootId) return;

        if (me) {
          await this.onAuthorized(me);
        } else {
          if (saved) {
            this.ctx.log("saved Telegram session is no longer valid");
            rmSafe(this.sessionFile);
          }
          this.setState("logged_out");
        }
        return;
      } catch (e) {
        if (boot !== this.bootId) return;
        const detail = describeError(e);
        if (rpcMessage(e) === "API_ID_INVALID" || rpcMessage(e) === "API_ID_PUBLISHED_FLOOD") {
          await this.destroyClient();
          this.setState("error", { error: FRIENDLY[rpcMessage(e)] });
          return;
        }
        this.ctx.log(`telegram connect failed (attempt ${attempt + 1}): ${detail}`);
        this.emitConnection("offline", detail);
        await this.destroyClient();
        await sleep(Math.min(30_000, 2000 * 2 ** Math.min(attempt, 4)));
      }
    }
  }

  private async onAuthorized(me: Api.User) {
    this.rememberEntity(me);
    this.self = this.selfInfo(me);
    this.saveSession();
    if (!this.saveTimer) {
      this.saveTimer = setInterval(() => {
        this.saveSession();
        if (this.stateDirty) this.saveUpdateState();
      }, 60_000);
      this.saveTimer.unref?.();
    }
    // Mute can be inherited from "all private chats / groups / channels": know the defaults before chats.list runs.
    await Promise.race([this.loadNotifyScopes(), sleep(4000)]);
    this.foldersAfterChats = true; // chats.list re-announces the folders once the dialogs are loaded
    this.setState("logged_in");
    // Subscribing to updates: the server only streams them after an authorized request. With a saved stream
    // position that request is getDifference, which also brings in whatever arrived while the bridge was away.
    this.loadUpdateState(this.self.id);
    void this.catchUp("connect");
    this.scheduleFolders(300, true);
    void this.refreshSelfAvatar();
  }

  private async loadNotifyScopes() {
    const client = this.client;
    if (!client) return;
    const scopes: [NotifyScope, Api.TypeInputNotifyPeer][] = [
      ["users", new Api.InputNotifyUsers()],
      ["chats", new Api.InputNotifyChats()],
      ["broadcasts", new Api.InputNotifyBroadcasts()],
    ];
    await Promise.all(
      scopes.map(async ([scope, peer]) => {
        try {
          this.notifyScope.set(scope, await client.invoke(new Api.account.GetNotifySettings({ peer })));
        } catch (e) {
          this.ctx.log(`could not load ${scope} notify defaults: ${describeError(e)}`);
        }
      }),
    );
  }

  private selfInfo(me: Api.User): SelfInfo {
    const id = String(me.id);
    const info: SelfInfo = { id, name: entityName(me) ?? "Me" };
    if (me.phone) info.phone = `+${me.phone}`;
    const avatar = this.avatarIfCached(id);
    if (avatar) info.avatar = avatar;
    return info;
  }

  private async refreshSelfAvatar() {
    const self = this.self;
    if (!self) return;
    const path = await this.downloadAvatar(self.id).catch(() => undefined);
    if (path && this.self && this.self.id === self.id && this.self.avatar !== path && this.state === "logged_in") {
      this.self = { ...this.self, avatar: path };
      this.setState("logged_in");
    }
  }

  private async onSessionLost() {
    if (this.state !== "logged_in") return;
    this.ctx.log("telegram session was terminated by the server");
    this.bootId++;
    this.self = undefined;
    rmSafe(this.sessionFile);
    rmSafe(this.stateFile);
    await this.destroyClient();
    this.resetCaches();
    this.emitConnection("offline");
    this.setState("logged_out", { error: "Your Telegram session was terminated. Please log in again." });
  }

  // -------------------------------------------------------------- login

  async login(params: { method?: string; cookies?: string; phone?: string }): Promise<void> {
    const method = params.method ?? "qr";
    if (method === "cookies") throw new BridgeError("unsupported", "Telegram does not support cookie login");
    if (method !== "qr" && method !== "phone") {
      throw new BridgeError("invalid_params", `unknown login method: ${method}`);
    }
    const phone = (params.phone ?? "").trim();
    if (method === "phone" && !/^\+?[\d\s().-]{5,}$/.test(phone)) {
      throw new BridgeError("invalid_params", "phone is required in international format, e.g. +84901234567");
    }

    const creds = this.resolveCreds(params as Record<string, unknown>);
    if (!creds) {
      this.setState("error", { error: MISSING_CREDS });
      throw new BridgeError("invalid_params", MISSING_CREDS);
    }
    if (this.state === "logged_in") return;
    this.creds = creds;

    // Requests are handled concurrently: a second auth.login that arrives while this one is still awaiting
    // (cancel, destroy, connect) supersedes it. The older call stops silently so exactly one flow survives.
    const ticket = ++this.loginTicket;
    const superseded = () => ticket !== this.loginTicket;

    // Reuse the warm, never-used, unauthenticated connection from start() when we can.
    const reusable =
      this.client &&
      this.client.connected &&
      this.clientFresh &&
      !this.clientDirty &&
      !this.run &&
      this.state === "logged_out";

    this.bootId++; // aborts a pending restore() retry loop
    await this.cancelRun(); // a running QR/phone flow is torn down first, never left running beside the new one
    if (superseded()) return;

    if (!reusable) {
      await this.destroyClient();
      if (superseded()) return;
      this.setState("connecting");
      try {
        const client = this.createClient("");
        this.client = client;
        this.emitConnection("connecting");
        await client.connect();
        if (superseded()) return; // the newer call destroyed this client and owns the state now
        this.emitConnection("online");
      } catch (e) {
        if (superseded()) return;
        const detail = describeError(e);
        this.emitConnection("offline", detail);
        await this.destroyClient();
        this.setState("error", { error: `Cannot connect to Telegram: ${detail}` });
        throw new BridgeError("internal", `Cannot connect to Telegram: ${detail}`);
      }
    }

    const client = this.client;
    if (!client) throw new BridgeError("internal", "Telegram client unavailable");
    this.clientDirty = true;
    const run = new LoginRun();
    this.run = run;
    void (method === "qr" ? this.runQr(run, client, creds) : this.runPhone(run, client, creds, phone));

    await Promise.race([run.first.promise, sleep(30_000)]);
    if (run.failure) throw run.failure;
  }

  private async cancelRun() {
    const run = this.run;
    if (!run) return;
    this.run = undefined;
    run.cancelled = true;
    run.stopQrTimer();
    run.pending?.resolve(""); // an empty answer makes GramJS call onError, which stops the flow
    run.pending = undefined;
    run.first.resolve();
    run.fire();
  }

  private setAuthState(run: LoginRun, state: "awaiting_qr" | "awaiting_code" | "awaiting_password", error?: string) {
    this.setState(state, error ? { error } : {});
    run.fire();
  }

  private async runQr(run: LoginRun, client: TelegramClient, creds: Creds) {
    this.setAuthState(run, "awaiting_qr");
    try {
      const user = await client.signInUserWithQrCode(creds, {
        qrCode: async ({ token, expires }) => {
          if (run.cancelled) throw new Error("LOGIN_CANCELLED");
          if (run.passwordPhase) return;
          await this.emitQr(run, token, expires);
        },
        password: (hint) => this.askPassword(run, hint),
        onError: (err) => this.onLoginError(run, err),
      });
      await this.finishLogin(run, user);
    } catch (e) {
      if (run.cancelled) return;
      // The flow was alive (a code was shown, nobody scanned it yet) and then died, e.g. the network dropped:
      // report an expired QR so the app restarts it, instead of leaving a dead code on screen.
      // Failures before the first code (bad API id, flood wait, no connection) stay real errors: the app must not loop on them.
      if (run.qrCount > 0 && !run.scanned && !run.passwordPhase && !run.fatal) {
        this.expireQr(run, describeError(e));
        return;
      }
      this.failRun(run, e);
    }
  }

  /**
   * The QR flow is over without a login: tear it down and tell the app, which restarts it while the QR screen is
   * visible. Marks the run cancelled so GramJS's token loop stops at its next callback and late errors are ignored.
   */
  private expireQr(run: LoginRun, reason: string) {
    if (this.run !== run || run.cancelled || run.passwordPhase) return;
    this.ctx.log(`telegram QR login ended: ${reason}`);
    this.run = undefined;
    run.cancelled = true;
    run.stopQrTimer();
    run.first.resolve();
    run.fire();
    void this.destroyClient(); // the next auth.login builds a fresh connection
    this.setState("logged_out", { error: "qr_expired" });
  }

  /**
   * GramJS re-exports the token every 30 s and calls our qrCode callback each time, but never gives up and never
   * notices if its loop stalls. This timer fires if no replacement token shows up after the shown one expired
   * (plus a grace period), and after QR_MAX_TOKENS unscanned codes.
   */
  private armQrWatchdog(run: LoginRun, expiresAtMs: number) {
    run.stopQrTimer();
    const last = run.qrCount >= QR_MAX_TOKENS;
    // Server-side `expires` vs. a skewed local clock: never wait less than 0 or more than 45 s for the expiry itself.
    const untilExpiry = Math.min(Math.max(expiresAtMs - Date.now(), 0), 45_000);
    const wait = untilExpiry + (last ? 0 : QR_GRACE_MS);
    run.qrTimer = setTimeout(
      () => this.expireQr(run, last ? `${QR_MAX_TOKENS} QR codes expired unscanned` : "no fresh QR code arrived"),
      wait,
    );
    run.qrTimer.unref?.();
  }

  /** UpdateLoginToken: the user scanned and confirmed. The login must now complete promptly. */
  private onQrScanned() {
    const run = this.run;
    if (!run || run.cancelled || run.qrCount === 0 || run.scanned) return;
    run.scanned = true;
    run.stopQrTimer();
    run.qrTimer = setTimeout(() => {
      if (this.run === run && !run.passwordPhase) this.expireQr(run, "login did not complete after the QR was scanned");
    }, QR_SCANNED_TIMEOUT_MS);
    run.qrTimer.unref?.();
  }

  private async runPhone(run: LoginRun, client: TelegramClient, creds: Creds, phone: string) {
    this.setState("connecting");
    try {
      const user = await client.signInUser(creds, {
        phoneNumber: phone,
        phoneCode: () => this.askCode(run),
        password: (hint) => this.askPassword(run, hint),
        // Never silently create an account for an unregistered number.
        firstAndLastNames: async () => {
          throw new Error("NEW_ACCOUNT_NOT_SUPPORTED");
        },
        onError: (err) => this.onLoginError(run, err),
      });
      await this.finishLogin(run, user);
    } catch (e) {
      this.failRun(run, e);
    }
  }

  private async emitQr(run: LoginRun, token: Buffer, expires: number) {
    if (run.qrCount >= QR_MAX_TOKENS) {
      // The cap's own timer normally fires first; this covers GramJS delivering the next token ahead of it.
      this.expireQr(run, `${QR_MAX_TOKENS} QR codes expired unscanned`);
      return;
    }
    const qr = `tg://login?token=${token.toString("base64url")}`;
    if (this.state !== "awaiting_qr") this.setAuthState(run, "awaiting_qr");
    let image: string | undefined;
    try {
      image = await toDataURL(qr, { errorCorrectionLevel: "L", margin: 2, width: 360 });
    } catch (e) {
      this.ctx.log("QR image rendering failed", e);
    }
    if (run.cancelled || run.passwordPhase || run.scanned) return;
    // Every token GramJS hands us is forwarded: each replaces the previous code on screen. `expires` is unix seconds.
    const expiresAt = expires * 1000;
    run.qrCount++;
    this.ctx.emit("auth.qr", { qr, image, expiresAt });
    this.armQrWatchdog(run, expiresAt);
    run.first.resolve();
  }

  private askCode(run: LoginRun): Promise<string> {
    if (run.cancelled) return Promise.resolve("");
    this.setAuthState(run, "awaiting_code", run.takePromptError());
    run.first.resolve();
    return new Promise((resolve) => {
      run.pending = { kind: "code", resolve };
    });
  }

  private askPassword(run: LoginRun, _hint?: string): Promise<string> {
    run.passwordPhase = true;
    run.stopQrTimer(); // the QR was scanned; the user may now take as long as they need for the 2FA password
    if (run.cancelled) return Promise.resolve("");
    if (run.queuedPassword) {
      const p = run.queuedPassword;
      run.queuedPassword = undefined;
      return Promise.resolve(p);
    }
    this.setAuthState(run, "awaiting_password", run.takePromptError());
    run.first.resolve();
    return new Promise((resolve) => {
      run.pending = { kind: "password", resolve };
    });
  }

  /** Decide whether GramJS should keep going (false = re-prompt) or abort the flow (true). */
  private async onLoginError(run: LoginRun, err: Error): Promise<boolean> {
    if (run.cancelled) return true;
    const wait = floodSeconds(err);
    const msg = rpcMessage(err) || err.message;
    this.ctx.log(`telegram login error: ${msg}`);
    if (wait !== undefined) {
      run.fatal = `Too many attempts. Try again in ${wait} seconds.`;
      return true;
    }
    switch (msg) {
      case "PHONE_CODE_INVALID":
      case "PHONE_CODE_EMPTY":
      case "Code is empty":
        run.promptError = "Wrong code, please try again.";
        return false;
      case "PASSWORD_HASH_INVALID":
      case "Password is empty":
        run.promptError = "Wrong password, please try again.";
        return false;
      case "SRP_ID_INVALID":
        run.promptError = "Please enter your password again.";
        return false;
      case "PHONE_CODE_EXPIRED":
        run.fatal = "The login code has expired. Start the login again to get a new code.";
        return true;
      case "NEW_ACCOUNT_NOT_SUPPORTED":
        run.fatal =
          "This phone number is not registered on Telegram. Create the account in the official Telegram app first.";
        return true;
      default:
        run.fatal = describeError(err);
        return true;
    }
  }

  private async finishLogin(run: LoginRun, user: Api.TypeUser) {
    run.stopQrTimer();
    if (run.cancelled) return;
    if (!(user instanceof Api.User)) throw new Error("Telegram returned an unexpected login result");
    if (this.run === run) this.run = undefined;
    await this.onAuthorized(user);
    run.first.resolve();
    run.fire();
  }

  private failRun(run: LoginRun, e: unknown) {
    run.stopQrTimer();
    if (run.cancelled) return;
    const message = run.fatal ?? describeError(e);
    const code = INVALID_ERRORS.has(rpcMessage(e)) ? "invalid_params" : "internal";
    run.failure = new BridgeError(code, message);
    if (this.run === run) this.run = undefined;
    this.clientDirty = true;
    this.ctx.log(`telegram login failed: ${message}`);
    this.setState("error", { error: message });
    run.first.resolve();
    run.fire();
  }

  async submit(params: { password?: string; code?: string }): Promise<void> {
    const run = this.run;
    const pending = run?.pending;
    if (!run || !pending) {
      throw new BridgeError("invalid_params", "No login is waiting for a code or password");
    }
    let value: string;
    if (pending.kind === "code") {
      value = (params.code ?? "").replace(/[\s-]/g, "");
      if (!value) throw new BridgeError("invalid_params", "code is required");
      if (params.password) run.queuedPassword = params.password; // in case 2FA follows
    } else {
      value = params.password ?? "";
      if (!value) throw new BridgeError("invalid_params", "password is required");
    }
    const next = run.waitTransition();
    run.pending = undefined;
    pending.resolve(value);
    // Report the resulting state (logged_in / awaiting_password / re-prompt / error), not the stale one.
    await Promise.race([next, sleep(25_000)]);
  }

  async logout(): Promise<void> {
    this.bootId++;
    await this.cancelRun();
    const client = this.client;
    if (client && this.self) {
      try {
        await client.invoke(new Api.auth.LogOut());
      } catch (e) {
        this.ctx.log("auth.logOut failed (continuing)", describeError(e));
      }
    }
    await this.destroyClient();
    rmSafe(this.sessionFile);
    rmSafe(this.stateFile);
    try {
      rmSync(this.avatarDir, { recursive: true, force: true });
      mkdirSync(this.avatarDir, { recursive: true });
    } catch {
      /* ignore */
    }
    this.resetCaches();
    this.self = undefined;
    this.emitConnection("offline");
    this.setState("logged_out");
  }

  async shutdown(): Promise<void> {
    this.stopped = true;
    this.bootId++;
    if (this.saveTimer) clearInterval(this.saveTimer);
    if (this.foldersTimer) clearTimeout(this.foldersTimer);
    if (this.stateTimer) clearTimeout(this.stateTimer);
    if (this.gapTimer) clearTimeout(this.gapTimer);
    for (const t of this.channelGapTimers.values()) clearTimeout(t);
    this.saveSession();
    this.saveUpdateState();
    await this.cancelRun();
    for (const t of this.typingTimers.values()) clearTimeout(t);
    await this.destroyClient();
  }

  // -------------------------------------------------------------- helpers: guard / entities / peers

  private requireClient(): TelegramClient {
    this.requireLogin();
    if (!this.client) throw new BridgeError("not_logged_in", "Not logged in");
    return this.client;
  }

  private async guard<T>(fn: () => Promise<T>): Promise<T> {
    try {
      return await fn();
    } catch (e) {
      throw this.translate(e);
    }
  }

  private translate(e: unknown): BridgeError {
    if (e instanceof BridgeError) return e;
    if (isAuthError(e)) {
      void this.onSessionLost();
      return new BridgeError("not_logged_in", "Your Telegram session is no longer valid. Please log in again.");
    }
    const m = rpcMessage(e);
    if (NOT_FOUND_ERRORS.has(m)) return new BridgeError("not_found", FRIENDLY[m] ?? m);
    if (INVALID_ERRORS.has(m)) return new BridgeError("invalid_params", FRIENDLY[m] ?? m);
    return new BridgeError("internal", describeError(e));
  }

  private mapEnv(): MapEnv {
    return {
      selfId: this.self?.id ?? "",
      selfName: this.self?.name ?? "Me",
      entity: (id) => this.entities.get(id),
      readOutboxMax: (chatId) => this.readOutbox.get(chatId) ?? 0,
      avatar: (id) => this.avatarIfCached(id),
    };
  }

  private rememberEntity(e: Entity | undefined) {
    if (!e || e instanceof Api.UserEmpty || e instanceof Api.ChatEmpty) return;
    let id: string;
    try {
      id = utils.getPeerId(e);
    } catch {
      return;
    }
    const prev = this.entities.get(id);
    if (prev && isMinEntity(e) && !isMinEntity(prev)) return; // never downgrade a full entity to a "min" one
    this.entities.set(id, e);
    if (!isMinEntity(e)) {
      try {
        this.inputPeers.set(id, utils.getInputPeer(e));
      } catch {
        /* no access hash yet */
      }
    }
  }

  private rememberEntities(src: unknown) {
    if (!src) return;
    if (src instanceof Map) {
      for (const e of src.values()) this.rememberEntity(e as Entity);
    } else if (Array.isArray(src)) {
      for (const e of src) this.rememberEntity(e as Entity);
    }
  }

  private async ensureEntity(id: string): Promise<Entity | undefined> {
    const have = this.entities.get(id);
    if (have) return have;
    if (!this.client || this.failedEntities.has(id)) return undefined;
    try {
      this.rememberEntity((await this.client.getEntity(helpers.returnBigInt(id))) as Entity);
    } catch {
      this.failedEntities.add(id);
    }
    return this.entities.get(id);
  }

  private async inputPeer(chatId: string): Promise<Api.TypeInputPeer> {
    if (!/^-?\d+$/.test(chatId)) throw new BridgeError("invalid_params", `invalid chatId: ${chatId}`);
    if (this.self && chatId === this.self.id) return new Api.InputPeerSelf();
    const cached = this.inputPeers.get(chatId);
    if (cached) return cached;
    const client = this.requireClient();
    try {
      const peer = await client.getInputEntity(helpers.returnBigInt(chatId));
      this.inputPeers.set(chatId, peer);
      return peer;
    } catch {
      throw new BridgeError("not_found", `Unknown chat ${chatId}`);
    }
  }

  private avatarIfCached(id: string): string | undefined {
    if (!/^-?\d+$/.test(id)) return undefined;
    const p = join(this.avatarDir, `${id}.jpg`);
    return existsSync(p) ? p : undefined;
  }

  private rememberMessage(msg: Message) {
    this.recent.set(`${msg.chatId}:${msg.id}`, msg);
    if (!msg.chatId.startsWith("-100")) this.idToChat.set(Number(msg.id), msg.chatId);
  }

  // -------------------------------------------------------------- chats

  /** Turn a dialogs response (GetDialogs / GetPeerDialogs) into protocol chats and update caches. */
  private ingestDialogs(r: {
    dialogs: Api.TypeDialog[];
    messages: Api.TypeMessage[];
    chats: Api.TypeChat[];
    users: Api.TypeUser[];
  }): Chat[] {
    const local = new Map<string, Entity>();
    for (const e of [...r.users, ...r.chats]) {
      if (e instanceof Api.UserEmpty || e instanceof Api.ChatEmpty) continue;
      this.rememberEntity(e);
      try {
        local.set(utils.getPeerId(e), e);
      } catch {
        /* skip */
      }
    }
    const msgs = new Map<string, RawMessage>();
    for (const m of r.messages) {
      if (m instanceof Api.Message || m instanceof Api.MessageService) msgs.set(`${peerKey(m.peerId)}:${m.id}`, m);
    }
    const dialogs = r.dialogs.filter((d): d is Api.Dialog => d instanceof Api.Dialog);
    for (const d of dialogs) {
      const key = peerKey(d.peer);
      this.readOutbox.set(key, d.readOutboxMaxId);
      // A channel dialog carries the channel's current pts: the starting point for its getChannelDifference.
      if (d.pts !== undefined && d.pts !== null && d.peer instanceof Api.PeerChannel) this.noteDialogPts(key, d.pts);
    }

    const env = this.mapEnv();
    const out: Chat[] = [];
    for (const d of dialogs) {
      const key = peerKey(d.peer);
      const entity = local.get(key) ?? this.entities.get(key);
      if (!entity) continue;
      const raw = msgs.get(`${key}:${d.topMessage}`);
      const last = raw ? this.mapRaw(raw) : undefined;
      if (last) this.rememberMessage(last);
      const scope = notifyScopeOf(entity);
      const chat = buildChat(entity, d, last, env, this.notifyScope.get(scope));
      if (!chat) continue;
      this.notifyOwn.set(chat.id, d.notifySettings);
      this.chatScope.set(chat.id, scope);
      this.chats.set(chat.id, chat);
      out.push(chat);
    }
    return out;
  }

  /**
   * Main-list chats (up to `limit`), plus archived ones (up to ARCHIVE_LIST_LIMIT) flagged `archived` so the app's
   * folder rules can match them. Sorted pinned first, then newest; a pin inside the archive is archive-local and
   * does not lift a chat above the main list.
   */
  async listChats(limit: number): Promise<Chat[]> {
    const client = this.requireClient();
    return this.guard(async () => {
      const want = clamp(limit, 1, 500);
      const main = await this.loadDialogs(client, 0, want);
      let archived: Chat[] = [];
      try {
        archived = await this.loadDialogs(client, 1, Math.min(want, ARCHIVE_LIST_LIMIT));
      } catch (e) {
        if (isAuthError(e)) throw e;
        this.ctx.log(`could not load archived chats: ${describeError(e)}`); // the main list is still good
      }
      const seen = new Set(main.map((c) => c.id));
      const out = [...main, ...archived.filter((c) => !seen.has(c.id))];
      const rank = (c: Chat) => Number(!!c.pinned && !c.archived);
      out.sort((a, b) => rank(b) - rank(a) || b.timestamp - a.timestamp);

      if (this.foldersAfterChats) {
        // First list of this session: the dialogs are in, announce the folders again so the app can fill them.
        this.foldersAfterChats = false;
        if (this.foldersJson) this.publishFolders(this.folders, true);
        else this.scheduleFolders(0, true);
      }
      return out;
    });
  }

  /** Page through GetDialogs for one dialog folder (0 = main list, 1 = archive). */
  private async loadDialogs(client: TelegramClient, folderId: number, want: number): Promise<Chat[]> {
    const out: Chat[] = [];
    const seen = new Set<string>();
    let offsetDate = 0;
    let offsetId = 0;
    let offsetPeer: Api.TypeInputPeer = new Api.InputPeerEmpty();
    let excludePinned = false;

    for (let page = 0; page < 10 && out.length < want; page++) {
      const pageSize = Math.min(100, want - out.length);
      const r: Api.messages.TypeDialogs = await client.invoke(
        new Api.messages.GetDialogs({
          offsetDate,
          offsetId,
          offsetPeer,
          limit: pageSize,
          hash: helpers.returnBigInt(0),
          excludePinned,
          folderId,
        }),
      );
      if (r instanceof Api.messages.DialogsNotModified) break;
      for (const c of this.ingestDialogs(r)) {
        if (!seen.has(c.id)) {
          seen.add(c.id);
          out.push(c);
        }
      }
      if (!(r instanceof Api.messages.DialogsSlice) || r.dialogs.length < pageSize) break;

      // advance the cursor to the last dialog whose top message we can see
      let advanced = false;
      for (let i = r.dialogs.length - 1; i >= 0 && !advanced; i--) {
        const d: Api.TypeDialog = r.dialogs[i];
        if (!(d instanceof Api.Dialog)) continue;
        const top = r.messages.find(
          (m) => (m instanceof Api.Message || m instanceof Api.MessageService) && m.id === d.topMessage && peerKey(m.peerId) === peerKey(d.peer),
        );
        if (top && (top instanceof Api.Message || top instanceof Api.MessageService)) {
          offsetId = top.id;
          offsetDate = top.date;
          offsetPeer = this.inputPeers.get(peerKey(d.peer)) ?? new Api.InputPeerEmpty();
          advanced = true;
        }
      }
      if (!advanced) break;
      excludePinned = true;
    }

    return out.slice(0, want);
  }

  // -------------------------------------------------------------- folders

  /** messages.getDialogFilters, mapped to protocol folders. Pure fetch: callers decide what to cache and emit. */
  private async fetchFolders(client: TelegramClient): Promise<Folder[]> {
    const r = await client.invoke(new Api.messages.GetDialogFilters());
    return mapFolders(r, this.self?.id);
  }

  async listFolders(): Promise<Folder[]> {
    const client = this.requireClient();
    return this.guard(async () => {
      const folders = await this.fetchFolders(client);
      if (this.client === client) {
        this.folders = folders;
        this.publishFolders(folders, false); // the app is also told if this fetch found a change
      }
      return folders;
    });
  }

  /** Emit folders.update; unless `force`, only when the list differs from what was last emitted. */
  private publishFolders(folders: Folder[], force: boolean) {
    const json = JSON.stringify(folders);
    if (!force && json === this.foldersJson) return;
    this.foldersJson = json;
    this.ctx.emit("folders.update", { folders });
  }

  /** Debounced refetch: update bursts (reordering, edits on another device) collapse into one request. */
  private scheduleFolders(delayMs: number, force = false) {
    if (force) this.foldersForce = true;
    if (this.foldersTimer) clearTimeout(this.foldersTimer);
    this.foldersTimer = setTimeout(() => {
      this.foldersTimer = undefined;
      const f = this.foldersForce;
      this.foldersForce = false;
      void this.refreshFolders(f);
    }, delayMs);
    this.foldersTimer.unref?.();
  }

  private async refreshFolders(force: boolean) {
    const client = this.client;
    if (!client || this.state !== "logged_in") return;
    const boot = this.bootId;
    try {
      const folders = await this.fetchFolders(client);
      if (boot !== this.bootId || this.client !== client) return; // logged out / replaced while fetching
      this.folders = folders;
      this.publishFolders(folders, force);
    } catch (e) {
      if (isAuthError(e)) void this.onSessionLost();
      else this.ctx.log(`could not load chat folders: ${describeError(e)}`);
    }
  }

  /** Re-read one dialog from the server (exact unread count, read state, last message). */
  private async refreshDialog(chatId: string, emitUpsert: boolean): Promise<Chat | undefined> {
    const client = this.requireClient();
    const peer = await this.inputPeer(chatId);
    const r = await client.invoke(new Api.messages.GetPeerDialogs({ peers: [new Api.InputDialogPeer({ peer })] }));
    const chats = this.ingestDialogs(r);
    const chat = chats.find((c) => c.id === chatId) ?? chats[0];
    if (chat && emitUpsert) this.ctx.emit("chat.upsert", { chat });
    return chat;
  }

  private async ensureDialogState(chatId: string) {
    if (this.readOutbox.has(chatId)) return;
    try {
      await this.refreshDialog(chatId, false);
    } catch (e) {
      this.ctx.log(`could not load dialog state for ${chatId}: ${describeError(e)}`);
    }
    if (!this.readOutbox.has(chatId)) this.readOutbox.set(chatId, 0);
  }

  // -------------------------------------------------------------- avatars

  async chatAvatar(chatId: string): Promise<string | undefined> {
    this.requireClient();
    if (!/^-?\d+$/.test(chatId)) throw new BridgeError("invalid_params", `invalid chatId: ${chatId}`);
    return this.guard(() => this.downloadAvatar(chatId));
  }

  private downloadAvatar(id: string): Promise<string | undefined> {
    const existing = this.avatarInflight.get(id);
    if (existing) return existing;
    const p = this.avatarSlots.run(() => this.fetchAvatar(id)).finally(() => this.avatarInflight.delete(id));
    this.avatarInflight.set(id, p);
    return p;
  }

  private async fetchAvatar(id: string): Promise<string | undefined> {
    const file = join(this.avatarDir, `${id}.jpg`);
    const st = safeStat(file);
    if (st && Date.now() - st.mtimeMs < AVATAR_TTL_MS) return file;
    const client = this.client;
    if (!client) return st ? file : undefined;

    const entity = this.entities.get(id) ?? (await this.ensureEntity(id));
    if (!entity) return st ? file : undefined;
    if (!hasProfilePhoto(entity)) {
      if (st) rmSafe(file); // photo was removed
      return undefined;
    }
    try {
      // isBig must be passed explicitly: GramJS dereferences the params object.
      const buf = await client.downloadProfilePhoto(entity, { isBig: false });
      if (Buffer.isBuffer(buf) && buf.length > 0) {
        mkdirSync(this.avatarDir, { recursive: true });
        atomicWrite(file, buf, 0o644);
        return file;
      }
    } catch (e) {
      this.ctx.log(`avatar download failed for ${id}: ${describeError(e)}`);
    }
    return st ? file : undefined;
  }

  // -------------------------------------------------------------- messages

  /** TL message -> protocol message (entities resolved, album items kept in sequence). */
  private mapRaw(raw: RawMessage, opts?: { forceOut?: boolean }): Message | undefined {
    const msg = mapMessage(raw, this.mapEnv(), opts);
    return msg ? this.albums.stamp(msg) : undefined;
  }

  private async toProtocol(raw: RawMessage, opts?: { forceOut?: boolean }): Promise<Message | undefined> {
    if (raw instanceof Api.Message || raw instanceof Api.MessageService) {
      let senderKey: string | undefined;
      if (raw.fromId) senderKey = peerKey(raw.fromId);
      else if (raw.peerId instanceof Api.PeerUser && !raw.out) senderKey = peerKey(raw.peerId);
      if (senderKey && !this.entities.has(senderKey)) await this.ensureEntity(senderKey);
    }
    return this.mapRaw(raw, opts);
  }

  /** One getMessages call for the replied-to messages `ids` of a chat; returns the ones that still exist. */
  private async fetchReplyTargets(chatId: string, ids: number[]): Promise<Map<number, Message>> {
    const client = this.requireClient();
    const peer = await this.inputPeer(chatId);
    const fetched = await client.getMessages(peer, { ids });
    const found = new Map<number, Message>();
    for (const raw of fetched) {
      if (!raw) continue; // deleted (MessageEmpty) or from another chat
      this.rememberEntities((raw as unknown as RawUpdate)._entities);
      const m = await this.toProtocol(raw);
      if (m) found.set(Number(m.id), m);
    }
    return found;
  }

  /**
   * Fill replyTo.text/senderName/kind of `mapped` (made from `raws`, same order) with the message each one answers:
   * from the same page, from messages seen before, from earlier lookups, else one batched fetch for all that are missing.
   */
  private fillReplies(chatId: string, raws: RawMessage[], mapped: Message[]): Promise<void> {
    return this.replies.fill(chatId, raws, mapped);
  }

  /**
   * `message.update` replaces the whole message in the app, so a re-mapped message must not lose its quote: take the
   * resolved `replyTo` of the version we sent before (when it still points at the same message and was resolved),
   * otherwise resolve it again.
   */
  private async keepOrFillReply(msg: Message, raw: RawMessage, prev: Message | undefined) {
    if (!msg.replyTo) return;
    if (prev?.replyTo && prev.replyTo.id === msg.replyTo.id && prev.replyTo.kind) msg.replyTo = prev.replyTo;
    else await this.fillReplies(msg.chatId, [raw], [msg]);
  }

  async listMessages(chatId: string, limit: number, before?: string): Promise<Message[]> {
    const client = this.requireClient();
    return this.guard(async () => {
      const peer = await this.inputPeer(chatId);
      await this.ensureDialogState(chatId);
      const offsetId = before ? parseMsgId(before) : 0;
      const batch = await client.getMessages(peer, { limit: clamp(limit, 1, 200), offsetId });

      const raws = [...batch] as RawMessage[];
      let lastEntities: unknown;
      for (const raw of raws) {
        const ents = (raw as unknown as RawUpdate)._entities;
        if (ents && ents !== lastEntities) {
          this.rememberEntities(ents);
          lastEntities = ents;
        }
      }
      raws.reverse(); // GramJS returns newest first; the protocol wants ascending

      const out: Message[] = [];
      const kept: RawMessage[] = [];
      for (const raw of raws) {
        const m = await this.toProtocol(raw);
        if (m) {
          out.push(m);
          kept.push(raw);
        }
      }
      await this.fillReplies(chatId, kept, out);
      for (const m of out) this.rememberMessage(m);
      return out;
    });
  }

  /**
   * messages.search: Telegram's own server search (one chat, or every chat with SearchGlobal), plus the messages this
   * bridge has seen recently, matched accent-insensitively so "hop dong" also finds "hợp đồng" there.
   *
   * `kinds` become server filters (see `searchFilters`; several kinds with no common filter are several requests,
   * merged). `senderId` becomes `fromId` of messages.search, so "everything this member sent / shared in this chat" is
   * answered by the server; SearchGlobal has no sender filter, so without `chatId` the sender is checked afterwards.
   * Whatever the server cannot narrow (a document filter also returns videos sent as files, …) is checked afterwards
   * and the next server page is read to fill the result, so a page is short only when the history really ends.
   */
  async searchMessages(p: SearchParams): Promise<Message[]> {
    const client = this.requireClient();
    return this.guard(async () => {
      const filters = searchFilters(p.kinds);
      const maxDate = p.before ? Math.floor(p.before / 1000) : 0;
      const serverLimit = Math.min(p.limit, SEARCH_PAGE_LIMIT);
      const peer = p.chatId ? await this.inputPeer(p.chatId) : undefined;
      let from: Api.TypeInputPeer | undefined;
      if (peer && p.senderId) {
        try {
          from = await this.inputPeer(p.senderId);
        } catch {
          from = undefined; // sender not known to this session: the server cannot filter, the check below does
        }
      }
      const bounds = new Matcher({ ...p, query: "" });
      const boundsAnyKind = new Matcher({ ...p, query: "", kinds: undefined }); // for the server's Url filter

      const read = async (filter: Api.TypeMessagesFilter): Promise<Message[]> => {
        const accept = filter instanceof Api.InputMessagesFilterUrl ? boundsAnyKind : bounds;
        const got: Message[] = [];
        let offsetId = 0;
        let offsetRate = 0;
        let offsetPeer: Api.TypeInputPeer = new Api.InputPeerEmpty();
        for (let page = 0; page < SEARCH_MAX_PAGES && got.length < p.limit; page++) {
          const r = peer
            ? await client.invoke(
                new Api.messages.Search({
                  peer,
                  q: p.query,
                  fromId: from,
                  filter,
                  minDate: 0,
                  maxDate,
                  offsetId,
                  addOffset: 0,
                  limit: serverLimit,
                  maxId: 0,
                  minId: 0,
                  hash: helpers.returnBigInt(0),
                }),
              )
            : await client.invoke(
                new Api.messages.SearchGlobal({
                  q: p.query,
                  filter,
                  minDate: 0,
                  maxDate,
                  offsetRate,
                  offsetPeer,
                  offsetId,
                  limit: serverLimit,
                }),
              );
          if (r instanceof Api.messages.MessagesNotModified) break;
          for (const e of [...r.users, ...r.chats]) this.rememberEntity(e as Entity);
          const raws = r.messages as RawMessage[];
          for (const raw of raws) {
            const m = await this.toProtocol(raw);
            if (!m || !accept.test(m)) continue;
            this.rememberMessage(m);
            got.push(m);
          }
          const last = raws[raws.length - 1] as { id?: number; peerId?: Api.TypePeer } | undefined;
          if (raws.length < serverLimit || !last?.id) break; // the server has no more
          offsetId = last.id;
          if (!peer) {
            if (!(r instanceof Api.messages.MessagesSlice) || r.nextRate === undefined || !last.peerId) break;
            offsetRate = r.nextRate;
            offsetPeer = await this.inputPeer(peerKey(last.peerId));
          }
        }
        return got;
      };

      const out: Message[] = [];
      // SearchGlobal rejects "no text and no filter"; with only a sender and no chat the local matches are all there is.
      const everything = filters.length === 1 && filters[0] instanceof Api.InputMessagesFilterEmpty;
      if (peer || p.query || !everything) {
        for (const list of await Promise.all(filters.map(read))) out.push(...list);
      }
      const local = new Matcher(p);
      for (const m of this.recent.values()) {
        if ((!p.chatId || m.chatId === p.chatId) && local.test(m)) out.push(m);
      }
      return newestFirst(out, p.limit);
    });
  }

  /**
   * Mention spans -> InputMessageEntityMentionName entities (offsets are UTF-16 like ours). The entity needs the
   * user's InputUser with access hash, which `inputPeer` has for everyone we have seen (members of the chat, senders).
   * A user we cannot resolve is skipped: the text is still sent, just without that highlight.
   */
  private mentionEntities(text: string, raw: unknown): Promise<Api.TypeMessageEntity[] | undefined> {
    return buildMentionEntities(text, raw, async (userId) => {
      try {
        return utils.getInputUser(await this.inputPeer(userId));
      } catch (e) {
        this.ctx.log(`mention of ${userId} skipped: ${describeError(e)}`);
        return undefined;
      }
    });
  }

  /**
   * Runs `send` with the mention entities. If Telegram refuses one of them (a user whose access hash it does not
   * accept, bad bounds) the message goes out again with the same text and no entities, as the protocol promises
   * ("just without that highlight"); `[]` rather than `undefined` so GramJS does not Markdown-parse the text.
   */
  private async withMentionFallback<T>(
    entities: Api.TypeMessageEntity[] | undefined,
    send: (entities: Api.TypeMessageEntity[] | undefined) => Promise<T>,
  ): Promise<T> {
    try {
      return await send(entities);
    } catch (e) {
      if (!entities || !MENTION_REJECTED.has(rpcMessage(e))) throw e;
      this.ctx.log(`Telegram refused the mention (${rpcMessage(e)}); sending the text without highlights`);
      return send([]);
    }
  }

  async sendMessage(p: {
    chatId: string;
    text?: string;
    replyTo?: string;
    filePath?: string;
    mentions?: MentionSpan[];
  }): Promise<Message> {
    const client = this.requireClient();
    return this.guard(async () => {
      const peer = await this.inputPeer(p.chatId);
      const replyTo = p.replyTo ? parseMsgId(p.replyTo) : undefined;
      // Explicit entities make GramJS skip its default markdown parsing, so offsets stay exactly as the app sent them
      // (a message with mentions is therefore sent as plain text plus the mention entities).
      const formattingEntities = await this.mentionEntities(p.text ?? "", p.mentions);

      const send = (entities: Api.TypeMessageEntity[] | undefined): Promise<Api.Message> => {
        if (!p.filePath) return client.sendMessage(peer, { message: p.text ?? "", replyTo, formattingEntities: entities });
        if (!safeStat(p.filePath)?.isFile()) throw new BridgeError("invalid_params", `file not found: ${p.filePath}`);
        return client.sendFile(peer, {
          file: p.filePath,
          caption: p.text || undefined,
          replyTo,
          formattingEntities: entities,
          voiceNote: VOICE_EXT.has(extname(p.filePath).toLowerCase()),
        });
      };
      const sent = await this.withMentionFallback(formattingEntities, send);
      return this.recordSent(sent);
    });
  }

  /** messages.forward: Telegram's own forward (keeps the "Forwarded from" header; no download or re-upload). */
  async forwardMessage(fromChatId: string, messageId: string, toChatId: string): Promise<Message> {
    const client = this.requireClient();
    return this.guard(async () => {
      const [fromPeer, toPeer] = [await this.inputPeer(fromChatId), await this.inputPeer(toChatId)];
      // Typed as Message[], but gramjs actually returns one array per 100-message chunk.
      const batches = (await client.forwardMessages(toPeer, { messages: [parseMsgId(messageId)], fromPeer })) as unknown as
        (Api.Message | Api.Message[] | undefined)[];
      const sent = batches.flat().find((m): m is Api.Message => m instanceof Api.Message);
      return this.recordSent(sent);
    });
  }

  private async recordSent(sent: Api.Message | undefined): Promise<Message> {
    if (!sent) throw new BridgeError("internal", "Telegram did not return the sent message");

    this.rememberEntities((sent as unknown as RawUpdate)._entities);
    const msg = this.mapRaw(sent, { forceOut: true });
    if (!msg) throw new BridgeError("internal", "Telegram returned an unreadable message");
    await this.fillReplies(msg.chatId, [sent], [msg]);

    this.sentKeys.set(`${msg.chatId}:${msg.id}`, true);
    this.rememberMessage(msg);

    const chat = this.chats.get(msg.chatId);
    if (chat) {
      chat.lastMessage = msg;
      chat.timestamp = msg.timestamp;
      chat.unread = 0; // sending implicitly reads the chat
      delete chat.markedUnread;
      this.ctx.emit("chat.upsert", { chat });
    }
    return msg;
  }

  async markRead(chatId: string): Promise<void> {
    const client = this.requireClient();
    await this.guard(async () => {
      const peer = await this.inputPeer(chatId);
      // messages.readHistory / channels.readHistory: no maxId => the whole history. The server fans this out as
      // UpdateReadHistoryInbox to the user's other devices, which is what clears their badge.
      await client.markAsRead(peer);
      const chat = this.chats.get(chatId);
      if (chat?.markedUnread) {
        // The manual "mark as unread" flag is separate from the read pointer; opening the chat clears it too.
        try {
          await client.invoke(new Api.messages.MarkDialogUnread({ unread: false, peer: new Api.InputDialogPeer({ peer }) }));
        } catch (e) {
          this.ctx.log(`could not clear unread mark of ${chatId}: ${describeError(e)}`);
        }
      }
      if (chat && (chat.unread !== 0 || chat.markedUnread)) {
        chat.unread = 0;
        delete chat.markedUnread;
        this.ctx.emit("chat.upsert", { chat });
      }
    });
  }

  async muteChat(chatId: string, until: number): Promise<Chat> {
    const client = this.requireClient();
    return this.guard(async () => {
      const peer = await this.inputPeer(chatId);
      const muteUntil = muteUntilSeconds(until);
      await client.invoke(
        new Api.account.UpdateNotifySettings({
          peer: new Api.InputNotifyPeer({ peer }),
          settings: new Api.InputPeerNotifySettings({ muteUntil }),
        }),
      );
      // A chat we never listed is read from the server first, which also gives it its notify-scope default.
      const chat = this.chats.get(chatId) ?? (await this.refreshDialog(chatId, false));
      if (!chat) throw new BridgeError("not_found", `Unknown chat ${chatId}`);
      // An explicit 0 (unmute) must win over a muted scope default, so it is stored rather than dropped.
      this.notifyOwn.set(chatId, new Api.PeerNotifySettings({ muteUntil }));
      this.applyMute(chat);
      this.ctx.emit("chat.upsert", { chat });
      return chat;
    });
  }

  /** messages.search with the pinned filter: newest message first, which is the order of Telegram's own pinned bar. */
  async pinnedMessages(chatId: string): Promise<Message[]> {
    const client = this.requireClient();
    return this.guard(async () => {
      const cached = this.pinsCache.get(chatId);
      if (cached) return cached;
      const epoch = this.pinsEpoch;
      const r = await client.invoke(
        new Api.messages.Search({
          peer: await this.inputPeer(chatId),
          q: "",
          filter: new Api.InputMessagesFilterPinned(),
          minDate: 0,
          maxDate: 0,
          offsetId: 0,
          addOffset: 0,
          limit: PINS_LIMIT,
          maxId: 0,
          minId: 0,
          hash: helpers.returnBigInt(0),
        }),
      );
      const out: Message[] = [];
      if (!(r instanceof Api.messages.MessagesNotModified)) {
        for (const e of [...r.users, ...r.chats]) this.rememberEntity(e as Entity);
        for (const raw of r.messages) {
          const m = await this.toProtocol(raw as RawMessage);
          if (!m) continue;
          this.rememberMessage(m);
          out.push(m);
        }
      }
      out.sort((a, b) => Number(b.id) - Number(a.id));
      if (epoch === this.pinsEpoch) this.pinsCache.set(chatId, out);
      return out;
    });
  }

  /** The pinned messages of a chat changed: drop the cached list and tell the app to ask again. */
  private invalidatePins(chatId: string) {
    this.pinsEpoch++;
    this.pinsCache.delete(chatId);
    this.ctx.emit("pins.update", { chatId });
  }

  /** An edited or deleted message that is in the cached pin list makes that list stale. */
  private pinnedMessageChanged(chatId: string, messageId: string) {
    if (this.pinsCache.get(chatId)?.some((m) => m.id === messageId)) this.invalidatePins(chatId);
  }

  async react(chatId: string, messageId: string, emoji: string): Promise<void> {
    const client = this.requireClient();
    await this.guard(async () => {
      const id = parseMsgId(messageId);
      const peer = await this.inputPeer(chatId);
      await client.invoke(
        new Api.messages.SendReaction({
          peer,
          msgId: id,
          // Telegram's allowed reactions are the bare forms ("❤", never "❤️"); the app shows them with U+FE0F.
          reaction: emoji ? [new Api.ReactionEmoji({ emoticon: reactionEmoticonForTelegram(emoji) })] : [], // empty list removes ours
        }),
      );
      await this.refreshMessage(chatId, id);
    });
  }

  /**
   * Members for the @mention picker: the other participant of a private chat, or up to MEMBERS_LIMIT participants of
   * a basic group / supergroup (GramJS getParticipants). Broadcast channels and groups that hide their member list
   * from non-admins yield []. Deleted accounts and the logged-in user are left out.
   */
  async chatMembers(chatId: string): Promise<Member[]> {
    const client = this.requireClient();
    if (!/^-?\d+$/.test(chatId)) throw new BridgeError("invalid_params", `invalid chatId: ${chatId}`);
    return this.membersCache.getOrLoad(chatId, () =>
      this.guard(async () => {
        const toMember = (e: Entity): Member | undefined => {
          if (!(e instanceof Api.User) || e.deleted) return undefined;
          const id = utils.getPeerId(e);
          const avatar = this.avatarIfCached(id);
          return { id, name: entityName(e) ?? id, ...(avatar ? { avatar } : {}) };
        };

        if (!chatId.startsWith("-")) {
          // Private chat (user ids are positive): the peer is the only other participant.
          const m = toMember((await this.ensureEntity(chatId)) as Entity);
          return m ? [m] : [];
        }

        const entity = this.entities.get(chatId);
        if (entity instanceof Api.Channel && entity.broadcast) return [];
        const peer = await this.inputPeer(chatId);
        let users: Api.User[];
        try {
          users = await client.getParticipants(peer, { limit: MEMBERS_LIMIT });
        } catch (e) {
          if (rpcMessage(e) === "CHAT_ADMIN_REQUIRED") return [];
          throw e;
        }
        const out: Member[] = [];
        for (const u of users) {
          this.rememberEntity(u);
          if (u.self || utils.getPeerId(u) === this.self?.id) continue;
          const m = toMember(u);
          if (m) out.push(m);
        }
        return out;
      }),
    );
  }

  /**
   * chats.direct (capability "direct_chat"): the one-to-one chat with a user seen in a group (`Message.senderId`).
   * A dialog we already know is returned as is; otherwise the server is asked for the dialog (read-only
   * messages.getPeerDialogs), and a user without one gets an empty chat built from the entity. Nothing is sent or
   * created: the chat exists on Telegram once a first message is sent. `chat.id` is the user id, which is what
   * messages.list / messages.send resolve through the access hash (or message reference) learned here.
   */
  async directChat(userId: string): Promise<Chat> {
    this.requireClient();
    if (!/^\d+$/.test(userId)) throw new BridgeError("invalid_params", `not a Telegram user id: ${userId}`);
    return this.guard(async () => {
      const known = this.chats.get(userId);
      if (known) return known;

      const user = await this.resolveUser(userId);
      if (user.deleted) throw new BridgeError("not_found", "This Telegram account has been deleted");

      let chat: Chat | undefined;
      try {
        chat = await this.refreshDialog(userId, false); // the real dialog, if there is one (registers it in the cache)
      } catch (e) {
        if (isAuthError(e)) throw e;
        this.ctx.log(`could not look up the dialog with ${userId}: ${describeError(e)}`);
      }
      if (chat?.id === userId) return chat;

      const scope = notifyScopeOf(user);
      const built = buildChat(user, undefined, undefined, this.mapEnv(), this.notifyScope.get(scope));
      if (!built) throw new BridgeError("not_found", `Unknown user ${userId}`);
      this.chatScope.set(built.id, scope);
      this.chats.set(built.id, built); // later messages, mute and read state find it like any other chat
      return built;
    });
  }

  /**
   * A user entity whose input peer we can address. Group participants and message senders usually arrive with an
   * access hash (`inputPeers`); a "min" user from a busy supergroup does not, and is then looked up through a
   * message of that group that shows them (InputUserFromMessage, as the official clients do).
   */
  private async resolveUser(userId: string): Promise<Api.User> {
    const client = this.requireClient();
    const usable = () => {
      const u = this.entities.get(userId);
      return u instanceof Api.User && this.inputPeers.has(userId) ? u : undefined;
    };
    const have = usable();
    if (have) return have;
    if (!(this.entities.get(userId) instanceof Api.User)) await this.ensureEntity(userId);
    const got = usable();
    if (got) return got;

    let seen: Message | undefined;
    for (const m of this.recent.values()) if (!m.fromMe && m.senderId === userId && m.chatId.startsWith("-")) seen = m;
    if (seen) {
      try {
        const peer = await this.inputPeer(seen.chatId);
        const msgId = Number(seen.id);
        const users = await client.invoke(
          new Api.users.GetUsers({ id: [new Api.InputUserFromMessage({ peer, msgId, userId: helpers.returnBigInt(userId) })] }),
        );
        for (const e of users) this.rememberEntity(e as Entity);
        if (!this.inputPeers.has(userId)) {
          this.inputPeers.set(userId, new Api.InputPeerUserFromMessage({ peer, msgId, userId: helpers.returnBigInt(userId) }));
        }
      } catch (e) {
        if (isAuthError(e)) throw e;
        this.ctx.log(`could not resolve user ${userId} through a message: ${describeError(e)}`);
      }
    }
    const u = this.entities.get(userId);
    if (u instanceof Api.User && this.inputPeers.has(userId)) return u;
    throw new BridgeError("not_found", `Unknown user ${userId}`);
  }

  /** Re-fetch one message and emit message.update (used after our own reaction). */
  private async refreshMessage(chatId: string, id: number) {
    const client = this.requireClient();
    const peer = await this.inputPeer(chatId);
    const [raw] = await client.getMessages(peer, { ids: id });
    if (!raw) return;
    this.rememberEntities((raw as unknown as RawUpdate)._entities);
    const msg = await this.toProtocol(raw);
    if (!msg) return;
    await this.keepOrFillReply(msg, raw, this.recent.get(`${chatId}:${id}`));
    this.rememberMessage(msg);
    this.ctx.emit("message.update", { message: msg });
  }

  async deleteMessage(chatId: string, messageId: string, forEveryone: boolean): Promise<void> {
    const client = this.requireClient();
    await this.guard(async () => {
      const id = parseMsgId(messageId);
      const peer = await this.inputPeer(chatId);
      await client.deleteMessages(peer, [id], { revoke: forEveryone });
      // Our own deletions are not echoed back as updates, so announce them ourselves.
      await this.applyDelete([id], chatId);
    });
  }

  /** messages.edit: new text (or caption) of one of our messages; Telegram allows it for 48 hours. */
  async editMessage(chatId: string, messageId: string, text: string, mentions?: MentionSpan[]): Promise<Message> {
    const client = this.requireClient();
    return this.guard(async () => {
      const peer = await this.inputPeer(chatId);
      const formattingEntities = await this.mentionEntities(text, mentions);
      const id = parseMsgId(messageId);
      const edited = await this.withMentionFallback(formattingEntities, (entities) =>
        client.editMessage(peer, { message: id, text, formattingEntities: entities }),
      );
      this.rememberEntities((edited as unknown as RawUpdate)._entities);
      const msg = await this.onEdit(edited, { forceOut: true });
      if (!msg) throw new BridgeError("internal", "Telegram returned an unreadable message");
      return msg;
    });
  }

  // -------------------------------------------------------------- storage (media cache policy)

  /** Run `fn` after every earlier storage job: scans, prunes and clears must not interleave. */
  private locked<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.storageChain.then(fn);
    this.storageChain = run.then(
      () => {},
      () => {},
    );
    return run;
  }

  /** Rebuild the media index from disk (the source of truth; also picks up files deleted behind our back). */
  private async scanMedia(): Promise<Map<string, MediaEntry>> {
    const idx = new Map<string, MediaEntry>();
    for (const f of await walkFiles(this.mediaDir)) {
      if (isTempName(f.path)) continue;
      idx.set(f.path, { size: f.size, used: Math.max(f.atimeMs, f.mtimeMs) });
    }
    this.mediaIndex = idx;
    return idx;
  }

  /** In-flight downloads own their `.part` / compression files and the final path(s); the pruner leaves them be. */
  private isBusyMedia(path: string): boolean {
    return this.busyMedia.has(path);
  }

  /** Remove `.part` / `.tmp` leftovers (a download or compression that died with the process). */
  private async sweepTempMedia() {
    try {
      for (const f of await walkFiles(this.mediaDir)) {
        if (isTempName(f.path) && !this.isBusyMedia(f.path)) rmSafe(f.path);
      }
    } catch {
      /* best effort */
    }
  }

  /**
   * Apply the storage policy to the media cache: drop files unused for `keepMediaDays`, then evict
   * least-recently-used files until the cache fits `maxCacheMB` (minus `reserveBytes` for a download about to
   * start). Uses the in-memory index unless `rescan` is set. Returns the bytes freed.
   */
  private pruneMedia(opts: { reserveBytes?: number; rescan?: boolean } = {}): Promise<number> {
    const cfg = this.storage;
    if (cfg.keepMediaDays <= 0 && cfg.maxCacheMB <= 0) return Promise.resolve(0);
    return this.locked(async () => {
      const idx = !opts.rescan && this.mediaIndex ? this.mediaIndex : await this.scanMedia();
      let freed = 0;
      const drop = async (path: string, e: MediaEntry) => {
        if (this.isBusyMedia(path)) return false;
        try {
          await unlink(path);
        } catch (err) {
          if ((err as NodeJS.ErrnoException)?.code !== "ENOENT") return false;
        }
        idx.delete(path);
        freed += e.size;
        return true;
      };

      if (cfg.keepMediaDays > 0) {
        const cutoff = Date.now() - cfg.keepMediaDays * DAY_MS;
        for (const [path, e] of [...idx]) if (e.used < cutoff) await drop(path, e);
      }
      if (cfg.maxCacheMB > 0) {
        const cap = cfg.maxCacheMB * 1024 * 1024;
        const target = cap - Math.min(Math.max(opts.reserveBytes ?? 0, 0), cap);
        let total = 0;
        for (const e of idx.values()) total += e.size;
        if (total > target) {
          for (const [path, e] of [...idx].sort((a, b) => a[1].used - b[1].used)) {
            if (total <= target) break;
            if (await drop(path, e)) total -= e.size;
          }
        }
      }
      return freed;
    });
  }

  async configureStorage(config: StorageConfig): Promise<void> {
    const n = (v: unknown) => (typeof v === "number" && Number.isFinite(v) && v > 0 ? v : 0);
    this.storage = {
      keepMediaDays: n(config.keepMediaDays),
      maxCacheMB: n(config.maxCacheMB),
      compressImages: config.compressImages !== false,
      maxStoredMessagesPerChat: Math.floor(n(config.maxStoredMessagesPerChat)), // no local message store here: nothing to trim
    };
    // Apply right away (app start, Settings change) without holding up the response; a rescan resyncs the index.
    this.pruneMedia({ rescan: true }).catch((e) => this.ctx.log(`media cache cleanup failed: ${describeError(e)}`));
  }

  /**
   * mediaBytes / mediaFiles: the downloaded-media cache (what storage.clear frees). databaseBytes: everything else
   * kept on disk: the session + API credentials and the avatar cache.
   */
  async storageUsage(): Promise<{ mediaBytes: number; mediaFiles: number; databaseBytes: number }> {
    const idx = await this.locked(() => this.scanMedia());
    let mediaBytes = 0;
    for (const e of idx.values()) mediaBytes += e.size;
    let databaseBytes = 0;
    for (const f of [this.sessionFile, this.apiFile]) databaseBytes += safeStat(f)?.size ?? 0;
    for (const f of await walkFiles(this.avatarDir)) databaseBytes += f.size;
    return { mediaBytes, mediaFiles: idx.size, databaseBytes };
  }

  /** Delete cached media: everything, or only files last used more than `olderThanDays` ago. Returns bytes freed. */
  async clearStorage(olderThanDays?: number): Promise<number> {
    if (olderThanDays !== undefined && !Number.isFinite(olderThanDays)) {
      throw new BridgeError("invalid_params", "olderThanDays must be a number");
    }
    const cutoff = olderThanDays === undefined || olderThanDays <= 0 ? Infinity : Date.now() - olderThanDays * DAY_MS;
    return this.locked(async () => {
      const idx = await this.scanMedia();
      let freed = 0;
      for (const [path, e] of [...idx]) {
        if (!(e.used < cutoff) || this.isBusyMedia(path)) continue;
        try {
          await unlink(path);
        } catch (err) {
          if ((err as NodeJS.ErrnoException)?.code !== "ENOENT") continue;
        }
        idx.delete(path);
        freed += e.size;
      }
      return freed;
    });
  }

  /** Cached file for `file`, either as downloaded or as its compressed HEIC twin. */
  private cachedMedia(file: string): { path: string; st: Stats } | undefined {
    for (const p of [file, withExt(file, ".heic")]) {
      const st = safeStat(p);
      if (st && st.isFile() && st.size > 0) return { path: p, st };
    }
    return undefined;
  }

  /** A cache hit counts as use: bump atime (mtime untouched) so LRU eviction and keepMediaDays see it. */
  private touchMedia(path: string, st: Stats) {
    const now = new Date();
    try {
      utimesSync(path, now, st.mtime);
    } catch {
      /* read-only or vanished: harmless */
    }
    const e = this.mediaIndex?.get(path);
    if (e) e.used = now.getTime();
  }

  /**
   * compressImages: re-encode a downloaded photo with macOS `sips` to HEIC (q60, longest side <= 1600 px).
   * Keeps the original if sips is unavailable/fails or the result is not smaller. Returns the path to serve.
   */
  private async compressPhoto(file: string, raw: Api.Message): Promise<string> {
    if (!this.storage.compressImages || this.sipsMissing || process.platform !== "darwin") return file;
    if (!isCompressibleMedia(raw) || !sniffCompressible(file)) return file;
    const out = withExt(file, ".heic");
    if (out === file) return file;
    const tmp = `${withExt(file, "")}.tmp.heic`; // `.tmp` marks it as scratch; the `.heic` suffix keeps sips quiet
    try {
      await execFileP("sips", ["-s", "format", "heic", "-s", "formatOptions", "60", "-Z", "1600", file, "--out", tmp], {
        timeout: 30_000,
      });
      const before = safeStat(file)?.size ?? 0;
      const after = safeStat(tmp)?.size ?? 0;
      if (after <= 0 || after >= before) {
        rmSafe(tmp);
        return file;
      }
      renameSync(tmp, out);
      rmSafe(file);
      return out;
    } catch (e) {
      rmSafe(tmp);
      if ((e as NodeJS.ErrnoException)?.code === "ENOENT") this.sipsMissing = true;
      this.ctx.log(`image compression skipped: ${describeError(e)}`);
      return file;
    }
  }

  async downloadMedia(chatId: string, messageId: string): Promise<string> {
    const client = this.requireClient();
    return this.guard(async () => {
      const id = parseMsgId(messageId);
      const peer = await this.inputPeer(chatId);
      const found = await client.getMessages(peer, { ids: id });
      const raw = found[0] as RawMessage | undefined;
      if (!(raw instanceof Api.Message) || !raw.media) {
        throw new BridgeError("not_found", "This message has no downloadable media");
      }
      const file = join(this.mediaDir, `${chatId}_${id}${mediaExtension(raw)}`);
      const hit = this.cachedMedia(file);
      if (hit) {
        this.touchMedia(hit.path, hit.st);
        return hit.path;
      }

      const inflight = this.downloadInflight.get(file);
      if (inflight) return inflight;
      const compressed = withExt(file, ".heic");
      const busy = [file, `${file}.part`, compressed, `${withExt(file, "")}.tmp.heic`];
      for (const p of busy) this.busyMedia.add(p);
      const job = (async () => {
        mkdirSync(this.mediaDir, { recursive: true });
        // Make room first: stale files by keepMediaDays, then least-recently-used until the new file fits maxCacheMB.
        await this.pruneMedia({ reserveBytes: mediaSizeHint(raw) }).catch((e) =>
          this.ctx.log(`media cache cleanup failed: ${describeError(e)}`),
        );
        const tmp = `${file}.part`;
        const fd = openSync(tmp, "w");
        let written = 0;
        // Synchronous writes: no flush races with the rename below (and no reliance on WriteStream).
        const writer = {
          write: (chunk: Buffer) => {
            writeSync(fd, chunk);
            written += chunk.length;
          },
          close: () => {},
        };
        try {
          await client.downloadMedia(raw, { outputFile: writer });
        } catch (e) {
          closeSync(fd);
          rmSafe(tmp);
          throw e;
        }
        closeSync(fd);
        if (written === 0) {
          rmSafe(tmp);
          throw new BridgeError("not_found", "This message has no downloadable file");
        }
        renameSync(tmp, file);
        const served = await this.compressPhoto(file, raw);
        const st = safeStat(served);
        if (st) this.mediaIndex?.set(served, { size: st.size, used: Date.now() });
        return served;
      })().finally(() => {
        this.downloadInflight.delete(file);
        for (const p of busy) this.busyMedia.delete(p);
      });
      this.downloadInflight.set(file, job);
      return job;
    });
  }

  async sendTyping(chatId: string, typing: boolean): Promise<void> {
    if (this.state !== "logged_in" || !this.client) return;
    try {
      const peer = await this.inputPeer(chatId);
      await this.client.invoke(
        new Api.messages.SetTyping({
          peer,
          action: typing ? new Api.SendMessageTypingAction() : new Api.SendMessageCancelAction(),
        }),
      );
    } catch (e) {
      this.ctx.log(`typing indicator failed: ${describeError(e)}`); // cosmetic; never fail the request
    }
  }

  // -------------------------------------------------------------- bot buttons

  /**
   * messages.clickButton: press button [row][column] of a message for the kinds only the account can act on.
   * The button is read again from the server (bots edit their keyboards all the time), so the app only sends its
   * position. Callback and game buttons ask the bot (messages.getBotCallbackAnswer); web-app buttons get the page
   * URL with the launch parameters attached (messages.requestWebView). Every other kind is the app's job.
   */
  async clickButton(chatId: string, messageId: string, row: number, column: number): Promise<ButtonAnswer> {
    const client = this.requireClient();
    return this.guard(async () => {
      const peer = await this.inputPeer(chatId);
      const id = parseMsgId(messageId);
      const [raw] = await client.getMessages(peer, { ids: id });
      if (!(raw instanceof Api.Message)) throw new BridgeError("not_found", "This message is no longer available");
      const markup = raw.replyMarkup;
      const rows = markup instanceof Api.ReplyInlineMarkup || markup instanceof Api.ReplyKeyboardMarkup ? markup.rows : [];
      const r = rows[row];
      const button = r instanceof Api.KeyboardButtonRow ? r.buttons[column] : undefined;
      if (!button) throw new BridgeError("not_found", "This button is no longer on the message");

      if (button instanceof Api.KeyboardButtonCallback || button instanceof Api.KeyboardButtonGame) {
        if (button instanceof Api.KeyboardButtonCallback && button.requiresPassword) {
          throw new BridgeError("unsupported", "This button needs your two-step verification password; use Telegram for it.");
        }
        const a = await client.invoke(
          new Api.messages.GetBotCallbackAnswer({
            peer,
            msgId: id,
            data: button instanceof Api.KeyboardButtonCallback ? button.data : undefined,
            game: button instanceof Api.KeyboardButtonGame ? true : undefined,
          }),
        );
        const out: ButtonAnswer = {};
        if (a.message) out.message = a.message;
        if (a.alert) out.alert = true;
        if (a.url) out.url = a.url;
        return out;
      }

      if (button instanceof Api.KeyboardButtonWebView || button instanceof Api.KeyboardButtonSimpleWebView) {
        const bot = await this.inputUserOfBot(raw, peer);
        const platform = process.platform === "win32" ? "tdesktop" : "macos";
        const res =
          button instanceof Api.KeyboardButtonSimpleWebView
            ? await client.invoke(new Api.messages.RequestSimpleWebView({ bot, url: button.url, platform }))
            : await client.invoke(new Api.messages.RequestWebView({ peer, bot, url: button.url, platform }));
        return { url: res.url };
      }

      throw new BridgeError("unsupported", "This button is handled by the app");
    });
  }

  /** The bot behind a message (the one it was sent via, its sender, or the bot the chat is with) as an InputUser. */
  private async inputUserOfBot(raw: Api.Message, chatPeer: Api.TypeInputPeer): Promise<Api.TypeInputUser> {
    const toUser = (p: Api.TypeInputPeer): Api.TypeInputUser | undefined => {
      if (p instanceof Api.InputPeerUser) return new Api.InputUser({ userId: p.userId, accessHash: p.accessHash });
      if (p instanceof Api.InputPeerSelf) return new Api.InputUserSelf();
      return undefined;
    };
    const candidates: string[] = [];
    if (raw.viaBotId) candidates.push(String(raw.viaBotId));
    if (raw.fromId instanceof Api.PeerUser) candidates.push(peerKey(raw.fromId));
    for (const id of candidates) {
      try {
        const u = toUser(await this.inputPeer(id));
        if (u) return u;
      } catch {
        /* try the next */
      }
    }
    const own = toUser(chatPeer);
    if (own) return own;
    throw new BridgeError("not_found", "Could not tell which bot this button belongs to");
  }

  // -------------------------------------------------------------- update sequence (pts) and catch-up

  private get stateFile() {
    return join(this.ctx.dataDir, "updates-state.json");
  }

  private loadUpdateState(selfId: string) {
    let text: string;
    try {
      text = readFileSync(this.stateFile, "utf8");
    } catch {
      return;
    }
    const saved = parseSavedState(text, selfId);
    if (!saved) return;
    this.updState = { pts: saved.pts, qts: saved.qts, date: saved.date, seq: saved.seq };
    for (const [id, pts] of Object.entries(saved.channels)) this.chanPts.set(id, pts);
  }

  private saveUpdateState() {
    this.stateDirty = false;
    const st = this.updState;
    if (!st || !this.self) return;
    const channels: Record<string, number> = {};
    for (const [id, pts] of this.chanPts) channels[id] = pts;
    const saved: SavedUpdateState = { selfId: this.self.id, ...st, channels };
    try {
      atomicWrite(this.stateFile, JSON.stringify(saved), 0o600);
    } catch (e) {
      this.ctx.log("could not save update state", describeError(e));
    }
  }

  private markStateDirty() {
    if (this.stateDirty) return;
    this.stateDirty = true;
    this.stateTimer = setTimeout(() => {
      this.stateTimer = undefined;
      if (this.stateDirty) this.saveUpdateState();
    }, STATE_SAVE_DELAY_MS);
    this.stateTimer.unref?.();
  }

  /**
   * Keep the account-wide and per-channel pts in step with one update. A step that does not follow the local
   * position is a gap; after a short grace period (updates may arrive out of order) the difference is fetched.
   * `channelsOnly`: the update came out of getDifference, whose state replaces the common pts afterwards.
   */
  private trackPts(u: unknown, opts: { channelsOnly?: boolean } = {}) {
    const st = this.updState;
    const c = opts.channelsOnly ? undefined : commonPts(u);
    if (c && st) {
      const verdict = judgePts(st.pts, c);
      if (verdict === "applied") {
        st.pts = c.pts;
        this.markStateDirty();
      } else if (verdict === "gap") {
        this.noteGap(c.pts - c.ptsCount);
      }
      const date = (u as { date?: unknown }).date;
      if (typeof date === "number" && date > st.date) st.date = date;
    }
    const ch = channelPts(u);
    if (ch) {
      const local = this.chanPts.get(ch.chatId);
      if (local === undefined) {
        this.chanPts.set(ch.chatId, ch.pts); // first sign of life from this channel: start counting here
        this.markStateDirty();
        return;
      }
      const verdict = judgePts(local, ch);
      if (verdict === "applied") {
        this.chanPts.set(ch.chatId, ch.pts);
        this.markStateDirty();
      } else if (verdict === "gap") {
        this.noteChannelGap(ch.chatId, ch.pts - ch.ptsCount);
      }
    }
  }

  /** Updates returned by a request (sends, forwards, reads) move the pts too; see createClient. */
  private noteResultUpdates(result: unknown) {
    if (!this.self) return;
    const st = this.updState;
    if (result instanceof Api.Updates || result instanceof Api.UpdatesCombined) {
      this.rememberEntities([...result.users, ...result.chats]);
      for (const u of result.updates) this.trackPts(u);
      if (st && result.date > st.date) st.date = result.date;
    } else if (result instanceof Api.UpdateShort) {
      this.trackPts(result.update);
      if (st && result.date > st.date) st.date = result.date;
    } else if (
      result instanceof Api.UpdateShortSentMessage ||
      result instanceof Api.UpdateShortMessage ||
      result instanceof Api.UpdateShortChatMessage
    ) {
      this.trackPts(result);
    }
  }

  private noteGap(expectedPts: number) {
    this.gapTarget = Math.max(this.gapTarget, expectedPts);
    if (this.gapTimer) return;
    this.gapTimer = setTimeout(() => {
      this.gapTimer = undefined;
      const target = this.gapTarget;
      this.gapTarget = 0;
      if (this.updState && this.updState.pts < target) void this.catchUp("pts gap");
    }, GAP_GRACE_MS);
    this.gapTimer.unref?.();
  }

  private noteChannelGap(chatId: string, expectedPts: number) {
    this.channelGapTargets.set(chatId, Math.max(this.channelGapTargets.get(chatId) ?? 0, expectedPts));
    if (this.channelGapTimers.has(chatId)) return;
    const t = setTimeout(() => {
      this.channelGapTimers.delete(chatId);
      const target = this.channelGapTargets.get(chatId) ?? 0;
      this.channelGapTargets.delete(chatId);
      const local = this.chanPts.get(chatId);
      if (local !== undefined && local < target) void this.channelCatchUp(chatId, "pts gap");
    }, GAP_GRACE_MS);
    t.unref?.();
    this.channelGapTimers.set(chatId, t);
  }

  /** A dialog read from the server tells the channel's current pts. */
  private noteDialogPts(chatId: string, pts: number) {
    const local = this.chanPts.get(chatId);
    if (local !== undefined && local >= pts) return;
    this.chanPts.set(chatId, pts);
    this.markStateDirty();
    // Only a channel the app already shows is worth the extra request for the messages in between; for the rest
    // the dialog itself (last message, unread count) is the catch-up.
    if (local !== undefined && this.chats.has(chatId)) void this.channelCatchUp(chatId, "dialog ahead", local);
  }

  /**
   * Fetch everything the server queued since our last known position (updates.getDifference), on connect,
   * reconnect, UpdatesTooLong, a pts gap and connection.resume. Missed messages are delivered as message.new
   * (live when fresh, so a message that came in while the laptop was asleep still notifies, as in Telegram's own
   * apps), then the affected chats are re-read for their exact unread counts.
   */
  private catchUp(reason: string): Promise<void> {
    if (this.catchUpJob) {
      this.catchUpAgain = true;
      return this.catchUpJob;
    }
    const job = (async () => {
      do {
        this.catchUpAgain = false;
        try {
          await this.catchUpOnce(reason);
        } catch (e) {
          if (isAuthError(e)) {
            void this.onSessionLost();
            return;
          }
          this.ctx.log(`telegram catch-up (${reason}) failed: ${describeError(e)}`);
          return;
        }
      } while (this.catchUpAgain && !this.stopped);
    })().finally(() => {
      this.catchUpJob = undefined;
    });
    this.catchUpJob = job;
    return job;
  }

  private async catchUpOnce(reason: string) {
    const client = this.client;
    if (!client || !this.self || this.state !== "logged_in") return;
    const boot = this.bootId;
    const st = this.updState;
    if (!st) {
      // Nothing to resume from: start counting now.
      const s = await client.invoke(new Api.updates.GetState());
      if (boot !== this.bootId || this.client !== client) return;
      this.updState = { pts: s.pts, qts: s.qts, date: s.date, seq: s.seq };
      this.saveUpdateState();
      return;
    }

    const touched = new Set<string>();
    let tooLong = false;
    let messages = 0;
    for (let page = 0; page < 50; page++) {
      const d = await client.invoke(
        new Api.updates.GetDifference({ pts: st.pts, date: st.date, qts: st.qts, ptsTotalLimit: DIFFERENCE_TOTAL_LIMIT }),
      );
      if (boot !== this.bootId || this.client !== client) return;
      if (d instanceof Api.updates.DifferenceEmpty) {
        st.date = d.date;
        st.seq = d.seq;
        break;
      }
      if (d instanceof Api.updates.DifferenceTooLong) {
        st.pts = d.pts;
        tooLong = true;
        break;
      }
      this.rememberEntities([...d.users, ...d.chats]);
      const news = d.newMessages.filter((m): m is RawMessage => m instanceof Api.Message || m instanceof Api.MessageService);
      messages += news.length;
      await this.processMissed(news, touched);
      for (const u of d.otherUpdates) await this.applyDifferenceUpdate(u, touched);
      const s = d instanceof Api.updates.Difference ? d.state : d.intermediateState;
      st.pts = s.pts;
      st.qts = s.qts;
      st.date = s.date;
      st.seq = s.seq;
      if (d instanceof Api.updates.Difference) break;
    }
    if (messages || tooLong) this.ctx.log(`telegram catch-up (${reason}): ${tooLong ? "too long, re-reading chats" : `${messages} missed message(s)`}`);
    if (tooLong) await this.resyncChats();
    else if (touched.size) await this.refreshDialogs([...touched]);
    this.saveUpdateState();
  }

  /** One update out of a difference: messages are delivered, UpdateChannelTooLong starts that channel's catch-up. */
  private async applyDifferenceUpdate(u: Api.TypeUpdate, touched: Set<string>) {
    if (u instanceof Api.UpdateNewMessage || u instanceof Api.UpdateNewChannelMessage) {
      this.trackPts(u, { channelsOnly: true });
      const raw = u.message;
      if (raw instanceof Api.Message || raw instanceof Api.MessageService) await this.processMissed([raw], touched);
      return;
    }
    await this.onUpdate(u, { fromDifference: true });
  }

  /**
   * Deliver messages that were not pushed to us (a difference page, the gap of a channel). Per chat and in order,
   * through the same queue as live messages; replies are resolved with one request per chat rather than one per
   * message. Messages already shown (seen live before the gap was noticed, or returned by our own send) are skipped.
   */
  private async processMissed(raws: RawMessage[], touched?: Set<string>) {
    const byChat = new Map<string, RawMessage[]>();
    for (const raw of raws) {
      const peer = (raw as { peerId?: Api.TypePeer }).peerId;
      if (!peer) continue; // MessageEmpty
      const list = byChat.get(peerKey(peer)) ?? [];
      list.push(raw);
      byChat.set(peerKey(peer), list);
    }
    for (const [chatId, list] of byChat) {
      await this.serial(async () => {
        list.sort((a, b) => a.id - b.id);
        const fresh: { raw: RawMessage; msg: Message }[] = [];
        for (const raw of list) {
          const msg = await this.toProtocol(raw);
          if (!msg) continue;
          if (isPinNotice(raw)) this.invalidatePins(chatId);
          const key = `${chatId}:${msg.id}`;
          const known = this.sentKeys.has(key) || this.recent.has(key);
          this.rememberMessage(msg);
          if (!known) fresh.push({ raw, msg });
        }
        if (!fresh.length) return;
        await this.fillReplies(
          chatId,
          fresh.map((x) => x.raw),
          fresh.map((x) => x.msg),
        );
        // The app needs the chat before the first message.new of a chat it has not seen.
        if (!this.chats.has(chatId)) await this.ensureChat(fresh[0]!.msg);
        for (const { raw, msg } of fresh) {
          if (!msg.fromMe) this.stopTyping(chatId, msg.senderId);
          const silent = (raw as { silent?: boolean }).silent === true;
          // Telegram's apps notify for what came in while the connection was down (a closed laptop), not for what a
          // fresh start finds; here "down" is anything after the stream's last sign of life, within a day.
          const missedWhileDown =
            this.streamAliveAt > 0 && msg.timestamp >= this.streamAliveAt - LIVE_MAX_AGE_MS && Date.now() - msg.timestamp <= DAY_MS;
          const live = !msg.fromMe && msg.kind !== "system" && !silent && (isFresh(msg.timestamp) || missedWhileDown);
          this.ctx.emit("message.new", { message: msg, live });
        }
        touched?.add(chatId);
      });
    }
  }

  /** Catch up one channel (updates.getChannelDifference) from its last known pts, or from `fromPts`. */
  private channelCatchUp(chatId: string, reason: string, fromPts?: number): Promise<void> {
    const running = this.channelJobs.get(chatId);
    if (running) {
      this.channelAgain.add(chatId);
      return running;
    }
    const job = (async () => {
      let start = fromPts;
      do {
        this.channelAgain.delete(chatId);
        try {
          await this.channelCatchUpOnce(chatId, reason, start);
        } catch (e) {
          if (isAuthError(e)) {
            void this.onSessionLost();
            return;
          }
          this.ctx.log(`telegram channel catch-up ${chatId} (${reason}) failed: ${describeError(e)}`);
          return;
        }
        start = undefined;
      } while (this.channelAgain.has(chatId) && !this.stopped);
    })().finally(() => {
      this.channelJobs.delete(chatId);
    });
    this.channelJobs.set(chatId, job);
    return job;
  }

  private async channelCatchUpOnce(chatId: string, reason: string, fromPts?: number) {
    const client = this.client;
    if (!client || !this.self || this.state !== "logged_in") return;
    const boot = this.bootId;
    const peer = await this.inputPeer(chatId);
    if (!(peer instanceof Api.InputPeerChannel)) return;
    const channel = new Api.InputChannel({ channelId: peer.channelId, accessHash: peer.accessHash });
    const known = fromPts ?? this.chanPts.get(chatId);
    if (known === undefined) {
      // Never saw this channel's pts: the dialog gives it, and whatever is newer than the last message we know.
      await this.fillChannelGap(chatId);
      return;
    }
    let pts: number = known;

    const touched = new Set<string>();
    let messages = 0;
    for (let page = 0; page < CHANNEL_DIFFERENCE_PAGES; page++) {
      const d: Api.updates.TypeChannelDifference = await client.invoke(
        new Api.updates.GetChannelDifference({ channel, filter: new Api.ChannelMessagesFilterEmpty(), pts, limit: 100 }),
      );
      if (boot !== this.bootId || this.client !== client) return;
      if (d instanceof Api.updates.ChannelDifferenceEmpty) {
        pts = d.pts;
        break;
      }
      if (d instanceof Api.updates.ChannelDifferenceTooLong) {
        // Too much happened: the server sends the dialog as it is now plus the latest messages.
        this.rememberEntities([...d.users, ...d.chats]);
        if (d.dialog instanceof Api.Dialog) {
          const dialogPts = d.dialog.pts;
          if (typeof dialogPts === "number") pts = dialogPts;
          const chats = this.ingestDialogs({ dialogs: [d.dialog], messages: d.messages, chats: d.chats, users: d.users });
          for (const chat of chats) this.ctx.emit("chat.upsert", { chat });
        }
        const latest = d.messages.filter((m): m is RawMessage => m instanceof Api.Message || m instanceof Api.MessageService);
        messages += latest.length;
        await this.processMissed(latest);
        break;
      }
      this.rememberEntities([...d.users, ...d.chats]);
      // Set first: the updates of this page carry pts values up to d.pts and must not read as gaps.
      pts = d.pts;
      this.chanPts.set(chatId, pts);
      const news = d.newMessages.filter((m): m is RawMessage => m instanceof Api.Message || m instanceof Api.MessageService);
      messages += news.length;
      await this.processMissed(news, touched);
      for (const u of d.otherUpdates) await this.applyDifferenceUpdate(u, touched);
      if (d.final !== false) break;
    }
    this.chanPts.set(chatId, pts);
    this.markStateDirty();
    if (messages) this.ctx.log(`telegram channel catch-up ${chatId} (${reason}): ${messages} missed message(s)`);
    if (touched.size) await this.refreshDialogs([...touched]);
  }

  /** Channel without a known pts: read its dialog (which has one) and the messages after the last one we know. */
  private async fillChannelGap(chatId: string) {
    const client = this.client;
    if (!client) return;
    const lastKnown = Number(this.chats.get(chatId)?.lastMessage?.id ?? 0);
    const chat = await this.refreshDialog(chatId, true); // records the dialog's pts via ingestDialogs
    if (!chat || !lastKnown) return;
    const top = Number(chat.lastMessage?.id ?? 0);
    if (top <= lastKnown) return;
    const peer = await this.inputPeer(chatId);
    const batch = await client.getMessages(peer, { minId: lastKnown, limit: 100 });
    const raws = [...batch] as RawMessage[]; // GramJS types every entry as Message; processMissed skips MessageEmpty
    for (const raw of raws) this.rememberEntities((raw as unknown as RawUpdate)._entities);
    await this.processMissed(raws);
  }

  /** Re-read dialogs from the server (exact unread counts, last messages) and announce them. */
  private async refreshDialogs(chatIds: string[]) {
    const client = this.client;
    if (!client) return;
    for (let i = 0; i < chatIds.length; i += 100) {
      const slice = chatIds.slice(i, i + 100);
      const peers: Api.InputDialogPeer[] = [];
      for (const id of slice) {
        try {
          peers.push(new Api.InputDialogPeer({ peer: await this.inputPeer(id) }));
        } catch {
          /* unknown peer: nothing to refresh */
        }
      }
      if (!peers.length) continue;
      try {
        const r = await client.invoke(new Api.messages.GetPeerDialogs({ peers }));
        for (const chat of this.ingestDialogs(r)) this.ctx.emit("chat.upsert", { chat });
      } catch (e) {
        if (isAuthError(e)) throw e;
        this.ctx.log(`could not refresh ${slice.length} chat(s) after catch-up: ${describeError(e)}`);
      }
    }
  }

  /** Too much was missed to replay: read the chat list again and announce every chat, like a fresh chats.list. */
  private async resyncChats() {
    try {
      for (const chat of await this.listChats(200)) this.ctx.emit("chat.upsert", { chat });
    } catch (e) {
      if (isAuthError(e)) throw e;
      this.ctx.log(`could not re-read chats after a long gap: ${describeError(e)}`);
    }
  }

  /**
   * connection.resume: the computer woke up or changed networks. GramJS notices a dead socket only at its next
   * ping (several seconds, longer under load); a request that is not answered quickly tells us now, and the
   * reconnect is forced. A live socket just catches up, since the server kept nothing for us while we slept.
   */
  async resume(): Promise<void> {
    const client = this.client;
    if (!client || this.state !== "logged_in" || !this.self) return;
    const alive = await Promise.race([
      client.invoke(new Api.updates.GetState()).then(
        () => true,
        () => false,
      ),
      sleep(RESUME_PROBE_MS).then(() => false),
    ]);
    if (this.client !== client) return;
    if (!alive) {
      this.ctx.log("telegram: no answer after resume, reconnecting");
      this.emitConnection("connecting", "reconnecting");
      try {
        (client as unknown as { _sender?: { reconnect?: () => void } })._sender?.reconnect?.();
      } catch (e) {
        this.ctx.log("telegram: forced reconnect failed", describeError(e));
      }
      return; // the connected event that follows runs the catch-up
    }
    await this.catchUp("resume");
  }

  // -------------------------------------------------------------- realtime

  private async onNewMessage(event: NewMessageEvent) {
    if (!this.self) return;
    const raw = event.message;
    this.rememberEntities(event._entities);
    this.rememberEntities((raw as unknown as RawUpdate)._entities);
    await this.serial(() => this.processNew(raw));
  }

  /** Run `fn` after every previously queued incoming-message job; a failing job never blocks the queue. */
  private serial<T>(fn: () => Promise<T>): Promise<T> {
    const run = this.newQueue.then(fn);
    this.newQueue = run.then(
      () => {},
      () => {},
    );
    return run;
  }

  private async processNew(raw: RawMessage) {
    const msg = await this.toProtocol(raw);
    if (!msg) return;
    const date = (raw as { date?: number }).date;
    if (this.updState && typeof date === "number" && date > this.updState.date) this.updState.date = date; // "date of last update"
    if (isPinNotice(raw)) this.invalidatePins(msg.chatId);
    const key = `${msg.chatId}:${msg.id}`;
    const alreadySent = this.sentKeys.has(key); // returned by our own sendMessage
    // Already known from a dialog/list/earlier event: a catch-up replay. It must not bump unread or notify again.
    const duplicate = this.recent.has(key);
    this.rememberMessage(msg);
    if (alreadySent) return;

    await this.fillReplies(msg.chatId, [raw], [msg]);
    if (!msg.fromMe) this.stopTyping(msg.chatId, msg.senderId);

    // Telegram's own clients also notify for messages fetched by catch-up (getDifference), but not for stale ones.
    // Not live: our own messages, silent sends (no notification by sender's choice), service lines, anything > 2 min old.
    const silent = (raw as { silent?: boolean }).silent === true;
    const live = !duplicate && !msg.fromMe && msg.kind !== "system" && !silent && isFresh(msg.timestamp);

    // The app needs the chat (title, avatar, mute) before the first message.new of a chat it has not seen.
    const fresh = await this.ensureChat(msg);
    if (live) this.ctx.log(`telegram: live message ${msg.id} in ${msg.chatId} delivered ${Date.now() - msg.timestamp} ms after its send time (second precision)`);
    this.ctx.emit("message.new", { message: msg, live });
    this.bumpChat(msg, { duplicate, fresh });
  }

  /**
   * Make sure the chat of `msg` is known and announced. Returns true when it was unknown, in which case the
   * dialog fetched from the server already counts this message in its unread number.
   */
  private async ensureChat(msg: Message): Promise<boolean> {
    if (this.chats.has(msg.chatId)) return false;
    try {
      await this.refreshDialog(msg.chatId, true); // exact server state, emitted as chat.upsert
    } catch (e) {
      this.ctx.log(`could not load new chat ${msg.chatId}: ${describeError(e)}`);
    }
    if (!this.chats.has(msg.chatId)) {
      // Server lookup failed: announce what we can build from the message and the entity cache.
      const ent = await this.ensureEntity(msg.chatId);
      const scope = ent ? notifyScopeOf(ent) : "chats";
      const chat = ent ? buildChat(ent, undefined, msg, this.mapEnv(), this.notifyScope.get(scope)) : undefined;
      if (chat) {
        chat.unread = msg.fromMe ? 0 : 1;
        this.chatScope.set(chat.id, scope);
        this.chats.set(chat.id, chat);
        this.ctx.emit("chat.upsert", { chat });
      }
    }
    return true;
  }

  /** Update the cached chat for a new message and emit the chat.upsert that follows every message.new. */
  private bumpChat(msg: Message, opts: { duplicate: boolean; fresh: boolean }) {
    const chat = this.chats.get(msg.chatId);
    if (!chat) return;
    if (msg.timestamp >= chat.timestamp) {
      chat.lastMessage = msg;
      chat.timestamp = msg.timestamp;
    }
    if (msg.fromMe) {
      // Writing from another device reads the chat (the server follows up with UpdateReadHistoryInbox).
      chat.unread = 0;
      delete chat.markedUnread;
    } else if (!opts.duplicate && !opts.fresh) {
      chat.unread += 1;
    }
    this.ctx.emit("chat.upsert", { chat });
  }

  /**
   * Raw update handler. `fromDifference`: the update was fetched by getDifference / getChannelDifference rather
   * than pushed; the stream position then comes from the difference itself, so no gap detection, and plain
   * messages (normally the NewMessage handler's job) are delivered from here.
   */
  private async onUpdate(u: unknown, opts: { fromDifference?: boolean } = {}) {
    if (u instanceof UpdateConnectionState) {
      const prev = this.connState;
      const s =
        u.state === UpdateConnectionState.connected
          ? "online"
          : u.state === UpdateConnectionState.disconnected
            ? "offline"
            : "connecting";
      this.emitConnection(s, s === "connecting" ? "reconnecting" : undefined);
      if (s === "online" && prev && prev !== "online" && this.self) {
        // The server drops what it could not deliver while we were away: fetch it (and re-arm the stream).
        void this.catchUp("reconnect");
      }
      return;
    }
    if (u instanceof Api.UpdateLoginToken) {
      this.onQrScanned(); // arrives before login completes, so before `self` is known
      return;
    }
    if (!this.self) return;
    if (!opts.fromDifference) this.streamAliveAt = Date.now();
    this.rememberEntities((u as RawUpdate)._entities);
    if (u instanceof Api.UpdatesTooLong) {
      // The server's way of saying "too much happened, ask me": the official clients call getDifference here.
      void this.catchUp("updatesTooLong");
      return;
    }
    if (u instanceof Api.UpdateChannelTooLong) {
      void this.channelCatchUp(channelKey(u.channelId), "channelTooLong");
      return;
    }
    this.trackPts(u, { channelsOnly: !!opts.fromDifference });

    if (u instanceof Api.UpdateNewMessage || u instanceof Api.UpdateNewChannelMessage) {
      // Plain messages arrive through the NewMessage handler; it drops service messages, so take those here.
      if (u.message instanceof Api.MessageService) {
        const svc = u.message;
        await this.serial(() => this.processNew(svc));
      } else if (opts.fromDifference && u.message instanceof Api.Message) {
        await this.processMissed([u.message]);
      }
    } else if (u instanceof Api.UpdateEditMessage || u instanceof Api.UpdateEditChannelMessage) {
      await this.onEdit(u.message);
    } else if (u instanceof Api.UpdateDeleteMessages) {
      await this.applyDelete(u.messages);
    } else if (u instanceof Api.UpdateDeleteChannelMessages) {
      await this.applyDelete(u.messages, channelKey(u.channelId));
    } else if (u instanceof Api.UpdateReadHistoryOutbox) {
      this.onReadOutbox(peerKey(u.peer), u.maxId);
    } else if (u instanceof Api.UpdateReadChannelOutbox) {
      this.onReadOutbox(channelKey(u.channelId), u.maxId);
    } else if (u instanceof Api.UpdateReadHistoryInbox) {
      this.onReadInbox(peerKey(u.peer), u.stillUnreadCount);
    } else if (u instanceof Api.UpdateReadChannelInbox) {
      this.onReadInbox(channelKey(u.channelId), u.stillUnreadCount);
    } else if (u instanceof Api.UpdateUserTyping) {
      this.onTyping(String(u.userId), String(u.userId), u.action);
    } else if (u instanceof Api.UpdateChatUserTyping) {
      this.onTyping(utils.getPeerId(new Api.PeerChat({ chatId: u.chatId })), peerKey(u.fromId), u.action);
    } else if (u instanceof Api.UpdateChannelUserTyping) {
      this.onTyping(channelKey(u.channelId), peerKey(u.fromId), u.action);
    } else if (u instanceof Api.UpdateUserStatus) {
      this.onUserStatus(String(u.userId), u.status);
    } else if (u instanceof Api.UpdateMessageReactions) {
      this.onReactions(peerKey(u.peer), u.msgId, u.reactions);
    } else if (u instanceof Api.UpdateNotifySettings) {
      if (u.peer instanceof Api.NotifyPeer) this.onNotifyChanged(peerKey(u.peer.peer), u.notifySettings);
      else if (u.peer instanceof Api.NotifyUsers) this.onScopeNotifyChanged("users", u.notifySettings);
      else if (u.peer instanceof Api.NotifyChats) this.onScopeNotifyChanged("chats", u.notifySettings);
      else if (u.peer instanceof Api.NotifyBroadcasts) this.onScopeNotifyChanged("broadcasts", u.notifySettings);
    } else if (u instanceof Api.UpdateDialogUnreadMark) {
      if (u.peer instanceof Api.DialogPeer) this.onUnreadMark(peerKey(u.peer.peer), !!u.unread);
    } else if (u instanceof Api.UpdateDialogPinned) {
      if (u.peer instanceof Api.DialogPeer) this.onPinChanged(peerKey(u.peer.peer), !!u.pinned);
    } else if (u instanceof Api.UpdatePinnedMessages) {
      this.invalidatePins(peerKey(u.peer));
    } else if (u instanceof Api.UpdatePinnedChannelMessages) {
      this.invalidatePins(channelKey(u.channelId));
    } else if (u instanceof Api.UpdateFolderPeers) {
      for (const fp of u.folderPeers) this.onArchiveChanged(peerKey(fp.peer), fp.folderId === 1);
    } else if (
      u instanceof Api.UpdateDialogFilter ||
      u instanceof Api.UpdateDialogFilters ||
      u instanceof Api.UpdateDialogFilterOrder
    ) {
      // Folder created / edited / deleted / reordered (here or on another device): refetch the full list.
      this.scheduleFolders(800);
    }
  }

  private async onEdit(raw: RawMessage, opts?: { forceOut?: boolean }): Promise<Message | undefined> {
    const msg = await this.toProtocol(raw, opts);
    if (!msg) return undefined;
    await this.keepOrFillReply(msg, raw, this.recent.get(`${msg.chatId}:${msg.id}`));
    this.replies.forget(msg.chatId, msg.id); // what a later reply to this message quotes has changed
    this.rememberMessage(msg);
    this.ctx.emit("message.update", { message: msg });
    this.pinnedMessageChanged(msg.chatId, msg.id);

    const chat = this.chats.get(msg.chatId);
    if (chat?.lastMessage?.id === msg.id) {
      chat.lastMessage = msg;
      this.ctx.emit("chat.upsert", { chat });
    }
    return msg;
  }

  /** chatId is only known for channel deletions; otherwise use the id -> chat index. */
  private async applyDelete(ids: number[], chatId?: string) {
    const refresh = new Set<string>();
    for (const id of ids) {
      const cid = chatId ?? this.idToChat.get(id);
      if (!cid) continue; // never seen this message in this process: cannot attribute it
      this.recent.delete(`${cid}:${id}`);
      this.replies.forget(cid, id);
      this.ctx.emit("message.delete", { chatId: cid, messageId: String(id) });
      this.pinnedMessageChanged(cid, String(id));
      if (this.chats.get(cid)?.lastMessage?.id === String(id)) refresh.add(cid);
    }
    for (const cid of refresh) {
      try {
        await this.refreshDialog(cid, true); // new last message + exact unread
      } catch (e) {
        this.ctx.log(`could not refresh ${cid} after delete: ${describeError(e)}`);
      }
    }
  }

  private onReadOutbox(chatId: string, maxId: number) {
    if (maxId <= (this.readOutbox.get(chatId) ?? 0)) return;
    this.readOutbox.set(chatId, maxId);

    const chat = this.chats.get(chatId);
    const last = chat?.lastMessage;
    const lastWasPending = !!last && last.fromMe && last.status !== undefined && last.status !== "read" && Number(last.id) <= maxId;

    for (const m of this.recent.values()) {
      if (m.chatId !== chatId || !m.fromMe || m.status === undefined || m.status === "read") continue;
      if (Number(m.id) <= maxId) {
        m.status = "read";
        this.ctx.emit("message.update", { message: m });
      }
    }
    if (chat && last && lastWasPending) {
      last.status = "read";
      this.ctx.emit("chat.upsert", { chat });
    }
  }

  /** Read on another device (or by us): the server tells us how many messages are still unread. */
  private onReadInbox(chatId: string, stillUnread: number) {
    const chat = this.chats.get(chatId);
    if (!chat) return;
    const clearMark = stillUnread === 0 && !!chat.markedUnread; // reading also clears a manual "unread" flag
    if (chat.unread === stillUnread && !clearMark) return;
    chat.unread = stillUnread;
    if (clearMark) delete chat.markedUnread;
    this.ctx.emit("chat.upsert", { chat });
  }

  private onUnreadMark(chatId: string, marked: boolean) {
    const chat = this.chats.get(chatId);
    if (!chat || !!chat.markedUnread === marked) return;
    if (marked) chat.markedUnread = true;
    else delete chat.markedUnread;
    this.ctx.emit("chat.upsert", { chat });
  }

  private onTyping(chatId: string, userId: string, action: Api.TypeSendMessageAction) {
    if (userId === this.self?.id) return; // our own typing from another device
    const typing = !(action instanceof Api.SendMessageCancelAction);
    const name = entityName(this.entities.get(userId));
    const k = `${chatId}|${userId}`;
    const old = this.typingTimers.get(k);
    if (old) clearTimeout(old);
    this.typingTimers.delete(k);
    this.ctx.emit("typing", { chatId, userId, name, typing });
    if (typing) {
      // Telegram never says "stopped"; the indicator simply expires.
      this.typingTimers.set(
        k,
        setTimeout(() => {
          this.typingTimers.delete(k);
          this.ctx.emit("typing", { chatId, userId, name, typing: false });
        }, TYPING_TTL_MS),
      );
    }
  }

  private stopTyping(chatId: string, userId: string) {
    const k = `${chatId}|${userId}`;
    const t = this.typingTimers.get(k);
    if (!t) return;
    clearTimeout(t);
    this.typingTimers.delete(k);
    this.ctx.emit("typing", { chatId, userId, name: entityName(this.entities.get(userId)), typing: false });
  }

  private onUserStatus(userId: string, status: Api.TypeUserStatus) {
    if (userId === this.self?.id) return;
    const p = userPresence(status);
    if (!p) return;
    const ent = this.entities.get(userId);
    if (ent instanceof Api.User) ent.status = status;
    const chat = this.chats.get(userId);
    if (chat) {
      chat.online = p.online;
      if (p.lastSeen !== undefined) chat.lastSeen = p.lastSeen;
    }
    this.ctx.emit("presence", { chatId: userId, online: p.online, lastSeen: p.lastSeen });
  }

  private onReactions(chatId: string, msgId: number, reactions: Api.TypeMessageReactions) {
    const cached = this.recent.get(`${chatId}:${msgId}`);
    if (!cached) return; // the UI cannot have this message unless it was listed (which caches it)
    const r = reactions instanceof Api.MessageReactions ? mapReactions(reactions) : undefined;
    if (r) cached.reactions = r;
    else delete cached.reactions;
    this.ctx.emit("message.update", { message: cached });
  }

  /** Recompute muted/mutedUntil of a cached chat from its own settings + scope default. True if it changed. */
  private applyMute(chat: Chat): boolean {
    const scope = this.chatScope.get(chat.id);
    const m = muteInfo(this.notifyOwn.get(chat.id), scope ? this.notifyScope.get(scope) : undefined);
    if (!!chat.muted === m.muted && chat.mutedUntil === m.mutedUntil) return false;
    chat.muted = m.muted;
    if (m.mutedUntil !== undefined) chat.mutedUntil = m.mutedUntil;
    else delete chat.mutedUntil;
    return true;
  }

  private onNotifyChanged(chatId: string, ns: Api.TypePeerNotifySettings) {
    this.notifyOwn.set(chatId, ns);
    const chat = this.chats.get(chatId);
    if (chat && this.applyMute(chat)) this.ctx.emit("chat.upsert", { chat });
  }

  private onScopeNotifyChanged(scope: NotifyScope, ns: Api.TypePeerNotifySettings) {
    this.notifyScope.set(scope, ns);
    for (const chat of this.chats.values()) {
      if (this.chatScope.get(chat.id) === scope && this.applyMute(chat)) this.ctx.emit("chat.upsert", { chat });
    }
  }

  private onPinChanged(chatId: string, pinned: boolean) {
    const chat = this.chats.get(chatId);
    if (!chat || !!chat.pinned === pinned) return;
    chat.pinned = pinned;
    this.ctx.emit("chat.upsert", { chat });
  }

  /** Chat moved into / out of the archive (folder 1). Chats we never listed arrive with the right flag later. */
  private onArchiveChanged(chatId: string, archived: boolean) {
    const chat = this.chats.get(chatId);
    if (!chat || !!chat.archived === archived) return;
    chat.archived = archived;
    this.ctx.emit("chat.upsert", { chat });
  }
}

// ------------------------------------------------------------------ module helpers

/** Marked id ("-100…") for a raw channel id. */
const channelKey = (channelId: Parameters<typeof helpers.returnBigInt>[0]) =>
  utils.getPeerId(new Api.PeerChannel({ channelId: helpers.returnBigInt(channelId) }));

function mediaExtension(raw: Api.Message): string {
  const media = raw.media;
  let fileName: string | undefined;
  let mime: string | undefined;
  if (media instanceof Api.MessageMediaDocument && media.document instanceof Api.Document) {
    mime = media.document.mimeType;
    for (const a of media.document.attributes) {
      if (a instanceof Api.DocumentAttributeFilename) fileName = a.fileName;
    }
  }
  const ok = (e: string) => /^\.[a-z0-9]{1,8}$/i.test(e);
  const fromName = fileName ? extname(fileName).toLowerCase() : "";
  if (ok(fromName)) return fromName;
  if (mime === "application/x-tgsticker") return ".tgs";
  try {
    const e = utils.getExtension(media);
    if (e && ok(e)) return e.toLowerCase();
  } catch {
    /* fall through */
  }
  return ".bin";
}
