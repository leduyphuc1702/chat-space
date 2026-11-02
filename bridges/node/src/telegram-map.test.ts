import { describe, expect, test } from "bun:test";
import { Api } from "telegram";
import { mapReactions, normalizeReactionEmoji, reactionEmoticonForTelegram } from "./telegram-map.ts";

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
