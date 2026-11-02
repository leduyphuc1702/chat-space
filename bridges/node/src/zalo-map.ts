import { open } from "node:fs/promises";
import { extname } from "node:path";
import { Reactions, ThreadType } from "zca-js";
import { BridgeError, type Media, type Message, type MessageKind, type Reaction } from "./types.ts";

// ---------------------------------------------------------------- ids

export function chatIdOf(type: ThreadType, threadId: string): string {
  return (type === ThreadType.Group ? "g:" : "u:") + threadId;
}

export function parseChatId(chatId: string): { type: ThreadType; id: string } {
  if (chatId.startsWith("g:") && chatId.length > 2) return { type: ThreadType.Group, id: chatId.slice(2) };
  if (chatId.startsWith("u:") && chatId.length > 2) return { type: ThreadType.User, id: chatId.slice(2) };
  throw new BridgeError("invalid_params", `bad chatId "${chatId}" (expected u:<uid> or g:<groupId>)`);
}

/** Stringify ids that may arrive as number / string / BigNumber. */
export function str(x: unknown): string {
  if (x === null || x === undefined) return "";
  return String(x);
}

function num(x: unknown): number | undefined {
  if (x === null || x === undefined || x === "") return undefined;
  const n = Number(x);
  return Number.isFinite(n) ? n : undefined;
}

/** Zalo timestamps are ms strings; tolerate seconds just in case. */
export function toMs(ts: unknown): number {
  const n = Number(ts);
  if (!Number.isFinite(n) || n <= 0) return Date.now();
  return n < 1e11 ? n * 1000 : n;
}

export function truncate(s: string, n: number): string {
  return s.length > n ? s.slice(0, n - 1) + "…" : s;
}

// ---------------------------------------------------------------- content

export interface Mapped {
  kind: MessageKind;
  text?: string;
  media?: Media;
  sticker?: { id: number; cateId: number };
}

function parseParams(params: unknown): Record<string, any> {
  if (params && typeof params === "object") return params as Record<string, any>;
  if (typeof params !== "string" || !params) return {};
  try {
    const p = JSON.parse(params);
    return p && typeof p === "object" ? p : {};
  } catch {
    return {};
  }
}

const MIME: Record<string, string> = {
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  png: "image/png",
  gif: "image/gif",
  webp: "image/webp",
  heic: "image/heic",
  mp4: "video/mp4",
  mov: "video/quicktime",
  m4v: "video/x-m4v",
  webm: "video/webm",
  mp3: "audio/mpeg",
  m4a: "audio/mp4",
  aac: "audio/aac",
  wav: "audio/wav",
  ogg: "audio/ogg",
  amr: "audio/amr",
  pdf: "application/pdf",
  zip: "application/zip",
  txt: "text/plain",
  json: "application/json",
  doc: "application/msword",
  docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  xls: "application/vnd.ms-excel",
  xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  ppt: "application/vnd.ms-powerpoint",
  pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
};

export function mimeFromName(nameOrUrl: string | undefined): string | undefined {
  if (!nameOrUrl) return undefined;
  let p = nameOrUrl;
  try {
    if (/^https?:/i.test(p)) p = new URL(p).pathname;
  } catch {}
  const ext = extname(p).slice(1).toLowerCase();
  return MIME[ext];
}

export function kindFromFile(path: string): MessageKind {
  const mime = mimeFromName(path) ?? "";
  if (mime === "image/gif") return "gif";
  if (mime.startsWith("image/")) return "image";
  if (mime.startsWith("video/")) return "video";
  return "file";
}

const KIND_LABEL: Partial<Record<MessageKind, string>> = {
  image: "Hình ảnh",
  video: "Video",
  voice: "Tin nhắn thoại",
  audio: "Âm thanh",
  file: "Tệp đính kèm",
  sticker: "Sticker",
  gif: "GIF",
  location: "Vị trí",
  contact: "Danh thiếp",
  link: "Liên kết",
  call: "Cuộc gọi",
  unsupported: "Tin nhắn chưa được hỗ trợ",
};

export function kindLabel(kind: MessageKind | undefined): string {
  return (kind && KIND_LABEL[kind]) || "";
}

const nonEmpty = (s: unknown): string | undefined => (typeof s === "string" && s.trim() ? s : undefined);

/**
 * Map a zca-js message (msgType + content) to protocol fields.
 * Content shapes are not typed by zca-js (`TOtherContent`); these follow what the
 * Zalo web client sends and every access is defensive.
 */
export function mapContent(msgType: string, content: unknown): Mapped {
  if (typeof content === "string") return { kind: "text", text: content };
  const c = (content && typeof content === "object" ? content : {}) as Record<string, any>;
  const p = parseParams(c.params);
  const title = nonEmpty(c.title);
  const desc = nonEmpty(c.description);
  const href = nonEmpty(c.href);
  const thumb = nonEmpty(c.thumb);

  switch (msgType) {
    case "webchat": {
      const text = nonEmpty(c.msg) ?? title;
      return text ? { kind: "text", text } : { kind: "unsupported" };
    }
    case "chat.photo":
    case "chat.doodle": {
      const url = nonEmpty(p.hd) ?? nonEmpty(p.rawUrl) ?? href;
      return {
        kind: "image",
        text: title,
        media: {
          url,
          thumbnail: thumb,
          mime: mimeFromName(url) ?? "image/jpeg",
          width: num(p.width) ?? num(p.w),
          height: num(p.height) ?? num(p.h),
          size: num(p.hdSize) ?? num(p.fileSize),
        },
      };
    }
    case "chat.gif": {
      return {
        kind: "gif",
        text: title,
        media: {
          url: href,
          thumbnail: thumb,
          mime: "image/gif",
          width: num(p.width) ?? num(p.w),
          height: num(p.height) ?? num(p.h),
        },
      };
    }
    case "chat.video.msg": {
      const ms = num(p.duration);
      return {
        kind: "video",
        text: title,
        media: {
          url: href,
          thumbnail: thumb,
          mime: mimeFromName(href) ?? "video/mp4",
          width: num(p.video_width) ?? num(p.width),
          height: num(p.video_height) ?? num(p.height),
          duration: ms !== undefined ? ms / 1000 : undefined,
          size: num(p.fileSize),
        },
      };
    }
    case "chat.voice": {
      const ms = num(p.duration);
      return {
        kind: "voice",
        media: {
          url: href,
          mime: mimeFromName(href) ?? "audio/aac",
          duration: ms !== undefined ? ms / 1000 : undefined,
          size: num(p.fileSize),
        },
      };
    }
    case "share.file": {
      const fileName = title ?? "file";
      return {
        kind: "file",
        text: fileName,
        media: {
          url: href,
          fileName,
          mime: mimeFromName(fileName),
          size: num(p.fileSize),
        },
      };
    }
    case "chat.sticker": {
      const id = num(c.id);
      if (id === undefined) return { kind: "sticker", text: "[Sticker]" };
      return { kind: "sticker", sticker: { id, cateId: num(c.catId) ?? num(c.cateId) ?? 0 } };
    }
    case "chat.location.new":
    case "chat.location": {
      const lat = nonEmpty(String(p.latitude ?? "")) ?? undefined;
      const lon = nonEmpty(String(p.longitude ?? "")) ?? undefined;
      const coords = lat && lon ? `${lat}, ${lon}` : undefined;
      return {
        kind: "location",
        text: desc ?? title ?? coords ?? "[Vị trí]",
        media: { url: href, thumbnail: thumb },
      };
    }
    case "chat.recommended":
    case "chat.link": {
      const action = String(c.action ?? "");
      if (/user|contact|card/i.test(action)) {
        return { kind: "contact", text: title ?? desc ?? "[Danh thiếp]", media: { thumbnail: thumb, url: href } };
      }
      const parts: string[] = [];
      for (const s of [title, desc, href]) if (s && !parts.includes(s)) parts.push(s);
      return { kind: "link", text: parts.join("\n") || undefined, media: { url: href, thumbnail: thumb } };
    }
    default: {
      const text = title ?? desc;
      return { kind: "unsupported", text };
    }
  }
}

/** zca-js `quote.cliMsgType` -> kind */
const CLI_TYPE_KIND: Record<number, MessageKind> = {
  1: "text",
  31: "voice",
  32: "image",
  36: "sticker",
  37: "image",
  38: "link",
  43: "location",
  44: "video",
  46: "file",
  49: "gif",
};

export function quoteToReply(q: any): Message["replyTo"] | undefined {
  if (!q || typeof q !== "object") return undefined;
  const id = str(q.globalMsgId);
  if (!id || id === "0") return undefined;
  const kind = CLI_TYPE_KIND[Number(q.cliMsgType)];
  let text = nonEmpty(q.msg);
  if (!text && kind && kind !== "text") text = `[${kindLabel(kind)}]`;
  return {
    id,
    text: text ? truncate(text, 200) : undefined,
    senderName: nonEmpty(q.fromD),
    kind,
  };
}

// ---------------------------------------------------------------- reactions

/** Zalo reaction code -> emoji. Codes missing here are shown as the raw code. */
const ICON_EMOJI: Array<[string, string]> = [
  [Reactions.HEART, "❤️"],
  [Reactions.LIKE, "👍"],
  [Reactions.HAHA, "😆"],
  [Reactions.WOW, "😮"],
  [Reactions.CRY, "😭"],
  [Reactions.ANGRY, "😡"],
  [Reactions.KISS, "😘"],
  [Reactions.TEARS_OF_JOY, "😂"],
  [Reactions.SHIT, "💩"],
  [Reactions.ROSE, "🌹"],
  [Reactions.BROKEN_HEART, "💔"],
  [Reactions.DISLIKE, "👎"],
  [Reactions.LOVE, "😍"],
  [Reactions.CONFUSED, "😕"],
  [Reactions.WINK, "😉"],
  [Reactions.BIRTHDAY, "🎂"],
  [Reactions.BOMB, "💣"],
  [Reactions.OK, "👌"],
  [Reactions.PEACE, "✌️"],
  [Reactions.PRAY, "🙏"],
  [Reactions.PUNCH, "👊"],
  [Reactions.BEER, "🍺"],
  [Reactions.HANDCLAP, "👏"],
  [Reactions.SUN, "☀️"],
];

const ICON_TO_EMOJI = new Map<string, string>(ICON_EMOJI);

/** Extra emoji users may send that should map onto a Zalo reaction. */
const EMOJI_ALIASES: Array<[string, string]> = [
  ["♥", Reactions.HEART],
  ["😄", Reactions.HAHA],
  ["😁", Reactions.HAHA],
  ["😲", Reactions.WOW],
  ["😢", Reactions.CRY],
  ["😠", Reactions.ANGRY],
  ["🤣", Reactions.TEARS_OF_JOY],
  ["🥰", Reactions.LOVE],
  ["🫶", Reactions.HEART],
];

const norm = (e: string) => e.replace(/\uFE0F/g, "").replace(/[\u{1F3FB}-\u{1F3FF}]/gu, "").trim();

const EMOJI_TO_ICON = new Map<string, string>([
  ...ICON_EMOJI.map(([icon, emoji]) => [norm(emoji), icon] as [string, string]),
  ...EMOJI_ALIASES.map(([emoji, icon]) => [norm(emoji), icon] as [string, string]),
]);

export function iconToEmoji(icon: string): string {
  return ICON_TO_EMOJI.get(icon) ?? icon;
}

/** emoji ("" = remove) -> zca-js reaction code, or undefined when unsupported. */
export function emojiToIcon(emoji: string): Reactions | undefined {
  if (!emoji.trim()) return Reactions.NONE;
  const hit = EMOJI_TO_ICON.get(norm(emoji));
  if (hit) return hit as Reactions;
  if ((Object.values(Reactions) as string[]).includes(emoji)) return emoji as Reactions;
  return undefined;
}

export function computeReactions(
  reacts: Record<string, string> | undefined,
  selfId: string,
): Reaction[] | undefined {
  if (!reacts) return undefined;
  const byEmoji = new Map<string, Reaction>();
  for (const [uid, icon] of Object.entries(reacts)) {
    if (!icon) continue;
    const emoji = iconToEmoji(icon);
    const r = byEmoji.get(emoji) ?? { emoji, count: 0, fromMe: false };
    r.count++;
    if (uid === selfId) r.fromMe = true;
    byEmoji.set(emoji, r);
  }
  return byEmoji.size ? [...byEmoji.values()] : undefined;
}

// ---------------------------------------------------------------- image metadata

/**
 * zca-js needs an `imageMetadataGetter` to upload images/gifs. We parse headers
 * ourselves (PNG/JPEG/GIF/WebP) so no native dependency (sharp) is needed.
 */
export async function readImageMeta(
  path: string,
): Promise<{ width: number; height: number; size: number } | null> {
  let fh;
  try {
    fh = await open(path, "r");
    const { size } = await fh.stat();
    const buf = Buffer.alloc(Math.min(size, 1 << 20));
    const { bytesRead } = await fh.read(buf, 0, buf.length, 0);
    const b = buf.subarray(0, bytesRead);
    const dim = imageDims(b);
    return dim ? { ...dim, size } : null;
  } catch {
    return null;
  } finally {
    await fh?.close().catch(() => {});
  }
}

function imageDims(b: Buffer): { width: number; height: number } | null {
  // PNG
  if (b.length >= 24 && b.readUInt32BE(0) === 0x89504e47) {
    return { width: b.readUInt32BE(16), height: b.readUInt32BE(20) };
  }
  // GIF
  if (b.length >= 10 && b.toString("latin1", 0, 3) === "GIF") {
    return { width: b.readUInt16LE(6), height: b.readUInt16LE(8) };
  }
  // WebP
  if (b.length >= 30 && b.toString("latin1", 0, 4) === "RIFF" && b.toString("latin1", 8, 12) === "WEBP") {
    const fmt = b.toString("latin1", 12, 16);
    if (fmt === "VP8 ") return { width: b.readUInt16LE(26) & 0x3fff, height: b.readUInt16LE(28) & 0x3fff };
    if (fmt === "VP8L") {
      const v = b.readUInt32LE(21);
      return { width: (v & 0x3fff) + 1, height: ((v >> 14) & 0x3fff) + 1 };
    }
    if (fmt === "VP8X") return { width: 1 + b.readUIntLE(24, 3), height: 1 + b.readUIntLE(27, 3) };
    return null;
  }
  // JPEG
  if (b.length > 4 && b[0] === 0xff && b[1] === 0xd8) {
    let i = 2;
    while (i + 9 < b.length) {
      if (b[i] !== 0xff) {
        i++;
        continue;
      }
      const marker = b[i + 1]!;
      if (marker === 0xff) {
        i++;
        continue;
      }
      if (marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
        i += 2;
        continue;
      }
      const isSof = marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc;
      if (isSof) return { height: b.readUInt16BE(i + 5), width: b.readUInt16BE(i + 7) };
      i += 2 + b.readUInt16BE(i + 2);
    }
  }
  return null;
}
