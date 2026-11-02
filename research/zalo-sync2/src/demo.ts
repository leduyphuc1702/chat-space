// Offline demo / self-test. No network access of any kind.
//   bun run src/demo.ts [path/to/libzproto_wasm_bg.wasm]
//
// Default wasm path is the extracted copy under /tmp/zalo-re. In production load it at runtime from
// /Applications/Zalo.app/Contents/Resources/app.asar (unpacked) - it is not redistributed.

import { existsSync } from "node:fs";
import { DEFAULT_ASAR, readWasmFromAsar } from "./asar";
import { Zproto } from "./zproto";
import {
  ConversationSyncChunk,
  ConvType,
  MessageStatus,
  MessageSyncChunk,
  PlatformType,
  decode,
  encode,
} from "./proto";
import {
  BatchType,
  CMD,
  PhoneSimulator,
  Sync2Receiver,
  TaskPriority,
  classifyControl,
  decodeFrame,
  partitionOf,
  readRecords,
  writeRecords,
  zstdCompress,
  zstdDecompress,
} from "./sync2";

const DEFAULT_WASM = "/tmp/zalo-re/app_extracted/pc-dist/libs/libzproto_wasm_bg.ecc0555ba9d0f8867a4699ce943ec745.wasm";
// `--from-app [app.asar]` reads the module straight out of the installed app (read-only).
let wasmSource: string | Uint8Array;
if (process.argv[2] === "--from-app") {
  const { name, bytes } = readWasmFromAsar(process.argv[3] ?? DEFAULT_ASAR);
  console.log(`loaded ${name} (${bytes.length} bytes) from app.asar, sha256 ${new Bun.CryptoHasher("sha256").update(bytes).digest("hex").slice(0, 16)}...`);
  wasmSource = bytes;
} else {
  wasmSource = process.argv[2] ?? DEFAULT_WASM;
  if (!existsSync(wasmSource)) {
    console.error("wasm not found:", wasmSource);
    process.exit(2);
  }
}

let failures = 0;
const ok = (name: string, cond: boolean, extra = "") => {
  console.log(`${cond ? "PASS" : "FAIL"}  ${name}${extra ? "  (" + extra + ")" : ""}`);
  if (!cond) failures++;
};
const throws = (name: string, fn: () => unknown) => {
  try {
    fn();
    ok(name, false, "did not throw");
  } catch (e: any) {
    ok(name, true, String(e?.message ?? e).slice(0, 60));
  }
};
const eq = (a: Uint8Array, b: Uint8Array) => Buffer.from(a).equals(Buffer.from(b));
const te = new TextEncoder();
const td = new TextDecoder();

const z = await Zproto.load(wasmSource);

// ---------------------------------------------------------------------------
console.log("\n== 1. keys ==");
const ik = z.generateKeyPair(); // identity key: long-lived per account/device (IdentityKeyManager Bw1J)
const ek = z.generateKeyPair(); // ephemeral key: per sync session
ok("public key is 33 bytes (0x05 prefix), private 32", ik.publicKey.length === 33 && ik.privateKey.length === 32 && ik.publicKey[0] === 5);
ok("IK and EK differ", !eq(ik.publicKey, ek.publicKey));
const agree1 = z.calculateAgreement(ek.privateKey, ik.publicKey);
const agree2 = z.calculateAgreement(ik.privateKey, ek.publicKey);
ok("DH agreement is symmetric", eq(agree1, agree2), `${agree1.length} bytes`);

// ---------------------------------------------------------------------------
console.log("\n== 2. metadata cipher + message cipher round trip (phone simulated with the same WASM) ==");
const phone = z.generateKeyPair();
const md = z.createMetadataCipher(te.encode("first chunk"), phone, ik.publicKey, ek.publicKey);
ok("metadata ciphertext starts with protobuf field1(32B sessionId)", md.ciphertext[0] === 0x0a && md.ciphertext[1] === 0x20);
const mdd = z.decryptMetadata(md.ciphertext, ik, ek.privateKey);
ok("decryptMetadata plaintext", td.decode(mdd.plaintext) === "first chunk");
ok("sessionId matches on both sides", md.sessionId === mdd.sessionId, md.sessionId);
ok("sessionRecord matches on both sides", eq(md.sessionRecord, mdd.sessionRecord), `${md.sessionRecord.length} bytes`);
ok("senderIdentity == phone IK public", eq(mdd.senderIdentity, phone.publicKey));
ok("receiverIdentity == our IK public", eq(md.receiverIdentity, ik.publicKey));
const m1 = z.encryptMessage(te.encode("batch payload"), md.sessionRecord);
const m2 = z.encryptMessage(te.encode("batch payload"), md.sessionRecord);
ok("encryptMessage is randomized", !eq(m1.ciphertext, m2.ciphertext));
ok("decryptMessage", td.decode(z.decryptMessage(m1.ciphertext, mdd.sessionRecord).plaintext) === "batch payload");
ok("records are independent (any order, stateless)", td.decode(z.decryptMessage(m2.ciphertext, mdd.sessionRecord).plaintext) === "batch payload");
const other = z.generateKeyPair();
throws("wrong EK private key rejected", () => z.decryptMetadata(md.ciphertext, ik, other.privateKey));
throws("wrong identity key rejected", () => z.decryptMetadata(md.ciphertext, other, ek.privateKey));
const bad = m1.ciphertext.slice();
bad[bad.length - 1] ^= 1;
throws("tampered message rejected (AEAD)", () => z.decryptMessage(bad, mdd.sessionRecord));

// ---------------------------------------------------------------------------
console.log("\n== 3. protobuf round trip (own codec) + independent cross-check (protobufjs) ==");
const GLOBAL_ID = "9223372036854775806"; // > 2^53: must stay a string
const syntheticMessages = {
  platform: PlatformType.PC,
  partitionsList: [
    {
      id: partitionOf("gid_peer_001", false),
      messagesList: [
        {
          senderId: "gid_peer_001",
          clientId: 1730000000123,
          msgStatus: MessageStatus.RECEIVED,
          msgType: 1,
          timestamp: 1730000000456,
          globalId: GLOBAL_ID,
          content: "xin chào, đây là tin nhắn thử 🙂",
          meta: {
            attachsList: [
              { type: "photo", extInfo: "{}", action: "", params: "{\"w\":10}", title: "t", href: "https://example.invalid/x", thumb: "", description: "", catId: -1, id: 7, childNumber: 0, remains: "", zinstantData: "", zinstantMsg: "" },
            ],
            mentionsList: [{ uid: "gid_peer_002", pos: 3, len: 5, type: 0 }],
            reference: { type: 2, data: "ref" },
            quote: { attach: "", ownerId: "gid_peer_002", ts: 1729999999000, fromD: "Alice", cliMsgId: 42, globalMsgId: "1234567890123456789", msg: "quoted", cliMsgType: 1, ttl: 0, partialQuote: "" },
            property: { size: 14, color: -16777216, type: 1, subType: 0, ext: "" },
            ttl: 86400,
          },
        },
        { senderId: "gid_me", clientId: 2, msgStatus: MessageStatus.SENT, msgType: 4, timestamp: 1730000001000, globalId: "5", content: "second", meta: undefined },
      ],
    },
    { id: partitionOf("gid_group_777", true), messagesList: [{ senderId: "gid_peer_003", clientId: 9, msgStatus: 0, msgType: 1, timestamp: 1730000002000, globalId: "6", content: "hi group", meta: undefined }] },
  ],
};
const wire = encode(MessageSyncChunk, syntheticMessages);
const back = decode(MessageSyncChunk, wire);
const norm = (o: any): any =>
  JSON.parse(JSON.stringify(o, (_k, v) => (v === undefined ? undefined : v)));
// decode() fills proto3 defaults for every scalar; compare only fields present in the input (non-default)
const subset = (want: any, got: any, path = ""): string | null => {
  if (Array.isArray(want)) {
    if (!Array.isArray(got) || got.length !== want.length) return `${path}: length`;
    for (let i = 0; i < want.length; i++) {
      const r = subset(want[i], got[i], `${path}[${i}]`);
      if (r) return r;
    }
    return null;
  }
  if (want && typeof want === "object") {
    for (const k of Object.keys(want)) {
      if (want[k] === undefined) continue;
      const r = subset(want[k], got?.[k], `${path}.${k}`);
      if (r) return r;
    }
    return null;
  }
  return want === got ? null : `${path}: ${want} != ${got}`;
};
const diff = subset(norm(syntheticMessages), back);
ok("message SyncChunk survives encode->decode", diff === null, diff ?? `${wire.length} bytes`);
ok("globalId > 2^53 preserved as decimal string", back.partitionsList[0].messagesList[0].globalId === GLOBAL_ID);
ok("negative int32 (color) preserved", back.partitionsList[0].messagesList[0].meta.property.color === -16777216);

const syntheticConvs = {
  platform: PlatformType.PC,
  conversationsList: [
    { convId: "gid_peer_001", convType: ConvType.ONEONE, respondedByMe: true, lastTs: 1730000001000, lastGlobalId: "5", lastClientId: 2 },
    { convId: "gid_group_777", convType: ConvType.GROUP, respondedByMe: false, lastTs: 1730000002000, lastGlobalId: "6", lastClientId: 9 },
  ],
};
const cwire = encode(ConversationSyncChunk, syntheticConvs);
ok("conversation SyncChunk survives encode->decode", subset(norm(syntheticConvs), decode(ConversationSyncChunk, cwire)) === null, `${cwire.length} bytes`);

// independent cross-check with protobufjs (read-only import of the bridge's node_modules)
try {
  const pbjs: any = await import("/Volumes/DATA/chat-space/bridges/node/node_modules/protobufjs/index.js");
  const root = pbjs.Root.fromJSON({
    nested: {
      T: {
        nested: {
          Mention: { fields: { uid: { id: 1, type: "string" }, pos: { id: 2, type: "int32" }, len: { id: 3, type: "int32" }, type: { id: 4, type: "int32" } } },
          Properties: { fields: { size: { id: 1, type: "int32" }, color: { id: 2, type: "int32" }, type: { id: 3, type: "int32" }, subType: { id: 4, type: "int32" }, ext: { id: 5, type: "string" } } },
          MetaData: { fields: { mentions: { rule: "repeated", id: 2, type: "Mention" }, property: { id: 5, type: "Properties" }, ttl: { id: 6, type: "uint64" } } },
          Message: { fields: { senderId: { id: 1, type: "string" }, clientId: { id: 2, type: "uint64" }, msgStatus: { id: 3, type: "int32" }, msgType: { id: 4, type: "int32" }, timestamp: { id: 5, type: "uint64" }, globalId: { id: 6, type: "int64" }, content: { id: 7, type: "string" }, meta: { id: 8, type: "MetaData" } } },
          Partition: { fields: { id: { id: 1, type: "string" }, messages: { rule: "repeated", id: 2, type: "Message" } } },
          SyncChunk: { fields: { platform: { id: 1, type: "int32" }, partitions: { rule: "repeated", id: 2, type: "Partition" } } },
        },
      },
    },
  });
  const SyncChunkT = root.lookupType("T.SyncChunk");
  const viaPbjs = SyncChunkT.toObject(SyncChunkT.decode(wire), { longs: String, defaults: true });
  const m0 = viaPbjs.partitions[0].messages[0];
  ok("protobufjs decodes our wire bytes (ids/content/ints)", m0.globalId === GLOBAL_ID && m0.content.startsWith("xin chào") && m0.meta.property.color === -16777216 && m0.meta.mentions[0].uid === "gid_peer_002" && viaPbjs.partitions[1].id === "group/gid_group_777");
  // and the reverse: bytes produced by protobufjs decode with our codec
  const msg = SyncChunkT.fromObject({ platform: 3, partitions: [{ id: "oneone/x", messages: [{ senderId: "s", clientId: "77", timestamp: "1730000000999", globalId: "-5", content: "from pbjs", msgType: 1, meta: { property: { color: -2 } } }] }] });
  const pbBytes = SyncChunkT.encode(msg).finish();
  const mine = decode(MessageSyncChunk, pbBytes);
  const mm = mine.partitionsList[0].messagesList[0];
  ok("our codec decodes protobufjs bytes (neg int64 string, neg int32)", mm.content === "from pbjs" && mm.globalId === "-5" && mm.clientId === 77 && mm.meta.property.color === -2);
} catch (e: any) {
  console.log("SKIP  protobufjs cross-check:", e?.message ?? e);
}

// ---------------------------------------------------------------------------
console.log("\n== 4. zstd ==");
const big = te.encode("zalo ".repeat(5000));
const zc = zstdCompress(big);
ok("zstd magic 28 b5 2f fd", zc[0] === 0x28 && zc[1] === 0xb5 && zc[2] === 0x2f && zc[3] === 0xfd);
ok("zstd round trip", eq(zstdDecompress(zc), big), `${big.length} -> ${zc.length} bytes`);

// ---------------------------------------------------------------------------
console.log("\n== 5. record framing (u32-LE length prefix) ==");
const recs = [te.encode("a"), new Uint8Array(0), te.encode("hello world"), crypto.getRandomValues(new Uint8Array(1000))];
const framed = writeRecords(recs);
const parsed = readRecords(framed);
ok("framing round trip incl. empty record", parsed.length === recs.length && parsed.every((r, i) => eq(r, recs[i])));
ok("first 4 bytes are little-endian length", framed[0] === 1 && framed[1] === 0 && framed[2] === 0 && framed[3] === 0);
throws("truncated blob detected", () => readRecords(framed.subarray(0, framed.length - 1)));

// ---------------------------------------------------------------------------
console.log("\n== 6. end to end: request frame -> metadata blob -> message blobs -> decoded messages ==");
const clientIk = z.generateKeyPair(); // would be persisted per account (IdentityKeyManager)
const rx = new Sync2Receiver(z, clientIk);
const req = rx.buildRequest({
  type: "message",
  priority: TaskPriority.HIGH,
  queries: [{ partition: partitionOf("gid_peer_001", false), from: 0, to: Number.MAX_SAFE_INTEGER, limit: 2147483647 }],
});
const frame = rx.buildRequestFrame(req, "req_1");
const fr = decodeFrame(frame);
ok("cmd 590 / subCmd 0 / version 1 frame", fr.cmd === CMD.SYNC_MESSAGE_REQUEST && fr.subCmd === 0 && fr.version === 1);
ok("frame body = {data:{...}, reqId}", fr.body.reqId === "req_1" && fr.body.data.syncId === rx.syncId && fr.body.data.req.queries[0].partition === "oneone/gid_peer_001");
ok("ek/ik are base64 of 33-byte keys", Buffer.from(fr.body.data.ek, "base64").length === 33 && Buffer.from(fr.body.data.ik, "base64").length === 33);

const sim = new PhoneSimulator(z); // consumes the request exactly as received over the wire
const firstChunk = { platform: PlatformType.MOBILE, partitionsList: [syntheticMessages.partitionsList[0]] };
const metaBlob = sim.makeMetadataBlob(fr.body.data, firstChunk);
const msgBlob = sim.makeMessageBlob("message", [
  { platform: PlatformType.MOBILE, partitionsList: [syntheticMessages.partitionsList[1]] },
  { platform: PlatformType.MOBILE, partitionsList: [{ id: "oneone/gid_peer_001", messagesList: [{ senderId: "gid_peer_001", clientId: 100, msgStatus: 4, msgType: 1, timestamp: 1730000003000, globalId: "100", content: "later message" }] }] },
]);
// the real client sees these as upload_batch control events pointing at msgUrl blobs; the metadata
// batch (batchType 2) MUST be decrypted before any batchType 1 batch.
throws("message batch before metadata batch is rejected", () => rx.loadBlob(msgBlob, BatchType.Message, "message"));
const metaOut = rx.loadBlob(metaBlob, BatchType.MetadataEmbedded, "message");
ok("metadata batch yields the embedded first chunk", metaOut.length === 1 && metaOut[0].partition === "oneone/gid_peer_001" && metaOut[0].data[0].content.startsWith("xin chào"));
const msgOut = rx.loadBlob(msgBlob, BatchType.Message, "message");
ok("message batch decodes (2 chunks -> 2 partitions)", msgOut.length === 2 && msgOut[0].partition === "group/gid_group_777" && msgOut[1].data[0].content === "later message");

// conversation list flows through the same machinery, different schema, partition ""
const rxC = new Sync2Receiver(z, clientIk);
const reqC = rxC.buildRequest({ type: "conversation", priority: TaskPriority.HIGH, queries: [{ partition: "", from: 0, to: Number.MAX_SAFE_INTEGER, limit: 2147483647 }] });
const simC = new PhoneSimulator(z);
const convBlob = simC.makeMetadataBlob(reqC, syntheticConvs);
const convOut = rxC.loadBlob(convBlob, BatchType.MetadataEmbedded, "conversation");
ok("conversation batch decodes", convOut[0].partition === "" && convOut[0].data.length === 2 && convOut[0].data[1].convType === ConvType.GROUP);

// ---------------------------------------------------------------------------
console.log("\n== 7. control event classification (synthetic cmd-601 contents) ==");
const IMEI = "00000000-0000-0000-0000-000000000000";
const c1 = classifyControl({ act_type: "transfer_msg", act: "transfer_after_login", data: JSON.stringify({ temp_key: "TK", imei: IMEI }), ts: 5 }, IMEI);
ok("transfer_after_login with matching imei -> tempKey", c1.kind === "tempKey" && c1.tempKey === "TK");
ok("transfer_after_login for another device ignored", classifyControl({ act_type: "transfer_msg", act: "transfer_after_login", data: { temp_key: "TK", imei: "other" } }, IMEI).kind !== "tempKey");
const c2 = classifyControl({ act_type: "transfer_sync2", act: "transfer_status", data: JSON.stringify({ status: 4, fromDevice: 0, syncId: rx.syncId }) }, IMEI);
ok("transfer_status Confirmed from mobile", c2.kind === "status" && c2.status === 4 && c2.syncId === rx.syncId);
const c3 = classifyControl({ act_type: "transfer_sync2", act: "upload_batch", data: JSON.stringify({ syncId: rx.syncId, idx: 0, isLast: false, err: 0, batchType: 2, msgUrl: "https://example.invalid/blob", scopes: [{ partition: "oneone/gid_peer_001", from: 0, to: 0 }] }) }, IMEI);
ok("upload_batch err=0 -> batch", c3.kind === "batch" && c3.batchType === BatchType.MetadataEmbedded && c3.scopes[0].partition === "oneone/gid_peer_001");
const c4 = classifyControl({ act_type: "transfer_sync2", act: "upload_batch", data: { syncId: "s", idx: 3, err: 702, batchType: 1 } }, IMEI);
ok("upload_batch err!=0 -> error", c4.kind === "error");

console.log(`\n${failures === 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED"}`);
process.exit(failures === 0 ? 0 : 1);
