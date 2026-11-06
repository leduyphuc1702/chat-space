import { mkdirSync } from "node:fs";
import { createInterface } from "node:readline";
import type { Adapter, AdapterContext } from "./adapter.ts";
import { parseSearch } from "./search.ts";
import { BridgeError, type EventName } from "./types.ts";

const VERSION = "0.1.0";

function arg(name: string): string | undefined {
  const i = process.argv.indexOf(`--${name}`);
  return i >= 0 ? process.argv[i + 1] : undefined;
}

const platform = arg("platform");
const dataDir = arg("data-dir");
if (!platform || !dataDir) {
  process.stderr.write("usage: main.ts --platform zalo|whatsapp|telegram --data-dir DIR\n");
  process.exit(2);
}
mkdirSync(dataDir, { recursive: true });

// Libraries love console.log; stdout is reserved for protocol frames.
const toStderr = (...a: unknown[]) =>
  process.stderr.write(a.map((x) => (typeof x === "string" ? x : safeJson(x))).join(" ") + "\n");
console.log = toStderr;
console.info = toStderr;
console.debug = toStderr;
console.warn = toStderr;

function safeJson(v: unknown) {
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}

// A string cut mid-emoji keeps a lone surrogate; JSON.stringify writes it as a `\ud83d` escape, which the
// app's decoder rejects together with the whole frame.
const wellFormed = (_key: string, v: unknown) => (typeof v === "string" ? v.toWellFormed() : v);

function write(frame: unknown) {
  process.stdout.write(JSON.stringify(frame, wellFormed) + "\n");
}

const ctx: AdapterContext = {
  dataDir,
  emit: (event: EventName, data: unknown) => write({ event, data }),
  log: toStderr,
};

async function loadAdapter(): Promise<Adapter> {
  switch (platform) {
    case "zalo":
      return new (await import("./zalo.ts")).ZaloAdapter(ctx);
    case "whatsapp":
      return new (await import("./whatsapp.ts")).WhatsAppAdapter(ctx);
    case "telegram":
      return new (await import("./telegram.ts")).TelegramAdapter(ctx);
    default:
      throw new Error(`unknown platform ${platform}`);
  }
}

const adapter = await loadAdapter();
let started = false;

type Params = Record<string, any>;
const handlers: Record<string, (p: Params) => Promise<unknown>> = {
  "bridge.hello": async (p) => {
    if (!started) {
      started = true;
      // Session restore can take a while; don't block the handshake on it.
      adapter.start(p.config ?? {}).catch((e) => {
        ctx.log("start failed", e);
        ctx.emit("auth.state", { state: "error", error: String(e?.message ?? e) });
      });
    }
    return { platform, version: VERSION, capabilities: adapter.capabilities };
  },
  "auth.status": async () => adapter.status(),
  "auth.login": async (p) => {
    await adapter.login(p);
    return { state: adapter.status().state };
  },
  "auth.submit": async (p) => {
    await adapter.submit(p);
    return { state: adapter.status().state };
  },
  "auth.logout": async () => {
    await adapter.logout();
    return {};
  },
  "chats.list": async (p) => ({ chats: await adapter.listChats(p.limit ?? 100) }),
  "chats.avatar": async (p) => ({ avatar: await adapter.chatAvatar(need(p, "chatId")) }),
  "chats.members": async (p) => ({ members: await adapter.chatMembers(need(p, "chatId")) }),
  "chats.direct": async (p) => ({ chat: await adapter.directChat(need(p, "userId")) }),
  "messages.list": async (p) => ({
    messages: await adapter.listMessages(need(p, "chatId"), p.limit ?? 50, p.before),
  }),
  "messages.search": async (p) => adapter.searchPage(parseSearch(p)),
  "messages.send": async (p) => {
    need(p, "chatId");
    if (!p.text && !p.filePath) throw new BridgeError("invalid_params", "text or filePath required");
    return { message: await adapter.sendMessage(p as any) };
  },
  "messages.forward": async (p) => ({
    message: await adapter.forwardMessage(need(p, "fromChatId"), need(p, "messageId"), need(p, "toChatId")),
  }),
  "messages.markRead": async (p) => {
    await adapter.markRead(need(p, "chatId"));
    return {};
  },
  "chats.mute": async (p) => {
    const until = Number(p.until);
    if (!Number.isFinite(until) || (until < 0 && until !== -1)) {
      throw new BridgeError("invalid_params", "until must be a ms timestamp, -1 (forever) or 0 (unmute)");
    }
    return { chat: await adapter.muteChat(need(p, "chatId"), until) };
  },
  "chats.pins": async (p) => ({ messages: await adapter.pinnedMessages(need(p, "chatId")) }),
  "messages.react": async (p) => {
    await adapter.react(need(p, "chatId"), need(p, "messageId"), p.emoji ?? "");
    return {};
  },
  "messages.delete": async (p) => {
    await adapter.deleteMessage(need(p, "chatId"), need(p, "messageId"), !!p.forEveryone);
    return {};
  },
  "messages.edit": async (p) => {
    const text = typeof p.text === "string" ? p.text : "";
    if (!text.trim()) throw new BridgeError("invalid_params", "text is required");
    return { message: await adapter.editMessage(need(p, "chatId"), need(p, "messageId"), text, p.mentions) };
  },
  "media.download": async (p) => ({
    path: await adapter.downloadMedia(need(p, "chatId"), need(p, "messageId")),
  }),
  "media.preview": async (p) => ({
    path: await adapter.previewMedia(need(p, "chatId"), need(p, "messageId")),
  }),
  "typing.send": async (p) => {
    await adapter.sendTyping(need(p, "chatId"), !!p.typing);
    return {};
  },
  "messages.clickButton": async (p) => {
    const row = Number(p.row);
    const column = Number(p.column);
    if (!Number.isInteger(row) || row < 0 || !Number.isInteger(column) || column < 0) {
      throw new BridgeError("invalid_params", "row and column must be non-negative integers");
    }
    return adapter.clickButton(need(p, "chatId"), need(p, "messageId"), row, column);
  },
  "connection.resume": async () => {
    await adapter.resume();
    return {};
  },
  "chats.folders": async () => ({ folders: await adapter.listFolders() }),
  "storage.configure": async (p) => {
    await adapter.configureStorage({
      keepMediaDays: Number(p.keepMediaDays ?? 0),
      maxCacheMB: Number(p.maxCacheMB ?? 0),
      compressImages: p.compressImages !== false,
      maxStoredMessagesPerChat: Number(p.maxStoredMessagesPerChat ?? 0),
    });
    return {};
  },
  "history.phoneSync": async (p) => {
    const action = p.action ?? "status";
    if (action !== "start" && action !== "cancel" && action !== "status") {
      throw new BridgeError("invalid_params", `unknown action "${action}"`);
    }
    return adapter.phoneSync(action);
  },
  "storage.usage": async () => adapter.storageUsage(),
  "storage.clear": async (p) => ({
    freedBytes: await adapter.clearStorage(p.olderThanDays === undefined ? undefined : Number(p.olderThanDays)),
  }),
};

function need(p: Params, key: string): string {
  const v = p?.[key];
  if (typeof v !== "string" || !v) throw new BridgeError("invalid_params", `${key} is required`);
  return v;
}

const rl = createInterface({ input: process.stdin });
rl.on("line", async (line) => {
  if (!line.trim()) return;
  let req: { id?: string; method?: string; params?: Params };
  try {
    req = JSON.parse(line);
  } catch {
    ctx.log("bad frame", line);
    return;
  }
  const { id, method, params } = req;
  const handler = method ? handlers[method] : undefined;
  if (!handler) {
    write({ id, error: { code: "unsupported", message: `unknown method ${method}` } });
    return;
  }
  try {
    write({ id, result: (await handler(params ?? {})) ?? {} });
  } catch (e: any) {
    const code = e instanceof BridgeError ? e.code : "internal";
    if (code === "internal") ctx.log(`${method} failed`, e?.stack ?? e);
    write({ id, error: { code, message: String(e?.message ?? e) } });
  }
});

// The app owns our lifetime: when it closes stdin, we exit.
rl.on("close", async () => {
  await adapter.shutdown().catch(() => {});
  process.exit(0);
});

process.on("unhandledRejection", (e) => ctx.log("unhandledRejection", (e as any)?.stack ?? e));
process.on("uncaughtException", (e) => ctx.log("uncaughtException", e?.stack ?? e));
