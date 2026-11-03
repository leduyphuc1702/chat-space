// Pure mapping helpers: GramJS TL objects -> ChatSpace protocol objects.
// No I/O and no state here; everything environment-specific comes in through MapEnv.
import { Api, utils } from "telegram";
import type { Chat, ChatKind, Folder, Media, Message, MessageKind, Reaction } from "./types.ts";

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
    const q = media.poll.question as unknown;
    const question = typeof q === "string" ? q : ((q as { text?: string })?.text ?? "");
    const answers = media.poll.answers.map((a) => {
      const t = (a as { text?: unknown }).text;
      return `• ${typeof t === "string" ? t : ((t as { text?: string })?.text ?? "")}`;
    });
    return { kind: "unsupported", text: [`Poll: ${question}`, ...answers].join("\n") };
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
            : media instanceof Api.MessageMediaPaidMedia
              ? "Paid media"
              : "Unsupported message";
  return { kind: "unsupported", text: [label, caption].filter(Boolean).join("\n") };
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
  if (a instanceof Api.MessageActionTopicEdit) return `${actor} edited a topic`;
  if (a instanceof Api.MessageActionSuggestProfilePhoto) return `${actor} suggested a profile photo`;
  if (a instanceof Api.MessageActionGiftPremium) {
    return `${actor} gifted Telegram Premium for ${plural(a.months, "month")}`;
  }
  return `${actor}: ${humanizeClass(a.className)}`;
}

// ---------------------------------------------------------------- replies

/** Id of the same-chat message this one replies to (forum-topic roots and cross-chat replies excluded). */
/** The service line "X pinned a message". */
export function isPinNotice(raw: Api.TypeMessage): boolean {
  return raw instanceof Api.MessageService && raw.action instanceof Api.MessageActionPinMessage;
}

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

export function previewText(m: Pick<Message, "text" | "kind">): string {
  const t = (m.text ?? "").trim();
  return (t || KIND_LABEL[m.kind]).slice(0, 200);
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
