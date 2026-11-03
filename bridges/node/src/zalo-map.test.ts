import { describe, expect, test } from "bun:test";
import { parsePinTopic, parsePinTopics, pinToMessage } from "./zalo-map.ts";

/** Topic shaped like Zalo PC builds it (getTopicDataFromMessage) and board/list returns it: type 2 = message. */
function topic(params: Record<string, unknown> | string, over: Record<string, unknown> = {}) {
  return {
    id: "6150361188449441790",
    type: 2,
    color: -16745729,
    emoji: "",
    startTime: -1,
    duration: -1,
    params: typeof params === "string" ? params : JSON.stringify(params),
    creatorId: "1111",
    createTime: 1_760_000_000_000,
    editorId: "1111",
    editTime: 1_760_000_000_000,
    repeat: 0,
    ...over,
  };
}

const base = { client_msg_id: "1760000000111", global_msg_id: "7777000111", senderUid: "2222", senderName: "Lan" };

describe("parsePinTopic", () => {
  test("text pin: params is a JSON string", () => {
    const pin = parsePinTopic(topic({ ...base, msg_type: 1, title: "Họp lúc 9h" }))!;
    expect(pin.id).toBe("6150361188449441790");
    expect(pin.msgId).toBe("7777000111");
    expect(pin.cliMsgId).toBe("1760000000111");
    expect(pin.senderUid).toBe("2222");
    expect(pin.senderName).toBe("Lan");
    expect(pin.pinnedAt).toBe(1_760_000_000_000);
    expect(pin.mapped).toEqual({ kind: "text", text: "Họp lúc 9h" });
  });

  test("a friend board entry may itself be a JSON string, and params already an object", () => {
    const entry = JSON.stringify(topic({ ...base, msg_type: 1, title: "hi" }));
    expect(parsePinTopic(entry)?.mapped.text).toBe("hi");
    const parsed = { ...topic("{}"), params: { ...base, msg_type: 1, title: "hi" } };
    expect(parsePinTopic(parsed)?.msgId).toBe("7777000111");
  });

  test("image / gif / video keep the thumbnail and caption", () => {
    const thumb = "https://f21-zpg-r.zdn.vn/abc/photo.jpg";
    expect(parsePinTopic(topic({ ...base, msg_type: 32, title: "ảnh", thumb }))!.mapped).toEqual({
      kind: "image",
      text: "ảnh",
      media: { url: thumb, thumbnail: thumb, mime: "image/jpeg" },
    });
    expect(parsePinTopic(topic({ ...base, msg_type: 37, thumb }))!.mapped.kind).toBe("image"); // doodle
    expect(parsePinTopic(topic({ ...base, msg_type: 49, thumb }))!.mapped.media).toEqual({
      url: thumb,
      thumbnail: thumb,
      mime: "image/gif",
    });
    expect(parsePinTopic(topic({ ...base, msg_type: 44, title: "clip", thumb }))!.mapped).toEqual({
      kind: "video",
      text: "clip",
      media: { thumbnail: thumb, mime: "video/mp4" },
    });
  });

  test("file: title is the name, extra (JSON string) carries the size", () => {
    const extra = JSON.stringify({ fileSize: "20480", checksum: "x", fileExt: "pdf", fdata: "{}", fType: 1 });
    const pin = parsePinTopic(topic({ ...base, msg_type: 46, title: "Bao cao.pdf", extra }))!;
    expect(pin.mapped).toEqual({
      kind: "file",
      text: "Bao cao.pdf",
      media: { fileName: "Bao cao.pdf", mime: "application/pdf", size: 20480 },
    });
    // extra as an object (already parsed by Zalo PC's cache) works too
    expect(parsePinTopic(topic({ ...base, msg_type: 46, title: "a.zip", extra: { fileSize: 5 } }))!.mapped.media?.size).toBe(5);
  });

  test("sticker: extra has the sticker id and category", () => {
    const pin = parsePinTopic(topic({ ...base, msg_type: 36, extra: JSON.stringify({ id: 1234, catId: 56, type: 3 }) }))!;
    expect(pin.mapped).toEqual({ kind: "sticker", text: undefined, media: undefined, sticker: { id: 1234, cateId: 56 } });
    expect(parsePinTopic(topic({ ...base, msg_type: 36 }))!.mapped).toMatchObject({ kind: "sticker", text: "[Sticker]" });
  });

  test("voice, location, contact and link", () => {
    expect(parsePinTopic(topic({ ...base, msg_type: 31 }))!.mapped).toEqual({ kind: "voice" });
    expect(parsePinTopic(topic({ ...base, msg_type: 43, title: "Hồ Gươm" }))!.mapped).toEqual({ kind: "location", text: "Hồ Gươm" });
    expect(parsePinTopic(topic({ ...base, msg_type: 43 }))!.mapped.text).toBe("[Vị trí]");
    const card = { ...base, msg_type: 38, title: "Minh", href: "https://zalo.me/9", extra: { action: "recommened.user" } };
    expect(parsePinTopic(topic(card))!.mapped.kind).toBe("contact");
    const link = { ...card, title: "Bài báo", extra: JSON.stringify({ action: "recommened.link" }) };
    expect(parsePinTopic(topic(link))!.mapped).toEqual({
      kind: "link",
      text: "Bài báo\nhttps://zalo.me/9",
      media: { url: "https://zalo.me/9", thumbnail: undefined },
    });
    expect(parsePinTopic(topic({ ...base, msg_type: 40, title: "x", href: "x" }))!.mapped.kind).toBe("link");
  });

  test("unknown message types still show the title", () => {
    expect(parsePinTopic(topic({ ...base, msg_type: 9999, title: "thẻ" }))!.mapped).toEqual({ kind: "unsupported", text: "thẻ" });
    expect(parsePinTopic(topic({ ...base, msg_type: "webchat", title: "t" }))!.mapped.kind).toBe("unsupported");
  });

  test("msg_type may arrive as a string", () => {
    expect(parsePinTopic(topic({ ...base, msg_type: "1", title: "a" }))!.mapped.kind).toBe("text");
  });

  test("a '0' id is no id; without any id the topic is not a message pin", () => {
    const pin = parsePinTopic(topic({ ...base, global_msg_id: "0", msg_type: 1, title: "a" }))!;
    expect(pin.msgId).toBe("");
    expect(pin.cliMsgId).toBe("1760000000111");
    expect(parsePinTopic(topic({ client_msg_id: "0", global_msg_id: "0", title: "note" }))).toBeUndefined();
    expect(parsePinTopic(topic({ title: "note" }))).toBeUndefined();
  });

  test("notes and polls are not pins even if their params look alike", () => {
    expect(parsePinTopic(topic({ ...base, title: "ghi chú" }, { type: 0 }))).toBeUndefined();
    expect(parsePinTopic(topic({ ...base, title: "poll" }, { type: 3 }))).toBeUndefined();
    expect(parsePinTopic(topic({ ...base, msg_type: 1 }, { type: "2" }))).toBeDefined();
  });

  test("garbage in, nothing out", () => {
    for (const raw of [undefined, null, 5, "", "not json", "[]", [], {}, { type: 2 }, { type: 2, params: "{bad" }]) {
      expect(parsePinTopic(raw)).toBeUndefined();
    }
  });

  test("missing createTime gives pinnedAt 0, seconds are tolerated", () => {
    expect(parsePinTopic(topic({ ...base, msg_type: 1 }, { createTime: undefined }))!.pinnedAt).toBe(0);
    expect(parsePinTopic(topic({ ...base, msg_type: 1 }, { createTime: 1_760_000_000 }))!.pinnedAt).toBe(1_760_000_000_000);
  });
});

describe("parsePinTopics", () => {
  const at = (n: number, createTime: number, over: Record<string, unknown> = {}) =>
    topic({ ...base, global_msg_id: String(n), client_msg_id: String(n), msg_type: 1, title: `m${n}` }, { id: `t${n}`, createTime, ...over });

  test("most recently pinned first, skipping non-message topics", () => {
    const pins = parsePinTopics([at(1, 1000), at(2, 3000), at(3, 2000), at(4, 9000, { type: 0 }), "junk"]);
    expect(pins.map((p) => p.id)).toEqual(["t2", "t3", "t1"]);
  });

  test("equal times keep Zalo's order", () => {
    const pins = parsePinTopics([at(1, 5000), at(2, 5000), at(3, 5000)]);
    expect(pins.map((p) => p.id)).toEqual(["t1", "t2", "t3"]);
  });

  test("empty board", () => {
    expect(parsePinTopics([])).toEqual([]);
  });
});

describe("pinToMessage", () => {
  const pin = parsePinTopic(topic({ ...base, msg_type: 1, title: "ghim" }))!;

  test("uses the global msgId (the id messages.list uses) and the pin time", () => {
    const m = pinToMessage(pin, "g:42", "9999");
    expect(m).toMatchObject({
      id: "7777000111",
      chatId: "g:42",
      senderId: "2222",
      senderName: "Lan",
      fromMe: false,
      timestamp: 1_760_000_000_000,
      kind: "text",
      text: "ghim",
    });
  });

  test("sender '0' is the account owner", () => {
    const own = parsePinTopic(topic({ ...base, senderUid: "0", msg_type: 1, title: "x" }))!;
    expect(pinToMessage(own, "u:5", "9999")).toMatchObject({ senderId: "9999", fromMe: true });
    expect(pinToMessage({ ...pin, senderUid: "9999" }, "u:5", "9999").fromMe).toBe(true);
  });

  test("without a global id the client id is the id; an unknown pin time falls back to now", () => {
    const p = parsePinTopic(topic({ ...base, global_msg_id: "0", msg_type: 1 }, { createTime: 0 }))!;
    const m = pinToMessage(p, "u:5", "9999", 123);
    expect(m.id).toBe("1760000000111");
    expect(m.timestamp).toBe(123);
  });

  test("unknown sender is not 'me'", () => {
    const p = parsePinTopic(topic({ client_msg_id: "5", msg_type: 1, title: "x" }))!;
    expect(pinToMessage(p, "u:5", "").fromMe).toBe(false);
  });
});
