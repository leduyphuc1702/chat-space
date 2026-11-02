// Read-only extraction of the zproto WASM from an installed Zalo app.asar (never writes anything).
// asar layout: [u32=4][u32 headerPickleSize][u32 payloadLen][u32 jsonLen][json header][file data...]
// file data base offset = 8 + headerPickleSize; entry.offset is relative to that base.

import { closeSync, openSync, readSync } from "node:fs";

export const DEFAULT_ASAR = "/Applications/Zalo.app/Contents/Resources/app.asar";

interface AsarNode {
  files?: Record<string, AsarNode>;
  size?: number;
  offset?: string;
  unpacked?: boolean;
}

function readAt(fd: number, pos: number, len: number): Buffer {
  const buf = Buffer.alloc(len);
  let got = 0;
  while (got < len) {
    const n = readSync(fd, buf, got, len - got, pos + got);
    if (n <= 0) throw new Error("asar: short read");
    got += n;
  }
  return buf;
}

export function readWasmFromAsar(asarPath = DEFAULT_ASAR): { name: string; bytes: Uint8Array } {
  const fd = openSync(asarPath, "r"); // O_RDONLY
  try {
    const head = readAt(fd, 0, 16);
    const headerPickleSize = head.readUInt32LE(4);
    const jsonLen = head.readUInt32LE(12);
    const header: AsarNode = JSON.parse(readAt(fd, 16, jsonLen).toString("utf8"));
    const base = 8 + headerPickleSize;
    // pc-dist/libs/libzproto_wasm_bg.<hash>.wasm (the "backup" variant has a different prefix)
    const libs = header.files?.["pc-dist"]?.files?.["libs"]?.files;
    const name = libs && Object.keys(libs).find((n) => /^libzproto_wasm_bg\..+\.wasm$/.test(n));
    if (!libs || !name) throw new Error("libzproto_wasm_bg.*.wasm not found in " + asarPath);
    const node = libs[name];
    if (node.unpacked || node.offset == null) throw new Error("wasm is stored unpacked; look in app.asar.unpacked");
    return { name, bytes: new Uint8Array(readAt(fd, base + Number(node.offset), node.size!)) };
  } finally {
    closeSync(fd);
  }
}
