import type { AuthState, ButtonAnswer, Chat, EventName, Folder, Member, MentionSpan, Message, SelfInfo, StorageConfig } from "./types.ts";
import { BridgeError } from "./types.ts";
import type { SearchParams } from "./search.ts";

export type Emit = (event: EventName, data: unknown) => void;

export interface AdapterContext {
  dataDir: string;
  emit: Emit;
  log: (...args: unknown[]) => void;
}

/**
 * One instance per sidecar process. Methods map 1:1 onto docs/PROTOCOL.md.
 * Adapters must never write to stdout; use ctx.log (stderr).
 */
export abstract class Adapter {
  abstract readonly platform: string;
  abstract readonly capabilities: string[];

  protected state: AuthState = "logged_out";
  protected self?: SelfInfo;

  constructor(protected ctx: AdapterContext) {}

  /** Called once after bridge.hello. Restore a saved session if there is one. */
  abstract start(config: Record<string, unknown>): Promise<void>;

  abstract login(params: { method?: string; cookies?: string; phone?: string }): Promise<void>;
  async submit(_params: { password?: string; code?: string }): Promise<void> {
    throw new BridgeError("unsupported", "auth.submit is not used by this platform");
  }
  abstract logout(): Promise<void>;

  abstract listChats(limit: number): Promise<Chat[]>;
  async chatAvatar(_chatId: string): Promise<string | undefined> {
    return undefined;
  }
  abstract listMessages(chatId: string, limit: number, before?: string): Promise<Message[]>;
  abstract sendMessage(params: {
    chatId: string;
    text?: string;
    replyTo?: string;
    filePath?: string;
    /** Only with capability "mentions"; adapters must run it through sanitizeMentions() before use. */
    mentions?: MentionSpan[];
  }): Promise<Message>;
  /** messages.search (capability "search"): newest first. */
  async searchMessages(_p: SearchParams): Promise<Message[]> {
    throw new BridgeError("unsupported", "search is not supported");
  }
  /** Platform-native forward of one message (capability "forward"). */
  async forwardMessage(_fromChatId: string, _messageId: string, _toChatId: string): Promise<Message> {
    throw new BridgeError("unsupported", "forward is not supported");
  }
  abstract markRead(chatId: string): Promise<void>;
  /**
   * chats.mute (capability "mute"): `until` is the ms epoch the mute ends, -1 = forever, 0 = unmute.
   * Returns the chat with its new mute state (also emitted as chat.upsert).
   */
  async muteChat(_chatId: string, _until: number): Promise<Chat> {
    throw new BridgeError("unsupported", "mute is not supported");
  }
  /** chats.pins (capability "pins"): the chat's pinned messages, most recently pinned first. */
  async pinnedMessages(_chatId: string): Promise<Message[]> {
    throw new BridgeError("unsupported", "pinned messages are not supported");
  }
  /** Members of a group chat (the other participant of a 1:1 chat). Adapters with capability "mentions" override this. */
  async chatMembers(_chatId: string): Promise<Member[]> {
    throw new BridgeError("unsupported", "chats.members is not supported");
  }
  /** chats.direct (capability "direct_chat"): the 1:1 chat with a user, possibly empty. Must send nothing. */
  async directChat(_userId: string): Promise<Chat> {
    throw new BridgeError("unsupported", "direct chats are not supported");
  }
  async react(_chatId: string, _messageId: string, _emoji: string): Promise<void> {
    throw new BridgeError("unsupported", "reactions are not supported");
  }
  async deleteMessage(_chatId: string, _messageId: string, _forEveryone: boolean): Promise<void> {
    throw new BridgeError("unsupported", "delete is not supported");
  }
  /** New text of one of our messages (capability "edit"); returns the edited message. */
  async editMessage(_chatId: string, _messageId: string, _text: string, _mentions?: MentionSpan[]): Promise<Message> {
    throw new BridgeError("unsupported", "edit is not supported");
  }
  async downloadMedia(_chatId: string, _messageId: string): Promise<string> {
    throw new BridgeError("unsupported", "media download is not supported");
  }
  async sendTyping(_chatId: string, _typing: boolean): Promise<void> {}
  /**
   * messages.clickButton (capability "bot_buttons"): press a bot button the account itself must answer (callback,
   * game, web app). Other kinds never reach the bridge.
   */
  async clickButton(_chatId: string, _messageId: string, _row: number, _column: number): Promise<ButtonAnswer> {
    throw new BridgeError("unsupported", "bot buttons are not supported");
  }
  /**
   * connection.resume: the computer woke up or its network changed. Adapters check that their socket is still
   * alive, reconnect at once instead of at the next keep-alive, and fetch whatever the platform queued meanwhile.
   */
  async resume(): Promise<void> {}
  /** History transfer from the phone (capability "phone_sync"): start, cancel or just report progress. */
  async phoneSync(_action: "start" | "cancel" | "status"): Promise<unknown> {
    throw new BridgeError("unsupported", "phone sync is not supported");
  }

  /** Chat folders; platforms without folders return []. */
  async listFolders(): Promise<Folder[]> {
    return [];
  }

  /** Storage policy from the app; may arrive again at any time (Settings change). */
  async configureStorage(_config: StorageConfig): Promise<void> {}
  async storageUsage(): Promise<{ mediaBytes: number; mediaFiles: number; databaseBytes: number }> {
    return { mediaBytes: 0, mediaFiles: 0, databaseBytes: 0 };
  }
  /** Delete cached media (all, or only files older than `olderThanDays`). Returns bytes freed. */
  async clearStorage(_olderThanDays?: number): Promise<number> {
    return 0;
  }

  async shutdown(): Promise<void> {}

  status() {
    return { state: this.state, self: this.self };
  }

  protected setState(state: AuthState, extra: { error?: string } = {}) {
    this.state = state;
    this.ctx.emit("auth.state", { state, self: this.self, ...extra });
  }

  protected requireLogin() {
    if (this.state !== "logged_in") throw new BridgeError("not_logged_in", "Not logged in");
  }
}
