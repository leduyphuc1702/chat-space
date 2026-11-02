// Decoding of server -> client WS frames outside zca-js (same rules as its utils.decodeEventData):
// body = {data, encrypt}; encrypt 0 = JSON text, 1 = base64 deflate, 2 = urlencoded base64 AES-GCM + deflate,
// 3 = urlencoded base64 AES-GCM without compression. AES-GCM layout: iv(16) | aad(16) | ciphertext+tag.

import { inflateRawSync, unzipSync } from "node:zlib";

export function frameHeader(buf: Uint8Array) {
  if (buf.byteLength < 4) return undefined;
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  return { version: dv.getUint8(0), cmd: dv.getUint16(1, true), subCmd: dv.getUint8(3) };
}

export function frameJson(buf: Uint8Array): any {
  if (buf.byteLength <= 4) return undefined;
  const text = new TextDecoder().decode(buf.subarray(4));
  return text ? JSON.parse(text) : undefined;
}

export async function decodeBody(parsed: any, cipherKey: string | undefined): Promise<any> {
  if (typeof parsed?.data !== "string" || typeof parsed?.encrypt !== "number") return parsed;
  const enc = parsed.encrypt as number;
  if (enc === 0) return JSON.parse(parsed.data);
  let bytes: Uint8Array<ArrayBuffer> = Buffer.from(enc === 1 ? parsed.data : decodeURIComponent(parsed.data), "base64");
  if (enc !== 1) {
    if (!cipherKey || bytes.length < 48) throw new Error("missing cipher key or short payload");
    const algorithm = { name: "AES-GCM", iv: bytes.subarray(0, 16), tagLength: 128, additionalData: bytes.subarray(16, 32) };
    const key = await crypto.subtle.importKey("raw", Buffer.from(cipherKey, "base64"), algorithm, false, ["decrypt"]);
    bytes = new Uint8Array(await crypto.subtle.decrypt(algorithm, key, bytes.subarray(32)));
  }
  const text = new TextDecoder().decode(enc === 3 ? bytes : inflate(bytes));
  return text ? JSON.parse(text) : undefined;
}

function inflate(b: Uint8Array): Uint8Array {
  if (b[0] === 0x7b || b[0] === 0x5b) return b; // already JSON
  try {
    return unzipSync(b); // zlib or gzip
  } catch {}
  try {
    return inflateRawSync(b);
  } catch {}
  throw new Error(`not deflate (len=${b.length}, magic=${Buffer.from(b.subarray(0, 2)).toString("hex")})`);
}
