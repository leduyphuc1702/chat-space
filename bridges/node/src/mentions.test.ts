import { describe, expect, test } from "bun:test";
import { rewriteWhatsAppMentions, sanitizeMentions, TtlCache, waJidUser } from "./mentions.ts";

describe("sanitizeMentions", () => {
  const text = "hi @Ann and @Bob!";
  test("keeps valid spans sorted by offset", () => {
    const out = sanitizeMentions(text, [
      { userId: "2", offset: 12, length: 4 },
      { userId: "1", offset: 3, length: 4 },
    ]);
    expect(out).toEqual([
      { userId: "1", offset: 3, length: 4 },
      { userId: "2", offset: 12, length: 4 },
    ]);
  });
  test("drops malformed, out-of-range and overlapping spans", () => {
    const out = sanitizeMentions(text, [
      { userId: "", offset: 3, length: 4 },
      { userId: "x", offset: -1, length: 4 },
      { userId: "x", offset: 3, length: 0 },
      { userId: "x", offset: 1.5, length: 2 },
      { userId: "x", offset: 15, length: 10 },
      { userId: "ok", offset: 3, length: 4 },
      { userId: "overlap", offset: 5, length: 4 },
      "junk",
      null,
    ]);
    expect(out).toEqual([{ userId: "ok", offset: 3, length: 4 }]);
  });
  test("drops spans that split a surrogate pair, keeps ones covering the whole emoji", () => {
    const emoji = "a\u{1F600}b"; // a, high, low, b
    expect(sanitizeMentions(emoji, [{ userId: "1", offset: 2, length: 1 }])).toEqual([]);
    expect(sanitizeMentions(emoji, [{ userId: "1", offset: 0, length: 2 }])).toEqual([]);
    expect(sanitizeMentions(emoji, [{ userId: "1", offset: 1, length: 2 }])).toEqual([{ userId: "1", offset: 1, length: 2 }]);
  });
  test("non-arrays and empty text give nothing", () => {
    expect(sanitizeMentions(text, undefined)).toEqual([]);
    expect(sanitizeMentions(text, "x")).toEqual([]);
    expect(sanitizeMentions("", [{ userId: "1", offset: 0, length: 1 }])).toEqual([]);
  });
});

describe("waJidUser", () => {
  test("strips server and device", () => {
    expect(waJidUser("84901234567@s.whatsapp.net")).toBe("84901234567");
    expect(waJidUser("84901234567:12@s.whatsapp.net")).toBe("84901234567");
    expect(waJidUser("1234567890123@lid")).toBe("1234567890123");
    expect(waJidUser("84901234567")).toBe("84901234567");
  });
});

describe("rewriteWhatsAppMentions", () => {
  test("replaces spans last-to-first so offsets stay valid", () => {
    const text = "hey @Ann Lee, meet @Bob";
    const r = rewriteWhatsAppMentions(text, [
      { userId: "111@s.whatsapp.net", offset: text.indexOf("@Ann Lee"), length: "@Ann Lee".length },
      { userId: "222@lid", offset: text.indexOf("@Bob"), length: "@Bob".length },
    ]);
    expect(r.text).toBe("hey @111, meet @222");
    expect(r.mentionedJid).toEqual(["111@s.whatsapp.net", "222@lid"]);
  });
  test("offsets are UTF-16 code units (emoji before the mention)", () => {
    const text = "😀 @Ann";
    // "😀" is 2 code units, then a space: the mention starts at 3 and is 4 long.
    const r = rewriteWhatsAppMentions(text, [{ userId: "111@s.whatsapp.net", offset: 3, length: 4 }]);
    expect(r.text).toBe("😀 @111");
  });
  test("same user twice is listed once; bare numbers get the PN server; device part is not in the text", () => {
    const r = rewriteWhatsAppMentions("@A @A", [
      { userId: "84901234567:5@s.whatsapp.net", offset: 0, length: 2 },
      { userId: "84901234567:5@s.whatsapp.net", offset: 3, length: 2 },
    ]);
    expect(r.text).toBe("@84901234567 @84901234567");
    expect(r.mentionedJid).toEqual(["84901234567:5@s.whatsapp.net"]);
    expect(rewriteWhatsAppMentions("@A", [{ userId: "84901234567", offset: 0, length: 2 }]).mentionedJid).toEqual([
      "84901234567@s.whatsapp.net",
    ]);
  });
  test("no mentions leaves the text alone", () => {
    expect(rewriteWhatsAppMentions("plain", [])).toEqual({ text: "plain", mentionedJid: [] });
  });
});

describe("TtlCache", () => {
  test("serves from cache until the ttl passes", () => {
    const c = new TtlCache<number>(1000);
    c.set("a", 1, 0);
    expect(c.get("a", 999)).toBe(1);
    expect(c.get("a", 1001)).toBeUndefined();
  });
  test("getOrLoad shares one in-flight load and caches the result", async () => {
    const c = new TtlCache<number>(60_000);
    let calls = 0;
    const load = async () => {
      calls++;
      await Promise.resolve();
      return 7;
    };
    const [a, b] = await Promise.all([c.getOrLoad("k", load), c.getOrLoad("k", load)]);
    expect([a, b, await c.getOrLoad("k", load), calls]).toEqual([7, 7, 7, 1]);
  });
  test("failed loads are not cached", async () => {
    const c = new TtlCache<number>(60_000);
    await expect(c.getOrLoad("k", async () => Promise.reject(new Error("boom")))).rejects.toThrow("boom");
    expect(await c.getOrLoad("k", async () => 3)).toBe(3);
  });
  test("clear() during a load keeps the stale result out of the cache", async () => {
    const c = new TtlCache<number>(60_000);
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const pending = c.getOrLoad("k", async () => {
      await gate;
      return 1;
    });
    c.clear();
    release();
    expect(await pending).toBe(1);
    expect(c.get("k")).toBeUndefined();
  });
  test("evicts the oldest entry past maxEntries", () => {
    const c = new TtlCache<number>(60_000, 2);
    c.set("a", 1);
    c.set("b", 2);
    c.set("c", 3);
    expect([c.get("a"), c.get("b"), c.get("c")]).toEqual([undefined, 2, 3]);
  });
});
