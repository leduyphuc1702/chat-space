// Adapter wiring for chats.mute / chats.pins with a fake socket and a scratch data dir (no network, no real account).
import { afterEach, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { WhatsAppAdapter } from "./whatsapp.ts";

const CHAT = "120363@g.us";
const ME = "84900@s.whatsapp.net";
const LINH = "84901@s.whatsapp.net";
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

const dirs: string[] = [];
afterEach(() => {
  for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true });
});

function setup(dir = mkdtempSync(join(tmpdir(), "wa-adapter-"))) {
  if (!dirs.includes(dir)) dirs.push(dir);
  const events: { event: string; data: any }[] = [];
  const modified: { mod: any; jid: string }[] = [];
  const a = new WhatsAppAdapter({ dataDir: dir, emit: (event, data) => events.push({ event, data }), log: () => {} });
  const x = a as any;
  x.store.load();
  x.store.accountId = ME;
  x.state = "logged_in";
  x.open = true;
  x.sock = { chatModify: async (mod: any, jid: string) => void modified.push({ mod, jid }) };
  x.store.ensureChat(CHAT, Date.now());
  const of = (name: string) => events.filter((e) => e.event === name).map((e) => e.data);
  return { a, x, dir, events, modified, of };
}

const text = (id: string, ts: number) => ({
  key: { remoteJid: CHAT, fromMe: false, id, participant: LINH },
  messageTimestamp: Math.floor(ts / 1000),
  message: { conversation: `text ${id}` },
});

const pinMsg = (id: string, target: string, type: 1 | 2, at: number, secs = 86400, fromMe = false) => ({
  key: { remoteJid: CHAT, fromMe, id, participant: fromMe ? undefined : LINH },
  messageTimestamp: Math.floor(at / 1000),
  message: {
    pinInChatMessage: { key: { remoteJid: CHAT, id: target, fromMe: false, participant: LINH }, type, senderTimestampMs: at },
    messageContextInfo: { messageAddOnDurationInSecs: secs },
  },
});

describe("capabilities", () => {
  test("mute and pins are declared", () => {
    const { a } = setup();
    expect(a.capabilities).toContain("mute");
    expect(a.capabilities).toContain("pins");
  });
});

describe("chats.mute", () => {
  test("forever goes out as -1 and comes back as mutedUntil -1; the chat is upserted", async () => {
    const { a, modified, of } = setup();
    const chat = await a.muteChat(CHAT, -1);
    expect(modified).toEqual([{ mod: { mute: -1 }, jid: CHAT }]);
    expect(chat).toMatchObject({ id: CHAT, muted: true, mutedUntil: -1 });
    expect(of("chat.upsert").at(-1).chat).toMatchObject({ id: CHAT, muted: true, mutedUntil: -1 });
  });

  test("until is sent as the absolute ms timestamp", async () => {
    const { a, modified } = setup();
    const until = Date.now() + 8 * 3600_000;
    expect(await a.muteChat(CHAT, until)).toMatchObject({ muted: true, mutedUntil: until });
    expect(modified[0]!.mod).toEqual({ mute: until });
  });

  test("0 unmutes", async () => {
    const { a, modified, of } = setup();
    await a.muteChat(CHAT, -1);
    const chat = await a.muteChat(CHAT, 0);
    expect(modified.at(-1)!.mod).toEqual({ mute: null });
    expect(chat.muted).toBeUndefined();
    expect(chat.mutedUntil).toBeUndefined();
    expect(of("chat.upsert").at(-1).chat.muted).toBeUndefined();
  });

  test("unknown chat, past time and offline are refused", async () => {
    const { a, x, modified } = setup();
    await expect(a.muteChat("nobody@g.us", -1)).rejects.toMatchObject({ code: "not_found" });
    await expect(a.muteChat(CHAT, Date.now() - 1000)).rejects.toMatchObject({ code: "invalid_params" });
    x.open = false;
    await expect(a.muteChat(CHAT, -1)).rejects.toMatchObject({ code: "internal" });
    expect(modified).toEqual([]);
  });

  test("a mute changed on the phone (app-state chats.update) still updates the chat", async () => {
    const { x, of } = setup();
    await x.onChats([{ id: CHAT, muteEndTime: -1 }], "delta");
    await sleep(300);
    expect(of("chat.upsert").at(-1).chat).toMatchObject({ muted: true, mutedUntil: -1 });
    const until = Date.now() + 3600_000;
    await x.onChats([{ id: CHAT, muteEndTime: until }], "delta");
    await sleep(300);
    expect(of("chat.upsert").at(-1).chat).toMatchObject({ muted: true, mutedUntil: until });
    await x.onChats([{ id: CHAT, muteEndTime: null }], "delta");
    await sleep(300);
    expect(of("chat.upsert").at(-1).chat.muted).toBeUndefined();
  });

  test("the echo Baileys emits for our own change leaves the same state", async () => {
    const { a, x } = setup();
    await a.muteChat(CHAT, -1);
    await x.onChats([{ id: CHAT, muteEndTime: -1 }], "delta");
    expect(x.store.getChat(CHAT).muteEnd).toBe(-1);
  });
});

describe("chats.pins", () => {
  test("a live pin: system line in the chat, pins.update, and the stored original comes back", async () => {
    const { a, x, of } = setup();
    const now = Date.now();
    x.store.setContact(LINH, { name: "Linh" });
    await x.onUpsert({ type: "notify", messages: [text("M1", now - 60_000)] });
    await x.onUpsert({ type: "notify", messages: [pinMsg("P1", "M1", 1, now - 1000)] });
    await sleep(400);
    expect(of("pins.update")).toEqual([{ chatId: CHAT }]);
    const sys = of("message.new").map((d) => d.message).find((m) => m.id === "P1");
    expect(sys).toMatchObject({ kind: "system", text: "Linh pinned a message" });
    expect(of("message.new").find((d) => d.message.id === "P1").live).toBe(false);
    const pins = await a.pinnedMessages(CHAT);
    expect(pins.map((m) => m.id)).toEqual(["M1"]);
    expect(pins[0]).toMatchObject({ kind: "text", text: "text M1", senderId: LINH });
    const listed = await a.listMessages(CHAT, 50);
    expect(listed.find((m) => m.id === "M1")).toEqual(pins[0]!);
  });

  test("a pinned message that is not stored is a minimal unsupported Message", async () => {
    const { a, x } = setup();
    const at = Date.now() - 1000;
    await x.onUpsert({ type: "notify", messages: [pinMsg("P1", "GONE", 1, at)] });
    expect(await a.pinnedMessages(CHAT)).toEqual([{ id: "GONE", chatId: CHAT, senderId: "", fromMe: false, timestamp: at, kind: "unsupported" }]);
  });

  test("unpin removes it, with pins.update, and leaves no message in the chat", async () => {
    const { a, x, of } = setup();
    const now = Date.now();
    await x.onUpsert({ type: "notify", messages: [text("M1", now - 60_000), pinMsg("P1", "M1", 1, now - 5000)] });
    await x.onUpsert({ type: "notify", messages: [pinMsg("U1", "M1", 2, now - 1000)] });
    await sleep(400);
    expect(await a.pinnedMessages(CHAT)).toEqual([]);
    expect(of("pins.update").length).toBeGreaterThanOrEqual(1);
    expect((await a.listMessages(CHAT, 50)).map((m) => m.id).sort()).toEqual(["M1", "P1"]);
  });

  test("newest pinned first, at most three, a deleted original is dropped", async () => {
    const { a, x } = setup();
    const now = Date.now();
    const msgs: any[] = [];
    for (const [i, id] of ["M1", "M2", "M3", "M4"].entries()) {
      msgs.push(text(id, now - 100_000 + i), pinMsg(`P${i}`, id, 1, now - 10_000 + i * 1000));
    }
    await x.onUpsert({ type: "notify", messages: msgs });
    expect((await a.pinnedMessages(CHAT)).map((m) => m.id)).toEqual(["M4", "M3", "M2"]);
    x.store.getMsg(CHAT, "M3").deleted = true;
    expect((await a.pinnedMessages(CHAT)).map((m) => m.id)).toEqual(["M4", "M2"]);
  });

  test("history sync (newest first) and offline messages are processed too", async () => {
    const { a, x } = setup();
    const now = Date.now();
    await x.onHistory({
      syncType: 1,
      chats: [],
      contacts: [],
      messages: [pinMsg("U1", "M1", 2, now - 1000), pinMsg("P1", "M1", 1, now - 5000), pinMsg("P2", "M2", 1, now - 4000)],
    });
    expect((await a.pinnedMessages(CHAT)).map((m) => m.id)).toEqual(["M2"]);
    await x.onUpsert({ type: "append", messages: [pinMsg("P3", "M3", 1, now - 3000)] });
    expect((await a.pinnedMessages(CHAT)).map((m) => m.id)).toEqual(["M3", "M2"]);
  });

  test("a pin made from this account (phone or another device) is recorded as well", async () => {
    const { a, x } = setup();
    await x.onUpsert({ type: "append", messages: [pinMsg("P1", "M1", 1, Date.now() - 1000, 86400, true)] });
    expect((await a.pinnedMessages(CHAT)).map((m) => m.id)).toEqual(["M1"]);
  });

  test("pins survive a restart", async () => {
    const first = setup();
    const now = Date.now();
    await first.x.onUpsert({ type: "notify", messages: [text("M1", now - 60_000), pinMsg("P1", "M1", 1, now - 1000, 7 * 86400)] });
    first.x.store.flushSync();
    const again = setup(first.dir);
    expect((await again.a.pinnedMessages(CHAT)).map((m) => m.id)).toEqual(["M1"]);
    expect((await again.a.pinnedMessages(CHAT))[0]).toMatchObject({ kind: "text", text: "text M1" });
  });

  test("a pin lapsing tells the app (pins.update) and disappears from chats.pins", async () => {
    const { a, x, of } = setup();
    await x.onUpsert({ type: "notify", messages: [pinMsg("P1", "M1", 1, Date.now() - 500, 1)] });
    await sleep(450);
    const before = of("pins.update").length;
    expect((await a.pinnedMessages(CHAT)).map((m) => m.id)).toEqual(["M1"]);
    await sleep(500);
    expect(of("pins.update").length).toBe(before + 1);
    expect(await a.pinnedMessages(CHAT)).toEqual([]);
    await a.shutdown();
  });
});
