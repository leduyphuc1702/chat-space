// Persistent WhatsApp state for the ChatSpace bridge (Baileys v7 ships no in-memory store).
//
// Layout under `${dataDir}/store/`:
//   index.json.gz         chats, contacts, LID<->PN map, group subjects
//   msgs/<chat>.json.gz   raw WAMessage protos for one chat (BufferJSON-encoded), capped
//
// Files are gzip-compressed JSON. The plain `index.json` / `msgs/<chat>.json` of older versions are still read
// (a gzip header is also accepted under either name) and are replaced by the compressed copy on the next write.
//
// Everything lives in memory; writes are debounced, atomic (tmp + rename) and per-chat so a
// busy chat does not rewrite the whole store.
import { BufferJSON, isLidUser, isPnUser, jidDecode, proto } from "baileys";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { promisify } from "node:util";
import { gunzipSync, gzip, gzipSync } from "node:zlib";

/** Messages kept per chat until the app pushes StorageConfig.maxStoredMessagesPerChat. */
export const MAX_MESSAGES_PER_CHAT = 500;
const STORE_VERSION = 1;
const GZ_LEVEL = 6;
const gzipAsync = promisify(gzip);

/** Read a (possibly gzip-compressed) file as UTF-8 text. */
function readText(path: string): string {
  let buf = readFileSync(path);
  if (buf.length > 2 && buf[0] === 0x1f && buf[1] === 0x8b) buf = gunzipSync(buf);
  return buf.toString("utf8");
}

/** protobuf Long | number | {low,high} (after a JSON round trip) -> number */
export function num(v: any): number {
  if (v == null) return 0;
  if (typeof v === "number") return v;
  if (typeof v === "string") return Number(v) || 0;
  if (typeof v === "object") {
    if (typeof v.toNumber === "function") return v.toNumber();
    if ("low" in v && "high" in v) return v.high * 4294967296 + (v.low >>> 0);
  }
  return 0;
}

/**
 * Mute end as WhatsApp reports it -> ms epoch; -1 = forever, 0 = not muted. History chunks carry seconds, app-state
 * patches ms; a negative value (how "Always" is sent) or a date centuries away means forever.
 */
export function muteEndMs(v: unknown): number {
  const n = v == null ? 0 : num(v);
  const ms = n < 0 ? -1 : n === 0 ? 0 : n > 1e12 ? n : n * 1000;
  return ms > 4e12 ? -1 : ms;
}

/** chats.mute `until` (ms epoch, -1 forever, 0 unmute) -> the value of chatModify({ mute }); null unmutes. */
export function muteArg(until: number): number | null {
  return until === 0 ? null : until < 0 ? -1 : Math.floor(until);
}

/** `user:device@server` -> `user@server`; legacy c.us -> s.whatsapp.net. Never throws. */
export function normJid(jid?: string | null): string {
  if (!jid) return "";
  const d = jidDecode(jid);
  if (!d) return jid;
  return `${d.user}@${d.server === "c.us" ? "s.whatsapp.net" : d.server}`;
}

export interface StoredChat {
  id: string;
  name?: string;
  unread: number;
  /** manually "marked as unread" on the phone (Baileys reports it as unreadCount -1) */
  markedUnread?: boolean;
  /** ms timestamp the chat was pinned at; 0/undefined = not pinned */
  pinned?: number;
  /** ms epoch the mute ends; -1 = forever */
  muteEnd?: number;
  archived?: boolean;
  /** ms epoch of last activity */
  timestamp: number;
}

/** WhatsApp lets a chat have this many pinned messages. */
export const MAX_PINS = 3;
/** Longest pin duration WhatsApp offers (30 days): an older pin event can no longer be live. */
const MAX_PIN_MS = 30 * 86400_000;
const MAX_DEAD_PINS = 20;

/** A pin or unpin of one message, as carried by a pinInChatMessage. */
export interface PinEvent {
  /** id of the pinned message */
  id: string;
  pin: boolean;
  /** ms the pin/unpin was made */
  at: number;
  /** ms the pin lapses (pins only) */
  expiresAt: number;
}

/**
 * The newest event about one message. `unpinned` records stay (as do lapsed pins) so an older event for the same
 * message arriving later, which history sync does (newest first), cannot bring the pin back.
 */
export interface StoredPin {
  id: string;
  pinnedAt: number;
  expiresAt: number;
  unpinned?: true;
}

const pinLive = (p: StoredPin, now: number) => !p.unpinned && p.expiresAt > now;
const activeOf = (list: StoredPin[], now: number) => list.filter((p) => pinLive(p, now)).sort((a, b) => b.pinnedAt - a.pinnedAt);

export interface StoredContact {
  id: string;
  /** address-book / saved name */
  name?: string;
  /** push name the contact chose */
  notify?: string;
  verifiedName?: string;
}

export interface GroupInfo {
  subject: string;
  size?: number;
  ts: number;
}

export interface StoredMsg {
  /** raw WAMessage proto (plain-object form) */
  raw: any;
  edited?: boolean;
  deleted?: boolean;
  /** local path of the downloaded media */
  path?: string;
}

type Log = (...args: unknown[]) => void;

export class WAStore {
  chats = new Map<string, StoredChat>();
  contacts = new Map<string, StoredContact>();
  lid2pn = new Map<string, string>();
  groups = new Map<string, GroupInfo>();
  /** pin records per chat (see StoredPin); persisted in the index */
  pins = new Map<string, StoredPin[]>();
  accountId?: string;
  accountName?: string;
  /** Fired when two chats of the same person were merged (LID chat folded into PN chat). */
  onMerge?: (to: string, from: string) => void;

  private msgs = new Map<string, StoredMsg[]>();
  private idx = new Map<string, Map<string, StoredMsg>>();
  private capFor = new Map<string, number>();
  private unsorted = new Set<string>();
  private dirtyIndex = false;
  private dirtyChats = new Set<string>();
  private removedFiles = new Set<string>();
  private timer?: ReturnType<typeof setTimeout>;
  private saving: Promise<void> = Promise.resolve();
  private epoch = 0;
  /** Only set once the app configured a limit: the disk copy is trimmed to it (and memory follows). */
  private persistMax?: number;
  private readonly indexPath: string;
  private readonly indexGz: string;
  private readonly msgDir: string;

  constructor(
    private dir: string,
    private log: Log,
    private cap = MAX_MESSAGES_PER_CHAT,
  ) {
    this.indexPath = join(dir, "index.json");
    this.indexGz = this.indexPath + ".gz";
    this.msgDir = join(dir, "msgs");
  }

  // ---------------------------------------------------------------- load / save

  load() {
    mkdirSync(this.msgDir, { recursive: true });
    for (const file of [this.indexGz, this.indexPath]) {
      if (!existsSync(file)) continue;
      try {
        const j = JSON.parse(readText(file));
        this.accountId = j.accountId;
        this.accountName = j.accountName;
        for (const c of j.chats ?? []) this.chats.set(c.id, c);
        for (const c of j.contacts ?? []) this.contacts.set(c.id, c);
        for (const [l, p] of j.lid2pn ?? []) this.lid2pn.set(l, p);
        for (const [id, g] of j.groups ?? []) this.groups.set(id, g);
        for (const [id, list] of j.pins ?? []) this.pins.set(id, list);
        if (file === this.indexPath) this.dirtyIndex = true; // legacy plain file: rewrite it compressed
        break;
      } catch (e) {
        this.log(`whatsapp store: ${file} unreadable${file === this.indexGz ? ", trying the older copy" : ", starting empty"}`, String(e));
        try {
          renameSync(file, file + ".corrupt");
        } catch {}
      }
    }
    try {
      // chatId -> candidate files (compressed copy tried first)
      const files = new Map<string, string[]>();
      for (const f of readdirSync(this.msgDir)) {
        const gz = f.endsWith(".json.gz");
        if (!gz && !f.endsWith(".json")) continue;
        const chatId = decodeURIComponent(f.slice(0, gz ? -8 : -5));
        const names = files.get(chatId) ?? [];
        names.push(f);
        files.set(chatId, names);
      }
      for (const [chatId, names] of files) {
        names.sort((a, b) => Number(b.endsWith(".gz")) - Number(a.endsWith(".gz")));
        for (const f of names) {
          try {
            const arr: StoredMsg[] = JSON.parse(readText(join(this.msgDir, f)), BufferJSON.reviver);
            const map = new Map<string, StoredMsg>();
            const list: StoredMsg[] = [];
            for (const sm of arr) {
              const id = sm?.raw?.key?.id;
              if (!id || map.has(id)) continue;
              map.set(id, sm);
              list.push(sm);
            }
            this.msgs.set(chatId, list);
            this.idx.set(chatId, map);
            if (list.length > this.cap) {
              // Until the app tells us its limit, keep what an earlier run saved (maybe under a bigger limit).
              if (this.persistMax !== undefined) list.splice(0, list.length - this.cap).forEach((sm) => map.delete(sm.raw.key.id));
              else this.capFor.set(chatId, list.length);
            }
            if (!f.endsWith(".gz")) this.dirtyChats.add(chatId); // legacy plain file: rewrite it compressed
            else for (const o of names) if (o !== f) rmSync(join(this.msgDir, o), { force: true }); // stale plain twin
            break;
          } catch (e) {
            this.log("whatsapp store: bad message file", f, String(e));
            try {
              renameSync(join(this.msgDir, f), join(this.msgDir, f + ".corrupt"));
            } catch {}
          }
        }
      }
    } catch {}
    if (this.dirtyIndex || this.dirtyChats.size) this.schedule();
    // Fold chats stored under a LID into their PN twin now that the whole map is loaded.
    for (const [lid, pn] of [...this.lid2pn]) this.mergeInto(lid, pn);
  }

  /**
   * StorageConfig.maxStoredMessagesPerChat: 0 = unlimited. Lowering it trims memory now and the disk copy on the next
   * write. Messages dropped here are fetched again from the phone when the user scrolls up (see fetchOlder()).
   */
  setLimit(n: number) {
    const lim = Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
    const cap = lim || Infinity;
    if (this.persistMax === cap && this.cap === cap) return;
    this.persistMax = cap;
    this.cap = cap;
    this.capFor.clear();
    for (const [chatId, list] of this.msgs) {
      if (list.length <= cap) continue;
      this.settle(chatId);
      this.dirtyChats.add(chatId);
    }
    this.schedule();
  }

  /** Cached media files were deleted: stored messages must not point at them any more. */
  forgetPaths(deleted: Iterable<string>) {
    const gone = new Set(deleted);
    if (!gone.size) return;
    for (const [chatId, list] of this.msgs) {
      let touched = false;
      for (const sm of list) {
        if (sm.path && gone.has(sm.path)) {
          sm.path = undefined;
          touched = true;
        }
      }
      if (touched) this.dirtyChats.add(chatId);
    }
    if (this.dirtyChats.size) this.schedule();
  }

  /** Resolves when the write in progress (if any) has finished. */
  async idle(): Promise<void> {
    await this.saving.catch(() => {});
  }

  markIndexDirty() {
    this.dirtyIndex = true;
    this.schedule();
  }

  markChatDirty(chatId: string) {
    this.dirtyChats.add(chatId);
    this.schedule();
  }

  private schedule() {
    if (this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      this.saving = this.saving.then(() => this.writeAsync()).catch((e) => this.log("whatsapp store save failed", String(e)));
    }, 2000);
    (this.timer as any).unref?.();
  }

  /** Compressed file of a chat; the pre-compression `.json` twin is removed whenever this one is written. */
  private msgPath(chatId: string) {
    return join(this.msgDir, encodeURIComponent(chatId) + ".json.gz");
  }

  /** Delete a store file in both its compressed and legacy plain form. */
  private rmBoth(gzPath: string) {
    rmSync(gzPath, { force: true });
    if (gzPath.endsWith(".gz")) rmSync(gzPath.slice(0, -3), { force: true });
  }

  private snapshot() {
    const files: [string, string][] = [];
    const lim = this.persistMax;
    for (const id of this.dirtyChats) {
      // The disk copy never keeps more than the configured limit, even if paging back loaded more into memory.
      let list = this.msgs.get(id) ?? [];
      if (lim && Number.isFinite(lim) && list.length > lim) list = list.slice(-lim);
      files.push([this.msgPath(id), JSON.stringify(list, BufferJSON.replacer)]);
    }
    this.dirtyChats.clear();
    const removed = [...this.removedFiles];
    this.removedFiles.clear();
    let index: string | undefined;
    if (this.dirtyIndex) {
      this.dirtyIndex = false;
      index = JSON.stringify({
        v: STORE_VERSION,
        accountId: this.accountId,
        accountName: this.accountName,
        chats: [...this.chats.values()],
        contacts: [...this.contacts.values()],
        lid2pn: [...this.lid2pn],
        groups: [...this.groups],
        pins: [...this.pins],
      });
    }
    return { files, removed, index };
  }

  private async writeAsync() {
    const epoch = this.epoch;
    const { files, removed, index } = this.snapshot();
    for (const f of removed) this.rmBoth(f);
    const put = async (path: string, text: string) => {
      const gz = await gzipAsync(text, { level: GZ_LEVEL }); // on a worker thread: no event-loop stall
      if (epoch !== this.epoch) return false; // reset() (logout) happened meanwhile: do not resurrect old data
      await writeFile(path + ".tmp", gz);
      if (epoch !== this.epoch) {
        rmSync(path + ".tmp", { force: true });
        return false;
      }
      renameSync(path + ".tmp", path);
      if (path.endsWith(".gz")) rmSync(path.slice(0, -3), { force: true });
      return true;
    };
    for (const [path, data] of files) if (!(await put(path, data))) return;
    if (index) await put(this.indexGz, index);
  }

  /** Synchronous flush for shutdown (await idle() first so no asynchronous write is still running). */
  flushSync() {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
    try {
      const { files, removed, index } = this.snapshot();
      for (const f of removed) this.rmBoth(f);
      const put = (path: string, text: string) => {
        writeFileSync(path + ".tmp", gzipSync(text, { level: GZ_LEVEL }));
        renameSync(path + ".tmp", path);
        if (path.endsWith(".gz")) rmSync(path.slice(0, -3), { force: true });
      };
      for (const [path, data] of files) put(path, data);
      if (index) put(this.indexGz, index);
    } catch (e) {
      this.log("whatsapp store flush failed", String(e));
    }
  }

  /** Forget everything (logout / different account). */
  reset() {
    if (this.timer) clearTimeout(this.timer);
    this.timer = undefined;
    this.epoch++;
    this.chats.clear();
    this.contacts.clear();
    this.lid2pn.clear();
    this.groups.clear();
    this.pins.clear();
    this.msgs.clear();
    this.idx.clear();
    this.capFor.clear();
    this.unsorted.clear();
    this.dirtyChats.clear();
    this.removedFiles.clear();
    this.dirtyIndex = false;
    this.accountId = undefined;
    this.accountName = undefined;
    rmSync(this.dir, { recursive: true, force: true });
    mkdirSync(this.msgDir, { recursive: true });
  }

  // ---------------------------------------------------------------- identity

  /** Canonical jid: device stripped, LID folded into its PN when the mapping is known. */
  canon(jid?: string | null): string {
    const n = normJid(jid);
    if (!n) return "";
    return (isLidUser(n) && this.lid2pn.get(n)) || n;
  }

  /** Remember that `lid` and `pn` are the same person; folds any LID-keyed chat into the PN one. */
  link(lid?: string | null, pn?: string | null): boolean {
    const l = normJid(lid);
    const p = normJid(pn);
    if (!l || !p || !isLidUser(l) || !isPnUser(p)) return false;
    if (this.lid2pn.get(l) === p) return false;
    this.lid2pn.set(l, p);
    this.dirtyIndex = true;
    this.mergeInto(l, p);
    this.schedule();
    return true;
  }

  /** Learn a LID/PN pair from any two jids in either order. */
  linkAny(a?: string | null, b?: string | null): boolean {
    if (!a || !b) return false;
    return isLidUser(normJid(a)) ? this.link(a, b) : this.link(b, a);
  }

  private mergeInto(from: string, to: string) {
    if (from === to) return;
    let changed = false;
    const a = this.chats.get(from);
    if (a) {
      const b = this.chats.get(to);
      if (!b) {
        this.chats.set(to, { ...a, id: to });
      } else {
        b.timestamp = Math.max(b.timestamp, a.timestamp);
        b.unread = Math.max(b.unread, a.unread);
        b.markedUnread = b.markedUnread || a.markedUnread || undefined;
        b.pinned = Math.max(b.pinned ?? 0, a.pinned ?? 0) || undefined;
        b.muteEnd ??= a.muteEnd;
        b.name ||= a.name;
        b.archived = b.archived && a.archived ? true : undefined;
      }
      this.chats.delete(from);
      changed = true;
    }
    const ma = this.msgs.get(from);
    if (ma) {
      for (const sm of ma) this.insertMsg(to, sm);
      this.msgs.delete(from);
      this.idx.delete(from);
      this.unsorted.delete(from);
      this.removedFiles.add(this.msgPath(from));
      this.dirtyChats.delete(from);
      this.settle(to);
      this.markChatDirty(to);
      changed = true;
    }
    const pa = this.pins.get(from);
    if (pa) {
      this.pins.delete(from);
      for (const p of pa) this.applyPin(to, { id: p.id, pin: !p.unpinned, at: p.pinnedAt, expiresAt: p.expiresAt });
      changed = true;
    }
    const ca = this.contacts.get(from);
    if (ca) {
      const cb = this.contacts.get(to) ?? { id: to };
      cb.name ||= ca.name;
      cb.notify ||= ca.notify;
      cb.verifiedName ||= ca.verifiedName;
      this.contacts.set(to, cb);
      this.contacts.delete(from);
      this.dirtyIndex = true;
    }
    if (changed) {
      this.dirtyIndex = true;
      this.onMerge?.(to, from);
    }
  }

  // ---------------------------------------------------------------- chats / contacts

  getChat(id: string) {
    return this.chats.get(id);
  }

  ensureChat(id: string, timestamp = 0): StoredChat {
    let c = this.chats.get(id);
    if (!c) {
      c = { id, unread: 0, timestamp };
      this.chats.set(id, c);
      this.dirtyIndex = true;
      this.schedule();
    }
    return c;
  }

  touchIndex() {
    this.markIndexDirty();
  }

  deleteChat(id: string) {
    if (this.chats.delete(id)) this.markIndexDirty();
    if (this.pins.delete(id)) this.markIndexDirty();
    if (this.msgs.delete(id)) {
      this.idx.delete(id);
      this.unsorted.delete(id);
      this.dirtyChats.delete(id);
      this.removedFiles.add(this.msgPath(id));
      this.schedule();
    }
  }

  setContact(id: string, patch: Partial<Omit<StoredContact, "id">>): boolean {
    if (!id) return false;
    const c = this.contacts.get(id) ?? { id };
    let changed = !this.contacts.has(id);
    for (const k of ["name", "notify", "verifiedName"] as const) {
      const v = patch[k];
      if (v && c[k] !== v) {
        c[k] = v;
        changed = true;
      }
    }
    if (changed) {
      this.contacts.set(id, c);
      this.markIndexDirty();
    }
    return changed;
  }

  contactName(jid: string): string | undefined {
    const c = this.contacts.get(this.canon(jid));
    return c?.name || c?.notify || c?.verifiedName || undefined;
  }

  // ---------------------------------------------------------------- pins

  /** Pins of a chat that have not lapsed, most recently pinned first. */
  activePins(chatId: string, now = Date.now()): StoredPin[] {
    return activeOf(this.pins.get(chatId) ?? [], now);
  }

  /**
   * Record a pin/unpin. The newest event about a message wins, whatever order they arrive in; a pin beyond MAX_PINS
   * pushes the oldest one out. Returns true when the chat's active pins changed.
   */
  applyPin(chatId: string, e: PinEvent, now = Date.now()): boolean {
    const list = this.pins.get(chatId) ?? [];
    const cur = list.find((p) => p.id === e.id);
    if (cur && cur.pinnedAt >= e.at) return false;
    const key = (l: StoredPin[]) => activeOf(l, now).map((p) => `${p.id}@${p.pinnedAt}`).join();
    const before = key(list);
    const next = list.filter((p) => p !== cur);
    next.push(e.pin ? { id: e.id, pinnedAt: e.at, expiresAt: e.expiresAt } : { id: e.id, pinnedAt: e.at, expiresAt: 0, unpinned: true });
    for (const p of activeOf(next, now).slice(MAX_PINS)) p.unpinned = true;
    // Dead records only matter while an older event could still be live.
    const dead = next
      .filter((p) => !pinLive(p, now) && p.pinnedAt > now - MAX_PIN_MS)
      .sort((a, b) => b.pinnedAt - a.pinnedAt)
      .slice(0, MAX_DEAD_PINS);
    const kept = [...next.filter((p) => pinLive(p, now)), ...dead];
    this.pins.set(chatId, kept);
    this.markIndexDirty();
    return key(kept) !== before;
  }

  /** Earliest moment an active pin lapses; undefined when there is none. */
  nextPinExpiry(now = Date.now()): number | undefined {
    let at: number | undefined;
    for (const list of this.pins.values()) {
      for (const p of list) if (pinLive(p, now) && (at === undefined || p.expiresAt < at)) at = p.expiresAt;
    }
    return at;
  }

  /** Chats where a pin lapsed after `since` and up to `now`. */
  lapsedPinChats(since: number, now = Date.now()): string[] {
    const out: string[] = [];
    for (const [chatId, list] of this.pins) {
      if (list.some((p) => !p.unpinned && p.expiresAt > since && p.expiresAt <= now)) out.push(chatId);
    }
    return out;
  }

  // ---------------------------------------------------------------- messages

  /** Messages of a chat, ascending by time. Do not mutate the array. */
  messages(chatId: string): StoredMsg[] {
    this.settle(chatId);
    return this.msgs.get(chatId) ?? [];
  }

  /** Chats that have stored messages. */
  messageChatIds(): string[] {
    return [...this.msgs.keys()];
  }

  getMsg(chatId: string, id: string): StoredMsg | undefined {
    return this.idx.get(chatId)?.get(id);
  }

  /** Insert or merge a raw WAMessage. `created` is true when the id was not known before. */
  upsertRaw(chatId: string, raw: any): { sm: StoredMsg; created: boolean } {
    const id = raw.key.id as string;
    const existing = this.getMsg(chatId, id);
    if (existing) {
      const old = existing.raw;
      // Keep our locally-merged reactions/receipts/status; take everything else from the newer copy.
      const merged = { ...old, ...raw };
      if (!raw.message && old.message) merged.message = old.message;
      if (old.reactions?.length && !raw.reactions?.length) merged.reactions = old.reactions;
      if (old.userReceipt?.length && !raw.userReceipt?.length) merged.userReceipt = old.userReceipt;
      const os = num(old.status);
      const ns = num(raw.status);
      if (old.status != null && (raw.status == null || (os > ns && ns !== 0))) merged.status = old.status;
      existing.raw = merged;
      this.markChatDirty(chatId);
      return { sm: existing, created: false };
    }
    const sm: StoredMsg = { raw };
    this.insertMsg(chatId, sm);
    // Amortised trim: bulk history inserts call settle() themselves once per chat.
    if ((this.msgs.get(chatId)?.length ?? 0) > this.capOf(chatId) * 1.2) this.settle(chatId);
    this.markChatDirty(chatId);
    return { sm, created: true };
  }

  private insertMsg(chatId: string, sm: StoredMsg) {
    const id = sm.raw?.key?.id;
    if (!id) return;
    let map = this.idx.get(chatId);
    if (!map) {
      map = new Map();
      this.idx.set(chatId, map);
    }
    if (map.has(id)) return;
    let list = this.msgs.get(chatId);
    if (!list) {
      list = [];
      this.msgs.set(chatId, list);
    }
    const last = list[list.length - 1];
    if (last && num(last.raw.messageTimestamp) > num(sm.raw.messageTimestamp)) this.unsorted.add(chatId);
    list.push(sm);
    map.set(id, sm);
  }

  private capOf(chatId: string): number {
    return Math.max(this.cap, this.capFor.get(chatId) ?? 0);
  }

  /**
   * Allow `extra` more messages than currently held in this chat. Used before asking the phone for
   * older history, otherwise the trim would immediately throw the fetched messages away again.
   */
  raiseCap(chatId: string, extra: number) {
    this.capFor.set(chatId, Math.max(this.capOf(chatId), (this.msgs.get(chatId)?.length ?? 0) + extra));
  }

  /** Sort + trim a chat after bulk inserts. Cheap no-op when nothing changed. */
  settle(chatId: string) {
    const list = this.msgs.get(chatId);
    if (!list) return;
    if (this.unsorted.delete(chatId)) {
      list.sort((a, b) => num(a.raw.messageTimestamp) - num(b.raw.messageTimestamp));
    }
    const cap = this.capOf(chatId);
    if (list.length > cap) {
      const drop = list.splice(0, list.length - cap);
      const map = this.idx.get(chatId);
      for (const sm of drop) map?.delete(sm.raw.key.id);
    }
  }

  removeMsg(chatId: string, id: string): boolean {
    const map = this.idx.get(chatId);
    const sm = map?.get(id);
    if (!sm) return false;
    map!.delete(id);
    const list = this.msgs.get(chatId)!;
    const i = list.indexOf(sm);
    if (i >= 0) list.splice(i, 1);
    this.markChatDirty(chatId);
    return true;
  }

  clearMessages(chatId: string) {
    if (!this.msgs.has(chatId)) return;
    this.msgs.set(chatId, []);
    this.idx.set(chatId, new Map());
    this.markChatDirty(chatId);
  }
}

// -------------------------------------------------------------------- raw message helpers
//
// IMPORTANT: Baileys hands us protobufjs class instances. JSON.stringify() on those goes through
// their toJSON(), which turns enums into strings, Longs into strings and bytes into bare base64 --
// a round trip would corrupt `status`, timestamps and the media keys. Everything we keep is therefore
// converted to a plain object with numbers/Buffers first.

const PROTO_OPTS = { longs: Number, enums: Number } as const;
const KEY_EXTRAS = ["remoteJidAlt", "participantAlt", "addressingMode", "remoteJidUsername", "participantUsername", "server_id"] as const;

export function plainKey(k: any): any {
  if (!k) return k;
  const out: any = proto.MessageKey.toObject(k, PROTO_OPTS);
  for (const f of KEY_EXTRAS) if (k[f] != null) out[f] = k[f];
  return out;
}

/** WAMessage (proto instance or plain) -> JSON-safe plain object. */
export function plainMsg(m: any): any {
  const out: any = proto.WebMessageInfo.toObject(m, PROTO_OPTS);
  out.key = plainKey(m.key);
  return out;
}

/** proto.IMessage -> JSON-safe plain object. */
export function plainContent(c: any): any {
  return c ? proto.Message.toObject(c, PROTO_OPTS) : c;
}

const WRAPPERS = ["ephemeralMessage", "viewOnceMessage", "viewOnceMessageV2", "viewOnceMessageV2Extension", "documentWithCaptionMessage", "deviceSentMessage"];
const LINK_PREVIEW = ["matchedText", "canonicalUrl", "title", "description", "jpegThumbnail", "previewType", "thumbnailDirectPath", "thumbnailSha256", "thumbnailEncSha256", "mediaKey", "mediaKeyTimestamp", "thumbnailHeight", "thumbnailWidth", "linkPreviewMetadata"];

/**
 * Stored content after an edit. An edit carries only the new text (`conversation` / `extendedTextMessage`, or the
 * media message with just a caption), so it is laid over the original: the quote and mentions in contextInfo and the
 * media keys survive, and a media message gets the text as its caption.
 */
export function editedContent(old: any, edited: any): any {
  const next = plainContent(edited);
  if (!old || !next) return next;
  const out = structuredClone(old);
  let holder = out;
  for (let hops = 0; hops < 4; hops++) {
    const w = WRAPPERS.find((k) => holder[k]?.message);
    if (!w) break;
    holder = holder[w].message;
  }
  const oldType = Object.keys(holder).find((k) => k !== "messageContextInfo" && holder[k] != null);
  const newType = Object.keys(next).find((k) => k !== "messageContextInfo" && next[k] != null);
  if (!oldType || !newType) return next;
  const body = next[newType];
  const text: unknown = newType === "conversation" ? body : (body?.text ?? body?.caption);
  const ci = (o: any) => (o && typeof o === "object" ? o.contextInfo : undefined);
  const mergedCi = ci(holder[oldType]) || ci(body) ? { ...ci(holder[oldType]), ...ci(body) } : undefined;

  if (oldType === "conversation") {
    delete holder.conversation;
    holder[newType] = body;
    return out;
  }
  if (oldType === "extendedTextMessage" && typeof text === "string") {
    const prev = holder.extendedTextMessage;
    const fresh = newType === "extendedTextMessage" ? { ...body } : { text };
    if (newType !== "extendedTextMessage") for (const k of LINK_PREVIEW) delete prev[k];
    holder.extendedTextMessage = newType === "extendedTextMessage" ? fresh : { ...prev, ...fresh };
    if (mergedCi) holder.extendedTextMessage.contextInfo = mergedCi;
    return out;
  }
  if (oldType === newType && typeof body === "object") {
    Object.assign(holder[oldType], body);
    if (mergedCi) holder[oldType].contextInfo = mergedCi;
    return out;
  }
  if (typeof text === "string" && /^(image|video|document)Message$/.test(oldType)) {
    holder[oldType].caption = text;
    if (mergedCi) holder[oldType].contextInfo = mergedCi;
    return out;
  }
  return next;
}

/** Upsert a reaction into raw.reactions keyed by canonical author. Returns true if anything changed. */
export function applyReaction(raw: any, reaction: any, canon: (j?: string | null) => string): boolean {
  const authorOf = (k: any) =>
    k?.fromMe ? "me" : canon(k?.participantAlt || k?.remoteJidAlt || k?.participant || k?.remoteJid);
  const author = authorOf(reaction.key);
  const before = JSON.stringify(raw.reactions ?? []);
  const next = (raw.reactions ?? []).filter((r: any) => authorOf(r.key) !== author);
  const text = reaction.text || "";
  if (text) next.push({ key: plainKey(reaction.key), text, senderTimestampMs: num(reaction.senderTimestampMs) });
  raw.reactions = next;
  return JSON.stringify(next) !== before;
}

/** Merge a delivery/read receipt into raw.userReceipt. */
export function applyReceipt(raw: any, receipt: any): void {
  const list: any[] = (raw.userReceipt = raw.userReceipt ?? []);
  const clean: any = { userJid: receipt.userJid };
  for (const f of ["receiptTimestamp", "readTimestamp", "playedTimestamp"] as const) {
    const v = num(receipt[f]);
    if (v) clean[f] = v;
  }
  const cur = list.find((r) => r.userJid === receipt.userJid);
  if (cur) Object.assign(cur, clean);
  else list.push(clean);
}
