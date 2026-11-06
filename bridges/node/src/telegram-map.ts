// Pure mapping helpers: GramJS TL objects -> ChatSpace protocol objects.
// No I/O and no state here; everything environment-specific comes in through MapEnv.
import { Api, utils } from "telegram";
import { sanitizeMentions } from "./mentions.ts";
import type { Button, Chat, ChatKind, Folder, Media, Message, MessageKind, Reaction, ReplyKeyboard } from "./types.ts";

export type Entity = Api.TypeUser | Api.TypeChat;

export interface MapEnv {
  selfId: string;
  selfName: string;
  entity(id: string): Entity | undefined;
  /** Highest outgoing message id the peer has read (0 if unknown). */
  readOutboxMax(chatId: string): number;
  /** Absolute path of an already-cached avatar, if any. Must be cheap. */
  avatar?(id: string): string | undefined;
}

/** Marked peer id: users positive, basic groups -id, channels/supergroups -100id. */
export const peerKey = (p: Api.TypePeer): string => utils.getPeerId(p);

const num = (x: unknown): number | undefined => {
  if (x === undefined || x === null) return undefined;
  const n = Number(String(x));
  return Number.isFinite(n) ? n : undefined;
};

export function isMinEntity(e: Entity): boolean {
  return (e as { min?: boolean }).min === true;
}

export function entityName(e?: Entity): string | undefined {
  if (!e) return undefined;
  if (e instanceof Api.User) {
    if (e.deleted) return "Deleted Account";
    const n = [e.firstName, e.lastName].filter(Boolean).join(" ").trim();
    return n || e.username || (e.phone ? `+${e.phone}` : undefined) || "Unknown";
  }
  if (
    e instanceof Api.Chat ||
    e instanceof Api.Channel ||
    e instanceof Api.ChatForbidden ||
    e instanceof Api.ChannelForbidden
  ) {
    return e.title;
  }
  return undefined;
}

export function entityKind(e: Entity): ChatKind {
  if (e instanceof Api.User) return e.self ? "self" : e.bot ? "bot" : "direct";
  if (e instanceof Api.Channel || e instanceof Api.ChannelForbidden) {
    return e.broadcast ? "channel" : "group";
  }
  return "group";
}

export function hasProfilePhoto(e: Entity): boolean {
  const photo = (e as { photo?: unknown }).photo;
  return photo instanceof Api.UserProfilePhoto || photo instanceof Api.ChatPhoto;
}

export function userPresence(
  status?: Api.TypeUserStatus,
): { online: boolean; lastSeen?: number } | undefined {
  if (!status) return undefined;
  if (status instanceof Api.UserStatusOnline) return { online: true };
  if (status instanceof Api.UserStatusOffline) return { online: false, lastSeen: status.wasOnline * 1000 };
  // recently / last week / last month / empty: Telegram hides the exact time.
  return { online: false };
}

const FIVE_YEARS_MS = 5 * 365 * 86_400_000;
const FOREVER_S = 2 ** 31 - 1;

/**
 * Mute state from Telegram's notify settings. `muteUntil` is in seconds; official clients store "forever" as
 * 2^31-1, so anything more than a few years away becomes -1. A peer without its own `muteUntil` inherits the
 * account-wide default of its scope (all private chats / all groups / all channels).
 * (`silent` only turns the sound off, banners still show, so it does not count as muted.)
 */
export function muteInfo(
  own?: Api.TypePeerNotifySettings,
  scope?: Api.TypePeerNotifySettings,
): { muted: boolean; mutedUntil?: number } {
  const pick = (ns?: Api.TypePeerNotifySettings) => (ns instanceof Api.PeerNotifySettings ? ns.muteUntil : undefined);
  const until = pick(own) ?? pick(scope);
  if (until === undefined || until <= 0) return { muted: false };
  const ms = until * 1000;
  if (ms <= Date.now()) return { muted: false };
  return { muted: true, mutedUntil: ms - Date.now() > FIVE_YEARS_MS ? -1 : ms };
}

/** `muteUntil` (seconds) for the protocol's `until`: ms epoch, -1 = forever, 0 = unmute. */
export function muteUntilSeconds(untilMs: number): number {
  if (untilMs === 0) return 0;
  if (untilMs < 0) return FOREVER_S;
  return Math.min(Math.ceil(untilMs / 1000), FOREVER_S);
}

export function isMuted(ns?: Api.TypePeerNotifySettings): boolean {
  return muteInfo(ns).muted;
}

export type NotifyScope = "users" | "chats" | "broadcasts";

export function notifyScopeOf(e: Entity): NotifyScope {
  if (e instanceof Api.User) return "users";
  if (e instanceof Api.Channel && e.broadcast) return "broadcasts";
  return "chats";
}

// ---------------------------------------------------------------- media

const dataUrl = (mime: string, b: Buffer) => `data:${mime};base64,${b.toString("base64")}`;

function sniffImageMime(b: Buffer): string | undefined {
  if (b.length > 3 && b[0] === 0xff && b[1] === 0xd8) return "image/jpeg";
  if (b.length > 4 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47) return "image/png";
  if (b.length > 12 && b.toString("ascii", 0, 4) === "RIFF" && b.toString("ascii", 8, 12) === "WEBP") {
    return "image/webp";
  }
  return undefined;
}

/** Inline thumbnail from the stripped (tiny JPEG) or cached size, if the object carries one. */
export function thumbFrom(sizes?: Api.TypePhotoSize[]): string | undefined {
  if (!sizes) return undefined;
  for (const s of sizes) {
    if (s instanceof Api.PhotoStrippedSize) {
      try {
        return dataUrl("image/jpeg", utils.strippedPhotoToJpg(s.bytes));
      } catch {
        /* malformed stripped size: ignore */
      }
    }
  }
  for (const s of sizes) {
    if (s instanceof Api.PhotoCachedSize) {
      const mime = sniffImageMime(s.bytes);
      if (mime) return dataUrl(mime, s.bytes);
    }
  }
  return undefined;
}

function photoMedia(photo: Api.Photo): Media {
  let best: { w: number; h: number; size?: number } | undefined;
  for (const s of photo.sizes) {
    if (
      s instanceof Api.PhotoSize ||
      s instanceof Api.PhotoCachedSize ||
      s instanceof Api.PhotoSizeProgressive
    ) {
      const size =
        s instanceof Api.PhotoSize
          ? s.size
          : s instanceof Api.PhotoSizeProgressive
            ? Math.max(...s.sizes)
            : s.bytes.length;
      if (!best || s.w * s.h > best.w * best.h) best = { w: s.w, h: s.h, size };
    }
  }
  return {
    mime: "image/jpeg",
    width: best?.w,
    height: best?.h,
    size: best?.size,
    thumbnail: thumbFrom(photo.sizes),
  };
}

function documentMedia(
  doc: Api.Document,
  flags: { voice?: boolean; round?: boolean },
): { kind: MessageKind; media: Media } {
  const media: Media = { mime: doc.mimeType, size: num(doc.size), thumbnail: thumbFrom(doc.thumbs) };
  let sticker: Api.DocumentAttributeSticker | undefined;
  let audio: Api.DocumentAttributeAudio | undefined;
  let video: Api.DocumentAttributeVideo | undefined;
  let imageSize: Api.DocumentAttributeImageSize | undefined;
  let animated = false;
  for (const a of doc.attributes) {
    if (a instanceof Api.DocumentAttributeSticker) sticker = a;
    else if (a instanceof Api.DocumentAttributeAudio) audio = a;
    else if (a instanceof Api.DocumentAttributeVideo) video = a;
    else if (a instanceof Api.DocumentAttributeAnimated) animated = true;
    else if (a instanceof Api.DocumentAttributeImageSize) imageSize = a;
    else if (a instanceof Api.DocumentAttributeFilename) media.fileName = a.fileName;
  }
  if (imageSize) {
    media.width = imageSize.w;
    media.height = imageSize.h;
  }
  if (video) {
    media.width = video.w;
    media.height = video.h;
    media.duration = Math.round(video.duration);
  }

  let kind: MessageKind = "file";
  if (sticker) {
    // image/webp (static), video/webm (video sticker) or application/x-tgsticker (animated .tgs)
    kind = "sticker";
  } else if (audio) {
    kind = audio.voice || flags.voice ? "voice" : "audio";
    media.duration = audio.duration;
    if (kind === "audio" && !media.fileName && (audio.title || audio.performer)) {
      media.fileName = [audio.performer, audio.title].filter(Boolean).join(" - ");
    }
  } else if (animated) {
    kind = "gif";
  } else if (video || flags.round) {
    kind = "video"; // includes round video notes
  } else if (doc.mimeType.startsWith("image/")) {
    kind = "image";
  }
  return { kind, media };
}

const coords = (g: Api.TypeGeoPoint) =>
  g instanceof Api.GeoPoint ? `${g.lat.toFixed(6)}, ${g.long.toFixed(6)}` : "unavailable";

const LINK_ONLY = /^https?:\/\/\S+$/i;

interface Mapped {
  kind: MessageKind;
  text?: string;
  media?: Media;
}

function mapMedia(raw: Api.Message): Mapped {
  const caption = raw.message || undefined;
  const media = raw.media;
  if (!media || media instanceof Api.MessageMediaEmpty) return { kind: "text", text: caption };

  if (media instanceof Api.MessageMediaPhoto) {
    if (media.photo instanceof Api.Photo) {
      return { kind: "image", text: caption, media: photoMedia(media.photo) };
    }
    return { kind: "unsupported", text: caption ?? "Photo (no longer available)" };
  }

  if (media instanceof Api.MessageMediaDocument) {
    if (media.document instanceof Api.Document) {
      const { kind, media: m } = documentMedia(media.document, { voice: media.voice, round: media.round });
      return { kind, text: caption, media: m };
    }
    return { kind: "unsupported", text: caption ?? "File (no longer available)" };
  }

  if (media instanceof Api.MessageMediaGeo) {
    return { kind: "location", text: coords(media.geo) };
  }
  if (media instanceof Api.MessageMediaGeoLive) {
    return { kind: "location", text: `Live location: ${coords(media.geo)}` };
  }
  if (media instanceof Api.MessageMediaVenue) {
    const lines = [media.title, media.address, coords(media.geo)].filter(Boolean);
    return { kind: "location", text: lines.join("\n") };
  }

  if (media instanceof Api.MessageMediaContact) {
    const name = [media.firstName, media.lastName].filter(Boolean).join(" ").trim();
    const phone = media.phoneNumber ? (media.phoneNumber.startsWith("+") ? media.phoneNumber : `+${media.phoneNumber}`) : "";
    return { kind: "contact", text: [name, phone].filter(Boolean).join("\n") || "Contact" };
  }

  if (media instanceof Api.MessageMediaWebPage) {
    const wp = media.webpage;
    const trimmed = (raw.message || "").trim();
    if (wp instanceof Api.WebPage && trimmed) {
      const linkOnly = LINK_ONLY.test(trimmed) || trimmed === wp.url || trimmed === wp.displayUrl;
      if (linkOnly) {
        const photo = wp.photo instanceof Api.Photo ? photoMedia(wp.photo) : undefined;
        return { kind: "link", text: raw.message, media: photo };
      }
    }
    return { kind: "text", text: caption };
  }

  if (media instanceof Api.MessageMediaPoll) {
    // Voting is not possible from here, so the poll is shown as the readable text it is (not as "unsupported").
    return { kind: "text", text: pollText(media) };
  }
  if (media instanceof Api.MessageMediaDice) {
    return { kind: "text", text: `${media.emoticon} ${media.value}` };
  }

  const label =
    media instanceof Api.MessageMediaGame
      ? "Game"
      : media instanceof Api.MessageMediaInvoice
        ? "Invoice"
        : media instanceof Api.MessageMediaStory
          ? "Story"
          : media instanceof Api.MessageMediaGiveaway
            ? "Giveaway"
            : media instanceof Api.MessageMediaGiveawayResults
              ? "Giveaway results"
              : media instanceof Api.MessageMediaPaidMedia
                ? "Paid media"
                : "Unsupported message";
  return { kind: "unsupported", text: [label, caption].filter(Boolean).join("\n") };
}

/** Telegram sends some strings as plain text and newer layers as TextWithEntities. */
const plainText = (x: unknown): string => (typeof x === "string" ? x : ((x as { text?: string } | null)?.text ?? ""));

/** "📊 Poll: question" with one line per option: ○ open, ◉ chosen by us, ✓ the quiz answer, and the vote count when known. */
export function pollText(media: Api.MessageMediaPoll): string {
  const poll = media.poll;
  const results = media.results?.results ?? [];
  const lines = [`\u{1F4CA} ${poll.quiz ? "Quiz" : "Poll"}: ${plainText(poll.question)}`.trim()];
  for (const a of poll.answers) {
    const r = results.find((x) => Buffer.compare(Buffer.from(x.option), Buffer.from(a.option)) === 0);
    const mark = r?.correct ? "\u2713" : r?.chosen ? "\u25C9" : "\u25CB";
    const votes = r && r.voters > 0 ? ` \u2014 ${r.voters}` : "";
    lines.push(`${mark} ${plainText(a.text)}${votes}`);
  }
  return lines.join("\n");
}

// ---------------------------------------------------------------- text links

const HAS_SCHEME = /^[a-z][a-z0-9+.-]*:\/\//i;
const MAX_LINKS = 100;

/**
 * Links of a message whose target is not simply the text: hidden URLs (MessageEntityTextUrl, "[words](url)"), URLs
 * written without a scheme ("t.me/x", "example.com/a": the url gets https://) and e-mail addresses (mailto:).
 * Offsets are UTF-16 into `text`, like Telegram's own, so they are only valid while `text` is the message text itself
 * (not a rendered placeholder such as a location or poll). Plain `https://…` URLs are left to the app.
 */
export function mapLinks(raw: Api.Message, text: string | undefined): NonNullable<Message["links"]> | undefined {
  if (!text || !raw.entities?.length || text !== raw.message) return undefined;
  const out: NonNullable<Message["links"]> = [];
  for (const e of raw.entities) {
    const { offset, length } = e;
    if (!(offset >= 0 && length > 0 && offset + length <= text.length)) continue;
    const shown = text.slice(offset, offset + length);
    let url: string | undefined;
    if (e instanceof Api.MessageEntityTextUrl) url = e.url?.trim();
    else if (e instanceof Api.MessageEntityUrl) url = HAS_SCHEME.test(shown) ? undefined : `https://${shown}`;
    else if (e instanceof Api.MessageEntityEmail) url = `mailto:${shown}`;
    if (url) out.push({ offset, length, url });
    if (out.length >= MAX_LINKS) break;
  }
  out.sort((a, b) => a.offset - b.offset);
  return out.length ? out : undefined;
}

const VS16 = "\uFE0F";
const TEXT_DEFAULT_EMOJI = /^\p{Emoji}$/u;
const EMOJI_DEFAULT_EMOJI = /^\p{Emoji_Presentation}$/u;

/**
 * Telegram reports reactions without the emoji variation selector ("❤" = U+2764 only), which the app renders as a
 * black text-style glyph. A lone scalar that is an emoji but defaults to text presentation (❤ ☺ ☹ ✌ ☝ ✍ ❣ ☃ 🕊 …) gets
 * U+FE0F appended. Emoji-by-default scalars (⭐ ⚡ 👍 …), ASCII like "#" or "1", and multi-scalar sequences are untouched.
 */
export function normalizeReactionEmoji(emoji: string): string {
  if (emoji.endsWith(VS16)) return emoji;
  const scalars = Array.from(emoji);
  if (scalars.length !== 1) return emoji;
  const cp = scalars[0]!.codePointAt(0)!;
  if (cp < 0x80 || !TEXT_DEFAULT_EMOJI.test(emoji) || EMOJI_DEFAULT_EMOJI.test(emoji)) return emoji;
  return emoji + VS16;
}

/**
 * Inverse for sending: Telegram's allowed reactions are the bare forms ("❤", "✍", and "❤‍🔥" = heart + ZWJ + fire), never
 * with U+FE0F, so every variation selector is stripped ("❤️" -> "❤").
 */
export function reactionEmoticonForTelegram(emoji: string): string {
  return emoji.replaceAll(VS16, "");
}

export function mapReactions(r?: Api.MessageReactions): Reaction[] | undefined {
  if (!r || !r.results || r.results.length === 0) return undefined;
  const out: Reaction[] = [];
  for (const rc of r.results) {
    let emoji: string | undefined;
    if (rc.reaction instanceof Api.ReactionEmoji) emoji = normalizeReactionEmoji(rc.reaction.emoticon);
    else if (rc.reaction instanceof Api.ReactionPaid) emoji = "\u2B50";
    // Custom-emoji reactions have no plain-text form; skipped.
    if (!emoji) continue;
    out.push({ emoji, count: rc.count, fromMe: rc.chosenOrder !== undefined && rc.chosenOrder !== null });
  }
  return out.length ? out : undefined;
}

// ---------------------------------------------------------------- service messages

const mmss = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, "0")}`;

function plural(n: number, unit: string) {
  return `${n} ${unit}${n === 1 ? "" : "s"}`;
}

function ttlText(sec: number): string {
  if (sec % 2678400 === 0) return plural(sec / 2678400, "month");
  if (sec % 604800 === 0) return plural(sec / 604800, "week");
  if (sec % 86400 === 0) return plural(sec / 86400, "day");
  if (sec % 3600 === 0) return plural(sec / 3600, "hour");
  return plural(sec, "second");
}

function humanizeClass(name: string): string {
  return name
    .replace(/^MessageAction/, "")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .toLowerCase();
}

const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

function callText(a: Api.MessageActionPhoneCall, out: boolean): string {
  const base = `${a.video ? "video" : "voice"} call`;
  const r = a.reason;
  if (r instanceof Api.PhoneCallDiscardReasonMissed) return cap(out ? `cancelled ${base}` : `missed ${base}`);
  if (r instanceof Api.PhoneCallDiscardReasonBusy) return cap(`declined ${base}`);
  let s = `${out ? "Outgoing" : "Incoming"} ${base}`;
  if (r instanceof Api.PhoneCallDiscardReasonDisconnect) s += " (connection lost)";
  else if (a.duration) s += ` (${mmss(a.duration)})`;
  return s;
}

function serviceText(a: Api.TypeMessageAction, actor: string, env: MapEnv): string {
  const names = (ids: unknown[]) =>
    ids.map((id) => entityName(env.entity(String(id))) ?? "a member").join(", ");

  if (a instanceof Api.MessageActionChatCreate) return `${actor} created the group "${a.title}"`;
  if (a instanceof Api.MessageActionChatEditTitle) return `${actor} changed the group name to "${a.title}"`;
  if (a instanceof Api.MessageActionChatEditPhoto) return `${actor} changed the group photo`;
  if (a instanceof Api.MessageActionChatDeletePhoto) return `${actor} removed the group photo`;
  if (a instanceof Api.MessageActionChatAddUser) {
    if (a.users.length === 1 && env.entity(String(a.users[0])) && entityName(env.entity(String(a.users[0]))) === actor) {
      return `${actor} joined the group`;
    }
    return `${actor} added ${names(a.users)}`;
  }
  if (a instanceof Api.MessageActionChatDeleteUser) {
    const who = entityName(env.entity(String(a.userId)));
    return who && who !== actor ? `${actor} removed ${who}` : `${actor} left the group`;
  }
  if (a instanceof Api.MessageActionChatJoinedByLink) return `${actor} joined the group via invite link`;
  if (a instanceof Api.MessageActionChatJoinedByRequest) return `${actor} joined the group`;
  if (a instanceof Api.MessageActionChannelCreate) return `Channel "${a.title}" created`;
  if (a instanceof Api.MessageActionChatMigrateTo) return "This group was upgraded to a supergroup";
  if (a instanceof Api.MessageActionChannelMigrateFrom) return `This supergroup was upgraded from "${a.title}"`;
  if (a instanceof Api.MessageActionPinMessage) return `${actor} pinned a message`;
  if (a instanceof Api.MessageActionHistoryClear) return "Chat history was cleared";
  if (a instanceof Api.MessageActionGameScore) return `${actor} scored ${a.score} in a game`;
  if (a instanceof Api.MessageActionPaymentSent) return "Payment sent";
  if (a instanceof Api.MessageActionScreenshotTaken) return `${actor} took a screenshot`;
  if (a instanceof Api.MessageActionCustomAction) return a.message;
  if (a instanceof Api.MessageActionBotAllowed) {
    return a.domain ? `${actor} logged in on ${a.domain}` : `${actor} allowed this bot to send messages`;
  }
  if (a instanceof Api.MessageActionContactSignUp) return `${actor} joined Telegram`;
  if (a instanceof Api.MessageActionGeoProximityReached) {
    const from = entityName(env.entity(utils.getPeerId(a.fromId))) ?? "Someone";
    const to = entityName(env.entity(utils.getPeerId(a.toId))) ?? "someone";
    return `${from} is now within ${a.distance} m of ${to}`;
  }
  if (a instanceof Api.MessageActionGroupCall) {
    return a.duration ? `Voice chat ended (${mmss(a.duration)})` : "Voice chat started";
  }
  if (a instanceof Api.MessageActionGroupCallScheduled) return "Voice chat scheduled";
  if (a instanceof Api.MessageActionInviteToGroupCall) return `${actor} invited ${names(a.users)} to the voice chat`;
  if (a instanceof Api.MessageActionSetMessagesTTL) {
    return a.period ? `${actor} set messages to auto-delete in ${ttlText(a.period)}` : `${actor} turned off auto-delete`;
  }
  if (a instanceof Api.MessageActionSetChatTheme) return `${actor} changed the chat theme`;
  if (a instanceof Api.MessageActionSetChatWallPaper) return `${actor} changed the chat wallpaper`;
  if (a instanceof Api.MessageActionTopicCreate) return `${actor} created the topic "${a.title}"`;
  if (a instanceof Api.MessageActionTopicEdit) {
    if (a.title) return `${actor} renamed the topic to "${a.title}"`;
    if (a.closed !== undefined) return `${actor} ${a.closed ? "closed" : "reopened"} the topic`;
    if (a.hidden !== undefined) return `${actor} ${a.hidden ? "hid" : "showed"} the topic`;
    return `${actor} edited a topic`;
  }
  if (a instanceof Api.MessageActionSuggestProfilePhoto) return `${actor} suggested a profile photo`;
  if (a instanceof Api.MessageActionGiftPremium) {
    return `${actor} gifted Telegram Premium for ${plural(a.months, "month")}`;
  }
  if (a instanceof Api.MessageActionGiftCode) {
    return `${actor} received a Telegram Premium gift code for ${plural(a.months, "month")}`;
  }
  if (a instanceof Api.MessageActionGiftStars) return `${actor} gifted ${plural(Number(a.stars), "Star")}`;
  if (a instanceof Api.MessageActionPrizeStars) return `${actor} won ${plural(Number(a.stars), "Star")} in a giveaway`;
  if (a instanceof Api.MessageActionStarGift || a instanceof Api.MessageActionStarGiftUnique) return `${actor} sent a gift`;
  if (a instanceof Api.MessageActionBoostApply) return `${actor} boosted the group ${plural(a.boosts, "time")}`;
  if (a instanceof Api.MessageActionGiveawayLaunch) return "A giveaway started";
  if (a instanceof Api.MessageActionGiveawayResults) {
    return `Giveaway ended: ${plural(a.winnersCount, "winner")}`;
  }
  if (a instanceof Api.MessageActionPaymentSentMe) return "Payment received";
  if (a instanceof Api.MessageActionPaymentRefunded) return "Payment refunded";
  if (a instanceof Api.MessageActionWebViewDataSent) return `${actor} sent data from a web app: ${a.text}`;
  if (a instanceof Api.MessageActionSecureValuesSent) return `${actor} shared Telegram Passport data`;
  if (a instanceof Api.MessageActionRequestedPeer) return `${actor} shared a chat with the bot`;
  return `${actor}: ${humanizeClass(a.className)}`;
}

// ---------------------------------------------------------------- replies

/** The service line "X pinned a message". */
export function isPinNotice(raw: Api.TypeMessage): boolean {
  return raw instanceof Api.MessageService && raw.action instanceof Api.MessageActionPinMessage;
}

/** Id of the same-chat message this one replies to (forum-topic roots and cross-chat replies excluded). */
export function replyTarget(raw: Api.TypeMessage, chatId?: string): number | undefined {
  if (!(raw instanceof Api.Message || raw instanceof Api.MessageService)) return undefined;
  const rh = raw.replyTo;
  if (!(rh instanceof Api.MessageReplyHeader)) return undefined;
  if (rh.replyToMsgId === undefined || rh.replyToMsgId === null) return undefined;
  if (rh.replyToScheduled) return undefined;
  if (rh.forumTopic && (rh.replyToTopId === undefined || rh.replyToTopId === null)) return undefined;
  if (rh.replyToPeerId && chatId && peerKey(rh.replyToPeerId) !== chatId) return undefined;
  return rh.replyToMsgId;
}

const KIND_LABEL: Record<MessageKind, string> = {
  text: "Message",
  image: "Photo",
  video: "Video",
  audio: "Audio",
  voice: "Voice message",
  file: "File",
  sticker: "Sticker",
  gif: "GIF",
  location: "Location",
  contact: "Contact",
  link: "Link",
  call: "Call",
  system: "Service message",
  unsupported: "Unsupported message",
};

const PREVIEW_MAX = 200;

/**
 * Text of a replied-to message for the quote above a reply. Cut by UTF-16 units but never between the halves of a
 * surrogate pair: a lone surrogate serialises as "\ud83d", which Swift's JSON decoder rejects, and that dropped the
 * whole `message.new` / `messages.list` frame the reply was in.
 */
export function previewText(m: Pick<Message, "text" | "kind">): string {
  const t = (m.text ?? "").trim() || KIND_LABEL[m.kind];
  if (t.length <= PREVIEW_MAX) return t;
  const last = t.charCodeAt(PREVIEW_MAX - 1);
  return t.slice(0, last >= 0xd800 && last <= 0xdbff ? PREVIEW_MAX - 1 : PREVIEW_MAX);
}

export type ReplyPreview = Pick<Message, "text" | "kind" | "senderName">;

/** Fill what the quote needs from the replied-to message. A quoted excerpt (`quoteText`) already in `text` is kept. */
export function applyReplyPreview(replyTo: NonNullable<Message["replyTo"]>, target: ReplyPreview): void {
  if (!replyTo.text) replyTo.text = previewText(target);
  if (target.senderName) replyTo.senderName = target.senderName;
  replyTo.kind = target.kind;
}

export function toReplyPreview(m: Message): ReplyPreview {
  return { text: previewText(m), kind: m.kind, senderName: m.senderName };
}

/**
 * Resolves `replyTo` of mapped messages to the message they answer, for as little network as possible: from the
 * page itself, from messages already seen (`recent`), from earlier lookups (kept, deleted targets too), and
 * otherwise with ONE batched `fetch` per call (callers pass a page, or the single live message).
 */
export class ReplyResolver {
  private readonly cache = new Map<string, ReplyPreview | null>(); // null = target is gone (deleted)

  constructor(
    private readonly deps: {
      recent(chatId: string, id: number): Message | undefined;
      /** One request for all `ids` of the chat; returns the ones that exist. */
      fetch(chatId: string, ids: number[]): Promise<Map<number, Message>>;
      log?(message: string): void;
    },
    private readonly max = 5000,
  ) {}

  forget(chatId: string, id: string | number) {
    this.cache.delete(`${chatId}:${id}`);
  }

  clear() {
    this.cache.clear();
  }

  private remember(key: string, v: ReplyPreview | null) {
    this.cache.delete(key);
    this.cache.set(key, v);
    if (this.cache.size > this.max) this.cache.delete(this.cache.keys().next().value as string);
  }

  /** `raws[i]` is the TL message `mapped[i]` was made from. Only same-chat replies are resolved (not cross-chat quotes). */
  async fill(chatId: string, raws: Api.TypeMessage[], mapped: Message[]): Promise<void> {
    const target = (i: number) => (raws.length === mapped.length ? replyTarget(raws[i]!, chatId) : undefined);
    const want = new Set<number>();
    for (let i = 0; i < mapped.length; i++) {
      const t = target(i);
      if (t !== undefined) want.add(t);
    }
    if (want.size === 0) return;

    const known = new Map<number, ReplyPreview>();
    for (const m of mapped) known.set(Number(m.id), m);
    const missing: number[] = [];
    for (const id of want) {
      if (known.has(id)) continue;
      const hit = this.deps.recent(chatId, id);
      if (hit) {
        known.set(id, hit);
        continue;
      }
      const cached = this.cache.get(`${chatId}:${id}`);
      if (cached) known.set(id, cached);
      else if (cached === undefined) missing.push(id); // null: known to be deleted, nothing to ask
    }

    // 100 ids is the most Telegram answers in one getMessages call; a page's replies rarely get near it.
    for (let i = 0; i < missing.length; i += 100) {
      const ids = missing.slice(i, i + 100);
      try {
        const found = await this.deps.fetch(chatId, ids);
        for (const id of ids) {
          const m = found.get(id);
          if (m) {
            const p = toReplyPreview(m);
            known.set(id, p);
            this.remember(`${chatId}:${id}`, p);
          } else {
            this.remember(`${chatId}:${id}`, null);
          }
        }
      } catch (e) {
        this.deps.log?.(`could not resolve replied-to messages in ${chatId}: ${e instanceof Error ? e.message : String(e)}`);
      }
    }

    for (let i = 0; i < mapped.length; i++) {
      const m = mapped[i]!;
      const t = target(i);
      const preview = t === undefined ? undefined : known.get(t);
      if (m.replyTo && preview) applyReplyPreview(m.replyTo, preview);
    }
  }
}

// ---------------------------------------------------------------- bot keyboards

/** The @username of a bot (or any user), from the new `usernames` list or the legacy field. */
export function usernameOf(e?: Entity): string | undefined {
  if (!(e instanceof Api.User)) return undefined;
  if (e.username) return e.username;
  const active = e.usernames?.find((u) => u.active) ?? e.usernames?.[0];
  return active?.username || undefined;
}

/**
 * One Telegram keyboard button -> protocol button. Buttons the app cannot act on (payments, sharing a phone
 * number / location / poll / chat with the bot) keep their label as `unsupported`, so a row still looks like the
 * bot designed it.
 */
export function mapButton(b: Api.TypeKeyboardButton, botUsername?: string): Button {
  const text = (b as { text?: string }).text ?? "";
  if (b instanceof Api.KeyboardButtonCallback || b instanceof Api.KeyboardButtonRequestPeer) {
    return b instanceof Api.KeyboardButtonCallback ? { text, kind: "callback" } : { text, kind: "unsupported" };
  }
  if (b instanceof Api.KeyboardButtonUrl) return { text, kind: "url", url: b.url };
  if (b instanceof Api.KeyboardButtonUrlAuth) return { text, kind: "login", url: b.url };
  if (b instanceof Api.KeyboardButtonSwitchInline) {
    const out: Button = { text, kind: "switch_inline" };
    if (botUsername) out.insert = `@${botUsername} ${b.query}`.trimEnd() + (b.query ? "" : " ");
    if (b.samePeer) out.samePeer = true;
    return out;
  }
  if (b instanceof Api.KeyboardButtonWebView || b instanceof Api.KeyboardButtonSimpleWebView) {
    return { text, kind: "web_app", url: b.url };
  }
  if (b instanceof Api.KeyboardButtonGame) return { text, kind: "game" };
  if (b instanceof Api.KeyboardButtonCopy) return { text, kind: "copy", copyText: b.copyText };
  if (b instanceof Api.KeyboardButtonUserProfile) return { text, kind: "user_profile", url: `tg://user?id=${b.userId}` };
  if (b instanceof Api.KeyboardButton) return { text, kind: "text" };
  // KeyboardButtonBuy, RequestPhone, RequestGeoLocation, RequestPoll, Input* variants
  return { text, kind: "unsupported" };
}

function mapRows(rows: Api.TypeKeyboardButtonRow[] | undefined, botUsername?: string): Button[][] {
  const out: Button[][] = [];
  for (const r of rows ?? []) {
    if (!(r instanceof Api.KeyboardButtonRow)) continue;
    const row = r.buttons.map((b) => mapButton(b, botUsername));
    if (row.length) out.push(row);
  }
  return out;
}

export interface MappedMarkup {
  buttons?: Button[][];
  replyKeyboard?: ReplyKeyboard;
  hideKeyboard?: boolean;
  forceReply?: { placeholder?: string; oneTime?: boolean };
}

/** `Message.replyMarkup` -> the protocol's inline buttons / bot keyboard fields (empty object when there is none). */
export function mapReplyMarkup(markup: Api.TypeReplyMarkup | undefined, botUsername?: string): MappedMarkup {
  if (!markup) return {};
  if (markup instanceof Api.ReplyInlineMarkup) {
    const buttons = mapRows(markup.rows, botUsername);
    return buttons.length ? { buttons } : {};
  }
  if (markup instanceof Api.ReplyKeyboardMarkup) {
    const rows = mapRows(markup.rows, botUsername);
    if (!rows.length) return {};
    const kb: ReplyKeyboard = { rows };
    if (markup.resize) kb.resize = true;
    if (markup.singleUse) kb.oneTime = true;
    if (markup.persistent) kb.persistent = true;
    if (markup.selective) kb.selective = true;
    if (markup.placeholder) kb.placeholder = markup.placeholder;
    return { replyKeyboard: kb };
  }
  if (markup instanceof Api.ReplyKeyboardHide) return { hideKeyboard: true };
  if (markup instanceof Api.ReplyKeyboardForceReply) {
    const fr: { placeholder?: string; oneTime?: boolean } = {};
    if (markup.placeholder) fr.placeholder = markup.placeholder;
    if (markup.singleUse) fr.oneTime = true;
    return { forceReply: fr };
  }
  return {};
}

// ---------------------------------------------------------------- messages

export interface MapOptions {
  /** Force fromMe (messages returned by our own send calls may lack the `out` flag). */
  forceOut?: boolean;
}

export function mapMessage(raw: Api.TypeMessage, env: MapEnv, opts: MapOptions = {}): Message | undefined {
  if (!(raw instanceof Api.Message || raw instanceof Api.MessageService)) return undefined;
  if (!raw.peerId) return undefined;

  const chatId = peerKey(raw.peerId);
  const fromMe = opts.forceOut ?? !!raw.out;
  const senderId = fromMe ? env.selfId : raw.fromId ? peerKey(raw.fromId) : chatId;
  const senderName = fromMe ? env.selfName : entityName(env.entity(senderId));

  const msg: Message = {
    id: String(raw.id),
    chatId,
    senderId,
    senderName,
    fromMe,
    timestamp: raw.date * 1000,
    kind: "text",
  };
  const senderAvatar = fromMe ? undefined : env.avatar?.(senderId);
  if (senderAvatar) msg.senderAvatar = senderAvatar;

  if (raw instanceof Api.MessageService) {
    const action = raw.action;
    if (action instanceof Api.MessageActionPhoneCall) {
      msg.kind = "call";
      msg.text = callText(action, fromMe);
    } else {
      msg.kind = "system";
      msg.text = serviceText(action, fromMe ? "You" : (senderName ?? "Someone"), env);
    }
    return msg;
  }

  const mapped = mapMedia(raw);
  msg.kind = mapped.kind;
  if (mapped.text) msg.text = mapped.text;
  if (mapped.media) msg.media = mapped.media;
  // `text` is always the words as written ("@Name" for a mention, never an id); entities only add information.
  const links = mapLinks(raw, mapped.text);
  if (links) msg.links = links;
  // Album: photos/videos sent together are separate messages that share a groupedId.
  if (raw.groupedId !== undefined && raw.groupedId !== null && String(raw.groupedId) !== "0") {
    msg.groupId = String(raw.groupedId);
  }

  const rt = replyTarget(raw, chatId);
  const rh = raw.replyTo;
  if (rt !== undefined) {
    msg.replyTo = { id: String(rt) };
    if (rh instanceof Api.MessageReplyHeader && rh.quoteText) msg.replyTo.text = rh.quoteText;
  } else if (rh instanceof Api.MessageReplyHeader && rh.replyToMsgId !== undefined && rh.quoteText) {
    // cross-chat reply: all we have is the quote
    msg.replyTo = { id: String(rh.replyToMsgId), text: rh.quoteText };
  }

  if (raw.fwdFrom) msg.forwarded = true;
  if (raw.editDate && !raw.editHide) msg.edited = true;
  const reactions = mapReactions(raw.reactions);
  if (reactions) msg.reactions = reactions;
  if (fromMe && !raw.post) {
    msg.status = raw.id <= env.readOutboxMax(chatId) ? "read" : "sent";
  }
  if (raw.replyMarkup) {
    // "Switch to inline" buttons need the bot's @username: the bot that sent the message, or the one it was sent via.
    const bot = raw.viaBotId ? env.entity(String(raw.viaBotId)) : env.entity(senderId);
    const kb = mapReplyMarkup(raw.replyMarkup, usernameOf(bot));
    if (kb.buttons) msg.buttons = kb.buttons;
    if (kb.replyKeyboard) msg.replyKeyboard = kb.replyKeyboard;
    if (kb.hideKeyboard) msg.hideKeyboard = true;
    if (kb.forceReply) msg.forceReply = kb.forceReply;
  }
  return msg;
}

// ---------------------------------------------------------------- chats

/** Returns undefined for entities that cannot be shown (empty, forbidden, upgraded/deactivated groups). */
export function buildChat(
  entity: Entity,
  dialog: Api.Dialog | undefined,
  last: Message | undefined,
  env: MapEnv,
  scopeNotify?: Api.TypePeerNotifySettings,
): Chat | undefined {
  if (
    entity instanceof Api.UserEmpty ||
    entity instanceof Api.ChatEmpty ||
    entity instanceof Api.ChatForbidden ||
    entity instanceof Api.ChannelForbidden
  ) {
    return undefined;
  }
  if (entity instanceof Api.Chat && (entity.migratedTo || entity.deactivated)) return undefined;

  const id = utils.getPeerId(entity);
  const kind = entityKind(entity);
  const mute = muteInfo(dialog?.notifySettings, scopeNotify);
  const chat: Chat = {
    id,
    title: kind === "self" ? "Saved Messages" : (entityName(entity) ?? "Unknown"),
    kind,
    unread: dialog?.unreadCount ?? 0, // server-authoritative
    pinned: !!dialog?.pinned,
    muted: mute.muted,
    timestamp: last?.timestamp ?? 0,
  };
  if (dialog?.unreadMark) chat.markedUnread = true;
  if (mute.mutedUntil !== undefined) chat.mutedUntil = mute.mutedUntil;
  if (last) chat.lastMessage = last;
  // Folder rules ("contacts" / "non-contacts") and the archive. Both stay absent when we have no source for them.
  if (entity instanceof Api.User) chat.isContact = !!entity.contact;
  if (dialog) chat.archived = dialog.folderId === 1; // folder 1 = archive

  if (entity instanceof Api.Chat) chat.memberCount = entity.participantsCount;
  else if (entity instanceof Api.Channel && entity.participantsCount !== undefined) {
    chat.memberCount = entity.participantsCount;
  }
  if (entity instanceof Api.User && !entity.bot && !entity.self) {
    const p = userPresence(entity.status);
    if (p) {
      chat.online = p.online;
      if (p.lastSeen !== undefined) chat.lastSeen = p.lastSeen;
    }
  }
  const avatar = env.avatar?.(id);
  if (avatar) chat.avatar = avatar;
  return chat;
}

// ---------------------------------------------------------------- folders (dialog filters)

/**
 * Chat id (same marked-id format as `Chat.id`: users +id, basic groups -id, channels -100id) for an InputPeer stored
 * in a dialog filter. `InputPeerSelf` needs the account's own id; `InputPeerEmpty` and unknown shapes give undefined.
 */
export function inputPeerKey(p: Api.TypeInputPeer, selfId?: string): string | undefined {
  switch ((p as { className?: string } | null)?.className) {
    case "InputPeerSelf":
      return selfId || undefined;
    case "InputPeerUser":
    case "InputPeerUserFromMessage":
      return peerKey(new Api.PeerUser({ userId: (p as Api.InputPeerUser).userId }));
    case "InputPeerChat":
      return peerKey(new Api.PeerChat({ chatId: (p as Api.InputPeerChat).chatId }));
    case "InputPeerChannel":
    case "InputPeerChannelFromMessage":
      return peerKey(new Api.PeerChannel({ channelId: (p as Api.InputPeerChannel).channelId }));
    default:
      return undefined;
  }
}

/** Older layers send the folder title as a plain string, newer ones as TextWithEntities. */
function filterTitle(t: unknown): string {
  if (typeof t === "string") return t;
  const text = (t as { text?: unknown } | null)?.text;
  return typeof text === "string" ? text : "";
}

/**
 * DialogFilter / DialogFilterChatlist -> Folder. DialogFilterDefault (the "All chats" slot) has no content and
 * yields undefined. Folder.id is String(filter.id); every peer list becomes chat ids in the Chat.id format.
 * Flag mapping: broadcasts -> channels; chatlists (shared folders) have no flags and no excluded peers.
 */
export function mapFolder(f: Api.TypeDialogFilter, selfId?: string): Folder | undefined {
  const cn = (f as { className?: string } | null)?.className;
  if (cn !== "DialogFilter" && cn !== "DialogFilterChatlist") return undefined;
  const filter = f as Api.DialogFilter | Api.DialogFilterChatlist;

  const ids = (peers: Api.TypeInputPeer[] | undefined): string[] => {
    const out: string[] = [];
    for (const p of peers ?? []) {
      let k: string | undefined;
      try {
        k = inputPeerKey(p, selfId);
      } catch {
        k = undefined;
      }
      if (k && !out.includes(k)) out.push(k);
    }
    return out;
  };

  const folder: Folder = {
    id: String(filter.id),
    title: filterTitle(filter.title).trim() || "Folder",
    includeChatIds: ids(filter.includePeers),
    excludeChatIds: cn === "DialogFilter" ? ids((filter as Api.DialogFilter).excludePeers) : [],
    pinnedChatIds: ids(filter.pinnedPeers),
  };
  if (filter.emoticon) folder.emoticon = filter.emoticon;

  if (cn === "DialogFilter") {
    const d = filter as Api.DialogFilter;
    if (d.contacts) folder.contacts = true;
    if (d.nonContacts) folder.nonContacts = true;
    if (d.groups) folder.groups = true;
    if (d.broadcasts) folder.channels = true;
    if (d.bots) folder.bots = true;
    if (d.excludeMuted) folder.excludeMuted = true;
    if (d.excludeRead) folder.excludeRead = true;
    if (d.excludeArchived) folder.excludeArchived = true;
  }
  return folder;
}

/**
 * messages.getDialogFilters result -> Folder[] in server order. Accepts the older plain array and the newer
 * `messages.DialogFilters { filters }` wrapper; unknown shapes give [].
 */
export function mapFolders(r: unknown, selfId?: string): Folder[] {
  const list = Array.isArray(r) ? r : (r as { filters?: unknown } | null)?.filters;
  if (!Array.isArray(list)) return [];
  const out: Folder[] = [];
  const seen = new Set<string>();
  for (const f of list) {
    const m = f ? mapFolder(f as Api.TypeDialogFilter, selfId) : undefined;
    if (m && !seen.has(m.id)) {
      seen.add(m.id);
      out.push(m);
    }
  }
  return out;
}

// ---------------------------------------------------------------- albums

/**
 * Items of an album are separate Telegram messages with the same `date` (whole seconds) and consecutive ids, and the
 * app orders messages by timestamp alone. Within one album, `AlbumClock` spreads equal timestamps by 1 ms in id order
 * so the items keep their sequence whatever order they are mapped in (history pages ascending, search results
 * newest first, live items one by one) and whichever of them is seen first. A message seen again (edit, reaction,
 * re-list) gets the timestamp it was given the first time, so the app never sees an item move.
 */
export class AlbumClock {
  private readonly albums = new Map<string, { id: number; ts: number }[]>();

  constructor(private readonly maxAlbums = 2000) {}

  stamp<T extends Pick<Message, "id" | "chatId" | "groupId" | "timestamp">>(m: T): T {
    if (!m.groupId) return m;
    const key = `${m.chatId}:${m.groupId}`;
    const id = Number(m.id);
    let items = this.albums.get(key);
    if (items) {
      this.albums.delete(key); // refresh recency
    } else {
      items = [];
    }
    this.albums.set(key, items);
    if (this.albums.size > this.maxAlbums) this.albums.delete(this.albums.keys().next().value as string);

    const seen = items.find((e) => e.id === id);
    if (seen) {
      m.timestamp = seen.ts;
      return m;
    }
    let prev: { id: number; ts: number } | undefined;
    let next: { id: number; ts: number } | undefined;
    for (const e of items) {
      if (e.id < id && (!prev || e.id > prev.id)) prev = e;
      if (e.id > id && (!next || e.id < next.id)) next = e;
    }
    let ts = m.timestamp;
    if (next && ts >= next.ts) ts = next.ts - 1;
    if (prev && ts <= prev.ts) ts = prev.ts + 1;
    m.timestamp = ts;
    if (items.length < 30) items.push({ id, ts });
    return m;
  }
}

// ---------------------------------------------------------------- mentions (sending)

/**
 * Mention spans of messages.send / messages.edit -> `InputMessageEntityMentionName` entities. The visible text stays
 * "@Name" (the app writes it); the entity only tells Telegram who it is, so other clients show the name as a link and
 * the person is notified. `inputUser` resolves a user id to an InputUser WITH its access hash; a user that cannot be
 * resolved is skipped (the text is still sent, just without that highlight). Offsets/lengths are UTF-16 code units,
 * like Telegram's, so spans are passed on unchanged once `sanitizeMentions` has checked them against `text`.
 */
export async function buildMentionEntities(
  text: string,
  raw: unknown,
  inputUser: (userId: string) => Promise<Api.TypeInputUser | undefined>,
): Promise<Api.TypeMessageEntity[] | undefined> {
  const entities: Api.TypeMessageEntity[] = [];
  for (const m of sanitizeMentions(text, raw)) {
    if (!/^\d+$/.test(m.userId)) continue;
    const userId = await inputUser(m.userId);
    if (!userId || userId instanceof Api.InputUserEmpty) continue;
    entities.push(new Api.InputMessageEntityMentionName({ offset: m.offset, length: m.length, userId }));
  }
  return entities.length ? entities : undefined;
}

/** Telegram refuses a send/edit whose mention entity it cannot accept; the text is then sent without entities. */
export const MENTION_REJECTED = new Set([
  "USER_ID_INVALID",
  "ENTITY_MENTION_USER_INVALID",
  "ENTITY_BOUNDS_INVALID",
  "INPUT_USER_DEACTIVATED",
]);

// ---------------------------------------------------------------- search

/**
 * Server-side filters for the requested kinds (messages.search / searchGlobal take one filter per request, so the
 * result can be several). image+video together is Telegram's PhotoVideo. A kind with no server filter (text, sticker,
 * or none given) means "everything", narrowed afterwards by the caller. Duplicates never occur.
 */
export function searchFilters(kinds?: readonly string[]): Api.TypeMessagesFilter[] {
  const k = new Set(kinds ?? []);
  const out: Api.TypeMessagesFilter[] = [];
  if (k.size === 0) return [new Api.InputMessagesFilterEmpty()];
  if (k.has("image") && k.has("video")) {
    out.push(new Api.InputMessagesFilterPhotoVideo());
    k.delete("image");
    k.delete("video");
  }
  for (const kind of k) {
    switch (kind) {
      case "image":
        out.push(new Api.InputMessagesFilterPhotos());
        break;
      case "video":
        out.push(new Api.InputMessagesFilterVideo());
        break;
      case "file":
        out.push(new Api.InputMessagesFilterDocument());
        break;
      case "link":
        out.push(new Api.InputMessagesFilterUrl());
        break;
      case "voice":
        out.push(new Api.InputMessagesFilterVoice());
        break;
      case "audio":
        out.push(new Api.InputMessagesFilterMusic());
        break;
      case "gif":
        out.push(new Api.InputMessagesFilterGif());
        break;
      case "location":
        out.push(new Api.InputMessagesFilterGeo());
        break;
      case "contact":
        out.push(new Api.InputMessagesFilterContacts());
        break;
      default:
        return [new Api.InputMessagesFilterEmpty()]; // text, sticker, …: no server filter
    }
  }
  return out;
}
