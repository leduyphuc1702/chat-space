import { describe, expect, test } from "bun:test";
import { proto } from "baileys";
import { isStorable, mapMessage, pinEvent, type MapCtx } from "./whatsapp-map.ts";
import { plainMsg } from "./whatsapp-store.ts";

const ctx: MapCtx = {
  selfId: "84900@s.whatsapp.net",
  selfName: "Me",
  canon: (j) => j ?? "",
  nameOf: (j) => (j === "84901@s.whatsapp.net" ? "Linh" : undefined),
  groupSize: () => undefined,
};

const TS = 1_800_000_000; // s
const target = { remoteJid: "g@g.us", fromMe: false, id: "TARGET", participant: "84901@s.whatsapp.net" };

const pinMsg = (type: number, opts: { secs?: number; fromMe?: boolean; wrap?: boolean } = {}) => {
  const inner: any = { pinInChatMessage: { key: target, type, senderTimestampMs: (TS + 5) * 1000 } };
  if (opts.secs) inner.messageContextInfo = { messageAddOnDurationInSecs: opts.secs };
  return {
    key: { remoteJid: "g@g.us", fromMe: !!opts.fromMe, id: "PIN1", participant: opts.fromMe ? undefined : "84901@s.whatsapp.net" },
    messageTimestamp: TS + 6,
    message: opts.wrap ? { deviceSentMessage: { destinationJid: "g@g.us", message: inner } } : inner,
  };
};

describe("pinEvent", () => {
  test("a pin takes its time from the sender and its duration from the context info", () => {
    expect(pinEvent(pinMsg(1, { secs: 7 * 86400 }))).toEqual({
      id: "TARGET",
      pin: true,
      at: (TS + 5) * 1000,
      expiresAt: (TS + 5 + 7 * 86400) * 1000,
    });
  });

  test("24 hours and 30 days", () => {
    expect(pinEvent(pinMsg(1, { secs: 86400 }))!.expiresAt).toBe((TS + 5 + 86400) * 1000);
    expect(pinEvent(pinMsg(1, { secs: 30 * 86400 }))!.expiresAt).toBe((TS + 5 + 30 * 86400) * 1000);
  });

  test("a pin without a duration lasts 7 days", () => {
    expect(pinEvent(pinMsg(1))!.expiresAt).toBe((TS + 5 + 7 * 86400) * 1000);
  });

  test("an unpin", () => {
    expect(pinEvent(pinMsg(2))).toEqual({ id: "TARGET", pin: false, at: (TS + 5) * 1000, expiresAt: 0 });
  });

  test("protobuf longs and the device-sent wrapper of history are understood", () => {
    const m: any = pinMsg(1, { secs: 86400, fromMe: true, wrap: true });
    m.message.deviceSentMessage.message.pinInChatMessage.senderTimestampMs = { low: (TS + 5) * 1000, high: 0, toNumber: () => (TS + 5) * 1000 };
    expect(pinEvent(m)).toMatchObject({ id: "TARGET", pin: true, at: (TS + 5) * 1000 });
  });

  test("pinInChat attached to the pinned message (history sync) counts too, with its own id as default", () => {
    const raw = {
      key: { remoteJid: "g@g.us", fromMe: false, id: "TARGET" },
      messageTimestamp: TS,
      message: { conversation: "hello" },
      pinInChat: { type: 1, senderTimestampMs: (TS + 9) * 1000, messageAddOnContextInfo: { messageAddOnDurationInSecs: 86400 } },
    };
    expect(pinEvent(raw)).toEqual({ id: "TARGET", pin: true, at: (TS + 9) * 1000, expiresAt: (TS + 9 + 86400) * 1000 });
  });

  test("ordinary messages and unknown pin types are not pin events", () => {
    expect(pinEvent({ key: { id: "X" }, messageTimestamp: TS, message: { conversation: "hi" } })).toBeNull();
    expect(pinEvent(pinMsg(0))).toBeNull();
    expect(pinEvent({ key: { id: "X" }, messageTimestamp: TS })).toBeNull();
  });
});

describe("pin messages as Baileys delivers them", () => {
  // The wire form: protobuf-decoded instances (Long timestamps, numeric enums), then the plain copy kept in the store.
  const decoded = (type: number, secs: number) =>
    proto.WebMessageInfo.decode(
      proto.WebMessageInfo.encode({
        key: { remoteJid: "g@g.us", fromMe: false, id: "PIN2", participant: "84901@s.whatsapp.net" },
        messageTimestamp: TS + 6,
        message: {
          pinInChatMessage: { key: target, type, senderTimestampMs: (TS + 5) * 1000 },
          messageContextInfo: { messageAddOnDurationInSecs: secs },
        },
      }).finish(),
    );

  test("the same event comes out of the live message and of its stored copy", () => {
    const live = decoded(proto.Message.PinInChatMessage.Type.PIN_FOR_ALL, 30 * 86400);
    const want = { id: "TARGET", pin: true, at: (TS + 5) * 1000, expiresAt: (TS + 5 + 30 * 86400) * 1000 };
    expect(pinEvent(live)).toEqual(want);
    const stored = JSON.parse(JSON.stringify(plainMsg(live)));
    expect(pinEvent(stored)).toEqual(want);
    expect(mapMessage({ raw: stored }, "g@g.us", ctx)).toMatchObject({ kind: "system", text: "Linh pinned a message" });
  });

  test("unpin", () => {
    const live = decoded(proto.Message.PinInChatMessage.Type.UNPIN_FOR_ALL, 0);
    expect(pinEvent(live)).toMatchObject({ id: "TARGET", pin: false });
    expect(isStorable(live)).toBe(false);
  });
});

describe("pin messages in the chat", () => {
  test("a pin shows as a system line naming who pinned it", () => {
    const m = mapMessage({ raw: pinMsg(1, { secs: 86400 }) }, "g@g.us", ctx)!;
    expect(m).toMatchObject({ id: "PIN1", kind: "system", text: "Linh pinned a message", fromMe: false });
  });

  test("your own pin, also from the device-sent wrapper", () => {
    expect(mapMessage({ raw: pinMsg(1, { fromMe: true }) }, "g@g.us", ctx)).toMatchObject({ kind: "system", text: "You pinned a message" });
    expect(mapMessage({ raw: pinMsg(1, { fromMe: true, wrap: true }) }, "g@g.us", ctx)).toMatchObject({ kind: "system", text: "You pinned a message" });
  });

  test("an unpin leaves no message", () => {
    expect(mapMessage({ raw: pinMsg(2) }, "g@g.us", ctx)).toBeNull();
  });

  test("only a pin is worth storing", () => {
    expect(isStorable(pinMsg(1))).toBe(true);
    expect(isStorable(pinMsg(1, { wrap: true }))).toBe(true);
    expect(isStorable(pinMsg(2))).toBe(false);
    expect(isStorable(pinMsg(0))).toBe(false);
  });
});
