import { describe, expect, test } from "bun:test";
import { mapContent } from "../zalo-map.ts";
import { syncToZaloMessage, SyncType, UNDO_TYPE, type SyncAttach, type SyncMessage } from "./convert.ts";

const attach = (a: Partial<SyncAttach>): SyncAttach => ({
  type: "",
  extInfo: "",
  action: "",
  params: "",
  title: "",
  href: "",
  thumb: "",
  description: "",
  catId: 0,
  id: 0,
  childNumber: 0,
  remains: "",
  zinstantData: "",
  zinstantMsg: "",
  ...a,
});

const msg = (m: Partial<SyncMessage>): SyncMessage => ({
  senderId: "peerGid",
  clientId: 1730000000123,
  msgStatus: 4,
  msgType: SyncType.Text,
  timestamp: 1730000000456,
  globalId: "7000000000000000001",
  content: "",
  meta: { attachsList: [], mentionsList: [] },
  ...m,
});

const peer = { isGroup: false, threadId: "111", senderUid: "111", fromMe: false, selfId: "999" };

describe("syncToZaloMessage", () => {
  test("text from the peer", () => {
    const d = syncToZaloMessage(msg({ content: "xin chào" }), peer)!;
    expect(d).toMatchObject({ msgId: "7000000000000000001", cliMsgId: "1730000000123", msgType: "webchat", uidFrom: "111", idTo: "999", ts: "1730000000456", content: "xin chào" });
    expect(mapContent(d.msgType as string, d.content)).toEqual({ kind: "text", text: "xin chào" });
  });

  test("own message in a 1:1 chat", () => {
    const d = syncToZaloMessage(msg({ senderId: "selfGid", content: "ok" }), { ...peer, senderUid: "999", fromMe: true })!;
    expect(d).toMatchObject({ uidFrom: "999", idTo: "111" });
  });

  test("rich text keeps its text", () => {
    const d = syncToZaloMessage(msg({ content: "đậm", meta: { attachsList: [attach({ action: "rtf", params: '{"styles":[]}' })] } }), peer)!;
    expect(mapContent(d.msgType as string, d.content)).toEqual({ kind: "text", text: "đậm" });
  });

  test("photo", () => {
    const a = attach({ href: "https://f.zdn.vn/a.jpg", thumb: "https://f.zdn.vn/t.jpg", params: JSON.stringify({ hd: "https://f.zdn.vn/hd.jpg", width: 800, height: 600 }) });
    const d = syncToZaloMessage(msg({ msgType: SyncType.Photo_V2, meta: { attachsList: [a] } }), peer)!;
    expect(d.msgType).toBe("chat.photo");
    const m = mapContent(d.msgType as string, d.content);
    expect(m.kind).toBe("image");
    expect(m.media).toMatchObject({ url: "https://f.zdn.vn/hd.jpg", thumbnail: "https://f.zdn.vn/t.jpg", width: 800, height: 600 });
  });

  test("photo caption from content", () => {
    const d = syncToZaloMessage(msg({ msgType: SyncType.Photo, content: "đẹp quá", meta: { attachsList: [attach({ href: "https://x/a.jpg" })] } }), peer)!;
    expect(mapContent(d.msgType as string, d.content).text).toBe("đẹp quá");
  });

  test("video", () => {
    const a = attach({ href: "https://v/a.mp4", thumb: "https://v/t.jpg", params: JSON.stringify({ duration: 12000, video_width: 720, video_height: 1280 }) });
    const d = syncToZaloMessage(msg({ msgType: SyncType.Video, meta: { attachsList: [a] } }), peer)!;
    const m = mapContent(d.msgType as string, d.content);
    expect(m.kind).toBe("video");
    expect(m.media).toMatchObject({ url: "https://v/a.mp4", duration: 12, width: 720, height: 1280 });
  });

  test("sticker", () => {
    const d = syncToZaloMessage(msg({ msgType: SyncType.Sticker, meta: { attachsList: [attach({ id: 21000, catId: 10 })] } }), peer)!;
    expect(mapContent(d.msgType as string, d.content)).toEqual({ kind: "sticker", sticker: { id: 21000, cateId: 10 } });
  });

  test("file gets its extension", () => {
    const a = attach({ title: "bao-cao", href: "https://f/x", params: JSON.stringify({ fileExt: "pdf", fileSize: 2048 }) });
    const d = syncToZaloMessage(msg({ msgType: SyncType.File, meta: { attachsList: [a] } }), peer)!;
    const m = mapContent(d.msgType as string, d.content);
    expect(m).toMatchObject({ kind: "file", text: "bao-cao.pdf", media: { fileName: "bao-cao.pdf", size: 2048, mime: "application/pdf" } });
  });

  test("voice, location, contact, gif", () => {
    const voice = syncToZaloMessage(msg({ msgType: SyncType.Voice, meta: { attachsList: [attach({ href: "https://a/v.aac", params: '{"duration":3000}' })] } }), peer)!;
    expect(mapContent(voice.msgType as string, voice.content)).toMatchObject({ kind: "voice", media: { duration: 3 } });
    const loc = syncToZaloMessage(msg({ msgType: SyncType.Location, meta: { attachsList: [attach({ params: '{"latitude":10.7,"longitude":106.6}' })] } }), peer)!;
    expect(mapContent(loc.msgType as string, loc.content)).toMatchObject({ kind: "location", text: "10.7, 106.6" });
    const card = syncToZaloMessage(msg({ msgType: SyncType.Contact, meta: { attachsList: [attach({ action: "recommened.user", title: "Ann" })] } }), peer)!;
    expect(mapContent(card.msgType as string, card.content)).toMatchObject({ kind: "contact", text: "Ann" });
    const link = syncToZaloMessage(msg({ msgType: SyncType.Contact, meta: { attachsList: [attach({ action: "recommened.link", title: "Báo", href: "https://vnexpress.net" })] } }), peer)!;
    expect(mapContent(link.msgType as string, link.content)).toMatchObject({ kind: "link" });
    const gif = syncToZaloMessage(msg({ msgType: SyncType.Gift, meta: { attachsList: [attach({ href: "https://g/a.gif" })] } }), peer)!;
    expect(mapContent(gif.msgType as string, gif.content)).toMatchObject({ kind: "gif", media: { url: "https://g/a.gif" } });
  });

  test("quote is passed through", () => {
    const quote = { attach: "", ownerId: "x", ts: 1, fromD: "Ann", cliMsgId: 5, globalMsgId: "6000", msg: "trước đó", cliMsgType: 1, ttl: 0, partialQuote: "" };
    const d = syncToZaloMessage(msg({ content: "trả lời", meta: { attachsList: [], quote } }), peer)!;
    expect(d.quote).toMatchObject({ globalMsgId: "6000", msg: "trước đó", fromD: "Ann" });
  });

  test("recall is flagged, unsupported kinds are dropped", () => {
    expect(syncToZaloMessage(msg({ msgType: SyncType.Undo }), peer)).toMatchObject({ msgType: UNDO_TYPE, recalled: true, content: "" });
    for (const t of [SyncType.Poll, SyncType.ECard, SyncType.ChatInfo, SyncType.ChatDelete, SyncType.VideoLive, 999]) {
      expect(syncToZaloMessage(msg({ msgType: t }), peer)).toBeUndefined();
    }
  });

  test("message without any id is dropped; missing global id keeps the client id", () => {
    expect(syncToZaloMessage(msg({ globalId: "0", clientId: 0 }), peer)).toBeUndefined();
    expect(syncToZaloMessage(msg({ globalId: "0", content: "a" }), peer)).toMatchObject({ msgId: "", cliMsgId: "1730000000123" });
  });
});
