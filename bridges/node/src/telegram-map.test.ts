import { describe, expect, test } from "bun:test";
import { Api } from "telegram";
import {
  isPinNotice,
  mapMessage,
  mapReactions,
  mapReplyMarkup,
  muteInfo,
  muteUntilSeconds,
  normalizeReactionEmoji,
  reactionEmoticonForTelegram,
} from "./telegram-map.ts";

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

describe("mapReplyMarkup", () => {
  const big = (n: number) => BigInt(n) as never;
  const row = (...buttons: Api.TypeKeyboardButton[]) => new Api.KeyboardButtonRow({ buttons });

  test("inline keyboards become button rows with the action the app needs", () => {
    const markup = new Api.ReplyInlineMarkup({
      rows: [
        row(
          new Api.KeyboardButtonCallback({ text: "Yes", data: Buffer.from("y") }),
          new Api.KeyboardButtonUrl({ text: "Docs", url: "https://example.com" }),
        ),
        row(
          new Api.KeyboardButtonSwitchInline({ text: "Share", query: "cats", samePeer: true }),
          new Api.KeyboardButtonWebView({ text: "App", url: "https://app.example.com" }),
          new Api.KeyboardButtonCopy({ text: "Copy", copyText: "ABC-123" }),
          new Api.KeyboardButtonBuy({ text: "Pay" }),
        ),
      ],
    });
    expect(mapReplyMarkup(markup, "catbot")).toEqual({
      buttons: [
        [
          { text: "Yes", kind: "callback" },
          { text: "Docs", kind: "url", url: "https://example.com" },
        ],
        [
          { text: "Share", kind: "switch_inline", insert: "@catbot cats", samePeer: true },
          { text: "App", kind: "web_app", url: "https://app.example.com" },
          { text: "Copy", kind: "copy", copyText: "ABC-123" },
          { text: "Pay", kind: "unsupported" },
        ],
      ],
    });
  });

  test("a switch-inline button without a known bot username has nothing to insert", () => {
    const markup = new Api.ReplyInlineMarkup({ rows: [row(new Api.KeyboardButtonSwitchInline({ text: "Share", query: "" }))] });
    expect(mapReplyMarkup(markup)).toEqual({ buttons: [[{ text: "Share", kind: "switch_inline" }]] });
    expect(mapReplyMarkup(markup, "catbot")).toEqual({ buttons: [[{ text: "Share", kind: "switch_inline", insert: "@catbot " }]] });
  });

  test("reply keyboards keep their flags; hide and force-reply are reported as such", () => {
    const kb = new Api.ReplyKeyboardMarkup({
      rows: [row(new Api.KeyboardButton({ text: "Menu" }), new Api.KeyboardButtonRequestPhone({ text: "Phone" })), row(new Api.KeyboardButton({ text: "Help" }))],
      resize: true,
      singleUse: true,
      placeholder: "Pick one",
    });
    expect(mapReplyMarkup(kb)).toEqual({
      replyKeyboard: {
        rows: [
          [
            { text: "Menu", kind: "text" },
            { text: "Phone", kind: "unsupported" },
          ],
          [{ text: "Help", kind: "text" }],
        ],
        resize: true,
        oneTime: true,
        placeholder: "Pick one",
      },
    });
    expect(mapReplyMarkup(new Api.ReplyKeyboardHide({}))).toEqual({ hideKeyboard: true });
    expect(mapReplyMarkup(new Api.ReplyKeyboardForceReply({ placeholder: "Your name", singleUse: true }))).toEqual({
      forceReply: { placeholder: "Your name", oneTime: true },
    });
    expect(mapReplyMarkup(undefined)).toEqual({});
    expect(mapReplyMarkup(new Api.ReplyInlineMarkup({ rows: [] }))).toEqual({});
  });

  test("mapMessage carries the keyboard and finds the bot's username for switch-inline", () => {
    const bot = new Api.User({ id: big(99), bot: true, username: "catbot", firstName: "Cat" });
    const raw = new Api.Message({
      id: 5,
      peerId: new Api.PeerUser({ userId: big(99) }),
      fromId: new Api.PeerUser({ userId: big(99) }),
      date: 1,
      message: "Choose",
      replyMarkup: new Api.ReplyInlineMarkup({
        rows: [row(new Api.KeyboardButtonSwitchInline({ text: "Share", query: "q" }), new Api.KeyboardButtonGame({ text: "Play" }))],
      }),
    });
    const env = { selfId: "1", selfName: "Me", entity: (id: string) => (id === "99" ? bot : undefined), readOutboxMax: () => 0 };
    const m = mapMessage(raw, env)!;
    expect(m.buttons).toEqual([
      [
        { text: "Share", kind: "switch_inline", insert: "@catbot q" },
        { text: "Play", kind: "game" },
      ],
    ]);
    expect(m.replyKeyboard).toBeUndefined();
    const plain = mapMessage(new Api.Message({ id: 6, peerId: new Api.PeerUser({ userId: big(99) }), date: 1, message: "x" }), env)!;
    expect(plain.buttons).toBeUndefined();
  });
});
