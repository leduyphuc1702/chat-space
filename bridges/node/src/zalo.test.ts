import { afterEach, beforeEach, describe, expect, setSystemTime, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { BoardType, FriendEventType, GroupEventType, MuteAction, ThreadType } from "zca-js";
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
    emit: (event, data) => events.push({ event, data }),
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
