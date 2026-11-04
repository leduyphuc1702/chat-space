import { afterEach, describe, expect, test } from "bun:test";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ZaloStore, type Entry } from "./zalo-store.ts";

const dirs: string[] = [];
afterEach(() => {
  for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true });
});

function entry(chatId: string, i: number): Entry {
  return {
    m: { id: String(i), chatId, senderId: "u1", fromMe: false, timestamp: 1_700_000_000_000 + i, kind: "text", text: `msg ${i}` },
    z: { t: chatId.startsWith("g:") ? 1 : 0, msgId: String(i), cliMsgId: String(i), uidFrom: "u1", idTo: "me", msgType: "webchat", ts: String(i), ttl: 0, st: 0, at: 0, cmd: 0 },
  } as Entry;
}

describe("ZaloStore.serializeChunked", () => {
  test("produces the same document as the one-shot serialisation, with the per-chat limit applied", async () => {
    const dir = mkdtempSync(join(tmpdir(), "zalo-store-"));
    dirs.push(dir);
    const store = new ZaloStore(dir, () => {});
    store.load();
    for (let i = 0; i < 40; i++) store.insert("g:1", entry("g:1", i));
    for (let i = 0; i < 5; i++) store.insert("u:2", entry("u:2", 100 + i));
    store.chat("g:1").unread = 3;
    store.meta.activeTs = 42;
    store.setMaxPerChat(10); // group chats are trimmed on disk, 1:1 chats never

    const chunked = JSON.parse(await store.serializeChunked());
    const oneShot = JSON.parse((store as unknown as { serialize(): string }).serialize());
    expect(chunked).toEqual(oneShot);
    expect(chunked.messages["g:1"]).toHaveLength(10);
    expect(chunked.messages["u:2"]).toHaveLength(5);
    expect(chunked.chats["g:1"].unread).toBe(3);
    expect(chunked.meta.activeTs).toBe(42);
  });

  test("an empty store round-trips", async () => {
    const dir = mkdtempSync(join(tmpdir(), "zalo-store-"));
    dirs.push(dir);
    const store = new ZaloStore(dir, () => {});
    store.load();
    expect(JSON.parse(await store.serializeChunked())).toEqual(JSON.parse((store as unknown as { serialize(): string }).serialize()));
  });
});
