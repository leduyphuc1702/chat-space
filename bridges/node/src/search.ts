import type { Message, MessageKind } from "./types.ts";
import { BridgeError } from "./types.ts";

/** messages.search params (docs/PROTOCOL.md, Search). */
export interface SearchParams {
  /** Every word must appear (case, accent and đ/d insensitive); empty = any message of `kinds`. */
  query: string;
  chatId?: string;
  kinds?: MessageKind[];
  /** Only messages strictly older than this (ms epoch): the next page. */
  before?: number;
  limit: number;
}

const KINDS = new Set<string>(["text", "image", "video", "audio", "voice", "file", "sticker", "gif", "location", "contact", "link"]);

export function parseSearch(p: Record<string, any>): SearchParams {
  const query = typeof p.query === "string" ? p.query.trim() : "";
  const kinds = Array.isArray(p.kinds) ? (p.kinds.filter((k: unknown) => typeof k === "string" && KINDS.has(k)) as MessageKind[]) : undefined;
  if (!query && !kinds?.length) throw new BridgeError("invalid_params", "query or kinds required");
  const limit = Math.min(Math.max(Math.floor(Number(p.limit)) || 50, 1), 200);
  const before = Number(p.before) > 0 ? Number(p.before) : undefined;
  const chatId = typeof p.chatId === "string" && p.chatId ? p.chatId : undefined;
  return { query, chatId, kinds: kinds?.length ? kinds : undefined, before, limit };
}

/** What people type when searching Vietnamese: lowercase, no accents, đ as d. */
export function fold(s: string): string {
  return s.normalize("NFD").replace(/\p{M}+/gu, "").replace(/[đĐ]/g, "d").toLowerCase();
}

export function terms(query: string): string[] {
  return fold(query).split(/\s+/).filter(Boolean);
}

const URL_RE = /https?:\/\/|www\./i;

/** The searchable text of a message: body/caption, file name, and link previews. */
export function haystack(m: Message): string {
  return fold([m.text, m.media?.fileName].filter(Boolean).join("\n"));
}

function kindMatches(m: Message, kinds: Set<string> | undefined): boolean {
  if (!kinds) return m.kind !== "system" && m.kind !== "call";
  if (kinds.has(m.kind)) return true;
  // Links mostly arrive as text messages that contain a URL.
  return kinds.has("link") && !!m.text && URL_RE.test(m.text);
}

/** The facts a search looks at, kept per stored message so repeated searches need not map it again. */
interface Summary {
  hay: string;
  kind: string;
  text?: string;
  deleted: boolean;
  timestamp: number;
  hidden?: boolean;
}

function summarize(m: Message | undefined): Summary {
  if (!m) return { hay: "", kind: "", deleted: true, timestamp: 0, hidden: true };
  return { hay: haystack(m), kind: m.kind, text: m.text, deleted: !!m.deleted, timestamp: m.timestamp };
}

export class Matcher {
  private readonly words: string[];
  private readonly kinds?: Set<string>;

  constructor(readonly p: SearchParams) {
    this.words = terms(p.query);
    this.kinds = p.kinds ? new Set(p.kinds) : undefined;
  }

  test(m: Message | Summary): boolean {
    const s: Summary = "hay" in m ? m : summarize(m);
    if (s.hidden || s.deleted || !kindMatches(s as unknown as Message, this.kinds)) return false;
    if (this.p.before !== undefined && s.timestamp >= this.p.before) return false;
    return this.words.every((w) => s.hay.includes(w));
  }
}

const summaries = new WeakMap<object, { stamp: unknown[]; s: Summary }>();

const sameStamp = (a: unknown[], b: unknown[]) => a.length === b.length && a.every((v, i) => v === b[i]);

/**
 * Newest-first search over local per-chat lists (each ascending by time). `toMessage` turns a stored entry into a
 * protocol message (undefined = not shown in the app). `stamp` lists the values that change when the entry is edited
 * or recalled in place, so what was computed for an entry is reused by later searches until then.
 */
export function searchLocal<T extends object>(
  chats: Iterable<[string, readonly T[]]>,
  p: SearchParams,
  toMessage: (entry: T, chatId: string) => Message | undefined,
  stamp: (entry: T) => unknown[],
): Message[] {
  const matcher = new Matcher(p);
  const hits: Message[] = [];
  for (const [chatId, list] of chats) {
    if (p.chatId && chatId !== p.chatId) continue;
    let found = 0;
    for (let i = list.length - 1; i >= 0 && found < p.limit; i--) {
      const e = list[i]!;
      const st = stamp(e);
      const cached = summaries.get(e);
      let s = cached && sameStamp(cached.stamp, st) ? cached.s : undefined;
      if (!s) {
        s = summarize(toMessage(e, chatId));
        summaries.set(e, { stamp: st, s });
      }
      if (!matcher.test(s)) continue;
      const m = toMessage(e, chatId);
      if (!m) continue;
      hits.push(m);
      found++;
    }
  }
  return newestFirst(hits, p.limit);
}

export function newestFirst(list: Message[], limit: number): Message[] {
  const seen = new Set<string>();
  return list
    .sort((a, b) => b.timestamp - a.timestamp)
    .filter((m) => {
      const k = m.chatId + "|" + m.id;
      if (seen.has(k)) return false;
      seen.add(k);
      return true;
    })
    .slice(0, limit);
}
