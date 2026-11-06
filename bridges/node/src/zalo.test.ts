import { afterEach, beforeEach, describe, expect, setSystemTime, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { BoardType, FriendEventType, GroupEventType, MuteAction, ThreadType } from "zca-js";
import { Adapter } from "./adapter.ts";
import { parseSearch } from "./search.ts";
import { ZaloAdapter } from "./zalo.ts";
import type { Entry } from "./zalo-store.ts";

const T0 = Date.UTC(2026, 10, 3, 12, 0, 0);
const SELF = "9999";

interface Emitted {
  event: string;
  data: any;
}

let dir: string;
let events: Emitted[];
let adapter: any;
let calls: { setMute: any[][]; getListBoard: any[][]; getFriendBoardList: any[][] };
let api: any;

beforeEach(() => {
  setSystemTime(new Date(T0));
  dir = mkdtempSync(join(tmpdir(), "zalo-test-"));
  events = [];
  calls = { setMute: [], getListBoard: [], getFriendBoardList: [] };
  adapter = new ZaloAdapter({
    dataDir: dir,
    // Like main.ts: a frame is serialised the moment it is emitted, later changes to the object do not reach it.
    emit: (event, data) => events.push({ event, data: JSON.parse(JSON.stringify(data ?? null)) }),
    log: () => {},
  });
  api = {
    setMute: async (...a: any[]) => {
      calls.setMute.push(a);
      return "";
    },
    getMute: async () => ({ chatEntries: [], groupChatEntries: [] }),
    getListBoard: async (...a: any[]) => {
      calls.getListBoard.push(a);
      return { items: [], count: 0 };
    },
    getFriendBoardList: async (...a: any[]) => {
      calls.getFriendBoardList.push(a);
      return { data: [], version: 0 };
    },
  };
  adapter.api = api;
  adapter.state = "logged_in";
  adapter.selfId = SELF;
});

afterEach(() => {
  adapter.store.reset(); // cancels the debounced disk write
  rmSync(dir, { recursive: true, force: true });
  setSystemTime();
});

const upserts = () => events.filter((e) => e.event === "chat.upsert").map((e) => e.data.chat);
const pinUpdates = () => events.filter((e) => e.event === "pins.update").map((e) => e.data);

describe("capabilities", () => {
  test("mute and pins are advertised", () => {
    expect(adapter.capabilities).toContain("mute");
    expect(adapter.capabilities).toContain("pins");
  });
});

describe("muteChat", () => {
  test("a duration is sent as the remaining seconds, the chat gets the end time", async () => {
    const chat = await adapter.muteChat("g:42", T0 + 3_600_000);
    expect(calls.setMute).toEqual([[{ duration: 3600, action: MuteAction.MUTE }, "42", ThreadType.Group]]);
    expect(chat).toMatchObject({ id: "g:42", muted: true, mutedUntil: T0 + 3_600_000 });
    expect(upserts()).toEqual([chat]);
  });

  test("a partial second rounds up", async () => {
    await adapter.muteChat("u:7", T0 + 1500);
    expect(calls.setMute[0]).toEqual([{ duration: 2, action: MuteAction.MUTE }, "7", ThreadType.User]);
  });

  test("-1 is forever", async () => {
    const chat = await adapter.muteChat("u:7", -1);
    expect(calls.setMute[0]![0]).toEqual({ duration: -1, action: MuteAction.MUTE });
    expect(chat).toMatchObject({ muted: true, mutedUntil: -1 });
  });

  test("0 unmutes", async () => {
    await adapter.muteChat("u:7", -1);
    const chat = await adapter.muteChat("u:7", 0);
    expect(calls.setMute[1]).toEqual([{ action: MuteAction.UNMUTE }, "7", ThreadType.User]);
    expect(chat.muted).toBeUndefined();
    expect(chat.mutedUntil).toBeUndefined();
    expect(upserts().at(-1)).toEqual(chat);
  });

  test("an end time that already passed unmutes", async () => {
    await adapter.muteChat("u:7", T0 - 1000);
    expect(calls.setMute[0]![0]).toEqual({ action: MuteAction.UNMUTE });
  });

  test("a failed request leaves the chat as it was", async () => {
    api.setMute = async () => {
      throw new Error("boom");
    };
    await expect(adapter.muteChat("u:7", -1)).rejects.toThrow("boom");
    expect(upserts()).toEqual([]);
    expect(adapter.store.chats["u:7"]).toBeUndefined();
  });

  test("a getMute poll right after does not revert it, even one that started before", async () => {
    let release!: (v: any) => void;
    api.getMute = () => new Promise((r) => (release = r));
    const poll = adapter.loadMuted(api); // reads the server before the mute below lands
    setSystemTime(new Date(T0 + 1000));
    await adapter.muteChat("u:7", -1);
    release({ chatEntries: [], groupChatEntries: [] });
    expect(await poll).toEqual([]);
    expect(adapter.buildChat("u:7")).toMatchObject({ muted: true, mutedUntil: -1 });

    api.getMute = async () => ({ chatEntries: [], groupChatEntries: [] });
    expect(await adapter.loadMuted(api)).toEqual([]); // still inside the settle window
    expect(adapter.buildChat("u:7").muted).toBe(true);
  });

  test("later polls apply again: the server is the truth after the window", async () => {
    await adapter.muteChat("u:7", -1);
    setSystemTime(new Date(T0 + 60_000));
    expect(await adapter.loadMuted(api)).toEqual(["u:7"]); // unmuted elsewhere
    expect(adapter.buildChat("u:7").muted).toBeUndefined();
  });

  test("an end time the server echoes a few seconds off is not a change", async () => {
    const until = T0 + 3_600_000;
    await adapter.muteChat("g:42", until);
    setSystemTime(new Date(T0 + 60_000));
    const nowS = Math.floor((T0 + 60_000) / 1000);
    // started a minute ago for an hour, plus 3 s of clock wobble
    api.getMute = async () => ({
      chatEntries: [],
      groupChatEntries: [{ id: "42", muteMode: MuteAction.MUTE, duration: 3603, startTime: nowS - 60, currentTime: nowS }],
    });
    expect(await adapter.loadMuted(api)).toEqual([]);
    expect(adapter.buildChat("g:42")).toMatchObject({ muted: true, mutedUntil: until });
  });

  test("an invalid chat id is rejected before anything is sent", async () => {
    await expect(adapter.muteChat("42", -1)).rejects.toThrow();
    expect(calls.setMute).toEqual([]);
  });
});

// ------------------------------------------------------------------ pins

function pinTopic(over: { msgId: string; cli?: string; sender?: string; title?: string; createTime: number; type?: number; msgType?: number }) {
  return {
    id: `topic-${over.msgId}`,
    type: over.type ?? 2,
    color: 0,
    emoji: "",
    startTime: -1,
    duration: -1,
    params: JSON.stringify({
      client_msg_id: over.cli ?? `c${over.msgId}`,
      global_msg_id: over.msgId,
      senderUid: over.sender ?? "2222",
      senderName: "Lan",
      msg_type: over.msgType ?? 1,
      title: over.title ?? `pinned ${over.msgId}`,
    }),
    creatorId: "2222",
    editorId: "2222",
    createTime: over.createTime,
    editTime: over.createTime,
    repeat: 0,
  };
}

function stored(chatId: string, id: string, cli: string, sender: string, text: string): Entry {
  return {
    m: { id, chatId, senderId: sender, senderName: "Stored Name", fromMe: sender === SELF, timestamp: T0 - 5000, kind: "text", text },
    z: { t: chatId.startsWith("g:") ? 1 : 0, msgId: id, cliMsgId: cli, uidFrom: sender, idTo: "", msgType: "webchat", ts: String(T0 - 5000), ttl: 0, st: 0, at: 0, cmd: 0 },
  };
}

describe("pinnedMessages", () => {
  test("group: pinned-message board items, newest pin first, stored originals preferred", async () => {
    adapter.store.insert("g:42", stored("g:42", "100", "c100", "2222", "the original text"));
    api.getListBoard = async (...a: any[]) => {
      calls.getListBoard.push(a);
      return {
        items: [
          { boardType: BoardType.Note, data: pinTopic({ msgId: "5", createTime: T0, type: 0 }) },
          { boardType: BoardType.PinnedMessage, data: pinTopic({ msgId: "100", createTime: T0 - 2000 }) },
          { boardType: BoardType.PinnedMessage, data: pinTopic({ msgId: "200", createTime: T0 - 1000, sender: "0", title: "mine" }) },
          { boardType: BoardType.Poll, data: { poll_id: 1, question: "?" } },
        ],
        count: 4,
      };
    };
    const msgs = await adapter.pinnedMessages("g:42");
    expect(calls.getListBoard).toEqual([[{ count: 50 }, "42"]]);
    expect(msgs.map((m: any) => m.id)).toEqual(["200", "100"]);
    expect(msgs[1]).toMatchObject({ id: "100", text: "the original text", senderName: "Stored Name", timestamp: T0 - 5000 });
    // not stored: built from the pin; sender "0" is the account owner
    expect(msgs[0]).toMatchObject({ id: "200", chatId: "g:42", senderId: SELF, fromMe: true, kind: "text", text: "mine", timestamp: T0 - 1000 });
  });

  test("an unstored pin takes the sender's name from the contacts when known", async () => {
    adapter.store.contacts["2222"] = { name: "Lan Anh", avatar: "https://a/lan.jpg", fr: 1 };
    api.getListBoard = async () => ({
      items: [{ boardType: BoardType.PinnedMessage, data: pinTopic({ msgId: "300", createTime: T0 }) }],
      count: 1,
    });
    const [m] = await adapter.pinnedMessages("g:42");
    expect(m).toMatchObject({ senderId: "2222", senderName: "Lan Anh", senderAvatar: "https://a/lan.jpg", fromMe: false });
  });

  test("1:1: friend board entries (JSON strings), matched to stored messages by client id and sender", async () => {
    adapter.store.insert("u:7", stored("u:7", "900", "cli-1", "7", "peer said"));
    adapter.store.insert("u:7", stored("u:7", "901", "cli-2", SELF, "I said")); // same cliMsgId, other sender, below
    api.getFriendBoardList = async (...a: any[]) => {
      calls.getFriendBoardList.push(a);
      return {
        data: [
          JSON.stringify(pinTopic({ msgId: "0", cli: "cli-1", sender: "7", createTime: T0 - 100 })),
          JSON.stringify(pinTopic({ msgId: "0", cli: "cli-2", sender: "7", createTime: T0 - 200 })), // cli-2 was sent by me, not 7
        ],
        version: 3,
      };
    };
    const msgs = await adapter.pinnedMessages("u:7");
    expect(calls.getFriendBoardList).toEqual([["7"]]);
    expect(msgs[0]).toMatchObject({ id: "900", text: "peer said" });
    expect(msgs[1]).toMatchObject({ id: "cli-2", senderId: "7", text: "pinned 0" });
  });

  test("an unpinned chat is []", async () => {
    expect(await adapter.pinnedMessages("g:42")).toEqual([]);
    expect(await adapter.pinnedMessages("u:7")).toEqual([]);
  });

  test("answers are cached per chat until a pin event or the TTL", async () => {
    await adapter.pinnedMessages("g:42");
    await adapter.pinnedMessages("g:42");
    await adapter.pinnedMessages("u:7");
    await adapter.pinnedMessages("u:7");
    expect(calls.getListBoard.length).toBe(1);
    expect(calls.getFriendBoardList.length).toBe(1);

    adapter.applyGroupEvent({ type: GroupEventType.NEW_PIN_TOPIC, act: "new_pin_topic", threadId: "42", isSelf: false, data: { topic: {}, groupId: "42" } });
    await adapter.pinnedMessages("g:42");
    expect(calls.getListBoard.length).toBe(2);
    expect(calls.getFriendBoardList.length).toBe(1);

    setSystemTime(new Date(T0 + 6 * 60_000));
    await adapter.pinnedMessages("g:42");
    expect(calls.getListBoard.length).toBe(3);
  });

  test("a pin event during a fetch makes the fetch run again", async () => {
    let n = 0;
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    api.getListBoard = async () => {
      n++;
      if (n === 1) await gate; // the first answer is from before the pin
      return {
        items: n === 1 ? [] : [{ boardType: BoardType.PinnedMessage, data: pinTopic({ msgId: "400", createTime: T0 }) }],
        count: 1,
      };
    };
    const pending = adapter.pinnedMessages("g:42");
    adapter.applyGroupEvent({ type: GroupEventType.NEW_PIN_TOPIC, act: "new_pin_topic", threadId: "42", isSelf: true, data: { topic: {}, groupId: "42" } });
    release();
    const msgs = await pending;
    expect(n).toBe(2);
    expect(msgs.map((m: any) => m.id)).toEqual(["400"]);
  });

  test("not logged in", async () => {
    adapter.state = "logged_out";
    await expect(adapter.pinnedMessages("g:42")).rejects.toThrow();
  });
});

describe("pins.update", () => {
  const groupEvent = (type: string, data: any = {}) => ({ type, act: type, threadId: "42", isSelf: false, data: { groupId: "42", ...data } });

  test("group pin, update, unpin and reorder events", () => {
    for (const t of [GroupEventType.NEW_PIN_TOPIC, GroupEventType.UPDATE_PIN_TOPIC, GroupEventType.UNPIN_TOPIC, GroupEventType.REORDER_PIN_TOPIC]) {
      adapter.applyGroupEvent(groupEvent(t));
    }
    expect(pinUpdates()).toEqual([{ chatId: "g:42" }, { chatId: "g:42" }, { chatId: "g:42" }, { chatId: "g:42" }]);
    expect(events.some((e) => e.event === "message.new")).toBe(false); // no system message for them
  });

  test("board events count only when the topic is a message (or unknown)", () => {
    adapter.applyGroupEvent(groupEvent(GroupEventType.UPDATE_BOARD, { groupTopic: { type: 0, params: "{}" } })); // note
    adapter.applyGroupEvent(groupEvent(GroupEventType.REMOVE_BOARD, { groupTopic: { type: 3 } })); // poll
    expect(pinUpdates()).toEqual([]);
    adapter.applyGroupEvent(groupEvent(GroupEventType.REMOVE_BOARD, { groupTopic: { type: 2 } }));
    adapter.applyGroupEvent(groupEvent(GroupEventType.REMOVE_TOPIC, { groupTopic: null }));
    expect(pinUpdates()).toEqual([{ chatId: "g:42" }, { chatId: "g:42" }]);
  });

  test("friend pin events", () => {
    adapter.applyFriendEvent({
      type: FriendEventType.PIN_CREATE,
      threadId: "7",
      isSelf: false,
      data: { topic: {}, actorId: "7", conversationId: "7", oldVersion: 1, version: 2 },
    });
    expect(pinUpdates()).toEqual([{ chatId: "u:7" }]); // thread and actor are the same chat
    adapter.applyFriendEvent({
      type: FriendEventType.PIN_UNPIN,
      threadId: "7",
      isSelf: true,
      data: { topic: { topicId: "1", topicType: 2 }, actorId: SELF, conversationId: "7", oldVersion: 2, version: 3 },
    });
    expect(pinUpdates().slice(1)).toEqual([{ chatId: "u:7" }]); // my own pin: not the Cloud chat
  });

  test("a friend event addressed to me by the peer covers the peer's chat too", () => {
    adapter.applyFriendEvent({
      type: FriendEventType.PIN_CREATE,
      threadId: SELF,
      isSelf: false,
      data: { topic: {}, actorId: "7", conversationId: SELF, oldVersion: 1, version: 2 },
    });
    expect(pinUpdates().map((p) => p.chatId).sort()).toEqual([`u:${SELF}`, "u:7"].sort());
  });
});

// ------------------------------------------------------------------ real message shapes (ids and names replaced)

interface RawOpts {
  sender: string;
  msgId: string;
  cli: string;
  ts: number;
  msgType: string;
  content: unknown;
  fromMe?: boolean;
  mentions?: unknown;
  dName?: string;
}

/** What zca-js hands the listener: ThreadType + data with the raw fields. */
function zmsg(chat: "g:42" | "u:7", o: RawOpts) {
  const group = chat.startsWith("g:");
  return {
    type: group ? ThreadType.Group : ThreadType.User,
    threadId: chat.slice(2),
    isSelf: !!o.fromMe,
    data: {
      msgId: o.msgId,
      cliMsgId: o.cli,
      uidFrom: o.sender,
      idTo: group ? "42" : o.fromMe ? "7" : SELF,
      msgType: o.msgType,
      ts: String(o.ts),
      ttl: 0,
      st: 0,
      at: 0,
      cmd: 0,
      content: o.content,
      mentions: o.mentions,
      dName: o.dName,
    },
  };
}

/** A stored entry as an older version left it: mapped fields plus the raw content. */
function rawEntry(chat: string, o: RawOpts & { kind?: string; text?: string; media?: any; deleted?: boolean }): Entry {
  const fromMe = o.fromMe ?? o.sender === SELF;
  return {
    m: { id: o.msgId, chatId: chat, senderId: o.sender, fromMe, timestamp: o.ts, kind: (o.kind ?? "text") as any, text: o.text, media: o.media, deleted: o.deleted || undefined },
    z: { t: chat.startsWith("g:") ? 1 : 0, msgId: o.msgId, cliMsgId: o.cli, uidFrom: o.sender, idTo: "", msgType: o.msgType, ts: String(o.ts), ttl: 0, st: 0, at: 0, cmd: 0, content: o.content },
  };
}

const photo = (layout: number, index: number, total: number, over: Record<string, unknown> = {}) => ({
  title: "",
  description: "",
  href: `https://photo-stal-1.zdn.vn/no/jxl/${layout}-${index}.jxl`,
  thumb: `https://photo-stal-1.zdn.vn/no/jxl/${layout}-${index}.jxl`,
  params: JSON.stringify({ group_layout_id: layout, id_in_group: index, total_item_in_group: total, is_group_layout: 1, width: 1290, height: 2796, ...over }),
  action: "",
  type: "",
  childnumber: 0,
});

const callContent = (action: string, params: Record<string, unknown>) => ({
  title: "sendBubbleMessage",
  description: "Cuộc gọi",
  href: "",
  thumb: "",
  params: JSON.stringify(params),
  action,
  type: "",
  childnumber: 0,
});

describe("stored history is mapped again on start (remapStored)", () => {
  test("old call logs read as calls in both directions, and a second run changes nothing", () => {
    const old = { kind: "link", text: "sendBubbleMessage\nCuộc gọi" };
    adapter.store.insert(
      "u:7",
      rawEntry("u:7", { ...old, sender: "7", msgId: "1", cli: "c1", ts: T0 - 5000, msgType: "chat.recommended", content: callContent("recommened.calltime", { reason: 0, calltype: 0, duration: 125 }) }),
    );
    adapter.store.insert(
      "u:7",
      rawEntry("u:7", { ...old, sender: SELF, msgId: "2", cli: "c2", ts: T0 - 4000, msgType: "chat.recommended", content: callContent("recommened.misscall", { reason: 4, calltype: 1, duration: 0 }) }),
    );
    adapter.store.insert(
      "u:7",
      rawEntry("u:7", { ...old, sender: "7", msgId: "3", cli: "c3", ts: T0 - 3000, msgType: "chat.recommended", content: callContent("recommened.misscall", { reason: 2, calltype: 0, duration: 0 }) }),
    );
    expect(adapter.remapStored()).toBe(3);
    const [a, b, c] = adapter.store.list("u:7").map((e: Entry) => e.m);
    expect(a).toMatchObject({ kind: "call", text: "Cuộc gọi thoại đến · 2 phút 5 giây", media: undefined });
    expect(b).toMatchObject({ kind: "call", text: "Cuộc gọi video bị huỷ" });
    expect(c).toMatchObject({ kind: "call", text: "Bạn bị nhỡ cuộc gọi thoại" });
    expect(adapter.remapStored()).toBe(0);
  });

  test("link cards keep their cached file path, deleted messages are not touched", () => {
    const link = { title: "https://s.example.vn/4B0", description: "Mua xe", href: "https://s.example.vn/4B0", thumb: "https://t/1.jpg", action: "recommened.link" };
    adapter.store.insert(
      "u:7",
      rawEntry("u:7", { sender: "7", msgId: "1", cli: "c1", ts: T0 - 5000, msgType: "chat.recommended", content: link, kind: "link", text: "old text", media: { url: "https://s.example.vn/4B0", thumbnail: "https://t/0.jpg", path: "/cache/thumb.jpg" } }),
    );
    adapter.store.insert("u:7", rawEntry("u:7", { sender: "7", msgId: "2", cli: "c2", ts: T0 - 4000, msgType: "chat.recommended", content: link, kind: "text", deleted: true }));
    adapter.remapStored();
    const [kept, gone] = adapter.store.list("u:7").map((e: Entry) => e.m);
    expect(kept).toMatchObject({ kind: "link", text: "https://s.example.vn/4B0\nMua xe", media: { thumbnail: "https://t/1.jpg", path: "/cache/thumb.jpg" } });
    expect(gone).toMatchObject({ kind: "text", deleted: true });
    expect(gone.text).toBeUndefined();
  });

  test("zinstant cards, notices and cards in an array no longer read as unsupported", () => {
    const item = { zinstantdata_id: "templateId=11845", data_type: 2 };
    const wc = (id: string, ts: number, content: unknown, msgType = "chat.webcontent") =>
      adapter.store.insert("g:42", rawEntry("g:42", { sender: "2222", msgId: id, cli: `c${id}`, ts, msgType, content, kind: "unsupported" }));
    wc("1", T0 - 5000, { action: "zinstant.bankcard", params: JSON.stringify({ item, customMsg: { msg: { vi: "[Tài khoản ngân hàng]" }, highLightsV2: [] } }) });
    wc("2", T0 - 4000, { action: "", params: JSON.stringify({ item, customMsg: { msg: { vi: "%1$s sẽ tham gia %2$s" }, highLightsV2: [{ dpn: "Phúc" }, { dpn: "Họp tuần" }] } }) });
    wc("3", T0 - 3000, { params: JSON.stringify({ customMsg: { msg: { vi: "%1$s đã xóa tin nhắn này" }, highLightsV2: [{ uid: 1, dpn: "Sơn" }] } }) }, "chat.delete");
    wc("4", T0 - 2000, [{ title: "Đăng nhập Zalo trên máy tính thành công", description: "Bằng: Mã QR", href: "", thumb: "" }], "chat.list.action");
    adapter.remapStored();
    const m = adapter.store.list("g:42").map((e: Entry) => [e.m.kind, e.m.text]);
    expect(m).toEqual([
      ["text", "[Tài khoản ngân hàng]"],
      ["system", "Phúc sẽ tham gia Họp tuần"],
      ["system", "Sơn đã xóa tin nhắn này"],
      ["text", "Đăng nhập Zalo trên máy tính thành công\nBằng: Mã QR"],
    ]);
  });

  test("a recall event listed as a message deletes its target and is dropped", () => {
    adapter.store.insert("g:42", rawEntry("g:42", { sender: "2222", msgId: "8447660817112", cli: "1793703907063", ts: T0 - 5000, msgType: "webchat", content: "chốt deal", text: "chốt deal" }));
    adapter.store.insert(
      "g:42",
      rawEntry("g:42", { sender: "2222", msgId: "8447735606857", cli: "c2", ts: T0 - 4000, msgType: "chat.undo", content: { globalMsgId: 8447660817112, cliMsgId: 1793703907063, deleteMsg: 0 }, kind: "unsupported" }),
    );
    // an empty chat.undo is the recalled message itself: already deleted, stays
    adapter.store.insert("g:42", rawEntry("g:42", { sender: "2222", msgId: "5", cli: "c5", ts: T0 - 3000, msgType: "chat.undo", content: "", deleted: true }));
    adapter.remapStored();
    const list = adapter.store.list("g:42").map((e: Entry) => e.m);
    expect(list.map((m: any) => m.id)).toEqual(["8447660817112", "5"]);
    expect(list[0]).toMatchObject({ deleted: true, kind: "text" });
    expect(list[0].text).toBeUndefined();
  });

  test("album photos get one groupId and come back in the order they were picked", () => {
    // real shape: id_in_group by server time 2,0,1 (parallel uploads); cliMsgId follows the pick order
    const layout = 1791865990221;
    const add = (id: string, index: number, cli: number, ts: number, sender = "2222") =>
      adapter.store.insert("g:42", rawEntry("g:42", { sender, msgId: id, cli: String(cli), ts, msgType: "chat.photo", content: photo(layout, index, 3), kind: "image" }));
    adapter.store.insert("g:42", rawEntry("g:42", { sender: "3333", msgId: "100", cli: "c100", ts: T0 - 9000, msgType: "webchat", content: "before", text: "before" }));
    add("p2", 2, 1791865990208, T0 - 5000);
    add("p0", 0, 1791865990164, T0 - 4900);
    add("p1", 1, 1791865990191, T0 - 4700);
    adapter.store.insert("g:42", rawEntry("g:42", { sender: "3333", msgId: "101", cli: "c101", ts: T0 - 1000, msgType: "webchat", content: "after", text: "after" }));
    add("other-p0", 0, 5, T0 - 4800, "4444"); // same layout id from another sender: its own album, left alone
    adapter.store.insert("g:42", rawEntry("g:42", { sender: "2222", msgId: "single", cli: "c9", ts: T0 - 800, msgType: "chat.photo", content: photo(0, 0, 0, { group_layout_id: 0, is_group_layout: 0 }), kind: "image" }));

    adapter.remapStored();
    const list = adapter.store.list("g:42");
    expect(list.map((e: Entry) => e.m.timestamp)).toEqual([...list.map((e: Entry) => e.m.timestamp)].sort((a, b) => a - b));
    const ids = list.map((e: Entry) => e.m.id);
    expect(ids.filter((i: string) => ["p0", "p1", "p2"].includes(i))).toEqual(["p0", "p1", "p2"]);
    const byId = (id: string) => list.find((e: Entry) => e.m.id === id)!.m;
    expect(["p0", "p1", "p2"].map((id) => byId(id).groupId)).toEqual([String(layout), String(layout), String(layout)]);
    expect(byId("other-p0").groupId).toBe(String(layout)); // same id, other sender: the app also compares senders
    expect(byId("single").groupId).toBeUndefined();
    expect(byId("100").groupId).toBeUndefined();
    // the album kept its own timestamps: nothing outside it moved
    expect([byId("p0").timestamp, byId("p1").timestamp, byId("p2").timestamp]).toEqual([T0 - 5000, T0 - 4900, T0 - 4700]);
    expect(byId("100").timestamp).toBe(T0 - 9000);
    expect(byId("101").timestamp).toBe(T0 - 1000);
    expect(adapter.remapStored()).toBe(0);
  });
});

describe("incoming albums, mentions and recall events (live)", () => {
  const newOnes = () => events.filter((e) => e.event === "message.new").map((e) => e.data.message);
  const updates = () => events.filter((e) => e.event === "message.update").map((e) => e.data.message);

  test("photos arriving in upload order are re-timed into pick order, siblings already shown get a message.update", async () => {
    const layout = 1792489746;
    const member = (msgId: string, index: number, cli: number, dt: number) =>
      zmsg("g:42", { sender: "2222", msgId, cli: String(cli), ts: T0 - 20_000 + dt, msgType: "chat.photo", content: photo(layout, index, 3) });
    await adapter.ingestZMessage(member("9003", 2, 1792489748113, 0), { replay: false });
    await adapter.ingestZMessage(member("9001", 0, 1792489746858, 400), { replay: false });
    await adapter.ingestZMessage(member("9002", 1, 1792489747545, 900), { replay: false });

    const list = adapter.store.list("g:42").map((e: Entry) => e.m);
    expect(list.map((m: any) => m.id)).toEqual(["9001", "9002", "9003"]);
    expect(list.map((m: any) => m.timestamp)).toEqual([T0 - 20_000, T0 - 20_000 + 400, T0 - 20_000 + 900]);
    expect(new Set(list.map((m: any) => m.groupId))).toEqual(new Set([String(layout)]));
    // every message.new carries its final time; the earlier ones were corrected by updates
    expect(newOnes().map((m: any) => [m.id, m.timestamp])).toEqual([
      ["9003", T0 - 20_000],
      ["9001", T0 - 20_000],
      ["9002", T0 - 20_000 + 400],
    ]);
    const last = new Map(updates().map((m: any) => [m.id, m.timestamp]));
    expect(last.get("9003")).toBe(T0 - 20_000 + 900);
    expect(newOnes().every((m: any) => m.groupId === String(layout))).toBe(true);
    // what the app fetches next is in order too
    adapter.histFetched.add("42");
    expect((await adapter.listMessages("g:42", 10)).map((m: any) => m.id)).toEqual(["9001", "9002", "9003"]);
  });

  test("a search that ran before the re-timing does not serve the old time", async () => {
    adapter.store.insert("g:42", rawEntry("g:42", { sender: "2222", msgId: "9003", cli: "1792489748113", ts: T0 - 5000, msgType: "chat.photo", content: photo(7, 2, 2), kind: "image" }));
    adapter.remapStored();
    const before = await adapter.searchMessages(parseSearch({ kinds: ["image"], chatId: "g:42" }));
    expect(before.map((m: any) => m.timestamp)).toEqual([T0 - 5000]);
    await adapter.ingestZMessage(zmsg("g:42", { sender: "2222", msgId: "9001", cli: "1792489746858", ts: T0 - 4000, msgType: "chat.photo", content: photo(7, 0, 2) }), { replay: false });
    const after = await adapter.searchMessages(parseSearch({ kinds: ["image"], chatId: "g:42" }));
    expect(after.map((m: any) => [m.id, m.timestamp])).toEqual([
      ["9003", T0 - 4000], // newest first
      ["9001", T0 - 5000],
    ]);
  });

  test("a mention written as an id is shown as @Name; a typed name is left alone", async () => {
    adapter.store.contacts["219184827"] = { name: "Phúc Lê", fr: 1 };
    const send = (msgId: string, text: string, mentions: unknown[]) =>
      adapter.ingestZMessage(zmsg("g:42", { sender: "2222", msgId, cli: `c${msgId}`, ts: T0 - 1000, msgType: "webchat", content: text, mentions }), { replay: false });
    await send("1", "@219184827 xem giúp", [{ uid: "219184827", pos: 0, len: 10, type: 0 }]);
    await send("2", "Dạ hi cả nhà @All 👋 và @Phúc Lê nhé", [
      { uid: "-1", pos: 12, len: 4, type: 1 },
      { uid: "219184827", pos: 24, len: 8, type: 0 },
    ]);
    expect(newOnes().map((m: any) => m.text)).toEqual(["@Phúc Lê xem giúp", "Dạ hi cả nhà @All 👋 và @Phúc Lê nhé"]);
  });

  test("a recall event from a history listing deletes its target and shows nothing", async () => {
    await adapter.ingestZMessage(zmsg("g:42", { sender: "2222", msgId: "8447660817112", cli: "1793703907063", ts: T0 - 5000, msgType: "webchat", content: "chốt deal" }), { replay: true, silent: true });
    await adapter.ingestZMessage(
      zmsg("g:42", { sender: "2222", msgId: "8447735606857", cli: "c2", ts: T0 - 4000, msgType: "chat.undo", content: { globalMsgId: 8447660817112, cliMsgId: 1793703907063, deleteMsg: 0 } }),
      { replay: true, silent: true },
    );
    expect(adapter.store.list("g:42").map((e: Entry) => [e.m.id, !!e.m.deleted])).toEqual([["8447660817112", true]]);
  });

  test("a live call log is a call from the first message.new", async () => {
    await adapter.ingestZMessage(
      zmsg("u:7", { sender: "7", msgId: "55", cli: "c55", ts: T0 - 1000, msgType: "chat.recommended", content: callContent("recommened.calltime", { callId: 202347544, duration: 179, isCaller: 0, isEnableCallback: 1, calltype: 1 }) }),
      { replay: false },
    );
    expect(newOnes()[0]).toMatchObject({ kind: "call", text: "Cuộc gọi video đến · 2 phút 59 giây", fromMe: false });
    await adapter.ingestZMessage(
      zmsg("u:7", { sender: SELF, fromMe: true, msgId: "56", cli: "c56", ts: T0 - 500, msgType: "chat.recommended", content: callContent("recommened.misscall", { isEnableCallback: 1, reason: 2, calltype: 0, duration: 0 }) }),
      { replay: false },
    );
    expect(newOnes()[1]).toMatchObject({ kind: "call", text: "Cuộc gọi thoại đi · Không trả lời", fromMe: true });
  });
});

describe("sending with mentions", () => {
  test("the returned and stored message keeps the typed @Name text; Zalo gets uid, pos and len", async () => {
    const sent: any[] = [];
    api.sendMessage = async (...a: any[]) => {
      sent.push(a);
      return { message: { msgId: 7001 }, attachment: [] };
    };
    const text = "chào @Phúc Lê nhé";
    const m = await adapter.sendMessage({ chatId: "g:42", text, mentions: [{ userId: "219184827", offset: 5, length: 8 }] });
    expect(m).toMatchObject({ id: "7001", chatId: "g:42", fromMe: true, kind: "text", text });
    expect(sent[0][0]).toMatchObject({ msg: text, mentions: [{ pos: 5, uid: "219184827", len: 8 }] });
    expect(sent[0].slice(1)).toEqual(["42", ThreadType.Group]);
    expect(adapter.store.find("g:42", "7001").m.text).toBe(text);
    // the listener's echo of the same message must not change it
    await adapter.ingestZMessage(
      zmsg("g:42", { sender: SELF, fromMe: true, msgId: "7001", cli: "c7001", ts: T0, msgType: "webchat", content: text, mentions: [{ uid: "219184827", pos: 5, len: 8, type: 0 }] }),
      { replay: false },
    );
    expect(adapter.store.list("g:42").map((e: Entry) => e.m.text)).toEqual([text]);
  });

  test("spans that do not fit the text are dropped, 1:1 chats send none", async () => {
    const sent: any[] = [];
    api.sendMessage = async (...a: any[]) => {
      sent.push(a);
      return { message: { msgId: sent.length }, attachment: [] };
    };
    await adapter.sendMessage({ chatId: "g:42", text: "hi @A", mentions: [{ userId: "1", offset: 3, length: 20 }] });
    expect(sent[0][0].mentions).toBeUndefined();
    await adapter.sendMessage({ chatId: "u:7", text: "hi @A", mentions: [{ userId: "1", offset: 3, length: 2 }] });
    expect(sent[1][0].mentions).toBeUndefined();
  });
});

describe("messages.search with senderId", () => {
  const msg = (chat: string, id: string, sender: string, ts: number, over: Partial<Entry["m"]> & { msgType?: string } = {}): Entry => {
    const e = rawEntry(chat, { sender, msgId: id, cli: `c${id}`, ts, msgType: over.msgType ?? "webchat", content: "x", kind: over.kind ?? "text", text: over.text, media: over.media });
    Object.assign(e.m, { deleted: over.deleted });
    return e;
  };

  beforeEach(() => {
    adapter.store.insert("g:42", msg("g:42", "1", "2222", T0 - 9000, { kind: "file", text: "Hợp đồng.pdf", media: { fileName: "Hợp đồng.pdf" } }));
    adapter.store.insert("g:42", msg("g:42", "2", "3333", T0 - 8000, { kind: "file", text: "Báo giá.xlsx", media: { fileName: "Báo giá.xlsx" } }));
    adapter.store.insert("g:42", msg("g:42", "3", "2222", T0 - 7000, { kind: "image" }));
    adapter.store.insert("g:42", msg("g:42", "4", "2222", T0 - 6000, { text: "xem https://example.vn/a nhé" }));
    adapter.store.insert("g:42", msg("g:42", "5", "2222", T0 - 5000, { kind: "call", text: "Cuộc gọi thoại đến · 5 giây" }));
    adapter.store.insert("g:42", msg("g:42", "6", "2222", T0 - 4000, { kind: "file", text: "Đã thu hồi.pdf", deleted: true }));
    adapter.store.insert("u:7", msg("u:7", "7", "2222", T0 - 3000, { kind: "file", text: "Khác chat.pdf", media: { fileName: "Khác chat.pdf" } }));
  });

  const ids = (r: any[]) => r.map((m) => m.id);

  test("empty query + kinds + senderId + chatId lists that member's files only (profile panel)", async () => {
    const r = await adapter.searchMessages(parseSearch({ chatId: "g:42", senderId: "2222", kinds: ["file"] }));
    expect(ids(r)).toEqual(["1"]);
  });

  test("links: text messages with a URL count, only from that sender", async () => {
    expect(ids(await adapter.searchMessages(parseSearch({ chatId: "g:42", senderId: "2222", kinds: ["link"] })))).toEqual(["4"]);
    expect(ids(await adapter.searchMessages(parseSearch({ chatId: "g:42", senderId: "3333", kinds: ["link"] })))).toEqual([]);
  });

  test("senderId alone lists everything they wrote, newest first, without deleted and call messages", async () => {
    expect(ids(await adapter.searchMessages(parseSearch({ chatId: "g:42", senderId: "2222" })))).toEqual(["4", "3", "1"]);
  });

  test("without chatId it spans every chat; with a query every word must match", async () => {
    expect(ids(await adapter.searchMessages(parseSearch({ senderId: "2222", kinds: ["file"] })))).toEqual(["7", "1"]);
    expect(ids(await adapter.searchMessages(parseSearch({ senderId: "2222", query: "hop dong" })))).toEqual(["1"]);
    expect(ids(await adapter.searchMessages(parseSearch({ senderId: "3333", query: "hop dong" })))).toEqual([]);
  });

  test("paging: before = the oldest timestamp of the previous page", async () => {
    const page1 = await adapter.searchMessages(parseSearch({ chatId: "g:42", senderId: "2222", limit: 2 }));
    expect(ids(page1)).toEqual(["4", "3"]);
    const page2 = await adapter.searchMessages(parseSearch({ chatId: "g:42", senderId: "2222", limit: 2, before: page1.at(-1)!.timestamp }));
    expect(ids(page2)).toEqual(["1"]);
  });

  test("my own messages are found by my uid", async () => {
    adapter.store.insert("g:42", msg("g:42", "8", SELF, T0 - 2000, { text: "tôi viết" }));
    expect(ids(await adapter.searchMessages(parseSearch({ chatId: "g:42", senderId: SELF })))).toEqual(["8"]);
  });
});

// ------------------------------------------------------------------ chats.direct

describe("chats.direct", () => {
  let userInfoCalls: string[];
  let sent: any[][];

  beforeEach(() => {
    userInfoCalls = [];
    sent = [];
    api.getUserInfo = async (id: string) => {
      userInfoCalls.push(id);
      return { changed_profiles: id === "5555" ? { "5555": { displayName: "Lan Anh", avatar: "https://a/lan.jpg" } } : {}, unchanged_profiles: {} };
    };
    api.sendMessage = async (...a: any[]) => {
      sent.push(a);
      return { message: { msgId: 9100 + sent.length }, attachment: [] };
    };
  });

  const noTraffic = () => {
    expect(sent).toEqual([]); // nothing is ever sent to the person
    expect(events.filter((e) => e.event === "message.new")).toEqual([]);
  };

  test("the capability is advertised, and the Zalo adapter overrides the base hook main.ts's chats.direct calls", () => {
    expect(adapter.capabilities).toContain("direct_chat");
    // the base class answers "unsupported"; a differently named method here would leave chats.direct unanswered on Zalo
    expect(ZaloAdapter.prototype.directChat).not.toBe(Adapter.prototype.directChat);
  });

  test("a group member we only know from the group: a new direct chat named from the contacts, nothing stored or sent", async () => {
    adapter.store.contacts["2222"] = { name: "Lan Anh", avatar: "https://a/lan.jpg" };
    adapter.friendsKnown = true; // a friend list was loaded and this person is not in it
    const chat = await adapter.directChat("2222");
    expect(chat).toEqual({
      id: "u:2222",
      title: "Lan Anh",
      avatar: "https://a/lan.jpg",
      kind: "direct",
      isContact: false,
      unread: 0,
      timestamp: 0,
    });
    expect(userInfoCalls).toEqual([]); // stored data was enough
    expect(adapter.store.chatIds()).toEqual([]);
    expect(adapter.store.chats["u:2222"]).toBeUndefined();
    noTraffic();
  });

  test("an existing 1:1 chat comes back as it is: messages, unread, pin and friend flag", async () => {
    adapter.store.contacts["2222"] = { name: "Lan Anh", fr: 1 };
    adapter.store.insert("u:2222", stored("u:2222", "700", "c700", "2222", "chào bạn"));
    adapter.store.chat("u:2222").unread = 2;
    adapter.store.chat("u:2222").pinned = true;
    const chat = await adapter.directChat("2222");
    expect(chat).toMatchObject({ id: "u:2222", title: "Lan Anh", kind: "direct", isContact: true, unread: 2, pinned: true });
    expect(chat.lastMessage).toMatchObject({ id: "700", text: "chào bạn" });
    expect(userInfoCalls).toEqual([]);
    noTraffic();
  });

  test("a name only a stored group message carries is used, and remembered for next time", async () => {
    const e = stored("g:42", "11", "c11", "2222", "hello nhóm");
    e.m.senderName = "Lan Anh (nhóm)";
    e.m.senderAvatar = "https://a/g.jpg";
    adapter.store.insert("g:42", e);
    const chat = await adapter.directChat("2222");
    expect(chat).toMatchObject({ id: "u:2222", title: "Lan Anh (nhóm)", avatar: "https://a/g.jpg", kind: "direct" });
    expect(adapter.store.contacts["2222"]).toMatchObject({ name: "Lan Anh (nhóm)" });
    expect(userInfoCalls).toEqual([]);
    // my own messages never lend a name to somebody else
    const mine = stored("g:42", "12", "c12", SELF, "tôi");
    mine.m.senderName = "Tôi";
    adapter.store.insert("g:42", mine);
    expect((await adapter.directChat("3333")).title).toBe("3333");
  });

  test("the name of a stored chat counts, a stored message from a 1:1 chat is not scanned", async () => {
    adapter.store.chat("u:2222").title = "Tên đã lưu";
    expect((await adapter.directChat("2222")).title).toBe("Tên đã lưu");
    expect(userInfoCalls).toEqual([]);
  });

  test("only when no name is held is getUserInfo asked, once: the name is remembered", async () => {
    const chat = await adapter.directChat("5555");
    expect(userInfoCalls).toEqual(["5555"]);
    expect(chat).toMatchObject({ id: "u:5555", title: "Lan Anh", avatar: "https://a/lan.jpg", kind: "direct", unread: 0 });
    expect(adapter.store.contacts["5555"]).toMatchObject({ name: "Lan Anh" });
    await adapter.directChat("5555");
    expect(userInfoCalls).toEqual(["5555"]);
    noTraffic();
  });

  test("a person Zalo does not describe still opens (titled with the uid) and is not asked again within the cache time", async () => {
    const chat = await adapter.directChat("6666");
    expect(chat).toMatchObject({ id: "u:6666", title: "6666", kind: "direct", unread: 0 });
    await adapter.directChat("6666");
    expect(userInfoCalls).toEqual(["6666"]);
    // a failing lookup does not fail the call either
    api.getUserInfo = async () => {
      throw new Error("boom");
    };
    expect((await adapter.directChat("7777")).title).toBe("7777");
  });

  test("two clicks at once share one lookup", async () => {
    const [a, b] = await Promise.all([adapter.directChat("5555"), adapter.directChat("5555")]);
    expect(userInfoCalls).toEqual(["5555"]);
    expect(a).toEqual(b);
  });

  test("without a connection the stored data answers and nothing is requested", async () => {
    adapter.api = undefined;
    expect((await adapter.directChat("5555")).title).toBe("5555");
    adapter.store.contacts["2222"] = { name: "Lan Anh" };
    expect((await adapter.directChat("2222")).title).toBe("Lan Anh");
    expect(userInfoCalls).toEqual([]);
  });

  test("my own uid is the Cloud chat", async () => {
    adapter.self = { id: SELF, name: "Phúc", avatar: "https://a/me.jpg" };
    expect(await adapter.directChat(SELF)).toMatchObject({ id: `u:${SELF}`, kind: "self", title: "Cloud của tôi", avatar: "https://a/me.jpg" });
    expect(userInfoCalls).toEqual([]);
  });

  test("a userId that is not a Zalo uid is rejected before anything is looked up", async () => {
    for (const bad of ["", "  ", "abc", "u:2222", "g:42", "22 22"]) {
      await expect(adapter.directChat(bad)).rejects.toMatchObject({ code: "invalid_params" });
    }
    expect(userInfoCalls).toEqual([]);
  });

  test("not logged in", async () => {
    adapter.state = "logged_out";
    await expect(adapter.directChat("2222")).rejects.toMatchObject({ code: "not_logged_in" });
  });

  test("messages.list on the new chat is [] (not an error), with or without a cursor, and asks Zalo for nothing", async () => {
    adapter.store.contacts["2222"] = { name: "Lan Anh" };
    const { id } = await adapter.directChat("2222");
    // a user thread has no paged history to fetch: the mock has no getGroupChatHistory / getUserChatHistory, so a call would throw
    expect(await adapter.listMessages(id, 50)).toEqual([]);
    expect(await adapter.listMessages(id, 50, "123")).toEqual([]);
  });

  test("messages.send to the new chat goes to the user thread and the chat then lists with its message", async () => {
    adapter.store.contacts["2222"] = { name: "Lan Anh" };
    const { id } = await adapter.directChat("2222");
    expect(adapter.store.list(id)).toEqual([]);
    const m = await adapter.sendMessage({ chatId: id, text: "chào Lan" });
    expect(sent).toHaveLength(1);
    expect(sent[0]![0]).toMatchObject({ msg: "chào Lan" });
    expect(sent[0]!.slice(1)).toEqual(["2222", ThreadType.User]); // the uid, as a user thread, though the chat was never listed
    expect(m).toMatchObject({ chatId: "u:2222", fromMe: true, kind: "text", text: "chào Lan" });
    expect(adapter.store.list("u:2222").map((e: Entry) => e.m.text)).toEqual(["chào Lan"]);
    const listed = (await adapter.listChats(50)).find((c: any) => c.id === "u:2222");
    expect(listed).toMatchObject({ title: "Lan Anh", kind: "direct" });
    expect(listed.lastMessage).toMatchObject({ text: "chào Lan" });
    expect((await adapter.directChat("2222")).lastMessage).toMatchObject({ text: "chào Lan" });
  });
});
