// Adapter wiring for chats.mute / chats.pins with a fake socket and a scratch data dir (no network, no real account).
import { afterEach, describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { proto } from "baileys";
import { WhatsAppAdapter, oldestFirst } from "./whatsapp.ts";

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

// ---------------------------------------------------------------------------------------------------------------
// Albums, mentions, search by sender

const HOA_PN = "84902@s.whatsapp.net";
const HOA_LID = "68388898529359@lid";
const Assoc = proto.MessageAssociation.AssociationType;

const albumParent = (id: string, ts: number) => ({
  key: { remoteJid: CHAT, fromMe: false, id, participant: LINH },
  messageTimestamp: ts,
  message: { messageContextInfo: { messageSecret: new Uint8Array(32) }, albumMessage: { expectedImageCount: 2, expectedVideoCount: 0 } },
});
const photo = (id: string, parent: string, ts: number, opts: { type?: number; caption?: string; from?: string } = {}) => ({
  key: { remoteJid: CHAT, fromMe: false, id, participant: opts.from ?? LINH },
  messageTimestamp: ts,
  message: {
    messageContextInfo: { messageAssociation: { associationType: opts.type ?? Assoc.MEDIA_ALBUM, parentMessageKey: { remoteJid: CHAT, fromMe: false, id: parent, participant: LINH } } },
    imageMessage: { mimetype: "image/jpeg", width: 10, height: 10, caption: opts.caption },
  },
});

describe("WhatsApp albums", () => {
  const now = Math.floor(Date.now() / 1000);
  const set = [albumParent("AL1", now - 10), photo("P1", "AL1", now - 9, { caption: "first" }), photo("P2", "AL1", now - 9), photo("HD1", "P1", now - 8, { type: Assoc.HD_IMAGE_DUAL_UPLOAD, caption: "first" })];

  test("live: the photos share the album's id as groupId, the parent and the HD copy are no messages", async () => {
    const { a, x, of } = setup();
    await x.onUpsert({ type: "notify", messages: set });
    const news = of("message.new").map((d) => d.message);
    expect(news.map((m) => [m.id, m.groupId, m.text])).toEqual([
      ["P1", "AL1", "first"],
      ["P2", "AL1", undefined],
    ]);
    expect((await a.listMessages(CHAT, 50)).map((m) => m.id)).toEqual(["P1", "P2"]);
    expect(x.store.getMsg(CHAT, "AL1")).toBeUndefined(); // nothing to keep of the parent or of the HD copy
    expect(x.store.getMsg(CHAT, "HD1")).toBeUndefined();
  });

  test("history sync (newest first) and the reloaded store give the same grouping", async () => {
    const first = setup();
    await first.x.onHistory({ syncType: 1, chats: [], contacts: [], messages: [...set].reverse() });
    const grouped = async (a: WhatsAppAdapter) => (await a.listMessages(CHAT, 50)).map((m) => [m.id, m.kind, m.groupId]);
    expect(await grouped(first.a)).toEqual([
      ["P1", "image", "AL1"],
      ["P2", "image", "AL1"],
    ]);
    first.x.store.flushSync();
    const again = setup(first.dir);
    expect(await grouped(again.a)).toEqual(await grouped(first.a));
  });

  test("albums stored by an earlier version (parent and HD copy included) are cleaned up on read", async () => {
    const { a, x } = setup();
    for (const raw of set) x.store.upsertRaw(CHAT, raw); // as the old ingest kept them
    expect((await a.listMessages(CHAT, 50)).map((m) => [m.id, m.groupId])).toEqual([
      ["P1", "AL1"],
      ["P2", "AL1"],
    ]);
  });

  test("a refreshed media message (re-upload) does not lose the album link", async () => {
    const { a, x } = setup();
    await x.onUpsert({ type: "notify", messages: set });
    await x.onMessageUpdates([{ key: { remoteJid: CHAT, id: "P1", fromMe: false, participant: LINH }, update: { message: { imageMessage: { mimetype: "image/jpeg", url: "https://fresh" } } } }]);
    expect(x.store.getMsg(CHAT, "P1").raw.message.imageMessage.url).toBe("https://fresh");
    expect((await a.listMessages(CHAT, 50)).find((m) => m.id === "P1")).toMatchObject({ groupId: "AL1" });
  });

  test("an edited caption keeps it too", async () => {
    const { a, x } = setup();
    await x.onUpsert({ type: "notify", messages: set });
    await x.onMessageUpdates([{ key: { remoteJid: CHAT, id: "P1", fromMe: false, participant: LINH }, update: { message: { editedMessage: { message: { conversation: "better caption" } } } } }]);
    expect((await a.listMessages(CHAT, 50)).find((m) => m.id === "P1")).toMatchObject({ groupId: "AL1", text: "better caption", edited: true });
  });
});

describe("WhatsApp mentions", () => {
  const mention = (id: string, ts: number, text: string, jids: string[], from = LINH) => ({
    key: { remoteJid: CHAT, fromMe: false, id, participant: from },
    messageTimestamp: Math.floor(ts / 1000),
    message: { extendedTextMessage: { text, contextInfo: { mentionedJid: jids } } },
  });

  function named() {
    const t = setup();
    t.x.store.setContact(LINH, { name: "Linh" });
    t.x.store.setContact(HOA_PN, { name: "Hoa Tran" });
    t.x.store.link(HOA_LID, HOA_PN);
    t.x.store.accountName = "Phuc";
    return t;
  }

  test("live, listed, searched and in the chat's last message the tag reads as a name", async () => {
    const { a, x, of } = named();
    await x.onUpsert({ type: "notify", messages: [mention("M1", Date.now() - 1000, "hi @68388898529359 and @84900", [HOA_LID, ME])] });
    expect(of("message.new")[0].message.text).toBe("hi @Hoa Tran and @Phuc");
    expect((await a.listMessages(CHAT, 50))[0]!.text).toBe("hi @Hoa Tran and @Phuc");
    expect((await a.searchMessages({ query: "hoa tran", limit: 10 })).map((m) => m.id)).toEqual(["M1"]);
    expect(((await a.listChats(10)).find((c) => c.id === CHAT) as any).lastMessage.text).toBe("hi @Hoa Tran and @Phuc");
  });

  test("a name learned later shows on the next read of an older message", async () => {
    const { a, x } = setup();
    await x.onUpsert({ type: "notify", messages: [mention("M1", Date.now() - 1000, "ping @68388898529359", [HOA_LID])] });
    expect((await a.listMessages(CHAT, 50))[0]!.text).toBe("ping @68388898529359");
    x.store.setContact(HOA_LID, { notify: "Hoa" });
    expect((await a.listMessages(CHAT, 50))[0]!.text).toBe("ping @Hoa");
  });
});

describe("WhatsApp search by sender", () => {
  const msg = (id: string, ts: number, from: string, body: any) => ({ key: { remoteJid: CHAT, fromMe: false, id, participant: from }, messageTimestamp: ts, message: body });
  const img = { imageMessage: { mimetype: "image/jpeg" } };
  const doc = { documentMessage: { mimetype: "application/pdf", fileName: "plan.pdf" } };

  async function chat() {
    const t = setup();
    t.x.store.link(HOA_LID, HOA_PN);
    const now = Math.floor(Date.now() / 1000) - 100;
    await t.x.onUpsert({
      type: "append",
      messages: [
        msg("L1", now, LINH, img),
        msg("H1", now + 1, HOA_LID, img), // sent as the LID, stored under the phone jid
        msg("H2", now + 2, HOA_PN, doc),
        msg("H3", now + 3, HOA_LID, { conversation: "see https://example.com" }),
        msg("L2", now + 4, LINH, doc),
      ],
    });
    return t;
  }
  const ids = async (a: WhatsAppAdapter, p: Record<string, any>) => (await a.searchMessages({ query: "", limit: 50, ...p })).map((m) => m.id);

  test("empty query + kinds + senderId + chatId: one member's photos", async () => {
    const { a } = await chat();
    expect(await ids(a, { senderId: LINH, kinds: ["image"], chatId: CHAT })).toEqual(["L1"]);
    expect(await ids(a, { senderId: HOA_PN, kinds: ["image"], chatId: CHAT })).toEqual(["H1"]);
    expect(await ids(a, { senderId: HOA_PN, kinds: ["file"], chatId: CHAT })).toEqual(["H2"]);
    expect(await ids(a, { senderId: HOA_PN, kinds: ["link"], chatId: CHAT })).toEqual(["H3"]);
  });

  test("the LID, the phone jid, a device jid and a bare number all name the same sender", async () => {
    const { a } = await chat();
    for (const who of [HOA_LID, HOA_PN, "84902:7@s.whatsapp.net", "84902", "68388898529359"]) {
      expect(await ids(a, { senderId: who, kinds: ["image", "file", "link"] })).toEqual(["H3", "H2", "H1"]);
    }
  });

  test("senderId on its own lists everything that person wrote, newest first; others are excluded", async () => {
    const { a } = await chat();
    expect(await ids(a, { senderId: HOA_LID })).toEqual(["H3", "H2", "H1"]);
    expect(await ids(a, { senderId: LINH })).toEqual(["L2", "L1"]);
    expect(await ids(a, { senderId: "nobody@s.whatsapp.net" })).toEqual([]);
  });

  test("it combines with a text query and with paging", async () => {
    const { a } = await chat();
    expect(await ids(a, { senderId: LINH, query: "plan" })).toEqual(["L2"]);
    expect(await ids(a, { senderId: HOA_PN, query: "plan" })).toEqual(["H2"]);
    const newest = await a.searchMessages({ query: "", senderId: HOA_PN, limit: 1 });
    expect(newest.map((m) => m.id)).toEqual(["H3"]);
    expect((await ids(a, { senderId: HOA_PN, limit: 1, before: newest[0]!.timestamp }))).toEqual(["H2"]);
  });

  test("a LID learned after the messages were first searched is still matched", async () => {
    const t = setup();
    const now = Math.floor(Date.now() / 1000) - 50;
    await t.x.onUpsert({ type: "append", messages: [msg("U1", now, "777@lid", img)] });
    expect(await ids(t.a, { senderId: "777@lid", kinds: ["image"] })).toEqual(["U1"]);
    expect(await ids(t.a, { senderId: "84777@s.whatsapp.net", kinds: ["image"] })).toEqual([]);
    t.x.store.link("777@lid", "84777@s.whatsapp.net");
    expect(await ids(t.a, { senderId: "84777@s.whatsapp.net", kinds: ["image"] })).toEqual(["U1"]);
    expect(await ids(t.a, { senderId: "777@lid", kinds: ["image"] })).toEqual(["U1"]);
  });
});

describe("WhatsApp send with mentions", () => {
  function sender(participants?: { id: string; phoneNumber?: string; lid?: string }[]) {
    const t = setup();
    t.x.store.setContact(HOA_PN, { name: "Hoa Tran" });
    t.x.store.link(HOA_LID, HOA_PN);
    if (participants) t.x.groupMeta.set(CHAT, { id: CHAT, subject: "G", participants });
    const calls: { jid: string; content: any }[] = [];
    t.x.sock = {
      user: { id: ME },
      sendMessage: async (jid: string, content: any, opts: any) => {
        calls.push({ jid, content });
        return proto.WebMessageInfo.fromObject({
          key: { remoteJid: jid, fromMe: true, id: opts?.messageId ?? "OUT" },
          messageTimestamp: Math.floor(Date.now() / 1000),
          status: 1,
          // what Baileys builds for a text with mentions
          message: { extendedTextMessage: { text: content.text, contextInfo: { mentionedJid: content.mentions } } },
        });
      },
    };
    return { ...t, calls };
  }
  const span = (userId: string) => ({ userId, offset: 3, length: 9 }); // "hi @Hoa Tran, lunch?"
  const TEXT = "hi @Hoa Tran, lunch?";

  test("WhatsApp gets @<number> and the jid; the app gets @Name back", async () => {
    const { a, calls } = sender([{ id: HOA_LID, phoneNumber: HOA_PN, lid: HOA_LID }]);
    const m = await a.sendMessage({ chatId: CHAT, text: TEXT, mentions: [span(HOA_LID)] });
    expect(calls[0]!.content).toMatchObject({ text: "hi @68388898529359, lunch?", mentions: [HOA_LID] });
    expect(m).toMatchObject({ fromMe: true, text: TEXT });
    expect((await a.listMessages(CHAT, 10)).at(-1)!.text).toBe(TEXT);
  });

  test("the other spelling of a member is switched to the one the group uses (LID group)", async () => {
    const { a, calls } = sender([{ id: HOA_LID, phoneNumber: HOA_PN, lid: HOA_LID }]);
    const m = await a.sendMessage({ chatId: CHAT, text: TEXT, mentions: [span(HOA_PN)] });
    expect(calls[0]!.content).toMatchObject({ text: "hi @68388898529359, lunch?", mentions: [HOA_LID] });
    expect(m.text).toBe(TEXT);
  });

  test("and the phone jid in a group addressed by phone number", async () => {
    const { a, calls } = sender([{ id: HOA_PN, lid: HOA_LID }]);
    const m = await a.sendMessage({ chatId: CHAT, text: TEXT, mentions: [span(HOA_LID)] });
    expect(calls[0]!.content).toMatchObject({ text: "hi @84902, lunch?", mentions: [HOA_PN] });
    expect(m.text).toBe(TEXT);
  });

  test("without group metadata the id is used as given", async () => {
    const { a, calls } = sender();
    await a.sendMessage({ chatId: CHAT, text: TEXT, mentions: [span(HOA_LID)] });
    expect(calls[0]!.content).toMatchObject({ text: "hi @68388898529359, lunch?", mentions: [HOA_LID] });
  });

  test("a text without mentions goes out untouched; bad spans are dropped", async () => {
    const { a, calls } = sender([{ id: HOA_LID, lid: HOA_LID }]);
    await a.sendMessage({ chatId: CHAT, text: "@Hoa Tran plain" });
    expect(calls[0]!.content).toEqual({ text: "@Hoa Tran plain" });
    await a.sendMessage({ chatId: CHAT, text: "short", mentions: [{ userId: HOA_LID, offset: 2, length: 50 }] });
    expect(calls[1]!.content).toEqual({ text: "short" });
  });

  test("editing keeps the same rule", async () => {
    const { a, calls } = sender([{ id: HOA_LID, phoneNumber: HOA_PN, lid: HOA_LID }]);
    const sent = await a.sendMessage({ chatId: CHAT, text: "hello" });
    const edited = await a.editMessage(CHAT, sent.id, TEXT, [span(HOA_PN)]);
    expect(calls[1]!.content).toMatchObject({ text: "hi @68388898529359, lunch?", mentions: [HOA_LID] });
    expect(edited).toMatchObject({ text: TEXT, edited: true });
  });
});

describe("oldestFirst (history chunks)", () => {
  const m = (chat: string, id: string, ts: number) => ({ key: { remoteJid: chat }, messageTimestamp: ts, id });
  test("each chat's newest-first run is turned around, ties included; chats keep their order", () => {
    const chunk = [m("A", "a3", 30), m("A", "a2b", 20), m("A", "a2a", 20), m("A", "a1", 10), m("B", "b2", 6), m("B", "b1", 5)];
    expect(oldestFirst(chunk).map((x) => x.id)).toEqual(["a1", "a2a", "a2b", "a3", "b1", "b2"]);
  });
  test("interleaved chats, ascending runs and single messages stay as they are", () => {
    expect(oldestFirst([m("A", "a2", 2), m("B", "b2", 2), m("A", "a1", 1), m("B", "b1", 1)]).map((x) => x.id)).toEqual(["a1", "a2", "b1", "b2"]);
    expect(oldestFirst([m("A", "a1", 1), m("A", "a2", 2)]).map((x) => x.id)).toEqual(["a1", "a2"]);
    expect(oldestFirst([m("A", "a1", 1)]).map((x) => x.id)).toEqual(["a1"]);
    expect(oldestFirst([])).toEqual([]);
  });
});

describe("WhatsApp unread count and hidden messages", () => {
  const now = Math.floor(Date.now() / 1000);
  const batch = (extra: any[] = []) => ({
    "chats.update": [{ id: CHAT, unreadCount: 4 + extra.length }], // what Baileys derives: +1 per real incoming message
    "messages.upsert": {
      type: "notify",
      messages: [albumParent("AL1", now - 10), photo("P1", "AL1", now - 9), photo("P2", "AL1", now - 9), photo("HD1", "P1", now - 8, { type: Assoc.HD_IMAGE_DUAL_UPLOAD }), ...extra],
    },
  });

  test("an album of two photos adds two unread, not four", async () => {
    const { x } = setup();
    await x.handleEvents(x.gen, x.sock, batch());
    expect(x.store.getChat(CHAT).unread).toBe(2);
  });

  test("a text next to the album counts, an encrypted add-on does not", async () => {
    const { x } = setup();
    const secret = { key: { remoteJid: CHAT, fromMe: false, id: "SE1", participant: LINH }, messageTimestamp: now, message: { secretEncryptedMessage: { targetMessageKey: { id: "P1" }, secretEncType: 2 } } };
    await x.handleEvents(x.gen, x.sock, batch([text("T1", now * 1000), secret]));
    expect(x.store.getChat(CHAT).unread).toBe(3); // P1, P2, T1
  });

  test("a chats.update without messages in the same batch is applied as it is", async () => {
    const { x } = setup();
    await x.handleEvents(x.gen, x.sock, { "chats.update": [{ id: CHAT, unreadCount: 3 }] });
    expect(x.store.getChat(CHAT).unread).toBe(3);
  });

  test("my own messages and absolute counts (history) are not touched", async () => {
    const { x } = setup();
    const mine = { ...albumParent("AL9", now), key: { remoteJid: CHAT, fromMe: true, id: "AL9" } };
    await x.handleEvents(x.gen, x.sock, { "chats.update": [{ id: CHAT, unreadCount: 1 }], "messages.upsert": { type: "notify", messages: [mine, text("T9", now * 1000)] } });
    expect(x.store.getChat(CHAT).unread).toBe(1);
    await x.handleEvents(x.gen, x.sock, { "chats.upsert": [{ id: CHAT, unreadCount: 9 }] });
    expect(x.store.getChat(CHAT).unread).toBe(9);
  });
});

describe("chats.direct", () => {
  const ts = () => Math.floor(Date.now() / 1000) - 60;
  const dm = (chat: string, id: string, at = ts()) => ({ key: { remoteJid: chat, fromMe: false, id }, messageTimestamp: at, message: { conversation: `dm ${id}` } });

  /** A socket that records everything chats.direct must not need: the only call it may make is the local LID lookup. */
  function spySock(pnForLid: Record<string, string> = {}) {
    const calls: string[] = [];
    const sock: any = {
      user: { id: ME },
      signalRepository: { lidMapping: { getPNForLID: async (lid: string) => (calls.push(`lookup ${lid}`), pnForLid[lid] ?? null) } },
      sendMessage: async (jid: string, content: any, opts: any) => {
        calls.push(`send ${jid}`);
        return proto.WebMessageInfo.fromObject({ key: { remoteJid: jid, fromMe: true, id: opts.messageId }, messageTimestamp: Math.floor(Date.now() / 1000), status: 1, message: { conversation: content.text } });
      },
      profilePictureUrl: async () => void calls.push("network avatar"),
      onWhatsApp: async () => void calls.push("network onWhatsApp"),
      groupMetadata: async () => void calls.push("network group"),
      presenceSubscribe: async () => {}, // listMessages subscribes; not part of chats.direct
    };
    return { sock, calls };
  }
  const withSock = (pnForLid?: Record<string, string>) => {
    const t = setup();
    const s = spySock(pnForLid);
    t.x.sock = s.sock;
    return { ...t, calls: s.calls };
  };

  test("the capability is advertised", () => {
    expect(setup().a.capabilities).toContain("direct_chat");
  });

  test("a stored chat with that person is returned as it is, with its unread count and last message", async () => {
    const { a, x, calls } = withSock();
    await x.onUpsert({ type: "append", messages: [dm(HOA_PN, "D1"), dm(HOA_PN, "D2")] });
    x.store.getChat(HOA_PN).unread = 2;
    x.store.setContact(HOA_PN, { name: "Hoa Tran" });
    const c = await a.directChat(HOA_PN);
    expect(c).toMatchObject({ id: HOA_PN, kind: "direct", title: "Hoa Tran", unread: 2, lastMessage: { id: "D2", text: "dm D2" } });
    expect(c).toEqual((await a.listChats(10)).find((x) => x.id === HOA_PN)!);
    expect(calls).toEqual([]); // a phone jid needs no lookup at all
  });

  test("a group member known by LID finds the chat once the LID is linked, from either spelling", async () => {
    const { a, x } = withSock();
    await x.onUpsert({ type: "append", messages: [dm(HOA_LID, "D1")] }); // arrived before the mapping was known
    expect((await a.directChat(HOA_LID)).id).toBe(HOA_LID);
    x.store.link(HOA_LID, HOA_PN); // folds the LID chat into the phone chat
    for (const who of [HOA_LID, HOA_PN, "84902:12@s.whatsapp.net", "68388898529359:3@lid"]) {
      expect(await a.directChat(who)).toMatchObject({ id: HOA_PN, lastMessage: { id: "D1" } });
    }
  });

  test("a chat stored under the LID is found by its LID even when no phone number is known", async () => {
    const { a, x } = withSock();
    await x.onUpsert({ type: "append", messages: [dm(HOA_LID, "D1")] });
    expect(await a.directChat(HOA_LID)).toMatchObject({ id: HOA_LID, lastMessage: { id: "D1" } });
    // the phone jid cannot be tied to it without the map: a fresh chat, nothing guessed
    expect(await a.directChat(HOA_PN)).toMatchObject({ id: HOA_PN, unread: 0 });
    expect((await a.directChat(HOA_PN)).lastMessage).toBeUndefined();
  });

  test("no chat yet: a described direct chat on the phone jid, with the contact's name; nothing is stored or sent", async () => {
    const { a, x, calls } = withSock();
    x.store.setContact(HOA_PN, { name: "Hoa Tran" });
    const before = Date.now();
    const c = await a.directChat(HOA_PN);
    expect(c).toMatchObject({ id: HOA_PN, kind: "direct", title: "Hoa Tran", unread: 0 });
    expect(c.timestamp).toBeGreaterThanOrEqual(before);
    expect(c.lastMessage).toBeUndefined();
    expect(x.store.getChat(HOA_PN)).toBeUndefined();
    expect((await a.listChats(10)).map((c) => c.id)).not.toContain(HOA_PN);
    expect(calls).toEqual([]);
  });

  test("the chat id works for messages.list (empty) and messages.send, which then creates and announces the chat", async () => {
    const { a, x, calls, of } = withSock();
    const c = await a.directChat(HOA_PN);
    expect(await a.listMessages(c.id, 50)).toEqual([]);
    const sent = await a.sendMessage({ chatId: c.id, text: "hello" });
    expect(sent).toMatchObject({ chatId: HOA_PN, fromMe: true, text: "hello" });
    expect(calls).toEqual([`send ${HOA_PN}`]);
    await sleep(300);
    expect(x.store.getChat(HOA_PN)).toBeDefined();
    expect(of("chat.upsert").at(-1).chat).toMatchObject({ id: HOA_PN, lastMessage: { text: "hello" } });
    expect((await a.listMessages(c.id, 50)).map((m) => m.id)).toEqual([sent.id]);
  });

  test("a LID whose phone number is in Baileys' local store resolves to the phone jid, and the pair is remembered", async () => {
    const { a, x, calls } = withSock({ [HOA_LID]: "84902:0@s.whatsapp.net" });
    x.store.setContact(HOA_PN, { name: "Hoa Tran" });
    const c = await a.directChat(HOA_LID);
    expect(c).toMatchObject({ id: HOA_PN, title: "Hoa Tran" });
    expect(calls).toEqual([`lookup ${HOA_LID}`]); // a local lookup, nothing else
    expect(x.store.lid2pn.get(HOA_LID)).toBe(HOA_PN);
    await a.sendMessage({ chatId: c.id, text: "hi" });
    expect(calls.at(-1)).toBe(`send ${HOA_PN}`);
  });

  test("a LID nobody can map stays a LID chat, titled with the push name if one was seen; send goes to the LID", async () => {
    const { a, x, calls } = withSock();
    x.store.setContact(HOA_LID, { notify: "Hoa" });
    const c = await a.directChat(HOA_LID);
    expect(c).toMatchObject({ id: HOA_LID, kind: "direct", title: "Hoa", unread: 0 });
    expect(await a.listMessages(c.id, 10)).toEqual([]);
    await a.sendMessage({ chatId: c.id, text: "hi" });
    expect(calls.filter((x) => !x.startsWith("lookup"))).toEqual([`send ${HOA_LID}`]);
  });

  test("a push name seen under the LID is the title once the LID is linked", async () => {
    const { a, x } = withSock();
    x.store.setContact(HOA_LID, { notify: "Hoa (push)" }); // seen in a group before the phone number was known
    expect((await a.directChat(HOA_LID)).title).toBe("Hoa (push)");
    x.store.link(HOA_LID, HOA_PN); // the contact is folded into the phone contact
    for (const who of [HOA_LID, HOA_PN]) expect(await a.directChat(who)).toMatchObject({ id: HOA_PN, title: "Hoa (push)" });
  });

  test("device suffixes and bare phone numbers are accepted", async () => {
    const { a } = withSock();
    expect((await a.directChat("84902:15@s.whatsapp.net")).id).toBe(HOA_PN);
    expect((await a.directChat("84902")).id).toBe(HOA_PN);
    expect((await a.directChat(` ${HOA_PN} `)).id).toBe(HOA_PN);
  });

  test("a cached picture is used, also when it was cached under the other spelling", async () => {
    const { a, x, dir } = withSock();
    x.store.link(HOA_LID, HOA_PN);
    expect((await a.directChat(HOA_LID)).avatar).toBeUndefined();
    mkdirSync(join(dir, "avatars"), { recursive: true });
    const file = join(dir, "avatars", "84902_s.whatsapp.net.jpg");
    writeFileSync(file, "jpg");
    expect((await a.directChat(HOA_LID)).avatar).toBe(file);
    expect((await a.directChat(HOA_PN)).avatar).toBe(file);
  });

  test("yourself resolves to the self chat", async () => {
    const { a } = withSock();
    expect(await a.directChat(ME)).toMatchObject({ id: ME, kind: "self" });
  });

  test("not a person: groups, broadcasts, channels, empty input", async () => {
    const { a, calls } = withSock();
    for (const bad of ["120363@g.us", "status@broadcast", "1234@newsletter", "", "   ", "not a jid", undefined as any]) {
      await expect(a.directChat(bad)).rejects.toMatchObject({ code: "invalid_params" });
    }
    expect(calls).toEqual([]);
  });

  test("it needs a session, but not a connection: cached knowledge is enough while offline", async () => {
    const { a, x } = withSock();
    await x.onUpsert({ type: "append", messages: [dm(HOA_PN, "D1")] });
    x.open = false;
    x.sock = undefined;
    expect(await a.directChat(HOA_PN)).toMatchObject({ id: HOA_PN, lastMessage: { id: "D1" } });
    expect((await a.directChat(HOA_LID)).id).toBe(HOA_LID); // no socket, no lookup: still answers
    x.state = "logged_out";
    await expect(a.directChat(HOA_PN)).rejects.toMatchObject({ code: "not_logged_in" });
  });
});
