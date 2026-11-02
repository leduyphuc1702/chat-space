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
