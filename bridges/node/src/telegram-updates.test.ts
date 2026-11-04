import { describe, expect, test } from "bun:test";
import { Api } from "telegram";
import { channelPts, commonPts, judgePts, parseSavedState } from "./telegram-updates.ts";

const big = (n: number) => BigInt(n) as never;
const userMsg = (id: number) =>
  new Api.Message({ id, peerId: new Api.PeerUser({ userId: big(7) }), date: 1, message: "hi" });
const channelMsg = (id: number) =>
  new Api.Message({ id, peerId: new Api.PeerChannel({ channelId: big(42) }), date: 1, message: "hi" });

describe("commonPts / channelPts", () => {
  test("account-wide updates report their pts step", () => {
    expect(commonPts(new Api.UpdateNewMessage({ message: userMsg(1), pts: 101, ptsCount: 1 }))).toEqual({ pts: 101, ptsCount: 1 });
    expect(commonPts(new Api.UpdateDeleteMessages({ messages: [1, 2], pts: 105, ptsCount: 2 }))).toEqual({ pts: 105, ptsCount: 2 });
    expect(
      commonPts(new Api.UpdateShortMessage({ id: 9, userId: big(7), message: "x", pts: 110, ptsCount: 1, date: 1 })),
    ).toEqual({ pts: 110, ptsCount: 1 });
  });
  test("channel updates are keyed by the channel's chat id and never count as common", () => {
    const u = new Api.UpdateNewChannelMessage({ message: channelMsg(3), pts: 50, ptsCount: 1 });
    expect(commonPts(u)).toBeUndefined();
    expect(channelPts(u)).toEqual({ chatId: "-10042", pts: 50, ptsCount: 1 });
    expect(channelPts(new Api.UpdateDeleteChannelMessages({ channelId: big(42), messages: [3], pts: 51, ptsCount: 1 }))).toEqual({
      chatId: "-10042",
      pts: 51,
      ptsCount: 1,
    });
  });
  test("updates without a pts_count (read inbox of a channel, typing, status) do not take part", () => {
    expect(commonPts(new Api.UpdateReadChannelInbox({ channelId: big(42), maxId: 3, stillUnreadCount: 0, pts: 60 }))).toBeUndefined();
    expect(channelPts(new Api.UpdateReadChannelInbox({ channelId: big(42), maxId: 3, stillUnreadCount: 0, pts: 60 }))).toBeUndefined();
    expect(commonPts(new Api.UpdateUserTyping({ userId: big(7), action: new Api.SendMessageTypingAction() }))).toBeUndefined();
    expect(commonPts(new Api.UpdatesTooLong())).toBeUndefined();
  });
});

describe("judgePts", () => {
  test("the next step applies, a repeat is a duplicate, a jump is a gap", () => {
    expect(judgePts(100, { pts: 101, ptsCount: 1 })).toBe("applied");
    expect(judgePts(100, { pts: 102, ptsCount: 2 })).toBe("applied");
    expect(judgePts(100, { pts: 100, ptsCount: 1 })).toBe("duplicate");
    expect(judgePts(100, { pts: 99, ptsCount: 0 })).toBe("duplicate");
    expect(judgePts(100, { pts: 102, ptsCount: 1 })).toBe("gap");
    expect(judgePts(100, { pts: 150, ptsCount: 3 })).toBe("gap");
  });
  test("a zero-count update at the current pts is a duplicate, past it a gap", () => {
    expect(judgePts(100, { pts: 100, ptsCount: 0 })).toBe("applied");
    expect(judgePts(100, { pts: 101, ptsCount: 0 })).toBe("gap");
  });
});

describe("parseSavedState", () => {
  test("accepts its own output and drops foreign or broken files", () => {
    const text = JSON.stringify({ selfId: "1", pts: 10, qts: 0, date: 1700000000, seq: 5, channels: { "-10042": 7, junk: 3, "-10043": -1 } });
    expect(parseSavedState(text, "1")).toEqual({ selfId: "1", pts: 10, qts: 0, date: 1700000000, seq: 5, channels: { "-10042": 7 } });
    expect(parseSavedState(text, "2")).toBeUndefined();
    expect(parseSavedState("{", "1")).toBeUndefined();
    expect(parseSavedState(JSON.stringify({ selfId: "1", pts: "x", date: 1 }), "1")).toBeUndefined();
  });
});
