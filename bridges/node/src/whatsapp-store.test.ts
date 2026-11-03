import { afterEach, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { MAX_PINS, WAStore, editedContent, muteArg, muteEndMs, type PinEvent } from "./whatsapp-store.ts";

const quote = { stanzaId: "Q1", participant: "123@s.whatsapp.net", quotedMessage: { conversation: "original" } };

describe("editedContent", () => {
  test("a plain text message takes the new text", () => {
    expect(editedContent({ conversation: "helo" }, { conversation: "hello" })).toEqual({ conversation: "hello" });
  });

  test("a reply keeps its quote when the edit is a bare conversation", () => {
    const old = { extendedTextMessage: { text: "ok", contextInfo: quote } };
    expect(editedContent(old, { conversation: "okay" })).toEqual({ extendedTextMessage: { text: "okay", contextInfo: quote } });
  });

  test("an edit that adds mentions merges them into the quote context", () => {
    const old = { extendedTextMessage: { text: "hi", contextInfo: quote } };
    const out = editedContent(old, { extendedTextMessage: { text: "hi @84901", contextInfo: { mentionedJid: ["84901@s.whatsapp.net"] } } });
    expect(out.extendedTextMessage.contextInfo).toEqual({ ...quote, mentionedJid: ["84901@s.whatsapp.net"] });
    expect(out.extendedTextMessage.text).toBe("hi @84901");
  });

  test("a link preview is dropped when the edit is plain text", () => {
    const old = { extendedTextMessage: { text: "see https://a.vn", matchedText: "https://a.vn", title: "A", canonicalUrl: "https://a.vn" } };
    expect(editedContent(old, { conversation: "no link now" })).toEqual({ extendedTextMessage: { text: "no link now" } });
  });

  test("a photo keeps its media keys and gets the text as caption", () => {
    const old = { imageMessage: { url: "u", mediaKey: "k", caption: "old", contextInfo: quote } };
    expect(editedContent(old, { conversation: "new caption" })).toEqual({
      imageMessage: { url: "u", mediaKey: "k", caption: "new caption", contextInfo: quote },
    });
    expect(editedContent(old, { imageMessage: { caption: "via image edit" } }).imageMessage).toMatchObject({ url: "u", mediaKey: "k", caption: "via image edit" });
  });

  test("wrapped content (disappearing chats) is edited inside the wrapper", () => {
    const old = { ephemeralMessage: { message: { extendedTextMessage: { text: "a", contextInfo: { expiration: 86400 } } } } };
    const out = editedContent(old, { conversation: "b" });
    expect(out.ephemeralMessage.message.extendedTextMessage).toEqual({ text: "b", contextInfo: { expiration: 86400 } });
  });

  test("the original object is not mutated", () => {
    const old = { conversation: "x" };
    editedContent(old, { conversation: "y" });
    expect(old).toEqual({ conversation: "x" });
  });
});

describe("mute values", () => {
  test("forever and timed mutes survive the trip out (muteArg) and back (muteEndMs)", () => {
    const in2h = Date.now() + 2 * 3600_000;
    expect(muteEndMs(muteArg(-1))).toBe(-1);
    expect(muteEndMs(muteArg(in2h))).toBe(in2h);
    expect(muteEndMs(muteArg(0))).toBe(0);
    expect(muteArg(0)).toBeNull();
    expect(muteArg(-1)).toBe(-1);
  });

  test("incoming values: seconds (history), ms (app state), negative or far future = forever, null = unmuted", () => {
    expect(muteEndMs(1_900_000_000)).toBe(1_900_000_000_000);
    expect(muteEndMs(1_900_000_000_000)).toBe(1_900_000_000_000);
    expect(muteEndMs({ low: -1, high: -1, unsigned: false, toNumber: () => -1 })).toBe(-1);
    expect(muteEndMs(99_999_999_999_999)).toBe(-1);
    expect(muteEndMs(null)).toBe(0);
    expect(muteEndMs(undefined)).toBe(0);
  });
});

describe("pins", () => {
  const dirs: string[] = [];
  const open = (dir = mkdtempSync(join(tmpdir(), "wa-pins-"))) => {
    if (!dirs.includes(dir)) dirs.push(dir);
    const s = new WAStore(dir, () => {});
    s.load();
    return { s, dir };
  };
  afterEach(() => {
    for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true });
  });

  const NOW = Date.now();
  const H = 3600_000;
  const pin = (id: string, at: number, lastsMs = 24 * H): PinEvent => ({ id, pin: true, at, expiresAt: at + lastsMs });
  const unpin = (id: string, at: number): PinEvent => ({ id, pin: false, at, expiresAt: 0 });
  const ids = (s: WAStore, chat = "c@s.whatsapp.net", now = NOW) => s.activePins(chat, now).map((p) => p.id);
  const CHAT = "c@s.whatsapp.net";

  test("a pin is recorded with its time and expiry; an unpinned chat has none", () => {
    const { s } = open();
    expect(ids(s)).toEqual([]);
    expect(s.applyPin(CHAT, pin("A", NOW - H, 7 * 24 * H), NOW)).toBe(true);
    expect(s.activePins(CHAT, NOW)).toEqual([{ id: "A", pinnedAt: NOW - H, expiresAt: NOW - H + 7 * 24 * H }]);
  });

  test("most recently pinned first", () => {
    const { s } = open();
    s.applyPin(CHAT, pin("A", NOW - 3 * H), NOW);
    s.applyPin(CHAT, pin("C", NOW - 1 * H), NOW);
    s.applyPin(CHAT, pin("B", NOW - 2 * H), NOW);
    expect(ids(s)).toEqual(["C", "B", "A"]);
  });

  test("pinning a message again replaces its entry (new time and expiry) and moves it to the front", () => {
    const { s } = open();
    s.applyPin(CHAT, pin("A", NOW - 3 * H), NOW);
    s.applyPin(CHAT, pin("B", NOW - 2 * H), NOW);
    expect(s.applyPin(CHAT, pin("A", NOW - H, 30 * 24 * H), NOW)).toBe(true);
    expect(s.activePins(CHAT, NOW)).toEqual([
      { id: "A", pinnedAt: NOW - H, expiresAt: NOW - H + 30 * 24 * H },
      { id: "B", pinnedAt: NOW - 2 * H, expiresAt: NOW - 2 * H + 24 * H },
    ]);
  });

  test("the same event twice is not a change", () => {
    const { s } = open();
    expect(s.applyPin(CHAT, pin("A", NOW - H), NOW)).toBe(true);
    expect(s.applyPin(CHAT, pin("A", NOW - H), NOW)).toBe(false);
  });

  test("unpin removes the entry; unpinning something not pinned changes nothing", () => {
    const { s } = open();
    s.applyPin(CHAT, pin("A", NOW - 2 * H), NOW);
    s.applyPin(CHAT, pin("B", NOW - H), NOW);
    expect(s.applyPin(CHAT, unpin("A", NOW - 60_000), NOW)).toBe(true);
    expect(ids(s)).toEqual(["B"]);
    expect(s.applyPin(CHAT, unpin("Z", NOW - 60_000), NOW)).toBe(false);
  });

  test("pins lapse at expiresAt", () => {
    const { s } = open();
    s.applyPin(CHAT, pin("A", NOW - 2 * H, 3 * H), NOW); // lapses at NOW + 1h
    s.applyPin(CHAT, pin("B", NOW - H, 24 * H), NOW);
    expect(ids(s, CHAT, NOW)).toEqual(["B", "A"]);
    expect(ids(s, CHAT, NOW + H)).toEqual(["B"]); // exactly at expiresAt: gone
    expect(ids(s, CHAT, NOW + 25 * H)).toEqual([]);
    expect(s.nextPinExpiry(NOW)).toBe(NOW + H);
    expect(s.nextPinExpiry(NOW + 2 * H)).toBe(NOW - H + 24 * H);
    expect(s.nextPinExpiry(NOW + 25 * H)).toBeUndefined();
    expect(s.lapsedPinChats(NOW, NOW + 30 * 60_000)).toEqual([]);
    expect(s.lapsedPinChats(NOW, NOW + 2 * H)).toEqual([CHAT]);
    expect(s.lapsedPinChats(NOW + 2 * H, NOW + 3 * H)).toEqual([]);
  });

  test("a pin that lapsed before it was seen is not active and is not a change", () => {
    const { s } = open();
    expect(s.applyPin(CHAT, pin("A", NOW - 3 * 24 * H, 24 * H), NOW)).toBe(false);
    expect(ids(s)).toEqual([]);
  });

  test(`at most ${MAX_PINS} pins: a newer one pushes the oldest out`, () => {
    const { s } = open();
    for (const [i, id] of ["A", "B", "C"].entries()) s.applyPin(CHAT, pin(id, NOW - (4 - i) * H), NOW);
    expect(ids(s)).toEqual(["C", "B", "A"]);
    expect(s.applyPin(CHAT, pin("D", NOW - H), NOW)).toBe(true);
    expect(ids(s)).toEqual(["D", "C", "B"]);
    expect(s.activePins(CHAT, NOW)).toHaveLength(MAX_PINS);
  });

  test("an older pin arriving after the cap is full does not displace newer ones", () => {
    const { s } = open();
    for (const [i, id] of ["A", "B", "C"].entries()) s.applyPin(CHAT, pin(id, NOW - (4 - i) * H), NOW);
    expect(s.applyPin(CHAT, pin("OLD", NOW - 10 * H), NOW)).toBe(false);
    expect(ids(s)).toEqual(["C", "B", "A"]);
  });

  test("events in any order give the same result (history sync is newest first)", () => {
    const events = [pin("A", NOW - 5 * H), unpin("A", NOW - 4 * H), pin("B", NOW - 3 * H), pin("A", NOW - 2 * H)];
    const results = new Set<string>();
    for (const order of [events, [...events].reverse(), [events[1]!, events[3]!, events[0]!, events[2]!]]) {
      const { s } = open();
      for (const e of order) s.applyPin(CHAT, e, NOW);
      results.add(ids(s).join());
    }
    expect([...results]).toEqual(["A,B"]);
  });

  test("an older pin cannot bring back a message that was unpinned later", () => {
    const { s } = open();
    s.applyPin(CHAT, unpin("A", NOW - H), NOW);
    expect(s.applyPin(CHAT, pin("A", NOW - 2 * H), NOW)).toBe(false);
    expect(ids(s)).toEqual([]);
  });

  test("chats are independent", () => {
    const { s } = open();
    s.applyPin(CHAT, pin("A", NOW - H), NOW);
    s.applyPin("g@g.us", pin("B", NOW - H), NOW);
    expect(ids(s, CHAT)).toEqual(["A"]);
    expect(ids(s, "g@g.us")).toEqual(["B"]);
  });

  test("pins survive a restart, including the records that keep old events dead", () => {
    const { s, dir } = open();
    s.applyPin(CHAT, pin("A", NOW - 2 * H, 7 * 24 * H), NOW);
    s.applyPin(CHAT, pin("B", NOW - H, 7 * 24 * H), NOW);
    s.applyPin(CHAT, unpin("A", NOW - 1000), NOW);
    const before = s.pins.get(CHAT);
    s.flushSync();
    const again = open(dir).s;
    expect(again.pins.get(CHAT)).toEqual(before);
    expect(again.activePins(CHAT, NOW).map((p) => p.id)).toEqual(["B"]);
    expect(again.applyPin(CHAT, pin("A", NOW - 3 * H, 7 * 24 * H), NOW)).toBe(false);
  });

  test("a LID chat folded into its PN chat brings its pins along", () => {
    const { s } = open();
    const lid = "111@lid";
    const pn = "8490@s.whatsapp.net";
    s.applyPin(lid, pin("A", NOW - 2 * H), NOW);
    s.applyPin(pn, pin("B", NOW - H), NOW);
    let merged = "";
    s.onMerge = (to) => (merged = to);
    s.link(lid, pn);
    expect(merged).toBe(pn);
    expect(s.pins.has(lid)).toBe(false);
    expect(ids(s, pn)).toEqual(["B", "A"]);
  });

  test("deleting a chat or resetting the store drops its pins", () => {
    const { s } = open();
    s.applyPin(CHAT, pin("A", NOW - H), NOW);
    s.applyPin("g@g.us", pin("B", NOW - H), NOW);
    s.deleteChat(CHAT);
    expect(s.pins.has(CHAT)).toBe(false);
    s.reset();
    expect(s.pins.size).toBe(0);
  });
});
