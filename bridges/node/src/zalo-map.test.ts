import { describe, expect, test } from "bun:test";
import {
  albumOf,
  albumTimestamps,
  callText,
  fixMentionText,
  formatCallDuration,
  mapContent,
  parsePinTopic,
  parsePinTopics,
  pinToMessage,
  recallEvent,
  renderCustomMsg,
} from "./zalo-map.ts";

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

  test("the client id (send time in ms) dates the message when it is before the pin", () => {
    const p = parsePinTopic(topic({ ...base, client_msg_id: "1759000000000", msg_type: 1 }))!;
    expect(pinToMessage(p, "g:42", "9999").timestamp).toBe(1_759_000_000_000);
  });

  test("unknown sender is not 'me'", () => {
    const p = parsePinTopic(topic({ client_msg_id: "5", msg_type: 1, title: "x" }))!;
    expect(pinToMessage(p, "u:5", "").fromMe).toBe(false);
  });
});

// ------------------------------------------------------------------ real message shapes (ids and names replaced)

/** A call log as Zalo delivers it: chat.recommended, title "sendBubbleMessage", the facts in the params JSON string. */
function callLog(action: string, params: Record<string, unknown>) {
  return {
    title: "sendBubbleMessage",
    description: "Cuộc gọi",
    href: "",
    thumb: "",
    childnumber: 0,
    action,
    params: JSON.stringify(params),
  };
}

const call = (action: string, params: Record<string, unknown>, fromMe: boolean) => mapContent("chat.recommended", callLog(action, params), { fromMe });

describe("call logs", () => {
  test("duration reads like Zalo PC: hours, minutes, seconds, zero units left out", () => {
    expect(formatCallDuration(125)).toBe("2 phút 5 giây");
    expect(formatCallDuration(45)).toBe("45 giây");
    expect(formatCallDuration(120)).toBe("2 phút");
    expect(formatCallDuration(5650)).toBe("1 giờ 34 phút 10 giây"); // longest call in the real store
    expect(formatCallDuration(3600)).toBe("1 giờ");
    expect(formatCallDuration(0)).toBe("");
    expect(formatCallDuration(NaN)).toBe("");
  });

  test("a connected voice call: the side comes from who sent the log when there is no isCaller (older logs)", () => {
    const params = { isEnableCallback: 1, reason: 0, calltype: 0, duration: 125 };
    expect(call("recommened.calltime", params, false)).toEqual({ kind: "call", text: "Cuộc gọi thoại đến · 2 phút 5 giây" });
    expect(call("recommened.calltime", params, true)).toEqual({ kind: "call", text: "Cuộc gọi thoại đi · 2 phút 5 giây" });
  });

  test("a connected video call carrying isCaller (newer logs): isCaller decides, not who sent it", () => {
    const params = { callId: 202347544, duration: 179, isCaller: 0, isEnableCallback: 1, calltype: 1 };
    expect(call("recommened.calltime", params, false).text).toBe("Cuộc gọi video đến · 2 phút 59 giây");
    expect(call("recommened.calltime", { ...params, isCaller: 1 }, true).text).toBe("Cuộc gọi video đi · 2 phút 59 giây");
    expect(call("recommened.calltime", { ...params, isCaller: 1 }, false).text).toBe("Cuộc gọi video đi · 2 phút 59 giây");
  });

  test("a call that lasted a second", () => {
    expect(call("recommened.calltime", { reason: 0, calltype: 0, duration: 1 }, false).text).toBe("Cuộc gọi thoại đến · 1 giây");
  });

  test("an incoming call that was missed or rejected, voice and video", () => {
    const miss = (reason: number, calltype = 0) => call("recommened.misscall", { isEnableCallback: 1, reason, calltype, duration: 0 }, false).text;
    for (const reason of [0, 1, 2, 4, 5]) expect(miss(reason)).toBe("Bạn bị nhỡ cuộc gọi thoại");
    expect(miss(2, 1)).toBe("Bạn bị nhỡ cuộc gọi video");
    expect(miss(3)).toBe("Bạn đã từ chối cuộc gọi thoại");
    expect(miss(3, 1)).toBe("Bạn đã từ chối cuộc gọi video");
  });

  test("an outgoing call that did not connect says why", () => {
    const out = (reason: number, calltype = 0) => call("recommened.misscall", { isEnableCallback: 1, reason, calltype, duration: 0 }, true).text;
    expect(out(2)).toBe("Cuộc gọi thoại đi · Không trả lời");
    expect(out(0)).toBe("Cuộc gọi thoại đi · Không trả lời");
    expect(out(2, 1)).toBe("Cuộc gọi video đi · Không trả lời");
    expect(out(1)).toBe("Cuộc gọi thoại đi · Người nhận bận");
    expect(out(3)).toBe("Người nhận từ chối cuộc gọi thoại");
    expect(out(4)).toBe("Cuộc gọi thoại bị huỷ");
    expect(out(5, 1)).toBe("Cuộc gọi video bị huỷ");
  });

  test("a group call log keeps Zalo's own wording, and the internal bubble title never shows", () => {
    const groupCall = {
      title: "sendGroupCallMessage",
      description: "Cuộc gọi nhóm",
      href: "",
      thumb: "",
      childnumber: 0,
      action: "recommened.groupcall",
      params: '{"callId":1779713426,"data":"{}","groupId":587848316,"members":[{"userId":111},{"userId":222}],"hostId":111,"type":0}',
    };
    expect(mapContent("chat.recommended", groupCall)).toEqual({ kind: "call", text: "Cuộc gọi nhóm" });
    // an unknown bubble action keeps its description but not the "send…" title
    const m = mapContent("chat.recommended", { ...groupCall, action: "recommened.something", description: "Nội dung" });
    expect(m.kind).toBe("link");
    expect(m.text).toBe("Nội dung");
  });

  test("callText tolerates missing or odd params", () => {
    expect(callText("recommened.calltime", {}, false)).toBe("Cuộc gọi thoại đến");
    expect(callText("recommened.misscall", { reason: "3", isCaller: "1" }, false)).toBe("Người nhận từ chối cuộc gọi thoại");
    expect(callText("recommened.calling", {}, false)).toBe("Cuộc gọi thoại đi");
    expect(callText("recommened.receivecall", { calltype: 1 }, true)).toBe("Cuộc gọi video đến");
  });

  test("params that are not valid JSON still give a call, not a crash", () => {
    const m = mapContent("chat.recommended", { ...callLog("recommened.misscall", {}), params: "{oops" }, { fromMe: false });
    expect(m).toEqual({ kind: "call", text: "Bạn bị nhỡ cuộc gọi thoại" });
  });
});

describe("cards and notices", () => {
  test("an official account's card is a contact, and its JSON description stays out of the text", () => {
    const vip = {
      title: "Trustmove",
      description: '{"qrCodeUrl":"https:\\/\\/page-photo-qr.zdn.vn\\/1\\/a.jpg","gUid":"ABC123"}',
      href: "https://zaloapp.com",
      thumb: "https://s160-ava-talk.zadn.vn/a.jpg",
      params: "ABC123",
      action: "recommened.vip",
    };
    expect(mapContent("chat.recommended", vip)).toEqual({
      kind: "contact",
      text: "Trustmove",
      media: { thumbnail: "https://s160-ava-talk.zadn.vn/a.jpg", url: "https://zaloapp.com" },
    });
  });

  test("a shared link: a URL already inside the title or description is not repeated", () => {
    const url = "https://tracking.example.vn/vi/AbCd";
    const m = mapContent("chat.recommended", {
      title: `Tài xế: NGUYỄN VĂN A\nPhương tiện: EVO, 29Y7-000.00\nĐường dẫn: ${url}`,
      description: url,
      href: url,
      thumb: "https://photo-link-talk.zadn.vn/x",
      action: "recommened.link",
    });
    expect(m.kind).toBe("link");
    expect(m.text).toBe(`Tài xế: NGUYỄN VĂN A\nPhương tiện: EVO, 29Y7-000.00\nĐường dẫn: ${url}`);
    expect(m.media?.url).toBe(url);
  });

  test("a link whose title is the URL keeps the page title below it", () => {
    const m = mapContent("chat.recommended", { title: "https://s.example.vn/4B0", description: "Mua xe scooter cho bé", href: "https://s.example.vn/4B0", action: "recommened.link" });
    expect(m.text).toBe("https://s.example.vn/4B0\nMua xe scooter cho bé");
  });

  test("chat.list.action: a news digest is a link with the articles listed", () => {
    const cards = [
      { title: "🎧 Bài hát mới tuần này", description: "Nghe ngay trên nền tảng nhạc", href: "https://music.example.vn/a", thumb: "https://zalo-article-photo.zadn.vn/1", params: "", action: "", type: "l.a.header.only" },
      { title: "🎧 Ca khúc thứ hai", description: "", href: "https://music.example.vn/b", thumb: "https://zalo-article-photo.zadn.vn/2", type: "l.a.child.full" },
      { title: "🎧 Album thứ ba", description: "", href: "https://music.example.vn/c", thumb: "https://zalo-article-photo.zadn.vn/3", type: "l.a.child.full" },
    ];
    expect(mapContent("chat.list.action", cards)).toEqual({
      kind: "link",
      text: "🎧 Bài hát mới tuần này\nNghe ngay trên nền tảng nhạc\n• 🎧 Ca khúc thứ hai\n• 🎧 Album thứ ba",
      media: { url: "https://music.example.vn/a", thumbnail: "https://zalo-article-photo.zadn.vn/1" },
    });
  });

  test("chat.list.action: the login notice is text, its button is not listed", () => {
    const cards = [
      { title: "Đăng nhập Zalo trên máy tính thành công", description: "Bạn đã đăng nhập Zalo Web trên Mac OS (Chrome)\n- Bằng: Mã QR\n- Tại: Hà Nội, Việt Nam\n", href: "", thumb: "https://stc-sp.zadn.vn/mac.png", action: "action.open.logindevices", type: "l.a.header.only" },
      { title: "Mở lịch sử đăng nhập", description: "", href: "", thumb: "", action: "action.open.logindevices", type: "l.a.child.full" },
    ];
    const m = mapContent("chat.list.action", cards);
    expect(m.kind).toBe("text");
    expect(m.text).toBe("Đăng nhập Zalo trên máy tính thành công\nBạn đã đăng nhập Zalo Web trên Mac OS (Chrome)\n- Bằng: Mã QR\n- Tại: Hà Nội, Việt Nam");
  });

  test("chat.list.action with nothing in it stays unsupported", () => {
    expect(mapContent("chat.list.action", [])).toEqual({ kind: "unsupported" });
  });

  test("renderCustomMsg fills %N$s from highLightsV2 in order", () => {
    expect(renderCustomMsg({ msg: { vi: "%1$s tạo nhắc hẹn mới %2$s – %3$s", en: "x" }, highLightsV2: [{ uid: 1, dpn: "Hà" }, { uid: 0, dpn: "Nộp tờ khai" }, { uid: 0, dpn: "10:00", type: 1, ts: 1737946800000 }] })).toEqual({
      text: "Hà tạo nhắc hẹn mới Nộp tờ khai – 10:00",
      templated: true,
    });
    expect(renderCustomMsg({ msg: { en: "Only English" } })).toEqual({ text: "Only English", templated: false });
    expect(renderCustomMsg({ msg: { vi: "%1$s %2$s xin chào" }, highLightsV2: [{ dpn: "Lan" }] })?.text).toBe("Lan xin chào"); // a missing value leaves no gap
    expect(renderCustomMsg("not json")).toBeUndefined();
    expect(renderCustomMsg({})).toBeUndefined();
  });

  const item = { zinstantdata_id: "templateId=11845", data_type: 2, version: 1, data_url: "https://zinst-stc.zadn.vn/static/3m/11845/x" };

  test("chat.webcontent: a bank account card reads as Zalo's own placeholder", () => {
    const content = {
      title: "",
      description: "",
      href: "",
      thumb: "",
      action: "zinstant.bankcard",
      params: JSON.stringify({ canUndo: 1, item, layoutType: 2, type: 0, customMsg: { msg: { en: "[Bank account]", vi: "[Tài khoản ngân hàng]" }, highLightsV2: [] } }),
    };
    expect(mapContent("chat.webcontent", content)).toEqual({ kind: "text", text: "[Tài khoản ngân hàng]" });
  });

  test("chat.webcontent: reminders, polls and birthdays are notices naming the people", () => {
    const notice = (vi: string, highLightsV2: unknown[]) => mapContent("chat.webcontent", { action: "", params: JSON.stringify({ item, customMsg: { msg: { vi }, highLightsV2 } }) });
    expect(notice("%1$s sẽ tham gia %2$s", [{ uid: 11, dpn: "Phúc" }, { uid: 0, dpn: "Họp tuần", type: 0 }])).toEqual({ kind: "system", text: "Phúc sẽ tham gia Họp tuần" });
    expect(notice("%1$s xóa nhắc hẹn %2$s.", [{ uid: 12, dpn: "Hà" }, { uid: 0, dpn: "Họp tuần" }])).toEqual({ kind: "system", text: "Hà xóa nhắc hẹn Họp tuần." });
    expect(notice("Hôm nay (08/08) là sinh nhật của %1$s, hãy gửi một lời chúc!", [{ uid: 13, dpn: "Trường", ignoreNickname: 0, type: 0 }]).text).toBe("Hôm nay (08/08) là sinh nhật của Trường, hãy gửi một lời chúc!");
  });

  test("chat.webcontent: an official account's article is a link to the post", () => {
    const bundle = { broadcastId: "1", post: { thumbnail: "https://zalo-article-photo.zadn.vn/9e37#427649456", description: "Hướng dẫn khách hàng.", title: "Điện mặt trời mái nhà", url: "https://post.oa.zalo.me/d?id=fc27", postId: "fc27" }, items: [] };
    const m = mapContent("chat.webcontent", {
      action: "",
      params: JSON.stringify({ item: { ...item, bundle_data: JSON.stringify(bundle) }, customMsg: { msg: { vi: "Điện mặt trời mái nhà" }, highLightsV2: [] } }),
    });
    expect(m).toEqual({
      kind: "link",
      text: "Điện mặt trời mái nhà\nHướng dẫn khách hàng.\nhttps://post.oa.zalo.me/d?id=fc27",
      media: { url: "https://post.oa.zalo.me/d?id=fc27", thumbnail: "https://zalo-article-photo.zadn.vn/9e37" },
    });
  });

  test("chat.webcontent.v2 keeps its fallback text; chat.delete is a system line naming who deleted", () => {
    const v2 = mapContent("chat.webcontent.v2", {
      zinstant_msg: JSON.stringify({ forwardType: 0, type: 27, customMsg: { msg: { vi: " THÔNG BÁO THANH TOÁN\nHóa đơn số #1 đến hạn", en: "x" }, highLightsV2: [] } }),
      zinstant_data: "{}",
      action: "",
    });
    expect(v2).toEqual({ kind: "text", text: "THÔNG BÁO THANH TOÁN\nHóa đơn số #1 đến hạn" });
    const del = mapContent("chat.delete", {
      title: "",
      params: JSON.stringify({ total_item_in_group: 5, original_type: 3, customMsg: { msg: { my: "%1$s deleted this message", en: "%1$s deleted this message", vi: "%1$s đã xóa tin nhắn này" }, highLightsV2: [{ uid: 106, dpn: "Sơn" }] } }),
    });
    expect(del).toEqual({ kind: "system", text: "Sơn đã xóa tin nhắn này" });
    expect(mapContent("chat.delete", { params: "{}" })).toEqual({ kind: "system", text: "Một tin nhắn đã bị xóa" });
  });

  test("an unknown type with a fallback is shown, one without stays unsupported", () => {
    expect(mapContent("chat.todo", { params: JSON.stringify({ customMsg: { msg: { vi: "%1$s giao việc cho %2$s" }, highLightsV2: [{ dpn: "A" }, { dpn: "B" }] } }) })).toEqual({ kind: "system", text: "A giao việc cho B" });
    expect(mapContent("chat.mystery", { title: "Tiêu đề" })).toEqual({ kind: "unsupported", text: "Tiêu đề" });
  });
});

describe("albums", () => {
  const photo = (params: Record<string, unknown>) => ({ title: "", href: "https://p/1.jxl", thumb: "https://p/1.jxl", params: JSON.stringify(params) });

  test("photos sent together share group_layout_id; the index is id_in_group", () => {
    const p = { group_layout_id: 1791865990221, id_in_group: 1, total_item_in_group: 3, is_group_layout: 1, width: 1080, height: 1920 };
    expect(albumOf("chat.photo", photo(p))).toEqual({ groupId: "1791865990221", index: 1, total: 3 });
    expect(albumOf("chat.video.msg", photo(p))?.groupId).toBe("1791865990221"); // photos and videos can share an album
  });

  test("a single photo is no album: no layout id, a zero layout id, or a group of one", () => {
    expect(albumOf("chat.photo", photo({ width: 10, height: 10 }))).toBeUndefined();
    expect(albumOf("chat.photo", photo({ group_layout_id: 0, id_in_group: 0, total_item_in_group: 0, is_group_layout: 0 }))).toBeUndefined();
    expect(albumOf("chat.photo", photo({ group_layout_id: 1791865990221, id_in_group: 0, total_item_in_group: 1, is_group_layout: 1 }))).toBeUndefined();
    expect(albumOf("chat.photo", photo({ group_layout_id: 1791865990221, id_in_group: 0, total_item_in_group: 2, is_group_layout: 0 }))).toBeUndefined();
  });

  test("other types and unreadable content never form an album", () => {
    expect(albumOf("webchat", "hi")).toBeUndefined();
    expect(albumOf("chat.photo", { params: "{oops" })).toBeUndefined();
    expect(albumOf("chat.sticker", photo({ group_layout_id: 5, total_item_in_group: 2 }))).toBeUndefined();
  });

  const member = (id: string, dt: number, cliDt: number, index: number) => ({ id, ts: 1_791_000_000_000 + dt, cli: 1_791_000_000_000 + cliDt, index });

  test("a real scrambled album (6 photos uploaded in parallel) is put back in the order they were picked", () => {
    // id_in_group by server time was 4,2,5,3,1,0; cliMsgId follows the pick order 0..5
    const album = [member("a4", 0, 40, 4), member("a2", 95, 20, 2), member("a5", 102, 50, 5), member("a3", 107, 30, 3), member("a1", 120, 10, 1), member("a0", 132, 0, 0)];
    const changes = albumTimestamps(album);
    const at = (id: string) => changes.get(id) ?? album.find((m) => m.id === id)!.ts;
    const order = album.map((m) => m.id).sort((x, y) => at(x) - at(y));
    expect(order).toEqual(["a0", "a1", "a2", "a3", "a4", "a5"]);
    // the album keeps the same set of timestamps
    expect(album.map((m) => at(m.id)).sort((x, y) => x - y)).toEqual(album.map((m) => m.ts).sort((x, y) => x - y));
  });

  test("an album already in order is left alone, and a second pass changes nothing", () => {
    const ordered = [member("a0", 0, 0, 0), member("a1", 130, 27, 1), member("a2", 215, 44, 2)];
    expect(albumTimestamps(ordered).size).toBe(0);
    const scrambled = [member("a1", 0, 27, 1), member("a0", 50, 0, 0)];
    const first = albumTimestamps(scrambled);
    const settled = scrambled.map((m) => ({ ...m, ts: first.get(m.id) ?? m.ts }));
    expect(albumTimestamps(settled).size).toBe(0);
  });

  test("members stamped in the same millisecond are spread 1 ms apart so no sort can swap them", () => {
    const tied = [member("b", 0, 20, 1), member("a", 0, 10, 0)];
    const changes = albumTimestamps(tied);
    const at = (id: string) => changes.get(id) ?? tied.find((m) => m.id === id)!.ts;
    expect(at("a")).toBeLessThan(at("b"));
    expect(at("b") - at("a")).toBe(1);
  });

  test("when the client clock is unknown the index decides", () => {
    const noCli = [member("x1", 0, 0, 1), member("x0", 40, 0, 0)].map((m) => ({ ...m, cli: 0 }));
    const changes = albumTimestamps(noCli);
    expect(changes.get("x0")).toBe(noCli[0]!.ts);
    expect(changes.get("x1")).toBe(noCli[1]!.ts);
  });

  test("an album spread over more than 15 minutes (a slow video upload) is left as Zalo stamped it", () => {
    const slow = [member("v2", 0, 20, 1), member("v1", 40 * 60_000, 0, 0)];
    expect(albumTimestamps(slow).size).toBe(0);
  });

  test("one message is never re-timed", () => {
    expect(albumTimestamps([member("solo", 0, 0, 0)]).size).toBe(0);
  });
});

describe("recall events and mentions", () => {
  test("an object chat.undo names the message to recall; an empty one is the recalled message itself", () => {
    expect(recallEvent("chat.undo", { globalMsgId: 8447660817112, cliMsgId: 1793703907063, deleteMsg: 0, srcId: "1", destId: "2" })).toEqual({ msgId: "8447660817112", cliMsgId: "1793703907063" });
    expect(recallEvent("chat.undo", "")).toBeUndefined();
    expect(recallEvent("chat.undo", {})).toBeUndefined();
    expect(recallEvent("webchat", { globalMsgId: 1 })).toBeUndefined();
  });

  const names: Record<string, string> = { "219184827": "Phúc Lê", "145680795": "Hue Nguyen" };
  const nameOf = (uid: string) => names[uid];

  test("a span that holds the typed name is left as it is (every real message)", () => {
    const text = "Dạ hi cả nhà @All 👋 em giới thiệu bạn @Ngọc Trần nhé";
    const at = (s: string) => text.indexOf(s);
    expect(fixMentionText(text, [{ uid: "-1", pos: at("@All"), len: 4, type: 1 }, { uid: "5", pos: at("@Ngọc Trần"), len: 10, type: 0 }], nameOf)).toBe(text);
  });

  test("a span that holds an id or a phone number becomes @Name, from the end so offsets stay valid", () => {
    const text = "@219184827 ơi và @145680795 xem giúp";
    expect(
      fixMentionText(text, [{ uid: "219184827", pos: 0, len: 10, type: 0 }, { uid: "145680795", pos: 17, len: 10, type: 0 }], nameOf),
    ).toBe("@Phúc Lê ơi và @Hue Nguyen xem giúp");
    expect(fixMentionText("@-1 họp", [{ uid: "-1", pos: 0, len: 3, type: 1 }], nameOf)).toBe("@All họp");
    expect(fixMentionText("0912345678 ơi", [{ uid: "219184827", pos: 0, len: 10, type: 0 }], nameOf)).toBe("@Phúc Lê ơi");
  });

  test("unknown people, bad spans and non-arrays change nothing", () => {
    const text = "@219184827 ơi";
    expect(fixMentionText(text, [{ uid: "999999999", pos: 0, len: 10 }], nameOf)).toBe(text); // no name known
    expect(fixMentionText(text, [{ uid: "219184827", pos: 5, len: 99 }], nameOf)).toBe(text); // out of range
    expect(fixMentionText(text, [{ uid: "219184827", pos: -1, len: 3 }], nameOf)).toBe(text);
    expect(fixMentionText(text, undefined, nameOf)).toBe(text);
    expect(fixMentionText("", [{ uid: "1", pos: 0, len: 1 }], nameOf)).toBe("");
  });

  test("overlapping spans: the later one is skipped, the text is not corrupted", () => {
    const text = "@219184827 x";
    const out = fixMentionText(text, [{ uid: "219184827", pos: 0, len: 10 }, { uid: "145680795", pos: 4, len: 6 }], nameOf);
    expect(out).toBe("@Phúc Lê x");
  });
});
