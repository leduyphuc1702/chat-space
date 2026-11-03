// WAMessage (Baileys proto) -> ChatSpace protocol Message.
import { extractMessageContent, getContentType, isPnUser, jidDecode, proto } from "baileys";
import { existsSync } from "node:fs";
import type { Media, Message, MessageKind, Reaction } from "./types.ts";
import { num, type PinEvent, type StoredMsg } from "./whatsapp-store.ts";

export interface MapCtx {
  selfId: string;
  selfName?: string;
  canon(jid?: string | null): string;
  /** saved name / push name for a (canonical) jid, if known */
  nameOf(jid: string): string | undefined;
  groupSize(chatId: string): number | undefined;
}

const Stub = proto.WebMessageInfo.StubType;
const Status = proto.WebMessageInfo.Status;

/** Stand-in display name when nothing better is known. */
export function fallbackName(jid: string): string {
  const user = jidDecode(jid)?.user ?? jid;
  return isPnUser(jid) ? `+${user}` : user;
}

const b64 = (x: any): string | undefined => {
  if (!x) return undefined;
  if (typeof x === "string") return x;
  try {
    const s = Buffer.from(x).toString("base64");
    return s || undefined;
  } catch {
    return undefined;
  }
};
const thumbUrl = (x: any) => {
  const s = b64(x);
  return s ? `data:image/jpeg;base64,${s}` : undefined;
};

interface Decoded {
  kind: MessageKind;
  text?: string;
  media?: Media;
  ci?: any;
}

const HIDDEN = new Set([
  "protocolMessage",
  "reactionMessage",
  "encReactionMessage",
  "pollUpdateMessage",
  "senderKeyDistributionMessage",
  "keepInChatMessage",
  "pinInChatMessage",
  "encEventResponseMessage",
  "messageHistoryBundle",
  "messageHistoryNotice",
  "requestPhoneNumberMessage",
  "peerDataOperationRequestMessage",
  "peerDataOperationRequestResponseMessage",
]);

function mediaOf(m: any, extra: Partial<Media> = {}): Media {
  const out: Media = {};
  if (m.mimetype) out.mime = String(m.mimetype);
  const w = num(m.width);
  const h = num(m.height);
  if (w) out.width = w;
  if (h) out.height = h;
  const size = num(m.fileLength);
  if (size) out.size = size;
  const th = thumbUrl(m.jpegThumbnail);
  if (th) out.thumbnail = th;
  return Object.assign(out, extra);
}

function vcardPhone(vcard?: string | null): string | undefined {
  if (!vcard) return undefined;
  const waid = /waid=(\d+)/i.exec(vcard)?.[1];
  if (waid) return `+${waid}`;
  const tel = /TEL[^:\n]*:([^\n\r]+)/i.exec(vcard)?.[1]?.trim();
  return tel || undefined;
}

/** Content (already unwrapped) -> kind/text/media. null = not user-visible. */
function decode(content: any): Decoded | null {
  if (!content) return null;
  if (content.deviceSentMessage?.message) content = extractMessageContent(content.deviceSentMessage.message) ?? content;
  const type = getContentType(content) as string | undefined;
  if (!type) return null;
  if (HIDDEN.has(type)) return null;
  const m = content[type];

  switch (type) {
    case "conversation":
      return { kind: "text", text: String(m ?? "") };

    case "extendedTextMessage": {
      const hasLink = !!(m.matchedText || m.canonicalUrl);
      const out: Decoded = { kind: hasLink ? "link" : "text", text: m.text ?? "", ci: m.contextInfo };
      if (hasLink) {
        // No dedicated link field in the protocol: expose the preview image only, the URL is in `text`.
        const th = thumbUrl(m.jpegThumbnail);
        if (th) out.media = { thumbnail: th };
      }
      return out;
    }

    case "imageMessage":
      return { kind: "image", text: m.caption || undefined, media: mediaOf(m), ci: m.contextInfo };

    case "videoMessage":
      return {
        kind: m.gifPlayback ? "gif" : "video",
        text: m.caption || undefined,
        media: mediaOf(m, { duration: num(m.seconds) || undefined }),
        ci: m.contextInfo,
      };

    case "ptvMessage":
      return { kind: "video", media: mediaOf(m, { duration: num(m.seconds) || undefined }), ci: m.contextInfo };

    case "audioMessage":
      return {
        kind: m.ptt ? "voice" : "audio",
        media: mediaOf(m, { duration: num(m.seconds) || undefined }),
        ci: m.contextInfo,
      };

    case "documentMessage":
      return {
        kind: "file",
        text: m.caption || undefined,
        media: mediaOf(m, { fileName: m.fileName || m.title || undefined }),
        ci: m.contextInfo,
      };

    case "stickerMessage":
      return { kind: "sticker", media: mediaOf(m, { mime: m.mimetype || "image/webp" }), ci: m.contextInfo };

    case "locationMessage":
    case "liveLocationMessage": {
      const lines = [m.name, m.address].filter(Boolean) as string[];
      lines.push(`https://maps.google.com/?q=${m.degreesLatitude},${m.degreesLongitude}`);
      const th = thumbUrl(m.jpegThumbnail);
      return { kind: "location", text: lines.join("\n"), media: th ? { thumbnail: th } : undefined, ci: m.contextInfo };
    }

    case "contactMessage": {
      const phone = vcardPhone(m.vcard);
      return { kind: "contact", text: [m.displayName, phone].filter(Boolean).join("\n"), ci: m.contextInfo };
    }

    case "contactsArrayMessage": {
      const names = (m.contacts ?? []).map((c: any) => c.displayName).filter(Boolean);
      return {
        kind: "contact",
        text: names.length ? names.join(", ") : m.displayName || "Contacts",
        ci: m.contextInfo,
      };
    }

    case "pollCreationMessage":
    case "pollCreationMessageV2":
    case "pollCreationMessageV3": {
      const opts = (m.options ?? []).map((o: any) => `- ${o.optionName}`);
      return { kind: "unsupported", text: [`Poll: ${m.name ?? ""}`, ...opts].join("\n"), ci: m.contextInfo };
    }

    case "groupInviteMessage": {
      const link = m.inviteCode ? `https://chat.whatsapp.com/${m.inviteCode}` : undefined;
      return {
        kind: "link",
        text: [`Group invitation: ${m.groupName ?? ""}`.trim(), m.caption, link].filter(Boolean).join("\n"),
        ci: m.contextInfo,
      };
    }

    case "eventMessage":
      return { kind: "unsupported", text: `Event: ${m.name ?? ""}`.trim(), ci: m.contextInfo };

    case "buttonsResponseMessage":
      return { kind: "text", text: m.selectedDisplayText || undefined, ci: m.contextInfo };
    case "listResponseMessage":
      return { kind: "text", text: m.title || undefined, ci: m.contextInfo };
    case "templateButtonReplyMessage":
      return { kind: "text", text: m.selectedDisplayText || undefined, ci: m.contextInfo };
    case "buttonsMessage":
      return { kind: "text", text: m.contentText || m.text || undefined, ci: m.contextInfo };
    case "listMessage":
      return { kind: "text", text: [m.title, m.description].filter(Boolean).join("\n") || undefined, ci: m.contextInfo };
    case "interactiveMessage":
      return { kind: "text", text: m.body?.text || undefined, ci: m.contextInfo };

    default:
      return { kind: "unsupported", text: "Unsupported message", ci: m?.contextInfo };
  }
}

/** Short label for a quoted message without text. */
function quoteLabel(d: Decoded): string | undefined {
  if (d.text) return d.text;
  switch (d.kind) {
    case "image":
      return "Photo";
    case "video":
      return "Video";
    case "gif":
      return "GIF";
    case "voice":
      return "Voice message";
    case "audio":
      return "Audio";
    case "file":
      return d.media?.fileName ?? "Document";
    case "sticker":
      return "Sticker";
    case "location":
      return "Location";
    default:
      return undefined;
  }
}

function replyOf(ci: any, ctx: MapCtx): Message["replyTo"] | undefined {
  if (!ci?.stanzaId) return undefined;
  const out: NonNullable<Message["replyTo"]> = { id: ci.stanzaId };
  const q = decode(extractMessageContent(ci.quotedMessage));
  if (q) {
    const label = quoteLabel(q);
    if (label) out.text = label.length > 300 ? label.slice(0, 300) : label;
    out.kind = q.kind;
  }
  if (ci.participant) {
    const who = ctx.canon(ci.participant);
    out.senderName = who === ctx.selfId ? ctx.selfName || "You" : ctx.nameOf(who) ?? fallbackName(who);
  }
  return out;
}

function aggregateReactions(raw: any): Reaction[] | undefined {
  const by = new Map<string, Reaction>();
  for (const r of raw.reactions ?? []) {
    const emoji = r?.text;
    if (!emoji) continue;
    const cur = by.get(emoji) ?? { emoji, count: 0, fromMe: false };
    cur.count++;
    if (r.key?.fromMe) cur.fromMe = true;
    by.set(emoji, cur);
  }
  return by.size ? [...by.values()] : undefined;
}

/** Delivery state of an outgoing message (undefined for incoming). */
export function statusOf(sm: StoredMsg, chatId: string, ctx: MapCtx): Message["status"] {
  const raw = sm.raw;
  if (!raw.key?.fromMe) return undefined;
  let s = raw.status == null ? Status.SERVER_ACK : num(raw.status);
  const receipts: any[] = raw.userReceipt ?? [];
  if (receipts.length && chatId.endsWith("@g.us")) {
    const size = ctx.groupSize(chatId);
    const needed = Math.max(1, (size ?? 2) - 1);
    const read = receipts.filter((r) => num(r.readTimestamp) || num(r.playedTimestamp)).length;
    const delivered = receipts.filter((r) => num(r.receiptTimestamp) || num(r.readTimestamp)).length;
    if (read >= needed) s = Math.max(s, Status.READ);
    else if (delivered > 0) s = Math.max(s, Status.DELIVERY_ACK);
  }
  switch (s) {
    case Status.ERROR:
      return "failed";
    case Status.PENDING:
      return "pending";
    case Status.SERVER_ACK:
      return "sent";
    case Status.DELIVERY_ACK:
      return "delivered";
    default:
      return "read";
  }
}

const paramJid = (s: string): string => {
  if (s.startsWith("{")) {
    try {
      const o = JSON.parse(s);
      return o.phoneNumber || o.id || s;
    } catch {}
  }
  return s;
};

function stubOf(raw: any, chatId: string, ctx: MapCtx): { kind: MessageKind; text: string } | null {
  const p: string[] = raw.messageStubParameters ?? [];
  const who = (j?: string | null) => {
    if (!j) return "Someone";
    const c = ctx.canon(j);
    return c === ctx.selfId ? "You" : ctx.nameOf(c) ?? fallbackName(c);
  };
  const actorJid = raw.key?.participant || raw.participant;
  const actor = raw.key?.fromMe ? "You" : who(actorJid);
  const targetJids = p.map(paramJid);
  const targets = targetJids.map(who).join(", ");
  const self = actor === "You";
  switch (raw.messageStubType) {
    case Stub.GROUP_CREATE:
      return { kind: "system", text: `${actor} created group "${p[0] ?? ""}"` };
    case Stub.GROUP_CHANGE_SUBJECT:
      return { kind: "system", text: `${actor} changed the group name to "${p[0] ?? ""}"` };
    case Stub.GROUP_CHANGE_ICON:
      return { kind: "system", text: `${actor} changed the group icon` };
    case Stub.GROUP_CHANGE_DESCRIPTION:
      return { kind: "system", text: `${actor} changed the group description` };
    case Stub.GROUP_CHANGE_INVITE_LINK:
      return { kind: "system", text: `${actor} reset the group invite link` };
    case Stub.GROUP_CHANGE_ANNOUNCE:
      return {
        kind: "system",
        text: p[0] === "on" ? "Only admins can send messages" : "All participants can send messages",
      };
    case Stub.GROUP_CHANGE_RESTRICT:
      return {
        kind: "system",
        text: p[0] === "on" ? "Only admins can edit group info" : "All participants can edit group info",
      };
    case Stub.GROUP_PARTICIPANT_ADD:
    case Stub.GROUP_PARTICIPANT_INVITE:
    case Stub.GROUP_PARTICIPANT_ADD_REQUEST_JOIN:
    case Stub.GROUP_PARTICIPANT_ACCEPT:
      if (targetJids.length === 1 && ctx.canon(targetJids[0]) === ctx.canon(actorJid)) {
        return { kind: "system", text: `${targets} joined` };
      }
      return { kind: "system", text: `${actor} added ${targets || "someone"}` };
    case Stub.GROUP_PARTICIPANT_REMOVE:
      return { kind: "system", text: self ? `You removed ${targets}` : `${actor} removed ${targets}` };
    case Stub.GROUP_PARTICIPANT_LEAVE:
      return { kind: "system", text: `${targets || actor} left` };
    case Stub.GROUP_PARTICIPANT_PROMOTE:
      return { kind: "system", text: `${targets} is now an admin` };
    case Stub.GROUP_PARTICIPANT_DEMOTE:
      return { kind: "system", text: `${targets} is no longer an admin` };
    case Stub.CHANGE_EPHEMERAL_SETTING: {
      const secs = Number(p[0]);
      if (!secs) return { kind: "system", text: "Disappearing messages turned off" };
      const days = Math.round(secs / 86400);
      return { kind: "system", text: `Disappearing messages turned on (${days >= 1 ? days + " day" + (days > 1 ? "s" : "") : secs + " s"})` };
    }
    case Stub.CALL_MISSED_VOICE:
    case Stub.CALL_MISSED_GROUP_VOICE:
      return { kind: "call", text: "Missed voice call" };
    case Stub.CALL_MISSED_VIDEO:
    case Stub.CALL_MISSED_GROUP_VIDEO:
      return { kind: "call", text: "Missed video call" };
    case Stub.CIPHERTEXT:
      return { kind: "unsupported", text: "Waiting for this message. This may take a while." };
    default:
      return null;
  }
}

const STORABLE_STUBS = new Set<number>([
  Stub.REVOKE,
  Stub.ADMIN_REVOKE,
  Stub.CIPHERTEXT,
  Stub.GROUP_CREATE,
  Stub.GROUP_CHANGE_SUBJECT,
  Stub.GROUP_CHANGE_ICON,
  Stub.GROUP_CHANGE_DESCRIPTION,
  Stub.GROUP_CHANGE_INVITE_LINK,
  Stub.GROUP_CHANGE_ANNOUNCE,
  Stub.GROUP_CHANGE_RESTRICT,
  Stub.GROUP_PARTICIPANT_ADD,
  Stub.GROUP_PARTICIPANT_INVITE,
  Stub.GROUP_PARTICIPANT_ADD_REQUEST_JOIN,
  Stub.GROUP_PARTICIPANT_ACCEPT,
  Stub.GROUP_PARTICIPANT_REMOVE,
  Stub.GROUP_PARTICIPANT_LEAVE,
  Stub.GROUP_PARTICIPANT_PROMOTE,
  Stub.GROUP_PARTICIPANT_DEMOTE,
  Stub.CHANGE_EPHEMERAL_SETTING,
  Stub.CALL_MISSED_VOICE,
  Stub.CALL_MISSED_GROUP_VOICE,
  Stub.CALL_MISSED_VIDEO,
  Stub.CALL_MISSED_GROUP_VIDEO,
]);

const PinType = proto.Message.PinInChatMessage.Type;
/** The app offers 24 h / 7 d / 30 d; a pin that does not say how long it lasts gets WhatsApp's default (7 d). */
const DEFAULT_PIN_SECS = 7 * 86400;

/** The pinInChatMessage of a message (also inside the device-sent wrapper of history) and the context info with its duration. */
function pinMessage(raw: any): { pin?: any; info?: any } {
  const outer: any = extractMessageContent(raw.message);
  const inner: any = outer?.deviceSentMessage?.message ? extractMessageContent(outer.deviceSentMessage.message) : outer;
  if (!inner?.pinInChatMessage) return {};
  return { pin: inner.pinInChatMessage, info: inner.messageContextInfo ?? outer.messageContextInfo ?? raw.message?.messageContextInfo };
}

/**
 * A pin or unpin carried by a message: either a pinInChatMessage (live and offline messages; its duration is in
 * messageContextInfo) or the `pinInChat` WhatsApp attaches to the pinned message itself (history sync).
 */
export function pinEvent(raw: any): PinEvent | null {
  const { pin: pm, info } = pinMessage(raw);
  const sent = num(raw.messageTimestamp) * 1000;
  let id: string | undefined;
  let type: number;
  let at: number;
  let secs: number;
  if (pm) {
    id = pm.key?.id;
    type = num(pm.type);
    at = num(pm.senderTimestampMs) || sent;
    secs = num(info?.messageAddOnDurationInSecs);
  } else if (raw.pinInChat) {
    const p = raw.pinInChat;
    id = p.key?.id || raw.key?.id;
    type = num(p.type);
    at = num(p.senderTimestampMs) || num(p.serverTimestampMs) || sent;
    secs = num(p.messageAddOnContextInfo?.messageAddOnDurationInSecs);
  } else {
    return null;
  }
  if (!id || (type !== PinType.PIN_FOR_ALL && type !== PinType.UNPIN_FOR_ALL)) return null;
  at ||= Date.now();
  const pin = type === PinType.PIN_FOR_ALL;
  return { id, pin, at, expiresAt: pin ? at + (secs > 0 ? secs : DEFAULT_PIN_SECS) * 1000 : 0 };
}

/** Cheap pre-check used at ingestion: would this message ever be user-visible? */
export function isStorable(raw: any): boolean {
  const content = extractMessageContent(raw.message);
  if (content) {
    // A pin shows as a system line; an unpin leaves nothing in the chat.
    if (pinMessage(raw).pin) return !!pinEvent(raw)?.pin;
    const t = getContentType(content);
    return !!t && !HIDDEN.has(t);
  }
  return raw.messageStubType != null && STORABLE_STUBS.has(num(raw.messageStubType));
}

/** Map one stored message. Returns null for protocol-only / invisible messages. */
export function mapMessage(sm: StoredMsg, chatId: string, ctx: MapCtx): Message | null {
  const raw = sm.raw;
  const key = raw.key;
  if (!key?.id) return null;
  const fromMe = !!key.fromMe;
  const isGroup = chatId.endsWith("@g.us");
  const senderId = fromMe ? ctx.selfId : isGroup ? ctx.canon(key.participant || raw.participant) || chatId : chatId;
  const base: Message = {
    id: key.id,
    chatId,
    senderId,
    senderName: fromMe
      ? ctx.selfName
      : (ctx.nameOf(senderId) ?? (raw.pushName || undefined) ?? fallbackName(senderId)),
    fromMe,
    timestamp: num(raw.messageTimestamp) * 1000,
    kind: "text",
  };
  if (fromMe) base.status = statusOf(sm, chatId, ctx);

  const revoked = raw.messageStubType === Stub.REVOKE || raw.messageStubType === Stub.ADMIN_REVOKE;
  if (sm.deleted || revoked) return { ...base, kind: "text", deleted: true };

  const content = extractMessageContent(raw.message);
  if (!content) {
    const stub = raw.messageStubType ? stubOf(raw, chatId, ctx) : null;
    return stub ? { ...base, kind: stub.kind, text: stub.text } : null;
  }

  if (pinMessage(raw).pin) {
    if (!pinEvent(raw)?.pin) return null;
    return { ...base, kind: "system", text: `${fromMe ? "You" : base.senderName ?? fallbackName(senderId)} pinned a message` };
  }

  const d = decode(content);
  if (!d) return null;

  const out: Message = { ...base, kind: d.kind };
  if (d.text) out.text = d.text;
  if (d.media) {
    out.media = d.media;
    if (sm.path && existsSync(sm.path)) {
      out.media.path = sm.path;
      // Photos are re-encoded to HEIC when "compress images" is on: the proto still says image/jpeg.
      if (sm.path.endsWith(".heic")) out.media.mime = "image/heic";
    }
  }
  const replyTo = replyOf(d.ci, ctx);
  if (replyTo) out.replyTo = replyTo;
  if (d.ci?.isForwarded || num(d.ci?.forwardingScore) > 0) out.forwarded = true;
  if (sm.edited || raw.message?.editedMessage) out.edited = true;
  const reactions = aggregateReactions(raw);
  if (reactions) out.reactions = reactions;
  return out;
}

/** Is there downloadable media in this message? */
export function mediaContent(raw: any): any | undefined {
  const c: any = extractMessageContent(raw.message);
  if (!c) return undefined;
  for (const k of ["imageMessage", "videoMessage", "audioMessage", "documentMessage", "stickerMessage", "ptvMessage"]) {
    if (c[k]) return c[k];
  }
  return undefined;
}
