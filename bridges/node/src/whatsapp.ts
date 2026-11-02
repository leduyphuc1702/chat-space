// WhatsApp adapter (Baileys v7, multi-device "linked device" protocol -- no WebView, no Puppeteer).
//
// Files:  whatsapp.ts (this: socket lifecycle, events, protocol methods)
//         whatsapp-store.ts (persistent chats/contacts/messages -- Baileys v7 has no in-memory store)
//         whatsapp-map.ts (WAMessage -> protocol Message)
import {
  Browsers,
  DisconnectReason,
  downloadMediaMessage,
  extractMessageContent,
  fetchLatestBaileysVersion,
  generateMessageIDV2,
  isJidBot,
  isJidBroadcast,
  isJidGroup,
  isJidNewsletter,
  isLidUser,
  isPnUser,
  jidDecode,
  makeWASocket,
  proto,
  useMultiFileAuthState,
  type AnyMessageContent,
  type BaileysEventMap,
  type GroupMetadata,
  type PresenceData,
  type WAMessage,
  type WASocket,
  type WAVersion,
} from "baileys";
import pino from "pino";
import QRCode from "qrcode";
import { createWriteStream, existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { rename, stat, writeFile } from "node:fs/promises";
import { pipeline } from "node:stream/promises";
import { basename, extname, join } from "node:path";
import { Adapter, type AdapterContext } from "./adapter.ts";
import * as mediaCache from "./media-cache.ts";
import { rewriteWhatsAppMentions, sanitizeMentions, TtlCache } from "./mentions.ts";
import {
  BridgeError,
  isFresh,
  type Chat,
  type ChatKind,
  type Member,
  type MentionSpan,
  type Message,
  type StorageConfig,
} from "./types.ts";
import { fallbackName, isStorable, mapMessage, mediaContent, statusOf, type MapCtx } from "./whatsapp-map.ts";
import {
  MAX_MESSAGES_PER_CHAT,
  WAStore,
  applyReaction,
  applyReceipt,
  editedContent,
  normJid,
  num,
  plainContent,
  plainMsg,
  type StoredChat,
  type StoredMsg,
} from "./whatsapp-store.ts";

type Events = Partial<BaileysEventMap>;

const Status = proto.WebMessageInfo.Status;
const Stub = proto.WebMessageInfo.StubType;
const META_AI_PN = "13135550002@s.whatsapp.net";
const AVATAR_TTL_MS = 24 * 3600_000;
const AVATAR_NEG_TTL_MS = 6 * 3600_000;
/** Protocol error string the app reacts to by calling auth.login again while the QR screen is open. */
const QR_EXPIRED = "qr_expired";
/** After Baileys ran out of QR refs (first one lives 60 s, the next ones 20 s) we reconnect this many times. */
const MAX_QR_RECONNECTS = 3;
/** A QR is dead at expiresAt; the next one (or a reconnect that yields one) must show up within this much more. */
const QR_GRACE_MS = 25_000;
const yieldLoop = () => new Promise<void>((r) => setImmediate(r));
const safeName = (s: string) => s.replace(/[^A-Za-z0-9._-]/g, "_");

export class WhatsAppAdapter extends Adapter {
  readonly platform = "whatsapp";
  // "edit" is deliberately absent: the protocol has no edit method yet (edits are received and shown).
  readonly capabilities = ["reactions", "reply", "delete", "edit", "typing", "read_receipts", "media_send", "voice", "presence", "mentions", "forward"];

  private store: WAStore;
  private readonly authDir: string;
  private readonly mediaDir: string;
  private readonly avatarDir: string;
  private readonly storeDir: string;
  private readonly logger: any;
  private readonly mc: MapCtx;

  // socket lifecycle
  private sock?: WASocket;
  private gen = 0;
  private open = false;
  private stopping = false;
  private loggingOut = false;
  private attempts = 0;
  private reconnectTimer?: ReturnType<typeof setTimeout>;
  private credsSave: Promise<void> = Promise.resolve();
  private queue: Promise<void> = Promise.resolve();
  private version?: WAVersion;

  // Browser identity. "Desktop" (+ syncFullHistory) makes Baileys declare webSubPlatform=DARWIN so the phone
  // sends more history, but WhatsApp's server was observed to drop that handshake (428 right after the client
  // payload, 4/4 runs, also under Node) while "Chrome" + syncFullHistory connects fine. So: try Desktop first,
  // fall back to Chrome when the server drops a handshake before any QR/open, and remember what worked.
  private browserChoice: "Desktop" | "Chrome" = "Desktop";
  private browserForced = false;
  private browserPersisted?: string;
  private attemptStartedAt = 0;
  private attemptProgress = false;
  private fastSwitchesLeft = 1;

  // QR login
  private qrIndex = 0;
  private qrCycles = 0;
  private lastQr?: { qr: string; image: string; expiresAt: number };
  /** bumped for every emitted QR; the watchdog only acts if no newer QR arrived since it was armed */
  private qrSerial = 0;
  private qrWatch?: ReturnType<typeof setTimeout>;

  // local storage policy (the app pushes the real one right after start)
  private storage: StorageConfig = {
    keepMediaDays: 0,
    maxCacheMB: 0,
    compressImages: false,
    maxStoredMessagesPerChat: MAX_MESSAGES_PER_CHAT,
  };
  /** media.download calls in progress, keyed by chat|message (concurrent callers share one download) */
  private downloads = new Map<string, Promise<string>>();

  // caches / transient state
  private groupMeta = new Map<string, GroupMetadata>();
  /** chats.members answers per chat, reused for a few minutes (the "@" picker is queried on every keystroke). */
  private membersCache = new TtlCache<Member[]>(5 * 60_000);
  private groupTimer?: ReturnType<typeof setInterval>;
  private lidMissing = new Set<string>();
  private ownSending = new Set<string>();
  private pendingUpserts = new Set<string>();
  private upsertTimer?: ReturnType<typeof setTimeout>;
  /** Chats the app already knows about (sent in a chat.upsert or chats.list this process). */
  private announced = new Set<string>();
  /** Read receipts that could not be sent (offline / failed); flushed when the socket opens again. */
  private pendingReads = new Map<string, any[]>();
  private historyBusyUntil = 0;
  private historyWaiters = new Map<string, () => void>();
  private historyWaitMs = 10_000; // how long a scroll-up waits for the phone's on-demand history answer
  private olderInflight = new Set<string>();
  private noMoreHistory = new Set<string>();
  private olderAdded = new Map<string, number>();
  private presenceSubscribed = new Map<string, number>();
  private presence = new Map<string, { online: boolean; lastSeen?: number }>();
  private typingState = new Map<string, ReturnType<typeof setTimeout>>();
  private availableAt = 0;
  private idleTimer?: ReturnType<typeof setTimeout>;
  private avatarInflight = new Map<string, Promise<string | undefined>>();
  private avatarNeg = new Map<string, number>();
  private avatarActive = 0;
  private avatarWaiters: (() => void)[] = [];

  constructor(ctx: AdapterContext) {
    super(ctx);
    this.authDir = join(ctx.dataDir, "auth");
    this.mediaDir = join(ctx.dataDir, "media");
    this.avatarDir = join(ctx.dataDir, "avatars");
    this.storeDir = join(ctx.dataDir, "store");
    this.store = new WAStore(this.storeDir, ctx.log);
    // Baileys logs go to stderr (fd 2) only; stdout is reserved for protocol frames.
    this.logger = pino({ level: process.env.CHATSPACE_WA_LOG || "warn" }, pino.destination(2));
    const a = this;
    this.mc = {
      get selfId() {
        return a.selfJid();
      },
      get selfName() {
        return a.self?.name || a.store.accountName;
      },
      canon: (j) => a.store.canon(j),
      nameOf: (j) => a.store.contactName(j),
      groupSize: (c) => a.groupSize(c),
    };
  }

  // ================================================================= lifecycle

  async start(_config: Record<string, unknown>): Promise<void> {
    mkdirSync(this.authDir, { recursive: true });
    this.loadBrowserChoice();
    this.store.load();
    this.store.onMerge = (to) => this.queueChatUpsert(to);
    if (this.store.accountId) {
      const user = jidDecode(this.store.accountId)?.user;
      this.self = { id: this.store.accountId, name: this.store.accountName || user || "", phone: user };
    }
    if (this.hasPairedCreds()) {
      this.setState("connecting");
      this.ctx.emit("connection", { state: "connecting", detail: "restoring session" });
      await this.connect();
    } else {
      this.self = undefined;
      this.setState("logged_out");
    }
  }

  async login(params: { method?: string; cookies?: string; phone?: string }): Promise<void> {
    if (params.method && params.method !== "qr") {
      throw new BridgeError("unsupported", "WhatsApp supports QR login only");
    }
    if (this.state === "logged_in" && this.open) return;
    // A socket is already showing a still-valid QR: show it again instead of starting a second flow. (Its QR
    // sequence and cycle counter keep running; they must not be reset by a repeated auth.login.)
    if (this.sock && this.state === "awaiting_qr" && this.lastQr && this.lastQr.expiresAt > Date.now() + 2000) {
      this.ctx.emit("auth.qr", this.lastQr);
      return;
    }
    // Otherwise (nothing running, a dead QR, a half-open pairing) restart from scratch: one socket, one flow.
    this.loggingOut = false;
    this.qrCycles = 0;
    this.attempts = 0;
    clearTimeout(this.reconnectTimer);
    this.clearQrWatch();
    // Stop the old socket before touching its auth files, or a late creds.update could rewrite them.
    this.teardownSocket();
    this.lastQr = undefined;
    // Leftovers of an unfinished pairing are useless; start from fresh keys.
    if (!this.hasPairedCreds()) rmSync(this.authDir, { recursive: true, force: true });
    this.setState("connecting");
    await this.connect();
  }

  /** Give up on pairing: tell the app the QR ran out (it restarts the flow if its QR screen is still open). */
  private expireQr() {
    clearTimeout(this.reconnectTimer);
    this.clearQrWatch();
    this.teardownSocket();
    this.lastQr = undefined;
    this.setState("logged_out", { error: QR_EXPIRED });
    this.ctx.emit("connection", { state: "offline", detail: "QR expired" });
  }

  /**
   * Safety net: whatever goes wrong between two QR codes (socket dropped, reconnect failing, no network), the app
   * must never keep showing a dead code in awaiting_qr. If no newer QR arrived shortly after the last one expired,
   * the flow is declared expired.
   */
  private armQrWatch(expiresAt: number) {
    clearTimeout(this.qrWatch);
    const serial = ++this.qrSerial;
    this.qrWatch = setTimeout(() => {
      this.qrWatch = undefined;
      if (this.stopping || this.loggingOut || this.state !== "awaiting_qr" || serial !== this.qrSerial) return;
      this.ctx.log("whatsapp: no fresh QR after the last one expired, giving up");
      this.expireQr();
    }, Math.max(0, expiresAt + QR_GRACE_MS - Date.now()));
  }

  private clearQrWatch() {
    clearTimeout(this.qrWatch);
    this.qrWatch = undefined;
    this.qrSerial++;
  }

  async logout(): Promise<void> {
    this.loggingOut = true;
    clearTimeout(this.reconnectTimer);
    this.clearQrWatch();
    const sock = this.sock;
    if (sock && this.open) {
      // Unlinks this device on the phone too.
      await Promise.race([sock.logout().catch(() => {}), new Promise((r) => setTimeout(r, 5000))]);
    }
    this.teardownSocket();
    await this.credsSave.catch(() => {});
    rmSync(this.authDir, { recursive: true, force: true });
    rmSync(this.mediaDir, { recursive: true, force: true });
    rmSync(this.avatarDir, { recursive: true, force: true });
    this.store.reset();
    this.groupMeta.clear();
    this.membersCache.clear();
    this.announced.clear();
    this.pendingReads.clear();
    this.self = undefined;
    this.lastQr = undefined;
    this.setState("logged_out");
    this.ctx.emit("connection", { state: "offline", detail: "logged out" });
    this.loggingOut = false;
  }

  async shutdown(): Promise<void> {
    this.stopping = true;
    clearTimeout(this.reconnectTimer);
    clearTimeout(this.upsertTimer);
    clearTimeout(this.idleTimer);
    this.clearQrWatch();
    if (this.groupTimer) clearInterval(this.groupTimer);
    await this.queue.catch(() => {});
    await this.store.idle(); // an asynchronous (compressing) save may still be running
    this.store.flushSync();
    this.teardownSocket();
  }

  private get browserFile() {
    return join(this.ctx.dataDir, "wa-browser.json");
  }

  private loadBrowserChoice() {
    const env = process.env.CHATSPACE_WA_BROWSER;
    if (env === "Desktop" || env === "Chrome") {
      this.browserChoice = env;
      this.browserForced = true;
      return;
    }
    try {
      const j = JSON.parse(readFileSync(this.browserFile, "utf8"));
      if (j?.browser === "Desktop" || j?.browser === "Chrome") {
        this.browserChoice = j.browser;
        this.browserPersisted = j.browser;
      }
    } catch {}
  }

  /** The current attempt got far enough (QR shown / logged in) to know this browser identity is accepted. */
  private markProgress() {
    this.attemptProgress = true;
    this.fastSwitchesLeft = 1;
    if (this.browserForced || this.browserPersisted === this.browserChoice) return;
    this.browserPersisted = this.browserChoice;
    void writeFile(this.browserFile, JSON.stringify({ browser: this.browserChoice })).catch(() => {});
    this.ctx.log(`whatsapp: browser identity "${this.browserChoice}" accepted by WhatsApp, remembered`);
  }

  private hasPairedCreds(): boolean {
    try {
      const j = JSON.parse(readFileSync(join(this.authDir, "creds.json"), "utf8"));
      return !!j?.me?.id;
    } catch {
      return false;
    }
  }

  /** Detach and close the current socket. Events from it are ignored from now on. */
  private teardownSocket() {
    this.gen++;
    const s = this.sock;
    this.sock = undefined;
    this.open = false;
    if (this.groupTimer) clearInterval(this.groupTimer);
    this.groupTimer = undefined;
    if (s) {
      try {
        void Promise.resolve(s.end(undefined)).catch(() => {});
      } catch {}
    }
  }

  private async waVersion(): Promise<WAVersion | undefined> {
    if (this.version) return this.version;
    try {
      const r = await fetchLatestBaileysVersion({ signal: AbortSignal.timeout(6000) });
      if (!r.error) this.version = r.version;
      else this.ctx.log("whatsapp: could not fetch latest WA version, using Baileys default", String(r.error));
    } catch (e) {
      this.ctx.log("whatsapp: version lookup failed, using Baileys default", String(e));
    }
    return this.version;
  }

  private async connect(): Promise<void> {
    if (this.stopping) return;
    clearTimeout(this.reconnectTimer);
    this.teardownSocket();
    const gen = ++this.gen;
    mkdirSync(this.authDir, { recursive: true });
    await this.credsSave.catch(() => {}); // pairing writes creds right before the 515 restart
    const { state, saveCreds } = await useMultiFileAuthState(this.authDir);
    const version = await this.waVersion();
    if (gen !== this.gen || this.stopping) return;

    const sock = makeWASocket({
      ...(version ? { version } : {}),
      auth: state,
      logger: this.logger,
      browser: Browsers.macOS(this.browserChoice), // see browserChoice: Desktop first, Chrome if the server drops it
      syncFullHistory: true,
      // Accept every history chunk type (Baileys' default drops FULL even when syncFullHistory is set).
      shouldSyncHistoryMessage: () => true,
      // Don't claim "online" just by being connected: keeps phone notifications working.
      markOnlineOnConnect: false,
      emitOwnEvents: true,
      generateHighQualityLinkPreview: false,
      shouldIgnoreJid: (jid: string) => !!(isJidBroadcast(jid) || isJidNewsletter(jid)),
      getMessage: async (key: { remoteJid?: string | null; id?: string | null }) => {
        const cid = this.store.canon(key.remoteJid);
        return this.store.getMsg(cid, key.id ?? "")?.raw?.message ?? undefined;
      },
      cachedGroupMetadata: async (jid: string) => this.groupMeta.get(jid),
    } as any);
    this.sock = sock;
    this.open = false;
    this.qrIndex = 0; // a new socket gets a new QR sequence: its first code lives 60 s, the following ones 20 s
    this.attemptStartedAt = Date.now();
    this.attemptProgress = false;

    sock.ev.on("creds.update", () => {
      this.credsSave = this.credsSave
        .then(() => saveCreds())
        .catch((e) => this.ctx.log("whatsapp: saving creds failed", String(e)));
      const me = sock.user;
      if (me?.name && this.self && this.self.name !== me.name) {
        this.self.name = me.name;
        this.store.accountName = me.name;
        this.store.markIndexDirty();
      }
    });
    sock.ev.process((events) => {
      this.enqueue(() => this.handleEvents(gen, sock, events));
    });
    this.ctx.emit("connection", { state: "connecting" });
  }

  private enqueue(fn: () => Promise<void>) {
    this.queue = this.queue.then(fn).catch((e) => this.ctx.log("whatsapp: event handler failed", e?.stack ?? e));
  }

  private scheduleReconnect(delayMs: number, why: string) {
    if (this.stopping || this.loggingOut) return;
    clearTimeout(this.reconnectTimer);
    this.ctx.emit("connection", { state: "connecting", detail: why });
    this.reconnectTimer = setTimeout(() => {
      this.connect().catch((e) => {
        this.ctx.log("whatsapp: reconnect failed", e?.stack ?? e);
        this.scheduleReconnect(this.nextBackoff(), "retry after error");
      });
    }, delayMs);
  }

  private nextBackoff(): number {
    const d = Math.min(60_000, 1000 * 2 ** Math.min(this.attempts++, 6));
    return d + Math.floor(Math.random() * 500);
  }

  // ================================================================= connection events

  private async handleEvents(gen: number, sock: WASocket, ev: Events) {
    if (gen !== this.gen) return;
    const step = async (name: string, fn: () => Promise<void> | void) => {
      if (gen !== this.gen) return;
      try {
        await fn();
      } catch (e: any) {
        this.ctx.log(`whatsapp: ${name} handler failed`, e?.stack ?? e);
      }
    };
    if (ev["connection.update"]) await step("connection.update", () => this.onConnection(gen, sock, ev["connection.update"]!));
    if (ev["lid-mapping.update"]) {
      await step("lid-mapping.update", () => {
        const m = ev["lid-mapping.update"]!;
        this.lidMissing.delete(normJid(m.lid));
        this.store.link(m.lid, m.pn);
      });
    }
    if (ev["messaging-history.set"]) await step("history", () => this.onHistory(ev["messaging-history.set"]!));
    if (ev["contacts.upsert"]) await step("contacts.upsert", () => this.onContacts(ev["contacts.upsert"]!));
    if (ev["contacts.update"]) await step("contacts.update", () => this.onContacts(ev["contacts.update"]!));
    if (ev["chats.upsert"]) await step("chats.upsert", () => this.onChats(ev["chats.upsert"]!, "absolute"));
    if (ev["chats.update"]) await step("chats.update", () => this.onChats(ev["chats.update"]! as any[], "delta"));
    if (ev["chats.delete"]) {
      await step("chats.delete", () => {
        for (const id of ev["chats.delete"]!) this.store.deleteChat(this.store.canon(id));
      });
    }
    if (ev["groups.upsert"]) await step("groups.upsert", () => ev["groups.upsert"]!.forEach((g) => this.noteGroup(g)));
    if (ev["groups.update"]) await step("groups.update", () => this.onGroupsUpdate(ev["groups.update"]!));
    if (ev["group-participants.update"]) {
      await step("group-participants.update", () => {
        const u = ev["group-participants.update"]!;
        this.groupMeta.delete(u.id); // stale; Baileys refetches on the next send
        this.membersCache.delete(u.id);
        for (const p of u.participants ?? []) this.store.linkAny(p.id, p.phoneNumber ?? p.lid);
      });
    }
    if (ev["messages.upsert"]) await step("messages.upsert", () => this.onUpsert(ev["messages.upsert"]!));
    if (ev["messages.update"]) await step("messages.update", () => this.onMessageUpdates(ev["messages.update"]!));
    if (ev["messages.delete"]) await step("messages.delete", () => this.onMessageDelete(ev["messages.delete"]!));
    if (ev["messages.reaction"]) await step("messages.reaction", () => this.onReactions(ev["messages.reaction"]!));
    if (ev["message-receipt.update"]) await step("receipts", () => this.onReceipts(ev["message-receipt.update"]!));
    if (ev["presence.update"]) await step("presence", () => this.onPresence(ev["presence.update"]!));
  }

  private async onConnection(gen: number, sock: WASocket, u: BaileysEventMap["connection.update"]) {
    if (u.qr) await this.onQr(u.qr, gen);
    if (u.isNewLogin) {
      // QR scanned; WhatsApp now closes the socket with 515 and we reconnect with the new creds.
      this.lastQr = undefined;
      this.clearQrWatch();
      this.setState("connecting");
    }
    if (u.connection === "open") await this.onOpen(gen, sock);
    if (u.connection === "close") await this.onClose(sock, u.lastDisconnect);
  }

  /**
   * Baileys emits a new `qr` every time the previous ref expired (first one after 60 s, the others after 20 s) and
   * closes the socket with 408 "QR refs attempts ended" when it has none left (handled in onClose). Every code is
   * forwarded with its expiry; armQrWatch() guarantees a dead code never lingers.
   */
  private async onQr(qr: string, gen: number) {
    this.markProgress();
    this.qrIndex++;
    // WhatsApp keeps the first QR alive ~60s and each following one ~20s.
    const expiresAt = Date.now() + (this.qrIndex === 1 ? 60_000 : 20_000);
    const image = await QRCode.toDataURL(qr, { margin: 2, scale: 8, errorCorrectionLevel: "M" });
    if (gen !== this.gen) return; // socket replaced/closed while the image was rendered
    this.lastQr = { qr, image, expiresAt };
    if (this.state !== "awaiting_qr") this.setState("awaiting_qr");
    this.ctx.emit("auth.qr", this.lastQr);
    this.armQrWatch(expiresAt);
  }

  private async onOpen(gen: number, sock: WASocket) {
    this.markProgress();
    this.clearQrWatch();
    this.open = true;
    this.attempts = 0;
    this.qrIndex = 0;
    this.qrCycles = 0;
    this.lastQr = undefined;
    const me = sock.user;
    const pn = normJid(me?.id);
    if (pn) {
      // Different account than the one the store belongs to: drop the old data.
      if (this.store.accountId && this.store.accountId !== pn) {
        this.ctx.log("whatsapp: account changed, resetting local store");
        this.store.reset();
        this.groupMeta.clear();
      }
      this.store.accountId = pn;
      this.store.accountName = me?.name || this.store.accountName;
      this.store.markIndexDirty();
      if (me?.lid) this.store.link(me.lid, pn);
      const user = jidDecode(pn)?.user;
      this.self = { id: pn, name: me?.name || this.self?.name || user || "", phone: user };
    }
    this.setState("logged_in");
    this.ctx.emit("connection", { state: "online" });
    void this.flushPendingReads(sock);

    // Background work after login: group subjects (cached), own avatar, periodic refresh.
    setTimeout(() => {
      if (gen === this.gen) void this.fetchGroups(sock);
    }, 3000);
    if (this.groupTimer) clearInterval(this.groupTimer);
    this.groupTimer = setInterval(() => {
      if (gen === this.gen && this.open) void this.fetchGroups(sock);
    }, 30 * 60_000);
    (this.groupTimer as any).unref?.();
    void this.chatAvatar(this.selfJid()).then((p) => {
      if (p && gen === this.gen && this.self && this.self.avatar !== p) {
        this.self.avatar = p;
        this.setState("logged_in");
      }
    });
  }

  private async onClose(sock: WASocket, last: BaileysEventMap["connection.update"]["lastDisconnect"]) {
    const err: any = last?.error;
    const code: number | undefined = err?.output?.statusCode ?? err?.statusCode;
    const detail = `${code ?? "?"} ${err?.message ?? "closed"}`;
    this.open = false;
    if (this.sock === sock) this.sock = undefined;
    this.ctx.emit("connection", { state: "offline", detail });
    if (this.stopping || this.loggingOut) return;
    this.ctx.log("whatsapp: connection closed", detail);

    // Dropped within seconds, before any QR/login, with a bare "Connection Terminated": the server rejected
    // this handshake. Try the other browser identity (alternates each time until one is accepted).
    if (
      code === DisconnectReason.connectionClosed &&
      !this.attemptProgress &&
      !this.browserForced &&
      Date.now() - this.attemptStartedAt < 8000
    ) {
      this.browserChoice = this.browserChoice === "Desktop" ? "Chrome" : "Desktop";
      this.ctx.log(`whatsapp: handshake rejected, retrying as browser "${this.browserChoice}"`);
      this.scheduleReconnect(this.fastSwitchesLeft-- > 0 ? 0 : this.nextBackoff(), `retrying as ${this.browserChoice}`);
      return;
    }

    switch (code) {
      case DisconnectReason.loggedOut: {
        this.teardownSocket();
        this.clearQrWatch();
        rmSync(this.authDir, { recursive: true, force: true });
        this.self = undefined;
        this.lastQr = undefined;
        this.setState("logged_out", { error: "WhatsApp session ended (device was unlinked)" });
        return;
      }
      case DisconnectReason.restartRequired:
        // Expected right after pairing (and occasionally later): reopen immediately with the saved creds.
        this.scheduleReconnect(0, "restart required");
        return;
      case DisconnectReason.connectionReplaced:
        this.setState("error", { error: "This WhatsApp session was replaced by another login" });
        return;
      case DisconnectReason.forbidden:
      case DisconnectReason.multideviceMismatch:
        this.setState("error", { error: `WhatsApp refused the connection (${detail})` });
        return;
      case DisconnectReason.timedOut:
        if (this.state === "awaiting_qr") {
          // 408 "QR refs attempts ended": every QR ref was shown without being scanned. Reconnect for a fresh set
          // of codes (the app is still showing the QR screen as far as we know); after a few rounds give up.
          if (this.qrCycles++ < MAX_QR_RECONNECTS) {
            this.qrIndex = 0;
            this.lastQr = undefined; // dead: login() must not show it again
            this.scheduleReconnect(0, "QR expired, requesting a new one");
          } else {
            this.expireQr();
          }
          return;
        }
      // fallthrough: ordinary timeout
      default:
        this.scheduleReconnect(this.nextBackoff(), detail);
    }
  }

  // ================================================================= identity helpers

  private selfJid(): string {
    return this.self?.id ?? this.store.accountId ?? "";
  }

  private skipJid(jid?: string | null): boolean {
    return !jid || !!isJidBroadcast(jid) || !!isJidNewsletter(jid);
  }

  /** Look the PN of a LID up in Baileys' own mapping store (best effort, negative-cached). */
  private async resolveLid(jid?: string | null) {
    const sock = this.sock;
    if (!jid || !sock) return;
    const n = normJid(jid);
    if (!isLidUser(n) || this.store.lid2pn.has(n) || this.lidMissing.has(n)) return;
    try {
      const pn = await sock.signalRepository.lidMapping.getPNForLID(n);
      if (pn) this.store.link(n, pn);
      else this.lidMissing.add(n);
    } catch {
      this.lidMissing.add(n);
    }
  }

  private groupSize(chatId: string): number | undefined {
    return this.groupMeta.get(chatId)?.participants?.length ?? this.store.groups.get(chatId)?.size;
  }

  // ================================================================= ingestion

  private async ingest(m: WAMessage): Promise<{ chatId: string; sm: StoredMsg; created: boolean } | null> {
    const key = m.key;
    if (!key?.id || !key.remoteJid || this.skipJid(key.remoteJid)) return null;
    if (!isStorable(m)) return null;
    if (key.remoteJidAlt) this.store.linkAny(key.remoteJid, key.remoteJidAlt);
    if (key.participant && key.participantAlt) this.store.linkAny(key.participant, key.participantAlt);
    await this.resolveLid(key.remoteJid);
    await this.resolveLid(key.participant);
    const chatId = this.store.canon(key.remoteJid);
    if (!chatId || this.skipJid(chatId)) return null;

    if (!key.fromMe && (m.pushName || m.verifiedBizName)) {
      const sender = isJidGroup(chatId) ? this.store.canon(key.participant) : chatId;
      if (sender && (isPnUser(sender) || isLidUser(sender))) {
        if (this.store.setContact(sender, { notify: m.pushName ?? undefined, verifiedName: m.verifiedBizName ?? undefined })) {
          if (this.store.getChat(sender)) this.queueChatUpsert(sender);
        }
      }
    }

    const raw = plainMsg(m);
    const ts = num(raw.messageTimestamp) * 1000;
    const chat = this.store.ensureChat(chatId, ts);
    if (ts > chat.timestamp) {
      chat.timestamp = ts;
      this.store.touchIndex();
    }
    const { sm, created } = this.store.upsertRaw(chatId, raw);
    return { chatId, sm, created };
  }

  private async onUpsert(up: BaileysEventMap["messages.upsert"]) {
    const now = Date.now();
    // Phase 1: store everything. Unread deltas (chats.update) of this batch were applied before we got here.
    const items: { chatId: string; msg: Message; created: boolean }[] = [];
    const ownTs = new Map<string, number>();
    for (const m of up.messages) {
      const r = await this.ingest(m);
      if (!r) continue;
      const msg = mapMessage(r.sm, r.chatId, this.mc);
      if (!msg) continue;
      items.push({ chatId: r.chatId, msg, created: r.created });
      if (r.created && msg.fromMe) ownTs.set(r.chatId, Math.max(ownTs.get(r.chatId) ?? 0, msg.timestamp));
    }
    // Phase 2: writing in a chat from the phone (or any other device) means it was read up to that message.
    // Done after the whole batch is stored so later incoming messages of the batch stay unread.
    for (const [chatId, ts] of ownTs) this.readUpTo(chatId, ts);

    // Phase 3: events, chat.upsert always ahead of the first message.new of a chat the app has not seen.
    for (const { chatId, msg, created } of items) {
      if (!created) {
        this.ctx.emit("message.update", { message: msg });
        this.queueChatUpsert(chatId);
        continue;
      }
      // Messages we are in the middle of sending are returned by messages.send instead.
      if (this.ownSending.has(msg.id)) {
        this.queueChatUpsert(chatId);
        continue;
      }
      // "notify" = arrived in real time. Baileys stamps a whole buffered batch with the type of its first message
      // and offline deliveries come as "append", so also require the message itself to be fresh.
      const live = up.type === "notify" && !msg.fromMe && msg.kind !== "system" && isFresh(msg.timestamp, now);
      if (!this.announced.has(chatId)) this.emitChatNow(chatId);
      if (live) this.stopTyping(chatId, msg.senderId);
      this.ctx.emit("message.new", { message: msg, live });
      // Updated unread right behind every live message (the notification reads it from here).
      if (live) this.emitChatNow(chatId);
      else this.queueChatUpsert(chatId);
    }
  }

  /** Stop a "typing" indicator we are still showing for this sender (their message just arrived). */
  private stopTyping(chatId: string, userId: string) {
    const k = `${chatId}|${userId}`;
    const t = this.typingState.get(k);
    if (!t) return;
    clearTimeout(t);
    this.typingState.delete(k);
    this.ctx.emit("typing", { chatId, userId, name: this.store.contactName(userId), typing: false });
  }

  /**
   * The user has seen this chat up to `tsMs` (they wrote something from another device, or another device
   * sent a read receipt): only incoming messages newer than that can still be unread.
   */
  private readUpTo(chatId: string, tsMs: number) {
    const sc = this.store.getChat(chatId);
    if (!sc || (sc.unread === 0 && !sc.markedUnread)) return;
    const list = this.store.messages(chatId); // ascending
    let after = 0;
    for (let i = list.length - 1; i >= 0; i--) {
      const raw = list[i]!.raw;
      if (num(raw.messageTimestamp) * 1000 <= tsMs) break;
      if (!raw.key?.fromMe && !raw.messageStubType) after++;
    }
    const next = Math.min(sc.unread, after);
    if (next === sc.unread && !(next === 0 && sc.markedUnread)) return;
    sc.unread = next;
    if (next === 0) sc.markedUnread = undefined;
    this.store.touchIndex();
    this.queueChatUpsert(chatId);
  }

  // History sync only stores message metadata + the tiny jpegThumbnail. Media is end-to-end encrypted and is
  // downloaded exclusively on demand by downloadMedia(), never from here.
  private async onHistory(h: BaileysEventMap["messaging-history.set"]) {
    for (const mp of h.lidPnMappings ?? []) this.store.link(mp.lid, mp.pn);
    this.onContacts(h.contacts ?? []);
    const onDemand = h.syncType === proto.HistorySync.HistorySyncType.ON_DEMAND;
    const touched = new Set<string>();
    for (const c of h.chats ?? []) {
      const id = this.applyChat(c as any, "absolute", !onDemand);
      if (id) touched.add(id);
    }
    let n = 0;
    for (const m of h.messages ?? []) {
      const r = await this.ingest(m);
      if (r) {
        touched.add(r.chatId);
        if (r.created) this.olderAdded.set(r.chatId, (this.olderAdded.get(r.chatId) ?? 0) + 1);
      }
      if (++n % 200 === 0) await yieldLoop();
    }
    for (const id of touched) {
      this.store.settle(id);
      this.queueChatUpsert(id);
    }
    this.historyBusyUntil = Date.now() + 4000;
    if (h.peerDataRequestSessionId) {
      this.historyWaiters.get(h.peerDataRequestSessionId)?.();
      this.historyWaiters.delete(h.peerDataRequestSessionId);
    }
    if (onDemand) {
      for (const [id, fn] of [...this.historyWaiters]) {
        fn();
        this.historyWaiters.delete(id);
      }
    }
    this.ctx.log(
      `whatsapp: history chunk type=${h.syncType} chats=${h.chats?.length ?? 0} messages=${h.messages?.length ?? 0} progress=${h.progress ?? "?"}`,
    );
  }

  private onContacts(list: Partial<import("baileys").Contact>[]) {
    for (const c of list) {
      if (!c.id) continue;
      if (c.lid) this.store.linkAny(c.id, c.lid);
      if (c.phoneNumber) this.store.linkAny(c.id, c.phoneNumber);
      const id = this.store.canon(c.id);
      if (!(isPnUser(id) || isLidUser(id))) continue;
      if (this.store.setContact(id, { name: c.name, notify: c.notify, verifiedName: c.verifiedName })) {
        if (this.store.getChat(id)) this.queueChatUpsert(id);
      }
    }
  }

  private async onChats(list: any[], mode: "absolute" | "delta") {
    for (const c of list) {
      const id = this.applyChat(c, mode, true);
      if (id) this.queueChatUpsert(id);
    }
  }

  /**
   * Apply a Baileys Chat / ChatUpdate to the store.
   * `absolute`: unreadCount is the new total. `delta`: unreadCount > 0 means "+n" (Baileys' convention
   * in chats.update), 0/null means read, < 0 means marked unread.
   */
  private applyChat(c: any, mode: "absolute" | "delta", trustUnread: boolean): string | undefined {
    const id: string | undefined = c?.id;
    if (!id || this.skipJid(id)) return undefined;
    if (c.pnJid && isLidUser(normJid(id))) this.store.link(id, c.pnJid);
    else if (c.lidJid && isPnUser(normJid(id))) this.store.link(c.lidJid, id);
    const cid = this.store.canon(id);
    if (this.skipJid(cid)) return undefined;
    const sc = this.store.ensureChat(cid);
    let dirty = false;

    const ts = num(c.conversationTimestamp) || num(c.lastMsgTimestamp);
    if (ts && ts * 1000 > sc.timestamp) {
      sc.timestamp = ts * 1000;
      dirty = true;
    }
    const title = c.displayName || c.name;
    if (typeof title === "string" && title) {
      if (isJidGroup(cid)) {
        if (sc.name !== title) {
          sc.name = title;
          dirty = true;
        }
      } else if (!this.store.contacts.get(cid)?.name) {
        this.store.setContact(cid, { name: title });
      }
    }
    if (("unreadCount" in c || c.markedAsUnread) && trustUnread) {
      const v = c.unreadCount;
      let unread = sc.unread;
      let marked = !!sc.markedUnread;
      if (mode === "absolute") {
        if (typeof v === "number") {
          // -1 = manually marked as unread: shown as a dot with a count of 0.
          if (v < 0) {
            unread = 0;
            marked = true;
          } else {
            unread = v;
            marked = false;
          }
        }
        if (c.markedAsUnread && unread === 0) marked = true;
      } else if (v === null || v === 0) {
        // Read here or on another device (app-state markChatAsRead, or the "initial sync says read" null).
        unread = 0;
        marked = false;
      } else if (typeof v === "number") {
        if (v < 0) marked = true; // "mark as unread" from the phone: keep the count, add the flag
        else unread += v; // delta: Baileys sends +1 per real incoming message
      }
      if (unread !== sc.unread || marked !== !!sc.markedUnread) {
        sc.unread = unread;
        sc.markedUnread = marked || undefined;
        dirty = true;
      }
    }
    if ("pinned" in c) {
      const p = c.pinned == null ? 0 : Math.max(num(c.pinned), 0);
      if ((sc.pinned ?? 0) !== p) {
        sc.pinned = p || undefined;
        dirty = true;
      }
    }
    if ("muteEndTime" in c) {
      const n = c.muteEndTime == null ? 0 : num(c.muteEndTime);
      // Seconds in history chunks, ms in app-state patches; -1 (or a date centuries away) = forever.
      let ms = n < 0 ? -1 : n === 0 ? 0 : n > 1e12 ? n : n * 1000;
      if (ms > 4e12) ms = -1;
      if ((sc.muteEnd ?? 0) !== ms) {
        sc.muteEnd = ms || undefined;
        dirty = true;
      }
    }
    if ("archived" in c && !!c.archived !== !!sc.archived) {
      sc.archived = !!c.archived || undefined;
      dirty = true;
    }
    if (dirty) this.store.touchIndex();
    return cid;
  }

  private async onMessageUpdates(updates: BaileysEventMap["messages.update"]) {
    for (const { key, update } of updates) {
      if (!key?.id || !key.remoteJid || this.skipJid(key.remoteJid)) continue;
      if (key.remoteJidAlt) this.store.linkAny(key.remoteJid, key.remoteJidAlt);
      await this.resolveLid(key.remoteJid);
      const chatId = this.store.canon(key.remoteJid);
      const sm = this.store.getMsg(chatId, key.id);
      if (!sm) continue;
      const u: any = update;
      let changed = false;

      if (u.messageStubType === Stub.REVOKE || u.message === null) {
        if (!sm.deleted) {
          sm.deleted = true;
          changed = true;
        }
      } else if (u.message?.editedMessage?.message) {
        sm.raw.message = editedContent(sm.raw.message, u.message.editedMessage.message);
        sm.edited = true;
        changed = true;
      } else if (u.message) {
        // e.g. refreshed media URL after a re-upload request
        sm.raw.message = plainContent(u.message);
        changed = true;
      }
      if (u.status != null && sm.raw.key?.fromMe) {
        const ns = num(u.status);
        const os = sm.raw.status == null ? -1 : num(sm.raw.status);
        if (ns > os || (os === Status.PENDING && ns === Status.ERROR)) {
          sm.raw.status = ns;
          changed = true;
        }
      } else if (u.status != null && !sm.raw.key?.fromMe && num(u.status) >= Status.READ) {
        // "read-self": another device of ours (the phone) read this incoming message.
        this.readUpTo(chatId, num(sm.raw.messageTimestamp) * 1000);
      }
      if (!changed) continue;
      this.store.markChatDirty(chatId);
      this.emitUpdate(sm, chatId);
    }
  }

  private emitUpdate(sm: StoredMsg, chatId: string) {
    const msg = mapMessage(sm, chatId, this.mc);
    if (!msg) return;
    this.ctx.emit("message.update", { message: msg });
    const list = this.store.messages(chatId);
    if (list[list.length - 1] === sm) this.queueChatUpsert(chatId);
  }

  private async onMessageDelete(d: BaileysEventMap["messages.delete"]) {
    if ("keys" in d) {
      for (const key of d.keys) {
        if (!key.id || !key.remoteJid || this.skipJid(key.remoteJid)) continue;
        const chatId = this.store.canon(key.remoteJid);
        if (this.store.removeMsg(chatId, key.id)) {
          this.ctx.emit("message.delete", { chatId, messageId: key.id });
          this.queueChatUpsert(chatId);
        }
      }
    } else if (d.all) {
      const chatId = this.store.canon(d.jid);
      this.store.clearMessages(chatId);
      this.queueChatUpsert(chatId);
    }
  }

  private async onReactions(list: BaileysEventMap["messages.reaction"]) {
    for (const { key, reaction } of list) {
      if (!key?.id || !key.remoteJid || this.skipJid(key.remoteJid)) continue;
      const rk: any = reaction.key;
      if (rk?.participant && rk?.participantAlt) this.store.linkAny(rk.participant, rk.participantAlt);
      await this.resolveLid(rk?.participant);
      const chatId = this.store.canon(key.remoteJid);
      const sm = this.store.getMsg(chatId, key.id);
      if (!sm) continue;
      if (applyReaction(sm.raw, reaction, (j) => this.store.canon(j))) {
        this.store.markChatDirty(chatId);
        this.emitUpdate(sm, chatId);
      }
    }
  }

  private async onReceipts(list: BaileysEventMap["message-receipt.update"]) {
    for (const { key, receipt } of list) {
      if (!key?.id || !key.remoteJid || this.skipJid(key.remoteJid)) continue;
      const chatId = this.store.canon(key.remoteJid);
      const sm = this.store.getMsg(chatId, key.id);
      if (!sm || !sm.raw.key?.fromMe) continue;
      const before = statusOf(sm, chatId, this.mc);
      applyReceipt(sm.raw, receipt);
      if (statusOf(sm, chatId, this.mc) !== before) {
        this.store.markChatDirty(chatId);
        this.emitUpdate(sm, chatId);
      }
    }
  }

  private onPresence(u: { id: string; presences: Record<string, PresenceData> }) {
    if (this.skipJid(u.id)) return;
    const chatId = this.store.canon(u.id);
    const group = !!isJidGroup(chatId);
    for (const [participant, p] of Object.entries(u.presences ?? {})) {
      const userId = this.store.canon(participant);
      if (userId === this.selfJid()) continue;
      const st = p.lastKnownPresence;
      const typing = st === "composing" || st === "recording";
      const k = `${chatId}|${userId}`;
      const was = this.typingState.has(k);
      clearTimeout(this.typingState.get(k));
      this.typingState.delete(k);
      if (typing) {
        // WhatsApp sends "paused" explicitly, but don't rely on it.
        this.typingState.set(
          k,
          setTimeout(() => {
            this.typingState.delete(k);
            this.ctx.emit("typing", { chatId, userId, name: this.store.contactName(userId), typing: false });
          }, 12_000),
        );
      }
      if (typing !== was) {
        this.ctx.emit("typing", { chatId, userId, name: this.store.contactName(userId), typing });
      }
      if (!group) {
        const online = st === "available" || typing;
        const lastSeen = p.lastSeen ? p.lastSeen * 1000 : undefined;
        this.presence.set(chatId, { online, lastSeen });
        this.ctx.emit("presence", { chatId, online, ...(lastSeen ? { lastSeen } : {}) });
      }
    }
  }

  // ================================================================= groups

  private noteGroup(meta: GroupMetadata) {
    if (!meta?.id) return;
    this.groupMeta.set(meta.id, meta);
    const size = meta.size ?? meta.participants?.length;
    this.store.groups.set(meta.id, { subject: meta.subject, size, ts: Date.now() });
    for (const p of meta.participants ?? []) {
      this.store.linkAny(p.id, p.phoneNumber ?? p.lid);
      if (p.name || p.notify) {
        const id = this.store.canon(p.id);
        if (this.store.setContact(id, { name: p.name, notify: p.notify }) && this.store.getChat(id)) this.queueChatUpsert(id);
      }
    }
    this.store.markIndexDirty();
    if (this.store.getChat(meta.id)) this.queueChatUpsert(meta.id);
  }

  private onGroupsUpdate(list: Partial<GroupMetadata>[]) {
    for (const g of list) {
      if (!g.id) continue;
      this.groupMeta.delete(g.id);
      const cur = this.store.groups.get(g.id);
      if (g.subject && cur?.subject !== g.subject) {
        this.store.groups.set(g.id, { subject: g.subject, size: g.size ?? cur?.size, ts: Date.now() });
        this.store.markIndexDirty();
        if (this.store.getChat(g.id)) this.queueChatUpsert(g.id);
      }
    }
  }

  private async fetchGroups(sock: WASocket) {
    try {
      const all = await sock.groupFetchAllParticipating();
      for (const meta of Object.values(all)) this.noteGroup(meta);
      this.ctx.log(`whatsapp: cached ${Object.keys(all).length} groups`);
    } catch (e: any) {
      this.ctx.log("whatsapp: groupFetchAllParticipating failed", String(e?.message ?? e));
    }
  }

  // ================================================================= chat building

  private queueChatUpsert(id: string) {
    if (!id || this.skipJid(id)) return;
    this.pendingUpserts.add(id);
    if (this.upsertTimer) return;
    // During a history sync the same chats are touched over and over: batch harder.
    const delay = Date.now() < this.historyBusyUntil ? 1500 : 200;
    this.upsertTimer = setTimeout(() => {
      this.upsertTimer = undefined;
      const ids = [...this.pendingUpserts];
      this.pendingUpserts.clear();
      for (const cid of ids) this.emitChat(cid);
    }, delay);
  }

  /** Emit a chat.upsert right now (no debounce) and drop any pending one for the same chat. */
  private emitChatNow(id: string) {
    this.pendingUpserts.delete(id);
    this.emitChat(id);
  }

  private emitChat(id: string) {
    const sc = this.store.getChat(id);
    if (!sc || !this.listable(sc)) return;
    try {
      this.ctx.emit("chat.upsert", { chat: this.buildChat(sc) });
      this.announced.add(id);
    } catch (e: any) {
      this.ctx.log("whatsapp: chat.upsert failed", e?.stack ?? e);
    }
  }

  private listable(sc: StoredChat): boolean {
    return !this.skipJid(sc.id) && (sc.timestamp > 0 || this.store.messages(sc.id).length > 0);
  }

  private chatKind(id: string): ChatKind {
    if (isJidGroup(id)) return "group";
    if (id && id === this.selfJid()) return "self";
    if (isJidBot(id) || id === META_AI_PN) return "bot";
    return "direct";
  }

  private chatTitle(sc: StoredChat): string {
    const id = sc.id;
    if (isJidGroup(id)) return this.store.groups.get(id)?.subject || sc.name || "Group";
    if (id === this.selfJid()) return `${this.self?.name || this.store.accountName || "You"} (You)`;
    if (jidDecode(id)?.user === "0") return "WhatsApp";
    return this.store.contactName(id) || sc.name || fallbackName(id);
  }

  private lastVisible(chatId: string): Message | undefined {
    const list = this.store.messages(chatId);
    for (let i = list.length - 1, n = 0; i >= 0 && n < 20; i--, n++) {
      const m = mapMessage(list[i]!, chatId, this.mc);
      if (m) return m;
    }
    return undefined;
  }

  private avatarFile(id: string) {
    return join(this.avatarDir, safeName(id) + ".jpg");
  }

  private buildChat(sc: StoredChat): Chat {
    const last = this.lastVisible(sc.id);
    const chat: Chat = {
      id: sc.id,
      title: this.chatTitle(sc),
      kind: this.chatKind(sc.id),
      unread: sc.unread,
      timestamp: Math.max(sc.timestamp, last?.timestamp ?? 0),
    };
    if (sc.markedUnread) chat.markedUnread = true;
    if (sc.archived) chat.archived = true;
    const av = this.avatarFile(sc.id);
    if (existsSync(av)) chat.avatar = av;
    if ((sc.pinned ?? 0) > 0) chat.pinned = true;
    if (sc.muteEnd === -1 || (sc.muteEnd ?? 0) > Date.now()) {
      chat.muted = true;
      chat.mutedUntil = sc.muteEnd === -1 ? -1 : sc.muteEnd;
    }
    if (last) {
      // The chat list does not need preview images.
      chat.lastMessage = last.media?.thumbnail ? { ...last, media: { ...last.media, thumbnail: undefined } } : last;
    }
    if (isJidGroup(sc.id)) {
      const n = this.groupSize(sc.id);
      if (n) chat.memberCount = n;
    } else {
      const p = this.presence.get(sc.id);
      if (p) {
        chat.online = p.online;
        if (p.lastSeen) chat.lastSeen = p.lastSeen;
      }
    }
    return chat;
  }

  // ================================================================= protocol methods

  private requireData() {
    if (this.state === "logged_out" || this.state === "awaiting_qr") throw new BridgeError("not_logged_in", "Not logged in");
  }

  private requireSock(): WASocket {
    if (this.state === "logged_out" || this.state === "awaiting_qr") throw new BridgeError("not_logged_in", "Not logged in");
    if (!this.sock || !this.open) throw new BridgeError("internal", "WhatsApp is offline, reconnecting. Try again in a moment.");
    return this.sock;
  }

  async listChats(limit: number): Promise<Chat[]> {
    this.requireData();
    const rows = [...this.store.chats.values()]
      .filter((sc) => this.listable(sc))
      .sort((a, b) => Number((b.pinned ?? 0) > 0) - Number((a.pinned ?? 0) > 0) || b.timestamp - a.timestamp)
      .slice(0, Math.max(1, limit));
    return rows.map((sc) => {
      this.announced.add(sc.id);
      return this.buildChat(sc);
    });
  }

  async chatAvatar(chatId: string): Promise<string | undefined> {
    const cid = this.store.canon(chatId);
    if (!cid || this.skipJid(cid)) return undefined;
    const file = this.avatarFile(cid);
    const cached = await stat(file).then((s) => s.mtimeMs).catch(() => 0);
    if (cached && Date.now() - cached < AVATAR_TTL_MS) return file;
    const negAt = this.avatarNeg.get(cid);
    if (negAt && Date.now() - negAt < AVATAR_NEG_TTL_MS) return cached ? file : undefined;
    const sock = this.sock;
    if (!sock || !this.open) return cached ? file : undefined;
    let p = this.avatarInflight.get(cid);
    if (!p) {
      p = this.limited(async () => {
        try {
          const url = await sock.profilePictureUrl(cid, "preview", 10_000);
          if (!url) throw new Error("no picture");
          const res = await fetch(url, { signal: AbortSignal.timeout(15_000) });
          if (!res.ok) throw new Error(`http ${res.status}`);
          mkdirSync(this.avatarDir, { recursive: true });
          await writeFile(file, new Uint8Array(await res.arrayBuffer()));
          this.avatarNeg.delete(cid);
          return file;
        } catch {
          // 401/404 = no picture or hidden by privacy settings; don't hammer the server.
          this.avatarNeg.set(cid, Date.now());
          return cached ? file : undefined;
        }
      }).finally(() => this.avatarInflight.delete(cid));
      this.avatarInflight.set(cid, p);
    }
    return p;
  }

  /** At most 3 concurrent avatar lookups. */
  private async limited<T>(fn: () => Promise<T>): Promise<T> {
    while (this.avatarActive >= 3) await new Promise<void>((r) => this.avatarWaiters.push(r));
    this.avatarActive++;
    try {
      return await fn();
    } finally {
      this.avatarActive--;
      this.avatarWaiters.shift()?.();
    }
  }

  private page(cid: string, limit: number, before?: string): { messages: Message[]; reachedStart: boolean } | null {
    const list = this.store.messages(cid);
    let end = list.length;
    if (before) {
      const i = list.findIndex((s) => s.raw.key.id === before);
      if (i < 0) return null;
      end = i;
    }
    const out: Message[] = [];
    let i = end - 1;
    for (; i >= 0 && out.length < limit; i--) {
      const m = mapMessage(list[i]!, cid, this.mc);
      if (m) out.push(m);
    }
    return { messages: out.reverse(), reachedStart: i < 0 };
  }

  async listMessages(chatId: string, limit: number, before?: string): Promise<Message[]> {
    this.requireData();
    const cid = this.store.canon(chatId);
    limit = Math.min(Math.max(Math.floor(limit) || 50, 1), 500);
    let pg = this.page(cid, limit, before);
    if (!pg) return [];
    if (pg.messages.length < limit && pg.reachedStart) {
      const missing = limit - pg.messages.length;
      if (before) {
        // The user is scrolling up past what we hold: worth waiting for the phone to answer.
        if (await this.fetchOlder(cid, missing)) pg = this.page(cid, limit, before) ?? pg;
      } else {
        // Initial load: never delay first paint. Prefetch so the next scroll-up finds it in the store.
        void this.fetchOlder(cid, missing);
      }
    }
    if (!before) this.subscribePresence(cid);
    return pg.messages;
  }

  /**
   * Ask the phone for messages older than the oldest one we hold (Baileys v7 fetchMessageHistory).
   * The answer arrives as a messaging-history.set event; we wait for it briefly so paging stays seamless.
   */
  private async fetchOlder(cid: string, want: number): Promise<boolean> {
    const sock = this.sock;
    if (!sock || !this.open || this.noMoreHistory.has(cid) || this.olderInflight.has(cid)) return false;
    const oldest = this.store.messages(cid)[0]?.raw;
    if (!oldest?.key?.id) return false;
    this.olderInflight.add(cid);
    try {
      const count = Math.min(Math.max(want, 20), 50);
      this.store.raiseCap(cid, count);
      const base = this.olderAdded.get(cid) ?? 0;
      const reqId = await sock.fetchMessageHistory(count, oldest.key, num(oldest.messageTimestamp) * 1000);
      await new Promise<void>((resolve) => {
        const t = setTimeout(() => {
          this.historyWaiters.delete(reqId);
          resolve();
        }, this.historyWaitMs);
        this.historyWaiters.set(reqId, () => {
          clearTimeout(t);
          resolve();
        });
      });
      // The history handler runs on the same queue; let it finish before we look.
      await this.queue;
      const added = (this.olderAdded.get(cid) ?? 0) > base;
      if (!added) this.noMoreHistory.add(cid);
      return added;
    } catch (e: any) {
      this.ctx.log("whatsapp: fetchMessageHistory failed", String(e?.message ?? e));
      return false;
    } finally {
      this.olderInflight.delete(cid);
    }
  }

  private subscribePresence(cid: string) {
    const sock = this.sock;
    if (!sock || !this.open || isJidGroup(cid) || this.skipJid(cid) || cid === this.selfJid()) return;
    if (Date.now() - (this.presenceSubscribed.get(cid) ?? 0) < 10 * 60_000) return;
    this.presenceSubscribed.set(cid, Date.now());
    sock.presenceSubscribe(cid).catch(() => {});
  }

  async sendMessage(params: {
    chatId: string;
    text?: string;
    replyTo?: string;
    filePath?: string;
    mentions?: MentionSpan[];
  }): Promise<Message> {
    const sock = this.requireSock();
    const cid = this.store.canon(params.chatId);
    if (!cid || this.skipJid(cid)) throw new BridgeError("invalid_params", "unsupported chat");

    let quoted: any;
    if (params.replyTo) {
      const q = this.store.getMsg(cid, params.replyTo);
      // Baileys mutates the quoted content while building contextInfo: hand it a copy.
      if (q) quoted = structuredClone({ key: q.raw.key, message: q.raw.message, messageTimestamp: q.raw.messageTimestamp, participant: q.raw.participant });
    }
    // WhatsApp shows a mention from the literal "@<number>" in the text plus the JID in mentionedJid, so the visible
    // "@Name" the app typed is replaced by "@<jid user>" (and that is also the text we echo back).
    let text = params.text ?? "";
    let mentions: string[] | undefined;
    const spans = sanitizeMentions(text, params.mentions);
    if (spans.length) {
      const r = rewriteWhatsAppMentions(text, spans);
      text = r.text;
      mentions = r.mentionedJid;
    }
    const base: AnyMessageContent = params.filePath ? await this.fileContent(params.filePath, text) : { text };
    const content = (mentions ? { ...base, mentions } : base) as AnyMessageContent;

    return this.deliver(cid, content, { quoted });
  }

  /**
   * messages.forward: WhatsApp's own forward. The content (media keys included) is reused, so nothing is downloaded or
   * re-uploaded, and the recipient sees "Forwarded" unless the original was our own message, like the phone app.
   */
  async forwardMessage(fromChatId: string, messageId: string, toChatId: string): Promise<Message> {
    this.requireSock();
    const from = this.store.canon(fromChatId);
    const to = this.store.canon(toChatId);
    if (!to || this.skipJid(to)) throw new BridgeError("invalid_params", "unsupported chat");
    const sm = this.store.getMsg(from, messageId);
    if (!sm?.raw?.message) throw new BridgeError("not_found", "message not found");
    const original = structuredClone({ key: sm.raw.key, message: sm.raw.message }) as WAMessage;
    return this.deliver(to, { forward: original });
  }

  private async deliver(cid: string, content: AnyMessageContent, opts: { quoted?: any } = {}): Promise<Message> {
    const sock = this.requireSock();
    const messageId = generateMessageIDV2(sock.user?.id);
    this.ownSending.add(messageId);
    try {
      const sent = await sock.sendMessage(cid, content, { quoted: opts.quoted, messageId });
      if (!sent) throw new BridgeError("internal", "WhatsApp did not accept the message");
      const raw = plainMsg(sent);
      raw.status = Math.max(num(raw.status), Status.SERVER_ACK);
      raw.messageTimestamp = num(raw.messageTimestamp) || Math.floor(Date.now() / 1000);
      const chat = this.store.ensureChat(cid);
      chat.timestamp = Math.max(chat.timestamp, num(raw.messageTimestamp) * 1000);
      this.store.touchIndex();
      const { sm } = this.store.upsertRaw(cid, raw);
      this.queueChatUpsert(cid);
      const msg = mapMessage(sm, cid, this.mc);
      if (!msg) throw new BridgeError("internal", "sent message could not be mapped");
      return msg;
    } finally {
      setTimeout(() => this.ownSending.delete(messageId), 20_000);
    }
  }

  /**
   * Members for the @mention picker. Group: participants from groupMetadata (the cached copy when we have one), ids
   * exactly as Baileys reports them (PN or LID jid); names from the participant, then the contact store, then the
   * phone number. 1:1: the peer. The logged-in user is left out.
   */
  async chatMembers(chatId: string): Promise<Member[]> {
    const sock = this.requireSock();
    const cid = this.store.canon(chatId);
    if (!cid || this.skipJid(cid)) throw new BridgeError("invalid_params", "unsupported chat");
    const avatarOf = (jid: string) => {
      const f = this.avatarFile(this.store.canon(jid));
      return existsSync(f) ? f : undefined;
    };
    return this.membersCache.getOrLoad(cid, async () => {
      if (!isJidGroup(cid)) {
        const avatar = avatarOf(cid);
        return [{ id: cid, name: this.store.contactName(cid) || fallbackName(cid), ...(avatar ? { avatar } : {}) }];
      }
      let meta = this.groupMeta.get(cid);
      if (!meta) {
        meta = await sock.groupMetadata(cid);
        this.noteGroup(meta);
      }
      const mine = new Set(
        [sock.user?.id, sock.user?.lid, this.selfJid()].map((j) => this.store.canon(j)).filter(Boolean),
      );
      const out: Member[] = [];
      const seen = new Set<string>();
      for (const p of meta.participants ?? []) {
        if (!p.id || seen.has(p.id)) continue;
        seen.add(p.id);
        const forms = [p.id, p.phoneNumber, p.lid].map((j) => this.store.canon(j)).filter(Boolean);
        if (forms.some((j) => mine.has(j))) continue;
        let name = p.name || p.notify || "";
        for (const j of forms) name ||= this.store.contactName(j) || "";
        const avatar = forms.map(avatarOf).find(Boolean);
        out.push({ id: p.id, name: name || fallbackName(p.phoneNumber || p.id), ...(avatar ? { avatar } : {}) });
      }
      out.sort((a, b) => a.name.localeCompare(b.name));
      return out;
    });
  }

  private async fileContent(filePath: string, caption?: string): Promise<AnyMessageContent> {
    if (!existsSync(filePath)) throw new BridgeError("not_found", `file not found: ${filePath}`);
    const mime = (Bun.file(filePath).type || "application/octet-stream").split(";")[0]!.trim();
    const fileName = basename(filePath);
    const media = { url: filePath }; // Baileys streams local paths
    const cap = caption || undefined;
    if (mime.startsWith("image/")) return { image: media, caption: cap, mimetype: mime };
    if (mime.startsWith("video/")) return { video: media, caption: cap, mimetype: mime };
    if (mime.startsWith("audio/")) {
      // Opus-in-Ogg or a file named voice*: send as a push-to-talk voice note.
      const ptt = mime === "audio/ogg" || /\.(ogg|opus)$/i.test(fileName) || /^voice/i.test(fileName);
      return { audio: media, ptt, mimetype: ptt ? "audio/ogg; codecs=opus" : mime };
    }
    return { document: media, mimetype: mime, fileName, caption: cap };
  }

  async markRead(chatId: string): Promise<void> {
    this.requireData();
    const cid = this.store.canon(chatId);
    const sc = this.store.getChat(cid);
    if (!sc) return;
    const hadMark = !!sc.markedUnread;
    const list = this.store.messages(cid);
    // Read receipts for the newest incoming messages: this is what makes the phone (and the sender) see them as read.
    const want = Math.min(Math.max(sc.unread, 1), 200);
    const keys: any[] = [];
    if (sc.unread > 0 || hadMark) {
      for (let i = list.length - 1; i >= 0 && keys.length < want; i--) {
        const raw = list[i]!.raw;
        if (!raw.key?.fromMe && !raw.messageStubType) keys.push(raw.key);
      }
    }
    if (sc.unread !== 0 || hadMark) {
      sc.unread = 0;
      sc.markedUnread = undefined;
      this.store.touchIndex();
      this.emitChatNow(cid);
    }
    const sock = this.sock;
    if (keys.length) {
      if (sock && this.open) {
        try {
          await sock.readMessages(keys);
        } catch (e: any) {
          this.ctx.log("whatsapp: readMessages failed, will retry on reconnect", String(e?.message ?? e));
          this.pendingReads.set(cid, keys);
        }
      } else {
        this.pendingReads.set(cid, keys); // offline: the receipts go out when the socket opens again
      }
    }
    if (hadMark && sock && this.open) {
      // The "marked as unread" flag lives in app state, not in receipts: clear it there too (best effort).
      const last = list[list.length - 1]?.raw;
      if (last?.key) {
        const patch = { markRead: true, lastMessages: [{ key: last.key, messageTimestamp: num(last.messageTimestamp) }] };
        void Promise.race([sock.chatModify(patch as any, cid), new Promise((_, rej) => setTimeout(() => rej(new Error("timeout")), 15_000))]).catch(
          (e: any) => this.ctx.log("whatsapp: clearing marked-unread failed", String(e?.message ?? e)),
        );
      }
    }
  }

  private async flushPendingReads(sock: WASocket) {
    if (!this.pendingReads.size) return;
    const batches = [...this.pendingReads];
    this.pendingReads.clear();
    for (const [cid, keys] of batches) {
      try {
        await sock.readMessages(keys);
      } catch (e: any) {
        this.ctx.log("whatsapp: deferred readMessages failed", String(e?.message ?? e));
        this.pendingReads.set(cid, keys);
        return;
      }
    }
  }

  async react(chatId: string, messageId: string, emoji: string): Promise<void> {
    const sock = this.requireSock();
    const cid = this.store.canon(chatId);
    const sm = this.store.getMsg(cid, messageId);
    if (!sm) throw new BridgeError("not_found", "message not found");
    await sock.sendMessage(cid, { react: { text: emoji, key: sm.raw.key } });
    const mine = { key: { remoteJid: cid, fromMe: true, id: "local" }, text: emoji };
    if (applyReaction(sm.raw, mine, (j) => this.store.canon(j))) {
      this.store.markChatDirty(cid);
      this.emitUpdate(sm, cid);
    }
  }

  /** messages.edit: WhatsApp lets the sender change a message's text for 15 minutes; the phone enforces the limit. */
  async editMessage(chatId: string, messageId: string, text: string, mentions?: MentionSpan[]): Promise<Message> {
    const sock = this.requireSock();
    const cid = this.store.canon(chatId);
    const sm = this.store.getMsg(cid, messageId);
    if (!sm) throw new BridgeError("not_found", "message not found");
    if (!sm.raw.key?.fromMe) throw new BridgeError("invalid_params", "Only your own messages can be edited");
    if (sm.deleted) throw new BridgeError("invalid_params", "A deleted message cannot be edited");
    let body = text;
    let mentionedJid: string[] | undefined;
    const spans = sanitizeMentions(text, mentions);
    if (spans.length) {
      const r = rewriteWhatsAppMentions(text, spans);
      body = r.text;
      mentionedJid = r.mentionedJid;
    }
    const content = { text: body, edit: sm.raw.key, ...(mentionedJid ? { mentions: mentionedJid } : {}) } as AnyMessageContent;
    await sock.sendMessage(cid, content);
    const edited = mentionedJid
      ? { extendedTextMessage: { text: body, contextInfo: { mentionedJid } } }
      : { conversation: body };
    sm.raw.message = editedContent(sm.raw.message, edited);
    sm.edited = true;
    this.store.markChatDirty(cid);
    this.emitUpdate(sm, cid);
    const msg = mapMessage(sm, cid, this.mc);
    if (!msg) throw new BridgeError("internal", "edited message could not be mapped");
    return msg;
  }

  async deleteMessage(chatId: string, messageId: string, forEveryone: boolean): Promise<void> {
    const sock = this.requireSock();
    const cid = this.store.canon(chatId);
    const sm = this.store.getMsg(cid, messageId);
    if (!sm) throw new BridgeError("not_found", "message not found");
    if (forEveryone) {
      if (!sm.raw.key.fromMe && !isJidGroup(cid)) {
        throw new BridgeError("invalid_params", "Only your own messages can be deleted for everyone");
      }
      await sock.sendMessage(cid, { delete: sm.raw.key });
      sm.deleted = true;
      this.store.markChatDirty(cid);
      this.emitUpdate(sm, cid);
    } else {
      await sock.chatModify(
        { deleteForMe: { deleteMedia: false, key: sm.raw.key, timestamp: num(sm.raw.messageTimestamp) } },
        cid,
      );
      this.store.removeMsg(cid, messageId);
      this.ctx.emit("message.delete", { chatId: cid, messageId });
      this.queueChatUpsert(cid);
    }
  }

  // ================================================================= storage (Settings -> Data and Storage)

  async configureStorage(config: StorageConfig): Promise<void> {
    this.storage = { ...config };
    this.store.setLimit(config.maxStoredMessagesPerChat);
    this.store.forgetPaths(mediaCache.prune(this.mediaDir, this.storage).deleted);
  }

  async storageUsage() {
    return {
      ...mediaCache.usage(this.mediaDir),
      // Store (messages/chats, compressed), the Baileys session and the avatar cache: everything but media.
      databaseBytes: mediaCache.treeSize(this.storeDir, this.authDir, this.avatarDir),
    };
  }

  async clearStorage(olderThanDays?: number): Promise<number> {
    const r = mediaCache.clear(this.mediaDir, olderThanDays);
    this.store.forgetPaths(r.deleted);
    return r.freedBytes;
  }

  /**
   * media.download. WhatsApp media is end-to-end encrypted: there is no URL the app could load itself, so this is
   * the only place it is fetched and decrypted -- on demand, never during history sync (onHistory/ingest store the
   * message and its tiny jpegThumbnail only). Cached files are reused (and marked as used), new ones go through the
   * StorageConfig prune/compress pipeline.
   */
  async downloadMedia(chatId: string, messageId: string): Promise<string> {
    const cid = this.store.canon(chatId);
    return mediaCache.inflight(this.downloads, `${cid}|${messageId}`, () => this.fetchMedia(cid, messageId));
  }

  private async fetchMedia(cid: string, messageId: string): Promise<string> {
    const sm = this.store.getMsg(cid, messageId);
    if (!sm) throw new BridgeError("not_found", "message not found");
    if (sm.path && existsSync(sm.path)) {
      mediaCache.touch(sm.path);
      return sm.path;
    }
    const mc = mediaContent(sm.raw);
    if (!mc) throw new BridgeError("invalid_params", "message has no downloadable media");

    mkdirSync(this.mediaDir, { recursive: true });
    const stem = join(this.mediaDir, safeName(messageId));
    const dest = `${stem}.${extFor(mc)}`;
    // On disk already (path forgotten, or compressed to HEIC by an earlier download)?
    const cached = [dest, `${stem}.heic`].find((p) => existsSync(p));
    let final = cached ?? dest;
    if (cached) {
      mediaCache.touch(cached);
    } else {
      // Make room first (age + size cap), leaving space for this file.
      this.store.forgetPaths(mediaCache.prune(this.mediaDir, this.storage, num(mc.fileLength)).deleted);
      // Work on a copy; JSON round trips drop empty `url`, which Baileys' media check insists on.
      const copy = structuredClone(sm.raw);
      const inner: any = extractMessageContent(copy.message);
      for (const k of ["imageMessage", "videoMessage", "audioMessage", "documentMessage", "stickerMessage", "ptvMessage"]) {
        if (inner?.[k] && !("url" in inner[k])) inner[k].url = "";
      }
      const sock = this.sock;
      const stream = await downloadMediaMessage(
        copy as WAMessage,
        "stream",
        {},
        {
          logger: this.logger,
          reuploadRequest: async (m) => {
            if (!sock || !this.open) throw new BridgeError("internal", "offline: cannot ask the phone to re-upload this media");
            return sock.updateMediaMessage(m);
          },
        },
      ).catch((e: any) => {
        throw new BridgeError("internal", `media download failed: ${e?.message ?? e}`);
      });
      const tmp = dest + ".part";
      try {
        await pipeline(stream, createWriteStream(tmp));
        await rename(tmp, dest);
      } catch (err: any) {
        rmSync(tmp, { force: true });
        throw new BridgeError("internal", `media download failed: ${err?.message ?? err}`);
      }
      // Photos only (not gif/sticker/video/documents): HEIC <= 1600 px when it is really smaller.
      if (mapMessage(sm, cid, this.mc)?.kind === "image") final = await mediaCache.compressImage(dest, this.storage);
    }
    sm.path = final;
    this.store.markChatDirty(cid);
    this.emitUpdate(sm, cid);
    return final;
  }

  async sendTyping(chatId: string, typing: boolean): Promise<void> {
    const sock = this.sock;
    if (!sock || !this.open) return; // typing indicators are best effort
    const cid = this.store.canon(chatId);
    if (!cid || this.skipJid(cid)) return;
    try {
      if (typing) {
        if (Date.now() - this.availableAt > 4 * 60_000) {
          this.availableAt = Date.now();
          await sock.sendPresenceUpdate("available");
        }
        await sock.sendPresenceUpdate("composing", cid);
        // Go back to "unavailable" a while after the last keystroke so the phone keeps its notifications.
        clearTimeout(this.idleTimer);
        this.idleTimer = setTimeout(() => {
          this.availableAt = 0;
          sock.sendPresenceUpdate("unavailable").catch(() => {});
        }, 90_000);
      } else {
        await sock.sendPresenceUpdate("paused", cid);
      }
    } catch (e: any) {
      this.ctx.log("whatsapp: sendTyping failed", String(e?.message ?? e));
    }
  }
}

const EXT_BY_MIME: Record<string, string> = {
  "image/jpeg": "jpg",
  "image/png": "png",
  "image/webp": "webp",
  "image/gif": "gif",
  "video/mp4": "mp4",
  "video/quicktime": "mov",
  "video/webm": "webm",
  "audio/ogg": "ogg",
  "audio/mpeg": "mp3",
  "audio/mp4": "m4a",
  "audio/aac": "aac",
  "audio/wav": "wav",
  "application/pdf": "pdf",
};

function extFor(mc: any): string {
  const fromName = mc.fileName ? extname(String(mc.fileName)).slice(1).toLowerCase().replace(/[^a-z0-9]/g, "") : "";
  if (fromName) return fromName;
  const mime = String(mc.mimetype ?? "").split(";")[0]!.trim().toLowerCase();
  return EXT_BY_MIME[mime] ?? (mime.split("/")[1]?.replace(/[^a-z0-9]/g, "") || "bin");
}
