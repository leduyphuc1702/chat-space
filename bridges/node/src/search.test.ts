import { expect, test } from "bun:test";
import { fold, Matcher, parseSearch, searchLocal } from "./search.ts";
import type { Message } from "./types.ts";

const msg = (id: string, chatId: string, ts: number, text?: string, extra: Partial<Message> = {}): Message => ({
  id,
  chatId,
  senderId: "u",
  fromMe: false,
  timestamp: ts,
  kind: "text",
  ...(text === undefined ? {} : { text }),
  ...extra,
});

test("fold drops case, accents and đ", () => {
  expect(fold("Hợp Đồng ĐÃ KÝ")).toBe("hop dong da ky");
});

test("every word must appear, in any order and accent-free", () => {
  const m = new Matcher(parseSearch({ query: "dong hop" }));
  expect(m.test(msg("1", "c", 1, "Gửi anh bản hợp đồng nhé"))).toBe(true);
  expect(m.test(msg("2", "c", 1, "hợp tác"))).toBe(false);
});

test("file names, kinds, links, deleted and page bounds", () => {
  const file = msg("1", "c", 10, undefined, { kind: "file", media: { fileName: "Báo giá Q3.xlsx" } });
  expect(new Matcher(parseSearch({ query: "bao gia" })).test(file)).toBe(true);
  expect(new Matcher(parseSearch({ query: "bao", kinds: ["image"] })).test(file)).toBe(false);
  expect(new Matcher(parseSearch({ kinds: ["link"] })).test(msg("2", "c", 1, "xem https://example.com"))).toBe(true);
  expect(new Matcher(parseSearch({ query: "x" })).test(msg("3", "c", 1, "x", { deleted: true }))).toBe(false);
  expect(new Matcher(parseSearch({ query: "x", before: 5 })).test(msg("4", "c", 5, "x"))).toBe(false);
  expect(() => parseSearch({ query: "  " })).toThrow();
});

test("searchLocal returns newest first across chats, capped, and notices in-place edits", () => {
  const a = [msg("a1", "A", 1, "lunch"), msg("a2", "A", 5, "lunch?")];
  const b = [msg("b1", "B", 3, "Lunch time"), msg("b2", "B", 4, "nope")];
  const run = (q: string, limit = 50, chatId?: string) =>
    searchLocal(
      [
        ["A", a],
        ["B", b],
      ],
      parseSearch({ query: q, limit, chatId }),
      (e) => e,
      (e) => [e.text, e.deleted],
    ).map((m) => m.id);
  expect(run("lunch")).toEqual(["a2", "b1", "a1"]);
  expect(run("lunch", 2)).toEqual(["a2", "b1"]);
  expect(run("lunch", 50, "B")).toEqual(["b1"]);
  b[1]!.text = "lunch after all";
  expect(run("lunch", 50, "B")).toEqual(["b2", "b1"]);
});

test("senderId alone is a valid search; kinds, query and chat narrow it further", () => {
  expect(parseSearch({ senderId: "u1" })).toMatchObject({ query: "", senderId: "u1", kinds: undefined });
  expect(() => parseSearch({ senderId: "" })).toThrow();
  const mine = msg("1", "c", 10, "hello", { senderId: "u1" });
  const other = msg("2", "c", 11, "hello", { senderId: "u2" });
  const photo = msg("3", "c", 12, undefined, { senderId: "u1", kind: "image" });
  const sys = msg("4", "c", 13, "u1 joined", { senderId: "u1", kind: "system" });
  expect(new Matcher(parseSearch({ senderId: "u1" })).test(mine)).toBe(true);
  expect(new Matcher(parseSearch({ senderId: "u1" })).test(other)).toBe(false);
  expect(new Matcher(parseSearch({ senderId: "u1" })).test(sys)).toBe(false); // system lines are left out as usual
  expect(new Matcher(parseSearch({ senderId: "u1", kinds: ["image"] })).test(photo)).toBe(true);
  expect(new Matcher(parseSearch({ senderId: "u1", kinds: ["image"] })).test(mine)).toBe(false);
  expect(new Matcher(parseSearch({ senderId: "u1", query: "hello" })).test(mine)).toBe(true);
  expect(new Matcher(parseSearch({ senderId: "u2", query: "hello" })).test(mine)).toBe(false);
  expect(new Matcher(parseSearch({ query: "hello" })).test(other)).toBe(true); // no senderId: everybody
});

test("searchLocal with senderId + kinds + chatId and a custom same-person check", () => {
  const a = [msg("a1", "A", 1, undefined, { senderId: "pn", kind: "image" }), msg("a2", "A", 2, undefined, { senderId: "lid", kind: "image" }), msg("a3", "A", 3, undefined, { senderId: "x", kind: "image" })];
  const b = [msg("b1", "B", 4, undefined, { senderId: "pn", kind: "image" })];
  const run = (p: Record<string, any>, same?: (a: string, b: string) => boolean) =>
    searchLocal(
      [
        ["A", a],
        ["B", b],
      ],
      parseSearch(p),
      (e) => e,
      (e) => [e.text],
      same,
    ).map((m) => m.id);
  expect(run({ senderId: "pn", kinds: ["image"] })).toEqual(["b1", "a1"]);
  expect(run({ senderId: "pn", kinds: ["image"], chatId: "A" })).toEqual(["a1"]);
  const samePerson = (m: string, wanted: string) => m === wanted || (m === "lid" && wanted === "pn");
  expect(run({ senderId: "pn", kinds: ["image"], chatId: "A" }, samePerson)).toEqual(["a2", "a1"]);
  expect(run({ senderId: "pn", kinds: ["image"], before: 4 }, samePerson)).toEqual(["a2", "a1"]);
});
