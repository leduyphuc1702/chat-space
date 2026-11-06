// Mirrors docs/PROTOCOL.md. Keep the two in sync.

export type AuthState =
  | "logged_out"
  | "connecting"
  | "awaiting_qr"
  | "awaiting_password"
  | "awaiting_code"
  | "awaiting_input"
  | "logged_in"
  | "error";

export interface SelfInfo {
  id: string;
  name: string;
  avatar?: string;
  phone?: string;
}

export type ChatKind = "direct" | "group" | "channel" | "bot" | "self";

export interface Chat {
  id: string;
  title: string;
  avatar?: string;
  kind: ChatKind;
  /** Server-authoritative unread count; must drop to 0 (via chat.upsert) when read on another device. */
  unread: number;
  /** Manually "marked as unread" (dot/badge shown with unread 0). */
  markedUnread?: boolean;
  pinned?: boolean;
  /** Notifications currently muted. */
  muted?: boolean;
  /** ms epoch the mute ends; -1 = forever; absent = not muted. */
  mutedUntil?: number;
  timestamp: number;
  lastMessage?: Message;
  memberCount?: number;
  online?: boolean;
  lastSeen?: number;
  /** Peer is in the user's contact list (used by folder rules). */
  isContact?: boolean;
  /** Chat lives in the archive (Telegram folder 1, WhatsApp archived). */
  archived?: boolean;
}

/** A user chat folder (Telegram dialog filter). The app evaluates membership itself. */
export interface Folder {
  id: string;
  title: string;
  /** Telegram folder emoticon (e.g. "🤖", "📢", "⭐", "💬"); the app maps it to an icon. */
  emoticon?: string;
  includeChatIds: string[];
  excludeChatIds: string[];
  pinnedChatIds: string[];
  contacts?: boolean;
  nonContacts?: boolean;
  groups?: boolean;
  channels?: boolean;
  bots?: boolean;
  excludeMuted?: boolean;
  excludeRead?: boolean;
  excludeArchived?: boolean;
}

/** Local storage policy pushed by the app (Settings → Data and Storage). */
export interface StorageConfig {
  /** Delete cached media not accessed for this many days; 0 = forever. */
  keepMediaDays: number;
  /** Cap for the media cache in MB; 0 = unlimited. Oldest-accessed files go first. */
  maxCacheMB: number;
  /** Re-encode downloaded photos to save space. */
  compressImages: boolean;
  /** Keep at most this many messages per chat on disk; older ones are refetched from the server. 0 = unlimited. */
  maxStoredMessagesPerChat: number;
}

export type MessageKind =
  | "text"
  | "image"
  | "video"
  | "audio"
  | "voice"
  | "file"
  | "sticker"
  | "gif"
  | "location"
  | "contact"
  | "link"
  | "call"
  | "system"
  | "unsupported";

export interface Media {
  mime?: string;
  url?: string;
  path?: string;
  thumbnail?: string;
  width?: number;
  height?: number;
  duration?: number;
  fileName?: string;
  size?: number;
}

export interface Reaction {
  emoji: string;
  count: number;
  fromMe: boolean;
}

/** One entry of chats.members (capability "mentions"). `id` is what messages.send expects in `mentions[].userId`. */
export interface Member {
  id: string;
  name: string;
  avatar?: string;
}

/** An @mention inside `text` of messages.send. offset/length are UTF-16 code units and cover the visible "@Name". */
export interface MentionSpan {
  userId: string;
  offset: number;
  length: number;
}

/**
 * What pressing a bot button does (capability "bot_buttons"). `callback`, `game` and `web_app` go through
 * messages.clickButton; `url`, `login` and `user_profile` open `url`; `text` sends the label as a message;
 * `switch_inline` puts `insert` into the composer; `copy` copies `copyText`; `unsupported` does nothing.
 */
export type ButtonKind =
  | "callback"
  | "url"
  | "login"
  | "switch_inline"
  | "web_app"
  | "game"
  | "text"
  | "copy"
  | "user_profile"
  | "unsupported";

export interface Button {
  text: string;
  kind: ButtonKind;
  /** url / login / user_profile / web_app: what to open (web_app: the bot's page, before the bridge adds auth data). */
  url?: string;
  /** switch_inline: "@bot query" ready to paste into the composer. */
  insert?: string;
  /** switch_inline: the bot asked for the query to be sent in this same chat. */
  samePeer?: boolean;
  /** copy: the text to put on the clipboard. */
  copyText?: string;
}

/** A bot's custom keyboard shown in place of the composer's own (ReplyKeyboardMarkup). */
export interface ReplyKeyboard {
  rows: Button[][];
  /** Fit the rows to their content instead of the usual keyboard height. */
  resize?: boolean;
  /** Hide after one button was pressed (the app may offer to show it again). */
  oneTime?: boolean;
  /** Keep it open even when the user hides it. */
  persistent?: boolean;
  /** Composer placeholder while the keyboard is active. */
  placeholder?: string;
  /** Meant only for the users the bot mentioned / replied to. */
  selective?: boolean;
}

export interface Message {
  id: string;
  chatId: string;
  senderId: string;
  senderName?: string;
  senderAvatar?: string;
  fromMe: boolean;
  timestamp: number;
  kind: MessageKind;
  text?: string;
  media?: Media;
  replyTo?: { id: string; text?: string; senderName?: string; kind?: MessageKind };
  forwarded?: boolean;
  edited?: boolean;
  deleted?: boolean;
  reactions?: Reaction[];
  status?: "pending" | "sent" | "delivered" | "read" | "failed";
  /** Inline keyboard under the bubble (bots), rows of buttons. */
  buttons?: Button[][];
  /** This message installs a bot keyboard for the chat. */
  replyKeyboard?: ReplyKeyboard;
  /** This message removes the chat's bot keyboard (ReplyKeyboardHide). */
  hideKeyboard?: boolean;
  /** The bot asks for a reply to this message (ReplyKeyboardForceReply); `placeholder` goes in the composer. */
  forceReply?: { placeholder?: string; oneTime?: boolean };
  /** Album: messages sent together share it and are drawn as one bubble. */
  groupId?: string;
  /** Text links whose URL is not part of `text` (UTF-16 offsets into `text`). */
  links?: { offset: number; length: number; url: string }[];
}

/** Result of messages.clickButton. */
export interface ButtonAnswer {
  /** What the bot answered (toast, or an alert when `alert` is set). */
  message?: string;
  alert?: boolean;
  /** A page to open (games, web apps, callback answers with a URL). */
  url?: string;
}

export type EventName =
  | "auth.state"
  | "auth.qr"
  | "connection"
  | "chat.upsert"
  | "folders.update"
  | "message.new"
  | "message.update"
  | "message.delete"
  | "typing"
  | "presence"
  | "history.phoneSync"
  | "pins.update"
  | "log";

/** Messages older than this are never "live" (replays, catch-up after a reconnect, backfill). */
export const LIVE_MAX_AGE_MS = 2 * 60_000;

/** Real-time arrival check shared by all bridges: `timestamp` is the message time in ms. */
export function isFresh(timestampMs: number, now = Date.now()): boolean {
  return timestampMs > 0 && now - timestampMs <= LIVE_MAX_AGE_MS;
}

export class BridgeError extends Error {
  constructor(
    public code: "not_logged_in" | "not_found" | "invalid_params" | "unsupported" | "internal",
    message: string,
  ) {
    super(message);
  }
}
