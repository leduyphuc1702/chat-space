import { describe, expect, test } from "bun:test";
import { Api } from "telegram";
import { isPinNotice, mapReactions, muteInfo, muteUntilSeconds, normalizeReactionEmoji, reactionEmoticonForTelegram } from "./telegram-map.ts";

const VS16 = "\uFE0F";

describe("normalizeReactionEmoji", () => {
  test("text-presentation scalars get U+FE0F", () => {
    for (const e of ["\u2764", "\u263A", "\u2639", "\u270C", "\u261D", "\u270D", "\u2763", "\u2603", "\u{1F54A}"]) {
      expect(normalizeReactionEmoji(e)).toBe(e + VS16);
    }
  });
  test("already-selected, emoji-by-default and multi-scalar strings are untouched", () => {
    for (const e of ["\u2764" + VS16, "\u{1F44D}", "\u26A1", "\u2B50", "\u{1F525}", "\u2764\u200D\u{1F525}", "\u{1F937}\u200D\u2642", "1", "#", ""]) {
      expect(normalizeReactionEmoji(e)).toBe(e);
    }
  });
  test("is idempotent", () => {
    const once = normalizeReactionEmoji("\u2764");
    expect(normalizeReactionEmoji(once)).toBe(once);
  });
});

describe("reactionEmoticonForTelegram", () => {
  test("strips every variation selector", () => {
    expect(reactionEmoticonForTelegram("\u2764" + VS16)).toBe("\u2764");
    expect(reactionEmoticonForTelegram("\u2764" + VS16 + "\u200D\u{1F525}")).toBe("\u2764\u200D\u{1F525}");
    expect(reactionEmoticonForTelegram("\u{1F44D}")).toBe("\u{1F44D}");
  });
  test("round-trips with normalizeReactionEmoji", () => {
    for (const bare of ["\u2764", "\u270D", "\u2603"]) {
      expect(reactionEmoticonForTelegram(normalizeReactionEmoji(bare))).toBe(bare);
    }
  });
});

describe("mapReactions", () => {
  test("reports the red heart with U+FE0F", () => {
    const r = new Api.MessageReactions({
      results: [
        new Api.ReactionCount({ reaction: new Api.ReactionEmoji({ emoticon: "\u2764" }), count: 2, chosenOrder: 0 }),
        new Api.ReactionCount({ reaction: new Api.ReactionEmoji({ emoticon: "\u{1F44D}" }), count: 1 }),
      ],
      recentReactions: [],
    });
    expect(mapReactions(r)).toEqual([
      { emoji: "\u2764" + VS16, count: 2, fromMe: true },
      { emoji: "\u{1F44D}", count: 1, fromMe: false },
    ]);
  });
});

describe("muteUntilSeconds", () => {
  const FOREVER = 2 ** 31 - 1;
  test("unmute is 0 and forever is 2^31-1", () => {
    expect(muteUntilSeconds(0)).toBe(0);
    expect(muteUntilSeconds(-1)).toBe(FOREVER);
  });
  test("a ms epoch becomes whole seconds, rounded up, and never passes 2^31-1", () => {
    expect(muteUntilSeconds(1_800_000_000_000)).toBe(1_800_000_000);
    expect(muteUntilSeconds(1_800_000_000_001)).toBe(1_800_000_001);
    expect(muteUntilSeconds(9_999_999_999_999)).toBe(FOREVER);
  });
  test("round-trips through muteInfo", () => {
    const own = (muteUntil: number) => new Api.PeerNotifySettings({ muteUntil });
    const soon = Math.ceil((Date.now() + 3_600_000) / 1000) * 1000;
    expect(muteInfo(own(muteUntilSeconds(-1)))).toEqual({ muted: true, mutedUntil: -1 });
    expect(muteInfo(own(muteUntilSeconds(soon)))).toEqual({ muted: true, mutedUntil: soon });
    expect(muteInfo(own(muteUntilSeconds(0)))).toEqual({ muted: false });
  });
  test("an explicit unmute beats a muted scope default", () => {
    const scope = new Api.PeerNotifySettings({ muteUntil: muteUntilSeconds(-1) });
    expect(muteInfo(undefined, scope).muted).toBe(true);
    expect(muteInfo(new Api.PeerNotifySettings({ muteUntil: 0 }), scope)).toEqual({ muted: false });
  });
});

describe("isPinNotice", () => {
  const svc = (action: Api.TypeMessageAction) =>
    new Api.MessageService({ id: 7, peerId: new Api.PeerUser({ userId: BigInt(1) as never }), date: 1, action });
  test("only the pin service line counts", () => {
    expect(isPinNotice(svc(new Api.MessageActionPinMessage()))).toBe(true);
    expect(isPinNotice(svc(new Api.MessageActionChatDeletePhoto()))).toBe(false);
    expect(isPinNotice(new Api.MessageEmpty({ id: 7 }))).toBe(false);
  });
});
