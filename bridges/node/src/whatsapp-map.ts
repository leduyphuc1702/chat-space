// WAMessage (Baileys proto) -> ChatSpace protocol Message.
import { extractMessageContent, getContentType, isPnUser, jidDecode, proto } from "baileys";
import { existsSync } from "node:fs";
import type { Media, Message, MessageKind, Reaction } from "./types.ts";
import { waJidUser } from "./mentions.ts";
import { num, type PinEvent, type StoredMsg } from "./whatsapp-store.ts";

export interface MapCtx {
  selfId: string;
  selfName?: string;
  canon(jid?: string | null): string;
  /** saved name / push name for a (canonical) jid, if known */
  nameOf(jid: string): string | undefined;
  groupSize(chatId: string): number | undefined;
  /** Other spellings (LID / phone jid) of the same person; `@<number>` tokens in a text may use any of them. */
  aliases?(jid: string): string[];
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
  /** URLs of the link preview (matchedText / canonicalUrl); mapMessage turns them into `links` once `text` is final */
  previewUrls?: string[];
}

/**
 * Content types that never show as a bubble: protocol traffic (edits, revokes, reactions, pins, key distribution,
 * history/peer-data requests), encrypted add-ons we cannot read (poll votes, event responses, secret-encrypted edits,
 * comments), the album parent (its photos are the album: see association()) and status / question / call plumbing.
 */
const HIDDEN = new Set([
  "protocolMessage",
  "reactionMessage",
  "encReactionMessage",
  "pollUpdateMessage",
  "senderKeyDistributionMessage",
  "fastRatchetKeySenderKeyDistributionMessage",
  "keepInChatMessage",
  "pinInChatMessage",
  "encEventResponseMessage",
  "encCommentMessage",
  "secretEncryptedMessage",
  "messageHistoryBundle",
  "messageHistoryNotice",
  "requestPhoneNumberMessage",
  "peerDataOperationRequestMessage",
  "peerDataOperationRequestResponseMessage",
  "albumMessage",
  "stickerSyncRmrMessage",
  "scheduledCallEditMessage",
  "bcallMessage",
  "limitSharingMessage",
  "statusNotificationMessage",
  "statusMentionMessage",
  "groupStatusMentionMessage",
  "statusQuestionAnswerMessage",
  "statusQuotedMessage",
  "statusStickerInteractionMessage",
  "questionMessage",
  "questionReplyMessage",
  "questionResponseMessage",
  "pollCreationOptionImageMessage",
]);

/**
 * Wrappers whose `.message` is the real content and that Baileys' extractMessageContent leaves alone (it unwraps
 * ephemeral, view-once v1/v2/ext, document-with-caption, edited, associated-child and group-status itself).
 */
const WRAPPERS = new Set([
  "deviceSentMessage",
  "groupMentionedMessage",
  "botInvokeMessage",
  "botForwardedMessage",
  "commentMessage",
  "pollCreationMessageV4",
]);

/** Layers that hold the `messageContextInfo` of a message, outermost first (see association()). */
const LAYERS = [
  "ephemeralMessage",
  "viewOnceMessage",
  "viewOnceMessageV2",
  "viewOnceMessageV2Extension",
  "documentWithCaptionMessage",
  "editedMessage",
  "associatedChildMessage",
  "groupStatusMessage",
  "groupStatusMessageV2",
  "deviceSentMessage",
];

const Assoc = proto.MessageAssociation.AssociationType;
/**
 * Companion uploads of a message that is already shown: the HD copy of a photo / video, the HEVC copy, the clip of a
 * motion photo. WhatsApp's own apps fold them into the original; here they would be a second, identical bubble.
 */
const COMPANION = new Set<number>([Assoc.HD_IMAGE_DUAL_UPLOAD, Assoc.HD_VIDEO_DUAL_UPLOAD, Assoc.HEVC_VIDEO_DUAL_UPLOAD, Assoc.MOTION_PHOTO]);

/**
 * The association a message declares to another one (messageContextInfo.messageAssociation). Albums: every photo /
 * video of the album points at the albumMessage that preceded it. The info sits at the top of the message or, for
 * wrapped content, in any layer, so all of them are searched. Read from the stored raw message, so it works the same
 * for live messages, history sync and a reloaded store.
 */
export function association(raw: any): { parentId: string; type: number } | undefined {
  let m: any = raw?.message;
  for (let i = 0; i < 6 && m; i++) {
    const a = m.messageContextInfo?.messageAssociation;
    if (a?.parentMessageKey?.id) {
      const t = typeof a.associationType === "string" ? (Assoc as any)[a.associationType] : a.associationType;
      return { parentId: String(a.parentMessageKey.id), type: num(t) };
    }
    const layer = LAYERS.find((k) => m[k]?.message);
    m = layer ? m[layer].message : undefined;
  }
  return undefined;
}

const isCompanion = (raw: any): boolean => {
  const a = association(raw);
  return !!a && COMPANION.has(a.type);
};

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

const str = (s: unknown): string | undefined => (typeof s === "string" && s.trim() ? s : undefined);
const joinLines = (...parts: unknown[]): string | undefined => parts.map(str).filter(Boolean).join("\n") || undefined;
/** The plain text of a nested proto message (a payment note). */
const noteOf = (msg: any): string | undefined => str(msg?.conversation) ?? str(msg?.extendedTextMessage?.text);
/** 12345 (thousandths) + "VND" -> "12.345 VND" */
const money = (amount1000: unknown, currency?: string | null): string | undefined => {
  const n = num(amount1000);
  return n ? `${n / 1000}${currency ? ` ${currency}` : ""}` : undefined;
};

/** Text of an interactive / template card: its heading and body. */
const cardText = (m: any): string | undefined => joinLines(m?.header?.title, m?.body?.text) ?? str(m?.footer?.text);

/** Content (already unwrapped) -> kind/text/media. null = not user-visible. */
function decode(content: any): Decoded | null {
  let type: string | undefined;
  for (let hops = 0; ; hops++) {
    if (!content) return null;
    type = getContentType(content) as string | undefined;
    if (!type || HIDDEN.has(type)) return null;
    if (!WRAPPERS.has(type)) break;
    // A wrapper without its inner message carries nothing to show.
    const inner = content[type]?.message;
    if (!inner || hops >= 3) return null;
    content = extractMessageContent(inner);
  }
  const m = content[type];

  switch (type) {
    case "conversation":
      return { kind: "text", text: String(m ?? "") };

    case "extendedTextMessage": {
      const previewUrls = [m.matchedText, m.canonicalUrl].filter((u): u is string => !!str(u));
      const out: Decoded = { kind: previewUrls.length ? "link" : "text", text: m.text ?? "", ci: m.contextInfo };
      if (previewUrls.length) {
        out.previewUrls = previewUrls;
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
      // Lottie stickers (animations in WhatsApp's own .was format) cannot be drawn: say what the sticker shows instead.
      if (m.isLottie || m.mimetype === "application/was") return lottieSticker(m);
      return { kind: "sticker", media: mediaOf(m, { mime: m.mimetype || "image/webp" }), ci: m.contextInfo };

    case "lottieStickerMessage":
      return lottieSticker(m?.message?.stickerMessage ?? {});

    case "stickerPackMessage":
      return { kind: "text", text: joinLines(`Sticker pack: ${m.name ?? ""}`.trim(), m.publisher), ci: m.contextInfo };

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
    case "pollCreationMessageV3":
    case "pollCreationMessageV5": {
      const opts = (m.options ?? []).map((o: any) => `- ${o.optionName}`);
      return { kind: "unsupported", text: [`Poll: ${m.name ?? ""}`, ...opts].join("\n"), ci: m.contextInfo };
    }

    case "pollResultSnapshotMessage":
    case "pollResultSnapshotMessageV3": {
      const votes = (m.pollVotes ?? []).map((v: any) => `- ${v.optionName}: ${num(v.optionVoteCount)}`);
      return { kind: "unsupported", text: [`Poll results: ${m.name ?? ""}`.trim(), ...votes].join("\n"), ci: m.contextInfo };
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
      return { kind: "unsupported", text: `${m.isCanceled ? "Cancelled event" : "Event"}: ${m.name ?? ""}`.trim(), ci: m.contextInfo };

    case "newsletterAdminInviteMessage":
      return { kind: "text", text: joinLines(`Channel admin invitation: ${m.newsletterName ?? ""}`.trim(), m.caption), ci: m.contextInfo };
    case "newsletterFollowerInviteMessageV2":
      return { kind: "text", text: joinLines(`Channel invitation: ${m.newsletterName ?? ""}`.trim(), m.caption), ci: m.contextInfo };

    // Replies to buttons / lists / cards. A response without anything to read is protocol traffic.
    case "buttonsResponseMessage":
    case "templateButtonReplyMessage":
      return visible({ kind: "text", text: str(m.selectedDisplayText), ci: m.contextInfo });
    case "listResponseMessage":
      return visible({ kind: "text", text: str(m.title) ?? str(m.singleSelectReply?.selectedRowId), ci: m.contextInfo });
    case "interactiveResponseMessage":
      return visible({ kind: "text", text: str(m.body?.text), ci: m.contextInfo });

    case "buttonsMessage":
      return { kind: "text", text: m.contentText || m.text || undefined, ci: m.contextInfo };
    case "listMessage":
      return { kind: "text", text: [m.title, m.description].filter(Boolean).join("\n") || undefined, ci: m.contextInfo };
    case "interactiveMessage":
      return { kind: "text", text: cardText(m) ?? "Interactive message", ci: m.contextInfo };
    case "templateMessage": {
      // Plain templates are already flattened by extractMessageContent; what is left is the interactive form.
      const t = m.interactiveMessageTemplate;
      return { kind: "text", text: (t && cardText(t)) ?? str(m.hydratedTemplate?.hydratedContentText) ?? "Template message", ci: m.contextInfo ?? t?.contextInfo };
    }
    case "highlyStructuredMessage": {
      const text = str(m.hydratedHsm?.hydratedTemplate?.hydratedContentText) ?? str(m.fallbackLg) ?? str(m.deterministicLg);
      return text ? { kind: "text", text } : { kind: "unsupported", text: unsupportedText(type) };
    }
    case "richResponseMessage": {
      // Meta AI answer: the readable parts of its sections.
      const text = joinLines(...(m.submessages ?? []).map((s: any) => s?.messageText));
      return { kind: "text", text: text ?? "AI response", ci: m.contextInfo };
    }

    // Shop and payments: one readable line each.
    case "orderMessage":
      return { kind: "text", text: joinLines(`Order: ${m.orderTitle ?? ""}`.trim(), m.itemCount ? `${num(m.itemCount)} items` : undefined, money(m.totalAmount1000, m.totalCurrencyCode), m.message), ci: m.contextInfo };
    case "productMessage": {
      const p = m.product ?? {};
      return { kind: "text", text: joinLines(`Product: ${p.title ?? ""}`.trim(), money(p.priceAmount1000, p.currencyCode), m.body, p.url), ci: m.contextInfo };
    }
    case "invoiceMessage":
      return { kind: "text", text: joinLines("Invoice", m.note) };
    case "requestPaymentMessage":
      return { kind: "text", text: joinLines("Payment request", money(m.amount1000, m.currencyCodeIso4217), noteOf(m.noteMessage)) };
    case "sendPaymentMessage":
      return { kind: "text", text: joinLines("Payment sent", noteOf(m.noteMessage)) };
    case "declinePaymentRequestMessage":
      return { kind: "text", text: "Payment request declined" };
    case "cancelPaymentRequestMessage":
      return { kind: "text", text: "Payment request cancelled" };
    case "paymentInviteMessage":
      return { kind: "text", text: "Payment invitation" };

    case "scheduledCallCreationMessage":
      return { kind: "text", text: `Scheduled call: ${m.title ?? ""}`.trim() };

    case "placeholderMessage":
      // WhatsApp keeps the content of some messages (business broadcasts, marketing) off linked devices.
      return { kind: "unsupported", text: "This message is not available here. Open WhatsApp on your phone to read it." };

    default:
      return { kind: "unsupported", text: unsupportedText(type), ci: m?.contextInfo };
  }
}

/** Fallback text: names the content type so a report of it can be acted on. */
const unsupportedText = (type: string): string => `Unsupported message (${type})`;

/** A decoded message with nothing to show is not a message. */
const visible = (d: Decoded): Decoded | null => (d.text ? d : null);

function lottieSticker(s: any): Decoded {
  return { kind: "unsupported", text: joinLines("Animated sticker", s?.accessibilityLabel), ci: s?.contextInfo };
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

/**
 * A tagged person is written in the text as `@<number>` (the user part of a phone jid or of a LID) and listed in
 * contextInfo.mentionedJid. Show `@Name` instead: the contact / push name, the account's own name for yourself, a
 * phone number as the last resort. A LID nobody can name stays as it is (a later sync can still resolve it, because
 * this runs every time the message is mapped). Tokens of people who are not in `mentionedJid` are left alone.
 */
export function resolveMentions(text: string, mentionedJid: unknown, ctx: MapCtx): string {
  if (!text || !Array.isArray(mentionedJid) || !mentionedJid.length || !text.includes("@")) return text;
  const names = new Map<string, string>();
  for (const j of mentionedJid) {
    if (typeof j !== "string" || !j) continue;
    const c = ctx.canon(j) || j;
    const name = c === ctx.selfId ? ctx.selfName || "You" : ctx.nameOf(c) ?? (isPnUser(c) ? fallbackName(c) : undefined);
    if (!name) continue;
    // The text may spell the person differently from the jid (LID in one, phone number in the other).
    for (const form of [j, c, ...(ctx.aliases?.(c) ?? [])]) {
      const user = waJidUser(form);
      if (user && !names.has(user)) names.set(user, name);
    }
  }
  if (!names.size) return text;
  // Only the users of this message's own mention list are looked for, longest first, and without word boundaries:
  // people do type a digit right behind a tag ("@1234567890123452 tuan") and the number cannot be mistaken for another.
  const users = [...names.keys()].sort((a, b) => b.length - a.length).map((u) => u.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  return text.replace(new RegExp(`@(${users.join("|")})`, "g"), (_, user: string) => `@${names.get(user)}`);
}

function replyOf(ci: any, ctx: MapCtx): Message["replyTo"] | undefined {
  if (!ci?.stanzaId) return undefined;
  const out: NonNullable<Message["replyTo"]> = { id: ci.stanzaId };
  const q = decode(extractMessageContent(ci.quotedMessage));
  if (q) {
    let label = quoteLabel(q);
    if (label) label = resolveMentions(label, q.ci?.mentionedJid, ctx);
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
    if (isCompanion(raw)) return false;
    return decode(content) !== null;
  }
  return raw.messageStubType != null && STORABLE_STUBS.has(num(raw.messageStubType));
}

/** `links` for a link preview whose URL the text does not contain (the app finds URLs that are written in the text). */
function previewLinks(text: string, urls: string[]): { text: string; links?: NonNullable<Message["links"]> } {
  const bare = (u: string) => u.trim().toLowerCase().replace(/^[a-z][a-z0-9+.-]*:\/\//, "").replace(/\/+$/, "");
  const hay = text.toLowerCase();
  if (urls.some((u) => hay.includes(bare(u)))) return { text };
  // matchedText is what the sender typed; canonicalUrl is where it really goes.
  const target = (urls[urls.length - 1] ?? "").trim();
  const url = /^[a-z][a-z0-9+.-]*:/i.test(target) ? target : `https://${target}`;
  // Nothing written to attach the link to: the URL is the text.
  if (!text.trim()) return { text: url };
  return { text, links: [{ offset: 0, length: text.length, url }] };
}

/** Map one stored message. Returns null for protocol-only / invisible messages. */
export function mapMessage(sm: StoredMsg, chatId: string, ctx: MapCtx): Message | null {
  const raw = sm.raw;
  const key = raw.key;
  if (!key?.id) return null;
  if (isCompanion(raw)) return null; // HD / motion companion of a photo that is shown already
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
  let text = d.text ? resolveMentions(d.text, d.ci?.mentionedJid, ctx) : d.text;
  if (d.previewUrls && text !== undefined) {
    const p = previewLinks(text, d.previewUrls);
    text = p.text;
    if (p.links) out.links = p.links;
  }
  if (text) out.text = text;
  // Album: every photo / video points at the albumMessage that opened it; the app draws messages with one groupId as one bubble.
  const assoc = association(raw);
  if (assoc?.type === Assoc.MEDIA_ALBUM) out.groupId = assoc.parentId;
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
