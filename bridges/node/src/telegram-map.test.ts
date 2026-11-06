import { describe, expect, test } from "bun:test";
import { Api } from "telegram";
import { TelegramAdapter } from "./telegram.ts";
import type { Chat, Message } from "./types.ts";
import {
  AlbumClock,
  buildMentionEntities,
  isPinNotice,
  mapLinks,
  mapMessage,
  mapReactions,
  mapReplyMarkup,
  muteInfo,
  muteUntilSeconds,
  normalizeReactionEmoji,
  previewText,
  reactionEmoticonForTelegram,
  ReplyResolver,
  searchFilters,
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

// ------------------------------------------------------------------ helpers for the mapping tests below

const big = (n: number) => BigInt(n) as never;
const env = (entities: Record<string, Api.TypeUser | Api.TypeChat> = {}) => ({
  selfId: "1",
  selfName: "Me",
  entity: (id: string) => entities[id],
  readOutboxMax: () => 0,
});
const user = (id: number, firstName: string) => new Api.User({ id: big(id), firstName, accessHash: big(id * 10) });
const photo = (id: number) =>
  new Api.MessageMediaPhoto({
    photo: new Api.Photo({
      id: big(id),
      accessHash: big(id),
      fileReference: Buffer.alloc(0),
      date: 1,
      sizes: [new Api.PhotoSize({ type: "x", w: 800, h: 600, size: 1000 })],
      dcId: 2,
    }),
  });
const raw = (id: number, extra: Partial<ConstructorParameters<typeof Api.Message>[0]> = {}) =>
  new Api.Message({
    id,
    peerId: new Api.PeerChannel({ channelId: big(50) }),
    fromId: new Api.PeerUser({ userId: big(7) }),
    date: 1_700_000_000,
    message: "",
    ...extra,
  });
const CHAT = "-10050";
const LONE_SURROGATE = /[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/;

// ------------------------------------------------------------------ replies

describe("previewText", () => {
  test("never cuts an emoji in half (a lone surrogate made Swift reject the whole frame)", () => {
    const text = "a".repeat(199) + "\u{1F4CA}" + "tail"; // the emoji straddles the 200-unit limit
    const p = previewText({ text, kind: "text" });
    expect(p).toBe("a".repeat(199));
    expect(LONE_SURROGATE.test(JSON.parse(JSON.stringify(p)))).toBe(false);
    expect(previewText({ text: "a".repeat(198) + "\u{1F4CA}" + "tail", kind: "text" })).toBe("a".repeat(198) + "\u{1F4CA}");
  });
  test("short text is kept and media without text gets its label", () => {
    expect(previewText({ text: "  hi  ", kind: "text" })).toBe("hi");
    expect(previewText({ kind: "image" })).toBe("Photo");
  });
});

describe("ReplyResolver", () => {
  const msg = (id: number, text: string, kind: Message["kind"] = "text", senderName = "Ann"): Message => ({
    id: String(id),
    chatId: CHAT,
    senderId: "7",
    senderName,
    fromMe: false,
    timestamp: 1,
    kind,
    text,
  });
  const replyingTo = (id: number, to: number, extra: Partial<ConstructorParameters<typeof Api.MessageReplyHeader>[0]> = {}) =>
    raw(id, { replyTo: new Api.MessageReplyHeader({ replyToMsgId: to, ...extra }) });
  const mapOf = (r: Api.Message) => mapMessage(r, env())!;

  test("a whole page costs ONE getMessages for everything the page and the caches do not hold", async () => {
    const calls: number[][] = [];
    const resolver = new ReplyResolver({
      recent: (_c, id) => (id === 20 ? msg(20, "from the recent cache") : undefined),
      fetch: async (_c, ids) => {
        calls.push(ids);
        return new Map(ids.filter((i) => i !== 40).map((i) => [i, msg(i, `fetched ${i}`, i === 30 ? "image" : "text", "Bob")]));
      },
    });
    const raws = [raw(11, { message: "same page" }), replyingTo(12, 11), replyingTo(13, 20), replyingTo(14, 30), replyingTo(15, 31), replyingTo(16, 30), replyingTo(17, 40)];
    const mapped = raws.map(mapOf);
    await resolver.fill(CHAT, raws, mapped);

    expect(calls).toEqual([[30, 31, 40]]); // 11 is on the page, 20 in the recent cache; 30 is asked once for two replies
    const q = (i: number) => mapped[i]!.replyTo!;
    expect(q(1)).toMatchObject({ id: "11", text: "same page", kind: "text" });
    expect(q(2)).toMatchObject({ id: "20", text: "from the recent cache", senderName: "Ann" });
    expect(q(3)).toMatchObject({ id: "30", text: "fetched 30", senderName: "Bob", kind: "image" });
    expect(q(5)).toMatchObject({ id: "30", text: "fetched 30" });
    expect(q(6)).toEqual({ id: "40" }); // deleted: nothing to show, nothing invented

    // Later pages: found and deleted targets are remembered, nothing is asked again.
    const again = [replyingTo(18, 30), replyingTo(19, 40)];
    const mappedAgain = again.map(mapOf);
    await resolver.fill(CHAT, again, mappedAgain);
    expect(calls.length).toBe(1);
    expect(mappedAgain[0]!.replyTo).toMatchObject({ text: "fetched 30", senderName: "Bob" });

    resolver.forget(CHAT, 30); // edited/deleted target: asked again
    await resolver.fill(CHAT, [again[0]!], [mappedAgain[0]!]);
    expect(calls).toEqual([[30, 31, 40], [30]]);
  });

  test("a quoted excerpt stays; the sender and kind still come from the message", async () => {
    const resolver = new ReplyResolver({ recent: () => undefined, fetch: async (_c, ids) => new Map(ids.map((i) => [i, msg(i, "whole long text", "text", "Bob")])) });
    const r = replyingTo(2, 1, { quote: true, quoteText: "long" });
    const m = mapOf(r);
    expect(m.replyTo).toEqual({ id: "1", text: "long" });
    await resolver.fill(CHAT, [r], [m]);
    expect(m.replyTo).toEqual({ id: "1", text: "long", senderName: "Bob", kind: "text" });
  });

  test("a cross-chat reply is never matched to a message of this chat that happens to share the id", async () => {
    const resolver = new ReplyResolver({ recent: () => undefined, fetch: async () => new Map() });
    const target = raw(5, { message: "local five" });
    const cross = raw(6, { replyTo: new Api.MessageReplyHeader({ replyToMsgId: 5, replyToPeerId: new Api.PeerChannel({ channelId: big(99) }), quoteText: "quoted elsewhere" }) });
    const mapped = [mapOf(target), mapOf(cross)];
    await resolver.fill(CHAT, [target, cross], mapped);
    expect(mapped[1]!.replyTo).toEqual({ id: "5", text: "quoted elsewhere" });
  });

  test("more than 100 missing targets go out in batches of 100, and a failing fetch leaves the ids as they are", async () => {
    const sizes: number[] = [];
    const logs: string[] = [];
    const resolver = new ReplyResolver({
      recent: () => undefined,
      fetch: async (_c, ids) => {
        sizes.push(ids.length);
        if (sizes.length === 2) throw new Error("flood");
        return new Map(ids.map((i) => [i, msg(i, "t")]));
      },
      log: (m) => logs.push(m),
    });
    const raws = Array.from({ length: 130 }, (_, i) => replyingTo(1000 + i, 1 + i));
    const mapped = raws.map(mapOf);
    await resolver.fill(CHAT, raws, mapped);
    expect(sizes).toEqual([100, 30]);
    expect(logs.length).toBe(1);
    expect(mapped[0]!.replyTo!.text).toBe("t");
    expect(mapped[129]!.replyTo).toEqual({ id: "130" });
  });
});

// ------------------------------------------------------------------ albums

describe("album grouping", () => {
  test("groupedId becomes groupId on every item, with its own real timestamp", () => {
    const a = mapMessage(raw(101, { groupedId: big(13579), media: photo(1), message: "caption" }), env())!;
    const b = mapMessage(raw(102, { groupedId: big(13579), media: photo(2) }), env())!;
    expect([a.groupId, b.groupId]).toEqual(["13579", "13579"]);
    expect([a.kind, b.kind]).toEqual(["image", "image"]);
    expect(a.text).toBe("caption");
    expect(a.timestamp).toBe(1_700_000_000_000);
    expect(mapMessage(raw(103, { message: "alone" }), env())!.groupId).toBeUndefined();
  });

  test("AlbumClock keeps the sequence whatever the order the items are mapped in", () => {
    const item = (id: number, groupId = "g"): Message => ({ id: String(id), chatId: CHAT, senderId: "7", fromMe: false, timestamp: 5000, kind: "image", groupId });
    const asc = new AlbumClock();
    expect([101, 102, 103].map((i) => asc.stamp(item(i)).timestamp)).toEqual([5000, 5001, 5002]);

    const desc = new AlbumClock(); // search results come newest first
    expect([103, 102, 101].map((i) => desc.stamp(item(i)).timestamp)).toEqual([5000, 4999, 4998]);

    // history shows an item again later (edit, reaction, re-list): it gets the timestamp it had
    expect(asc.stamp(item(102)).timestamp).toBe(5001);
    expect(asc.stamp(item(101)).timestamp).toBe(5000);
    // the other half of the album arrives after: it sorts before, not tied
    const half = new AlbumClock();
    expect([104, 105].map((i) => half.stamp(item(i)).timestamp)).toEqual([5000, 5001]);
    expect(half.stamp(item(103)).timestamp).toBe(4999);
  });

  test("items in different seconds, other albums and plain messages are left alone", () => {
    const clock = new AlbumClock();
    const at = (id: number, ts: number, groupId?: string): Message => ({ id: String(id), chatId: CHAT, senderId: "7", fromMe: false, timestamp: ts, kind: "image", groupId });
    expect(clock.stamp(at(1, 1000, "a")).timestamp).toBe(1000);
    expect(clock.stamp(at(2, 2000, "a")).timestamp).toBe(2000);
    expect(clock.stamp(at(3, 1000, "b")).timestamp).toBe(1000);
    expect(clock.stamp(at(4, 1000)).timestamp).toBe(1000);
  });
});

// ------------------------------------------------------------------ links

describe("links", () => {
  const withEntities = (message: string, entities: Api.TypeMessageEntity[]) => raw(1, { message, entities });

  test("hidden URLs under words, scheme-less URLs and e-mail addresses are listed with UTF-16 offsets", () => {
    const text = "\u{1F600} read the docs, t.me/chatspace or mail a@b.vn, https://plain.example/x";
    const docs = text.indexOf("the docs");
    const tme = text.indexOf("t.me/chatspace");
    const mail = text.indexOf("a@b.vn");
    const plain = text.indexOf("https://");
    const r = withEntities(text, [
      new Api.MessageEntityUrl({ offset: plain, length: "https://plain.example/x".length }),
      new Api.MessageEntityEmail({ offset: mail, length: 6 }),
      new Api.MessageEntityTextUrl({ offset: docs, length: 8, url: "https://docs.example/guide" }),
      new Api.MessageEntityUrl({ offset: tme, length: "t.me/chatspace".length }),
      new Api.MessageEntityBold({ offset: 0, length: 2 }),
    ]);
    const m = mapMessage(r, env())!;
    expect(m.text).toBe(text);
    expect(m.links).toEqual([
      { offset: docs, length: 8, url: "https://docs.example/guide" },
      { offset: tme, length: 14, url: "https://t.me/chatspace" },
      { offset: mail, length: 6, url: "mailto:a@b.vn" },
    ]);
    expect(text.slice(docs, docs + 8)).toBe("the docs"); // the offsets are into `text` as Swift's utf16 view sees it
  });

  test("entities outside the text are dropped, a message without entities has no links, a caption works", () => {
    expect(mapMessage(withEntities("abc", [new Api.MessageEntityTextUrl({ offset: 2, length: 5, url: "https://x.y" })]), env())!.links).toBeUndefined();
    expect(mapMessage(withEntities("abc", [new Api.MessageEntityTextUrl({ offset: 0, length: 0, url: "https://x.y" })]), env())!.links).toBeUndefined();
    expect(mapMessage(raw(2, { message: "abc" }), env())!.links).toBeUndefined();
    const cap = raw(3, { message: "see this", media: photo(1), entities: [new Api.MessageEntityTextUrl({ offset: 4, length: 4, url: "https://x.y" })] });
    expect(mapMessage(cap, env())!.links).toEqual([{ offset: 4, length: 4, url: "https://x.y" }]);
  });

  test("offsets are never reported for text the bridge made up (the poll or location text is not the message text)", () => {
    const r = raw(4, {
      message: "",
      media: new Api.MessageMediaGeo({ geo: new Api.GeoPoint({ lat: 1, long: 2, accessHash: big(1) }) }),
      entities: [new Api.MessageEntityTextUrl({ offset: 0, length: 3, url: "https://x.y" })],
    });
    expect(mapLinks(r, "1.000000, 2.000000")).toBeUndefined();
  });
});

// ------------------------------------------------------------------ mentions

describe("mentions", () => {
  test("incoming mention entities never put an id in the text", () => {
    const r = raw(1, {
      message: "hey Ann Lee, see this",
      entities: [new Api.MessageEntityMentionName({ offset: 4, length: 7, userId: big(1234567890) }), new Api.MessageEntityMention({ offset: 13, length: 4 })],
    });
    const m = mapMessage(r, env({ "7": user(7, "Bob") }))!;
    expect(m.text).toBe("hey Ann Lee, see this");
    expect(JSON.stringify(m)).not.toContain("1234567890");
    expect(m.senderName).toBe("Bob");
  });

  test("outgoing spans become InputMessageEntityMentionName with an InputUser that has the access hash, in UTF-16", async () => {
    const text = "\u{1F44B} hi @Ann Lee and @Bob!"; // the emoji is two UTF-16 units
    const ann = text.indexOf("@Ann Lee");
    const bob = text.indexOf("@Bob");
    const asked: string[] = [];
    const entities = await buildMentionEntities(
      text,
      [
        { userId: "11", offset: ann, length: 8 },
        { userId: "22", offset: bob, length: 4 },
        { userId: "33", offset: 0, length: 1 }, // splits the emoji: dropped
        { userId: "abc", offset: 3, length: 2 }, // not a Telegram user id: dropped
      ],
      async (id) => {
        asked.push(id);
        return new Api.InputUser({ userId: big(Number(id)), accessHash: big(Number(id) * 10) });
      },
    );
    expect(asked).toEqual(["11", "22"]);
    expect(entities).toHaveLength(2);
    const [a, b] = entities as Api.InputMessageEntityMentionName[];
    expect([a!.offset, a!.length, String(a!.userId instanceof Api.InputUser && a!.userId.userId), String((a!.userId as Api.InputUser).accessHash)]).toEqual([ann, 8, "11", "110"]);
    expect([b!.offset, b!.length]).toEqual([bob, 4]);
    expect(text.slice(a!.offset, a!.offset + a!.length)).toBe("@Ann Lee");
    expect(text.slice(b!.offset, b!.offset + b!.length)).toBe("@Bob");
  });

  test("a user that cannot be resolved is skipped; with none left there are no entities", async () => {
    expect(await buildMentionEntities("hi @Ann", [{ userId: "11", offset: 3, length: 4 }], async () => undefined)).toBeUndefined();
    expect(await buildMentionEntities("hi @Ann", [{ userId: "11", offset: 3, length: 4 }], async () => new Api.InputUserEmpty())).toBeUndefined();
    expect(await buildMentionEntities("hi", undefined, async () => undefined)).toBeUndefined();
  });
});

// ------------------------------------------------------------------ search filters

describe("searchFilters", () => {
  const names = (kinds?: string[]) => searchFilters(kinds).map((f) => f.className);
  test("kinds map to Telegram's server filters", () => {
    expect(names(["image"])).toEqual(["InputMessagesFilterPhotos"]);
    expect(names(["video"])).toEqual(["InputMessagesFilterVideo"]);
    expect(names(["image", "video"])).toEqual(["InputMessagesFilterPhotoVideo"]);
    expect(names(["file"])).toEqual(["InputMessagesFilterDocument"]);
    expect(names(["link"])).toEqual(["InputMessagesFilterUrl"]);
    expect(names(["voice"])).toEqual(["InputMessagesFilterVoice"]);
  });
  test("several kinds are several requests; a kind with no server filter means everything", () => {
    expect(names(["image", "video", "file"])).toEqual(["InputMessagesFilterPhotoVideo", "InputMessagesFilterDocument"]);
    expect(names(["link", "voice"])).toEqual(["InputMessagesFilterUrl", "InputMessagesFilterVoice"]);
    expect(names(["image", "text"])).toEqual(["InputMessagesFilterEmpty"]);
    expect(names(["sticker"])).toEqual(["InputMessagesFilterEmpty"]);
    expect(names(undefined)).toEqual(["InputMessagesFilterEmpty"]);
    expect(names([])).toEqual(["InputMessagesFilterEmpty"]);
  });
});

// ------------------------------------------------------------------ message types

describe("message types", () => {
  test("a poll reads as text with its options, votes and our choice, not as 'unsupported'", () => {
    const opt = (n: number) => Buffer.from([n]);
    const media = new Api.MessageMediaPoll({
      poll: new Api.Poll({
        id: big(1),
        question: new Api.TextWithEntities({ text: "Lunch?", entities: [] }),
        answers: [
          new Api.PollAnswer({ text: new Api.TextWithEntities({ text: "Pho", entities: [] }), option: opt(1) }),
          new Api.PollAnswer({ text: new Api.TextWithEntities({ text: "Bun", entities: [] }), option: opt(2) }),
        ],
      }),
      results: new Api.PollResults({
        results: [new Api.PollAnswerVoters({ option: opt(1), voters: 3, chosen: true }), new Api.PollAnswerVoters({ option: opt(2), voters: 0 })],
      }),
    });
    const m = mapMessage(raw(1, { media }), env())!;
    expect(m.kind).toBe("text");
    expect(m.text).toBe("\u{1F4CA} Poll: Lunch?\n\u25C9 Pho \u2014 3\n\u25CB Bun");
  });

  test("service lines read as sentences", () => {
    const svc = (action: Api.TypeMessageAction) =>
      mapMessage(new Api.MessageService({ id: 9, peerId: new Api.PeerChannel({ channelId: big(50) }), fromId: new Api.PeerUser({ userId: big(7) }), date: 1, action }), env({ "7": user(7, "Bob") }))!.text;
    expect(svc(new Api.MessageActionTopicEdit({ title: "News" }))).toBe('Bob renamed the topic to "News"');
    expect(svc(new Api.MessageActionTopicEdit({ closed: true }))).toBe("Bob closed the topic");
    expect(svc(new Api.MessageActionBoostApply({ boosts: 2 }))).toBe("Bob boosted the group 2 times");
    expect(svc(new Api.MessageActionGiveawayResults({ winnersCount: 3, unclaimedCount: 0 }))).toBe("Giveaway ended: 3 winners");
  });
});

// ------------------------------------------------------------------ the adapter against a fake Telegram client

describe("TelegramAdapter with a fake client", () => {
  type Fake = Record<string, (...a: any[]) => any>;
  const setup = (fake: Fake) => {
    const events: { event: string; data: any }[] = [];
    const logs: string[] = [];
    const adapter = new TelegramAdapter({
      dataDir: "/tmp/chatspace-telegram-test-nonexistent",
      emit: (event, data) => events.push({ event, data }),
      log: (...a) => logs.push(a.join(" ")),
    });
    const a = adapter as any;
    a.client = fake;
    a.state = "logged_in";
    a.self = { id: "1", name: "Me" };
    a.inputPeers.set(CHAT, new Api.InputPeerChannel({ channelId: big(50), accessHash: big(500) }));
    a.inputPeers.set("7", new Api.InputPeerUser({ userId: big(7), accessHash: big(70) }));
    a.inputPeers.set("8", new Api.InputPeerUser({ userId: big(8), accessHash: big(80) }));
    a.entities.set("7", user(7, "Bob"));
    return { adapter, a, events, logs };
  };
  const page = (...messages: Api.Message[]) => new Api.messages.Messages({ messages, chats: [], users: [] });
  const doc = (id: number, mime: string, attributes: Api.TypeDocumentAttribute[]) =>
    new Api.MessageMediaDocument({
      document: new Api.Document({ id: big(id), accessHash: big(id), fileReference: Buffer.alloc(0), date: 1, mimeType: mime, size: big(10), dcId: 2, attributes }),
    });

  test("search by member + kinds asks the server with fromId and the matching filter (the 'shared media' panel)", async () => {
    const requests: any[] = [];
    const { adapter } = setup({
      invoke: async (req: any) => {
        requests.push(req);
        return page(
          raw(103, { groupedId: big(9), media: photo(3) }),
          raw(102, { groupedId: big(9), media: photo(2) }),
          raw(101, { groupedId: big(9), media: photo(1), message: "caption" }),
        );
      },
    });
    const hits = await adapter.searchMessages({ query: "", chatId: CHAT, senderId: "7", kinds: ["image", "video"], limit: 3 });

    expect(requests).toHaveLength(1);
    const r = requests[0] as Api.messages.Search;
    expect(r).toBeInstanceOf(Api.messages.Search);
    expect(r.q).toBe("");
    expect(r.filter).toBeInstanceOf(Api.InputMessagesFilterPhotoVideo);
    expect(r.fromId).toBeInstanceOf(Api.InputPeerUser);
    expect(String((r.fromId as Api.InputPeerUser).userId)).toBe("7");
    expect(r.peer).toBeInstanceOf(Api.InputPeerChannel);
    expect(r.limit).toBe(3);

    expect(hits.map((m) => m.id)).toEqual(["103", "102", "101"]); // newest first
    expect(new Set(hits.map((m) => m.groupId))).toEqual(new Set(["9"]));
    expect(new Set(hits.map((m) => m.timestamp)).size).toBe(3); // same-second album items keep a sequence
    expect(hits.every((m) => m.senderId === "7")).toBe(true);
  });

  test("a server filter that is wider than the kind (videos sent as files) is topped up from the next server page", async () => {
    const requests: any[] = [];
    const videos = [raw(60, { media: doc(60, "video/mp4", [new Api.DocumentAttributeVideo({ duration: 1, w: 1, h: 1 })]) }), raw(59, { media: doc(59, "video/mp4", [new Api.DocumentAttributeVideo({ duration: 1, w: 1, h: 1 })]) })];
    const files = [raw(58, { date: 1_699_999_000, media: doc(58, "application/pdf", [new Api.DocumentAttributeFilename({ fileName: "a.pdf" })]) }), raw(57, { date: 1_699_998_000, media: doc(57, "application/pdf", [new Api.DocumentAttributeFilename({ fileName: "b.pdf" })]) })];
    const { adapter } = setup({
      invoke: async (req: any) => {
        requests.push(req);
        return requests.length === 1 ? page(...videos) : page(...files);
      },
    });
    const hits = await adapter.searchMessages({ query: "", chatId: CHAT, kinds: ["file"], limit: 2 });
    expect(requests).toHaveLength(2);
    expect(requests[0].filter).toBeInstanceOf(Api.InputMessagesFilterDocument);
    expect(requests[1].offsetId).toBe(59); // continues after the oldest message of the first page
    expect(hits.map((m) => m.id)).toEqual(["58", "57"]);
    expect(hits.every((m) => m.kind === "file")).toBe(true);
  });

  test("a sender alone with no chat has nothing the server can answer (SearchGlobal has no sender filter): local matches only", async () => {
    const requests: any[] = [];
    const { adapter } = setup({ invoke: async (req: any) => (requests.push(req), page()) });
    expect(await adapter.searchMessages({ query: "", senderId: "7", limit: 10 })).toEqual([]);
    expect(requests).toHaveLength(0);
  });

  test("a mention is sent as an entity over the typed name, and the message that comes back keeps the name", async () => {
    const sentWith: any[] = [];
    const text = "\u{1F44B} hi @Ann Lee, lunch?";
    const { adapter } = setup({
      sendMessage: async (_peer: unknown, opts: any) => {
        sentWith.push(opts);
        return raw(300, { message: opts.message, out: true, entities: [new Api.MessageEntityMentionName({ offset: 6, length: 8, userId: big(8) })] });
      },
    });
    const sent = await adapter.sendMessage({ chatId: CHAT, text, mentions: [{ userId: "8", offset: text.indexOf("@Ann"), length: 8 }] });

    expect(sentWith).toHaveLength(1);
    const [e] = sentWith[0].formattingEntities as Api.InputMessageEntityMentionName[];
    expect(e).toBeInstanceOf(Api.InputMessageEntityMentionName);
    expect([e!.offset, e!.length]).toEqual([6, 8]); // after the emoji's two UTF-16 units, "hi" and a space
    expect(text.slice(e!.offset, e!.offset + e!.length)).toBe("@Ann Lee");
    expect(e!.userId).toBeInstanceOf(Api.InputUser);
    expect(String((e!.userId as Api.InputUser).userId)).toBe("8");
    expect(String((e!.userId as Api.InputUser).accessHash)).toBe("80");
    expect(sentWith[0].message).toBe(text);
    expect(sent.text).toBe(text);
    expect(JSON.stringify(sent)).not.toMatch(/"text":"[^"]*\b8\b/);
  });

  test("when Telegram refuses the mention entity the same text goes out without it (never Markdown-parsed)", async () => {
    const attempts: any[] = [];
    const { adapter, logs } = setup({
      sendMessage: async (_peer: unknown, opts: any) => {
        attempts.push(opts);
        if (attempts.length === 1) throw Object.assign(new Error("bad user"), { errorMessage: "USER_ID_INVALID" });
        return raw(301, { message: opts.message, out: true });
      },
    });
    const sent = await adapter.sendMessage({ chatId: CHAT, text: "hi @Ann", mentions: [{ userId: "8", offset: 3, length: 4 }] });
    expect(attempts).toHaveLength(2);
    expect(attempts[0].formattingEntities).toHaveLength(1);
    expect(attempts[1].formattingEntities).toEqual([]); // [] and not undefined: GramJS would parse Markdown otherwise
    expect(sent.text).toBe("hi @Ann");
    expect(logs.some((l) => l.includes("USER_ID_INVALID"))).toBe(true);

    // other errors are not swallowed
    const { adapter: b } = setup({ sendMessage: async () => { throw Object.assign(new Error("x"), { errorMessage: "CHAT_WRITE_FORBIDDEN" }); } });
    await expect(b.sendMessage({ chatId: CHAT, text: "hi @Ann", mentions: [{ userId: "8", offset: 3, length: 4 }] })).rejects.toThrow();
  });

  test("a live reply arrives with the quoted message resolved by one getMessages, and nothing is asked twice", async () => {
    const asked: number[][] = [];
    const quoted = raw(200, { message: "the original question", fromId: new Api.PeerUser({ userId: big(7) }) });
    const { a, events } = setup({
      getMessages: async (_peer: unknown, opts: { ids: number[] }) => {
        asked.push(opts.ids);
        return opts.ids.map((id) => (id === 200 ? quoted : undefined));
      },
    });
    a.chats.set(CHAT, { id: CHAT, title: "Group", kind: "group", unread: 0, timestamp: 0 });
    const reply = (id: number, to: number) => raw(id, { date: Math.floor(Date.now() / 1000), message: `answer ${id}`, replyTo: new Api.MessageReplyHeader({ replyToMsgId: to }) });

    await a.processNew(reply(201, 200));
    await a.processNew(reply(202, 200));
    expect(asked).toEqual([[200]]);
    const news = events.filter((e) => e.event === "message.new").map((e) => e.data.message as Message);
    expect(news).toHaveLength(2);
    for (const m of news) expect(m.replyTo).toEqual({ id: "200", text: "the original question", senderName: "Bob", kind: "text" });
  });

  test("an update of a message whose earlier version is gone still carries its quote (message.update replaces the message)", async () => {
    const { a, events } = setup({
      getMessages: async (_peer: unknown, opts: { ids: number[] }) => opts.ids.map((id) => raw(id, { message: `text of ${id}` })),
    });
    a.chats.set(CHAT, { id: CHAT, title: "Group", kind: "group", unread: 0, timestamp: 0 });
    const edited = raw(210, { message: "edited", editDate: 5, replyTo: new Api.MessageReplyHeader({ replyToMsgId: 200 }) });
    await a.onEdit(edited);
    const m = events.find((e) => e.event === "message.update")!.data.message as Message;
    expect(m.edited).toBe(true);
    expect(m.replyTo).toMatchObject({ id: "200", text: "text of 200", kind: "text" });
  });
  // ---------------------------------------------------------------- chats.direct

  const peerDialogs = (extra: { dialogs?: Api.TypeDialog[]; messages?: Api.Message[]; users?: Api.TypeUser[] } = {}) =>
    new Api.messages.PeerDialogs({
      dialogs: [],
      messages: [],
      chats: [],
      users: [],
      state: new Api.updates.State({ pts: 1, qts: 0, date: 1, seq: 1, unreadCount: 0 }),
      ...extra,
    });

  test("chats.direct builds an empty chat for a group member without a dialog, sends nothing, and its id works for list/send", async () => {
    const invoked: any[] = [];
    const peers: any[] = [];
    let sends = 0;
    const { adapter, a, events } = setup({
      invoke: async (req: any) => (invoked.push(req), peerDialogs()),
      getMessages: async (peer: unknown) => (peers.push(peer), []),
      sendMessage: async (peer: unknown, opts: any) => {
        sends++;
        peers.push(peer);
        return raw(500, { message: opts.message, out: true, peerId: new Api.PeerUser({ userId: big(7) }) });
      },
    });
    expect(adapter.capabilities).toContain("direct_chat");

    const chat = await adapter.directChat("7");
    expect(chat).toMatchObject({ id: "7", title: "Bob", kind: "direct", unread: 0, timestamp: 0 });
    expect(chat.lastMessage).toBeUndefined();
    expect(invoked.every((r) => r instanceof Api.messages.GetPeerDialogs)).toBe(true); // read-only lookups only
    expect(sends).toBe(0);
    expect(events).toEqual([]); // nothing announced, nothing created
    expect(a.chats.get("7")).toBe(chat); // later messages / mute / read state find it
    expect(await adapter.directChat("7")).toBe(chat); // asked again: no second lookup
    expect(invoked).toHaveLength(1);

    await adapter.listMessages(chat.id, 20);
    await adapter.sendMessage({ chatId: chat.id, text: "hi" });
    for (const peer of peers) {
      expect(peer).toBeInstanceOf(Api.InputPeerUser);
      expect([String(peer.userId), String(peer.accessHash)]).toEqual(["7", "70"]); // the hash learned from the group
    }
  });

  test("chats.direct returns the real dialog when the server has one that we had not listed", async () => {
    const dialog = new Api.Dialog({
      peer: new Api.PeerUser({ userId: big(7) }),
      topMessage: 55,
      readInboxMaxId: 0,
      readOutboxMaxId: 0,
      unreadCount: 3,
      unreadMentionsCount: 0,
      unreadReactionsCount: 0,
      notifySettings: new Api.PeerNotifySettings({}),
    });
    const last = raw(55, { peerId: new Api.PeerUser({ userId: big(7) }), message: "hello there" });
    const { adapter } = setup({ invoke: async () => peerDialogs({ dialogs: [dialog], messages: [last], users: [user(7, "Bob")] }) });
    const chat = await adapter.directChat("7");
    expect(chat).toMatchObject({ id: "7", title: "Bob", kind: "direct", unread: 3, timestamp: 1_700_000_000_000 });
    expect(chat.lastMessage?.text).toBe("hello there");
  });

  test("chats.direct on a bot gives a bot chat, and a listed dialog is returned without any request", async () => {
    const invoked: any[] = [];
    const { adapter, a } = setup({ invoke: async (req: any) => (invoked.push(req), peerDialogs()) });
    a.entities.set("20", new Api.User({ id: big(20), firstName: "Helper", bot: true, accessHash: big(200) }));
    a.inputPeers.set("20", new Api.InputPeerUser({ userId: big(20), accessHash: big(200) }));
    expect((await adapter.directChat("20")).kind).toBe("bot");

    const listed: Chat = { id: "8", title: "Listed", kind: "direct", unread: 2, timestamp: 9 };
    a.chats.set("8", listed);
    invoked.length = 0;
    expect(await adapter.directChat("8")).toBe(listed);
    expect(invoked).toHaveLength(0);
  });

  test("chats.direct finds a 'min' member through a message of the group that shows them", async () => {
    const invoked: any[] = [];
    const { adapter, a } = setup({
      invoke: async (req: any) => {
        invoked.push(req);
        return req instanceof Api.users.GetUsers ? [new Api.User({ id: big(9), firstName: "Mina", accessHash: big(90) })] : peerDialogs();
      },
    });
    a.entities.set("9", new Api.User({ id: big(9), firstName: "Mina", min: true })); // seen in a group, no access hash
    a.recent.set(`${CHAT}:400`, { id: "400", chatId: CHAT, senderId: "9", fromMe: false, timestamp: 1, kind: "text", text: "hi all" });

    const chat = await adapter.directChat("9");
    expect(chat).toMatchObject({ id: "9", title: "Mina", kind: "direct" });
    const lookup = invoked.find((r) => r instanceof Api.users.GetUsers) as Api.users.GetUsers;
    const ref = lookup.id[0] as Api.InputUserFromMessage;
    expect(ref).toBeInstanceOf(Api.InputUserFromMessage);
    expect([ref.msgId, String(ref.userId), ref.peer.className]).toEqual([400, "9", "InputPeerChannel"]);
    const peer = a.inputPeers.get("9") as Api.InputPeerUser;
    expect([peer.className, String(peer.accessHash)]).toEqual(["InputPeerUser", "90"]); // usable by messages.send now
  });

  test("chats.direct refuses what is not a reachable user", async () => {
    const invoked: any[] = [];
    const { adapter, a } = setup({ invoke: async (req: any) => (invoked.push(req), peerDialogs()) });
    await expect(adapter.directChat(CHAT)).rejects.toMatchObject({ code: "invalid_params" }); // a group id
    await expect(adapter.directChat("abc")).rejects.toMatchObject({ code: "invalid_params" });
    await expect(adapter.directChat("12345")).rejects.toMatchObject({ code: "not_found" }); // never seen, no hash
    a.entities.set("30", new Api.User({ id: big(30), deleted: true, accessHash: big(300) }));
    a.inputPeers.set("30", new Api.InputPeerUser({ userId: big(30), accessHash: big(300) }));
    await expect(adapter.directChat("30")).rejects.toMatchObject({ code: "not_found" });
    expect(invoked).toHaveLength(0);
  });
});
