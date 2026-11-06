import { existsSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { promisify } from "node:util";
import { gunzipSync, gzip, gzipSync } from "node:zlib";
import { treeSize } from "./media-cache.ts";
import type { Message } from "./types.ts";

/**
 * Zalo has no paged history for 1:1 chats (only the realtime replay), so trimming them would lose messages for good;
 * only group chats, which getGroupChatHistory can refetch, are capped.
 */
const trimmable = (chatId: string) => !chatId.startsWith("u:");

/** Messages kept per chat until the app pushes StorageConfig.maxStoredMessagesPerChat. */
export const MAX_PER_CHAT = 300;
const SAVE_DEBOUNCE_MS = 1500;
const GZ_LEVEL = 6;
const gzipAsync = promisify(gzip);
const yieldLoop = () => new Promise<void>((r) => setImmediate(r));
/** How long one stretch of serialisation may keep the event loop before letting messages through. */
const SERIALIZE_SLICE_MS = 8;

/**
 * Raw Zalo fields we must keep to act on a message later (quote, react, undo,
 * delete, seen). Everything is stringified up front because zca-js hands out
 * BigNumber objects for ids that overflow 2^53.
 */
export interface ZMeta {
  /** 0 = user thread, 1 = group thread (zca-js ThreadType). */
  t: 0 | 1;
  msgId: string;
  cliMsgId: string;
  uidFrom: string;
  idTo: string;
  msgType: string;
  ts: string;
  ttl: number;
  st: number;
  at: number;
  cmd: number;
  content?: unknown;
  propertyExt?: unknown;
  /** reactor uid -> raw Zalo icon code (e.g. "/-heart") */
  reacts?: Record<string, string>;
  sticker?: { id: number; cateId: number };
}

export interface Entry {
  m: Message;
  z: ZMeta;
}

export interface StoredChat {
  id: string;
  title?: string;
  avatar?: string;
  unread: number;
  timestamp: number;
  pinned?: boolean;
  /** legacy flag; `mutedUntil` is authoritative when present (a bare `muted` means "forever") */
  muted?: boolean;
  /** ms epoch the mute ends (estimated from the server clock); -1 = forever */
  mutedUntil?: number;
  /** manually "marked as unread" in Zalo (getUnreadMark) */
  markedUnread?: boolean;
  /** ts of the newest incoming message we already reported as seen to Zalo */
  seenSent?: number;
}

export interface Contact {
  name: string;
  avatar?: string;
  /** true when the user is in the friend list */
  fr?: 1;
}

export interface GroupDir {
  name: string;
  avatar?: string;
  memberCount?: number;
}

interface FileShape {
  v: 1;
  selfId?: string;
  chats: Record<string, StoredChat>;
  messages: Record<string, Entry[]>;
  contacts: Record<string, Contact>;
  groups: Record<string, GroupDir>;
  meta: {
    /** last time we saw a live event (ms). Replayed messages newer than this count as unread. */
    activeTs?: number;
    /** highest global msgId seen via listener replay, per thread type (decimal strings) */
    lastMsgId?: { u?: string; g?: string };
    /** Zalo "global noised id" -> chat id ("u:<uid>" / "g:<groupId>"); phone sync addresses people and groups by it */
    gids?: Record<string, string>;
    /** conversations (global ids) an unfinished phone sync already imported; the next run skips them */
    syncDone?: string[];
  };
}

function empty(): FileShape {
  return { v: 1, chats: {}, messages: {}, contacts: {}, groups: {}, meta: {} };
}

/**
 * Persistent, debounced store for everything Zalo's API cannot give us back.
 *
 * On disk it is gzip-compressed JSON (`store.json.gz`). The old plain `store.json` is still read when there is no
 * `.gz` yet and is deleted after the first successful compressed write.
 */
export class ZaloStore {
  private data: FileShape = empty();
  private index = new Map<string, Map<string, Entry>>();
  private timer?: ReturnType<typeof setTimeout>;
  private isDirty = false;
  private readonly file: string;
  private readonly gzFile: string;

  /** In-memory cap per chat (Infinity = unlimited). Replaced by setMaxPerChat(). */
  private max = MAX_PER_CHAT;
  /** Only set once the app configured a limit: the disk copy is trimmed to it. */
  private persistMax?: number;
  /** Chats that may temporarily hold more than `max` (older history fetched on demand, or loaded from disk). */
  private capFor = new Map<string, number>();

  // write bookkeeping (see writeOnce / flush)
  private chain: Promise<void> = Promise.resolve();
  private inflight = false;
  private seq = 0;
  private epoch = 0;

  constructor(
    dataDir: string,
    private log: (...a: unknown[]) => void,
  ) {
    this.file = join(dataDir, "store.json");
    this.gzFile = this.file + ".gz";
  }

  /** Bytes the store occupies on disk. */
  get diskBytes(): number {
    return treeSize(this.gzFile, this.file);
  }

  load() {
    this.data = empty();
    this.index.clear();
    this.capFor.clear();
    for (const path of [this.gzFile, this.file]) {
      if (!existsSync(path)) continue;
      try {
        let buf = readFileSync(path);
        if (buf.length > 2 && buf[0] === 0x1f && buf[1] === 0x8b) buf = gunzipSync(buf);
        const raw = JSON.parse(buf.toString("utf8")) as Partial<FileShape>;
        if (raw && raw.v === 1) {
          this.data = { ...empty(), ...raw, meta: raw.meta ?? {} } as FileShape;
        }
        // Legacy plain file: write it back compressed soon (and drop the plain one).
        if (path === this.file) this.touch();
        break;
      } catch (e) {
        this.log(`zalo store: could not read ${path}, ${path === this.gzFile ? "trying the next copy" : "starting empty"}`, e);
        try {
          renameSync(path, path + ".corrupt");
        } catch {}
      }
    }
    for (const [chatId, list] of Object.entries(this.data.messages)) {
      if (list.length <= this.max || !trimmable(chatId)) continue;
      // Until the app tells us its limit, never throw away what an earlier run (maybe with a bigger limit) saved.
      // setMaxPerChat() clears this allowance and applies the real limit.
      if (this.persistMax !== undefined) this.trim(chatId, list, this.max);
      else this.capFor.set(chatId, Infinity);
    }
  }

  /**
   * StorageConfig.maxStoredMessagesPerChat: 0 = unlimited. Lowering it trims memory now and the disk copy on the next
   * write; the disk copy never keeps more than this per chat, even if paging back temporarily loaded more.
   */
  setMaxPerChat(n: number) {
    const lim = Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
    const max = lim || Infinity;
    if (this.persistMax === max && this.max === max) return;
    this.persistMax = max;
    this.max = max;
    this.capFor.clear();
    for (const [chatId, list] of Object.entries(this.data.messages)) {
      if (list.length > max && trimmable(chatId)) this.trim(chatId, list, max);
    }
    this.touch();
  }

  private capOf(chatId: string): number {
    if (!trimmable(chatId)) return Infinity;
    return Math.max(this.max, this.capFor.get(chatId) ?? 0);
  }

  /** Allow `extra` more messages than currently held (older history is about to be fetched into this chat). */
  raiseCap(chatId: string, extra: number) {
    if (!Number.isFinite(this.max)) return;
    this.capFor.set(chatId, Math.max(this.capOf(chatId), this.list(chatId).length + extra));
  }

  private trim(chatId: string, list: Entry[], cap: number) {
    const idx = this.index.get(chatId);
    for (const r of list.splice(0, list.length - cap)) idx?.delete(r.m.id);
  }

  get selfId() {
    return this.data.selfId;
  }
  set selfId(v: string | undefined) {
    this.data.selfId = v;
    this.touch();
  }
  get chats() {
    return this.data.chats;
  }
  get contacts() {
    return this.data.contacts;
  }
  get groups() {
    return this.data.groups;
  }
  set groups(g: Record<string, GroupDir>) {
    this.data.groups = g;
    this.touch();
  }
  get meta() {
    return this.data.meta;
  }
  chatIds(): string[] {
    return Object.keys(this.data.messages);
  }

  chat(chatId: string): StoredChat {
    let c = this.data.chats[chatId];
    if (!c) {
      c = { id: chatId, unread: 0, timestamp: 0 };
      this.data.chats[chatId] = c;
      this.touch();
    }
    return c;
  }

  list(chatId: string): Entry[] {
    return this.data.messages[chatId] ?? [];
  }

  private idx(chatId: string): Map<string, Entry> {
    let m = this.index.get(chatId);
    if (!m) {
      m = new Map();
      for (const e of this.data.messages[chatId] ?? []) m.set(e.m.id, e);
      this.index.set(chatId, m);
    }
    return m;
  }

  find(chatId: string, msgId: string): Entry | undefined {
    return this.idx(chatId).get(msgId);
  }

  findByCli(chatId: string, cliMsgId: string): Entry | undefined {
    if (!cliMsgId || cliMsgId === "0") return undefined;
    return this.list(chatId).find((e) => e.z.cliMsgId === cliMsgId);
  }

  /** Insert keeping timestamp order. Returns false if the id already exists. */
  insert(chatId: string, e: Entry): boolean {
    const idx = this.idx(chatId);
    if (idx.has(e.m.id)) return false;
    const list = (this.data.messages[chatId] ??= []);
    let i = list.length;
    while (i > 0 && list[i - 1]!.m.timestamp > e.m.timestamp) i--;
    list.splice(i, 0, e);
    idx.set(e.m.id, e);
    const cap = this.capOf(chatId);
    if (list.length > cap) this.trim(chatId, list, cap);
    this.touch();
    return true;
  }

  /** Put a chat's list back in timestamp order after some entries' `m.timestamp` were changed in place (stable). */
  resort(chatId: string) {
    const list = this.data.messages[chatId];
    if (!list) return;
    list.sort((a, b) => a.m.timestamp - b.m.timestamp);
    this.touch();
  }

  remove(chatId: string, msgId: string): boolean {
    const idx = this.idx(chatId);
    const e = idx.get(msgId);
    if (!e) return false;
    idx.delete(msgId);
    const list = this.data.messages[chatId]!;
    list.splice(list.indexOf(e), 1);
    this.touch();
    return true;
  }

  /** Mark the store dirty and schedule a debounced (asynchronous, compressed) write. */
  touch() {
    this.isDirty = true;
    if (this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      this.chain = this.chain.then(() => this.writeOnce()).catch(() => {});
    }, SAVE_DEBOUNCE_MS);
  }

  /** What goes to disk: everything, except that no chat keeps more than the configured limit. */
  private serialize(): string {
    const lim = this.persistMax;
    if (!lim || !Number.isFinite(lim)) return JSON.stringify(this.data);
    const messages: Record<string, Entry[]> = {};
    for (const [id, list] of Object.entries(this.data.messages)) messages[id] = list.length > lim && trimmable(id) ? list.slice(-lim) : list;
    return JSON.stringify({ ...this.data, messages });
  }

  /**
   * The same JSON as serialize(), built chat by chat with the event loop released every few milliseconds. With the
   * whole history on disk (phone sync) a one-shot JSON.stringify of everything takes seconds, during which nothing
   * is received or sent; this way a message arriving mid-write waits a few milliseconds at most. A chat changed
   * while this runs is picked up by the next write (touch() marks the store dirty again).
   */
  async serializeChunked(): Promise<string> {
    const { messages, ...rest } = this.data;
    const head = JSON.stringify(rest); // "{…}" without the messages
    const lim = this.persistMax && Number.isFinite(this.persistMax) ? this.persistMax : undefined;
    const parts: string[] = [];
    let sliceStart = performance.now();
    for (const [id, list] of Object.entries(messages)) {
      const kept = lim !== undefined && list.length > lim && trimmable(id) ? list.slice(-lim) : list;
      parts.push(`${JSON.stringify(id)}:${JSON.stringify(kept)}`);
      if (performance.now() - sliceStart > SERIALIZE_SLICE_MS) {
        await yieldLoop();
        sliceStart = performance.now();
      }
    }
    return `${head.slice(0, -1)}${head.length > 2 ? "," : ""}"messages":{${parts.join(",")}}}`;
  }

  /** Snapshot now, compress on a worker thread, then swap the file in one synchronous step. */
  private async writeOnce() {
    if (!this.isDirty) return;
    this.isDirty = false;
    this.inflight = true;
    const seq = ++this.seq;
    const epoch = this.epoch;
    const tmp = this.gzFile + ".tmp";
    try {
      const gz = await gzipAsync(await this.serializeChunked(), { level: GZ_LEVEL });
      await writeFile(tmp, gz);
      // reset() or a synchronous flush() overtook us while we were writing: our snapshot is stale.
      if (epoch !== this.epoch || seq !== this.seq) {
        rmSync(tmp, { force: true });
        return;
      }
      renameSync(tmp, this.gzFile);
      rmSync(this.file, { force: true }); // legacy plain copy
    } catch (e) {
      this.isDirty = true;
      rmSync(tmp, { force: true });
      this.log("zalo store: write failed", e);
    } finally {
      this.inflight = false;
    }
  }

  /** Synchronous write (shutdown). Also covers an asynchronous write that has not finished yet. */
  flush() {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
    if (!this.isDirty && !this.inflight) return;
    this.isDirty = false;
    this.seq++; // makes an in-flight writeOnce() discard its older snapshot
    const tmp = this.gzFile + ".sync.tmp"; // not the async writer's tmp: both may be alive for a moment
    try {
      writeFileSync(tmp, gzipSync(this.serialize(), { level: GZ_LEVEL }));
      renameSync(tmp, this.gzFile);
      rmSync(this.file, { force: true });
    } catch (e) {
      this.isDirty = true;
      rmSync(tmp, { force: true });
      this.log("zalo store: write failed", e);
    }
  }

  /** Forget everything (logout / account switch). */
  reset() {
    if (this.timer) clearTimeout(this.timer);
    this.timer = undefined;
    this.isDirty = false;
    this.epoch++;
    this.data = empty();
    this.index.clear();
    this.capFor.clear();
    for (const p of [this.file, this.gzFile, this.gzFile + ".tmp", this.gzFile + ".sync.tmp"]) {
      try {
        rmSync(p, { force: true });
      } catch {}
    }
  }
}
