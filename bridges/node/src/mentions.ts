// Dependency-free helpers shared by the bridges for @mentions (docs/PROTOCOL.md, "Mentions").
import type { MentionSpan } from "./types.ts";

const isLowSurrogate = (code: number) => code >= 0xdc00 && code <= 0xdfff;

/**
 * Validate the `mentions` param of messages.send against `text`.
 * Spans are UTF-16 code units. Anything malformed, out of range, splitting a surrogate pair or overlapping an earlier span is dropped
 * (the message is then still sent, just without that highlight). The result is sorted by offset.
 */
export function sanitizeMentions(text: string, raw: unknown): MentionSpan[] {
  if (!Array.isArray(raw) || !text) return [];
  const spans: MentionSpan[] = [];
  for (const m of raw) {
    if (!m || typeof m !== "object") continue;
    const { userId, offset, length } = m as Record<string, unknown>;
    if (typeof userId !== "string" || !userId) continue;
    if (typeof offset !== "number" || typeof length !== "number") continue;
    if (!Number.isInteger(offset) || !Number.isInteger(length) || offset < 0 || length <= 0) continue;
    const end = offset + length;
    if (end > text.length) continue;
    // A span that starts or ends between the two halves of an emoji would corrupt the text when rewritten.
    if (isLowSurrogate(text.charCodeAt(offset)) || (end < text.length && isLowSurrogate(text.charCodeAt(end)))) continue;
    spans.push({ userId, offset, length });
  }
  spans.sort((a, b) => a.offset - b.offset);
  const out: MentionSpan[] = [];
  let end = 0;
  for (const s of spans) {
    if (s.offset < end) continue;
    out.push(s);
    end = s.offset + s.length;
  }
  return out;
}

/** "84901234567:12@s.whatsapp.net" -> "84901234567"; "123@lid" -> "123"; a bare "84901234567" is returned as is. */
export function waJidUser(jid: string): string {
  const at = jid.indexOf("@");
  const user = at >= 0 ? jid.slice(0, at) : jid;
  const colon = user.indexOf(":");
  return colon >= 0 ? user.slice(0, colon) : user;
}

/**
 * WhatsApp renders a mention from the literal text "@<user part of the JID>" plus the JID in `mentionedJid`.
 * Replace every (sanitized, non-overlapping, ascending) span with that token. Spans are applied from last to first
 * so earlier offsets stay valid. `mentionedJid` keeps the JIDs exactly as given (a bare number gets the PN server).
 */
export function rewriteWhatsAppMentions(
  text: string,
  mentions: MentionSpan[],
): { text: string; mentionedJid: string[] } {
  let out = text;
  for (let i = mentions.length - 1; i >= 0; i--) {
    const { userId, offset, length } = mentions[i]!;
    out = out.slice(0, offset) + "@" + waJidUser(userId) + out.slice(offset + length);
  }
  const jids: string[] = [];
  for (const { userId } of mentions) {
    const jid = userId.includes("@") ? userId : `${userId}@s.whatsapp.net`;
    if (!jids.includes(jid)) jids.push(jid);
  }
  return { text: out, mentionedJid: jids };
}

/**
 * Small TTL cache that also shares one in-flight load between concurrent callers (typing "@" repeatedly must not
 * hit the server more than once per chat per `ttlMs`). Failed loads are never cached.
 */
export class TtlCache<V> {
  private readonly items = new Map<string, { at: number; value: V }>();
  private readonly inflight = new Map<string, Promise<V>>();

  constructor(
    private readonly ttlMs: number,
    private readonly maxEntries = 200,
  ) {}

  get(key: string, now = Date.now()): V | undefined {
    const e = this.items.get(key);
    if (!e) return undefined;
    if (now - e.at > this.ttlMs) {
      this.items.delete(key);
      return undefined;
    }
    return e.value;
  }

  set(key: string, value: V, now = Date.now()) {
    this.items.delete(key);
    this.items.set(key, { at: now, value });
    while (this.items.size > this.maxEntries) this.items.delete(this.items.keys().next().value as string);
  }

  delete(key: string) {
    this.items.delete(key);
  }

  clear() {
    this.items.clear();
    this.inflight.clear();
  }

  async getOrLoad(key: string, load: () => Promise<V>): Promise<V> {
    const hit = this.get(key);
    if (hit !== undefined) return hit;
    const pending = this.inflight.get(key);
    if (pending) return pending;
    const p = load()
      .then((v) => {
        // clear() during the load means the session changed: do not repopulate the cache.
        if (this.inflight.get(key) === p) this.set(key, v);
        return v;
      })
      .finally(() => {
        if (this.inflight.get(key) === p) this.inflight.delete(key);
      });
    this.inflight.set(key, p);
    return p;
  }
}
