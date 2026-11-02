// Sync v2 building blocks: record framing, zstd, request payload, WS frames, receiver pipeline,
// and a PHONE SIMULATOR used to exercise the receiver offline.
//
// Every "reads:" comment points to the place in Zalo PC 26.10.20 (pc-dist/*) the behaviour was
// taken from; "inferred:" marks guesses. See SPEC.md for the full list.

import { Zproto, type KeyPair } from "./zproto";
import {
  ConversationSyncChunk,
  MessageSyncChunk,
  PlatformType,
  decode,
  encode,
} from "./proto";

// ---- enums (compact-app-pc module PL82 / SCpg / o7eC) ----------------------
export const BatchType = { Message: 1, MetadataEmbedded: 2 } as const;
export const MobileStatus = {
  Active: 1,
  Idle: 2,
  WaitingConfirm: 3,
  Confirmed: 4,
  BypassConfirmed: 5,
  UserReject: 6,
  UserCancel: 7,
  MasterDeviceBusy: 8,
  MasterDeviceLowStorage: 9,
  MasterDeviceLogout: 10,
} as const;
export const FromDevice = { Mobile: 0, Web: 1, PC: 2 } as const;
export const SyncType = { SyncFull: 0, SyncOnDemand: 1 } as const;
export const TaskPriority = { HIGH: 0, NORMAL: 1, LOW: 2 } as const;
export type DbType = "conversation" | "message";

// ---- record framing --------------------------------------------------------
// reads: shared-worker.pp.js class Ql (line 16290): u32 little-endian length + payload, repeated.
export function readRecords(bin: Uint8Array): Uint8Array[] {
  const dv = new DataView(bin.buffer, bin.byteOffset, bin.byteLength);
  const out: Uint8Array[] = [];
  let ptr = 0;
  while (ptr < bin.byteLength) {
    const len = dv.getUint32(ptr, true);
    const start = ptr + 4;
    if (start + len > bin.byteLength) throw new Error(`record overruns blob (len=${len}, at=${ptr})`);
    out.push(bin.slice(start, start + len));
    ptr = start + len;
  }
  return out;
}
export function writeRecords(records: Uint8Array[]): Uint8Array {
  const total = records.reduce((n, r) => n + 4 + r.length, 0);
  const out = new Uint8Array(total);
  const dv = new DataView(out.buffer);
  let p = 0;
  for (const r of records) {
    dv.setUint32(p, r.length, true);
    out.set(r, p + 4);
    p += 4 + r.length;
  }
  return out;
}

// ---- zstd ------------------------------------------------------------------
// reads: shared-worker Codec class (line ~10640): zstd-codec ZstdStream.compress/decompress,
// i.e. a standard zstd frame. Bun ships a native implementation.
export const zstdCompress = (b: Uint8Array): Uint8Array => Bun.zstdCompressSync(b);
export const zstdDecompress = (b: Uint8Array): Uint8Array => Bun.zstdDecompressSync(b);

const b64 = (u: Uint8Array) => Buffer.from(u).toString("base64");
const unb64 = (s: string) => new Uint8Array(Buffer.from(s, "base64"));

// ---- WS frame ---------------------------------------------------------------
// reads: compact.pp.js WS manager _send (line 55871): u8 version, u16-LE cmd, u8 subCmd, JSON body.
// zca-js listen.js sendWs writes cmd as Int32LE at offset 1 then overwrites byte 3 with subCmd: same bytes
// for cmd < 65536.
export function encodeFrame(cmd: number, subCmd: number, body: unknown, version = 1): Uint8Array {
  const json = new TextEncoder().encode(JSON.stringify(body));
  const out = new Uint8Array(4 + json.length);
  const dv = new DataView(out.buffer);
  dv.setUint8(0, version);
  dv.setUint16(1, cmd, true);
  dv.setUint8(3, subCmd);
  out.set(json, 4);
  return out;
}
export function decodeFrame(buf: Uint8Array) {
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  return {
    version: dv.getUint8(0),
    cmd: dv.getUint16(1, true),
    subCmd: dv.getUint8(3),
    body: buf.byteLength > 4 ? JSON.parse(new TextDecoder().decode(buf.subarray(4))) : null,
  };
}

export const CMD = {
  SYNC_MESSAGE_REQUEST: 590,
  ACK_DELETE_SYNC_SESSION: 591,
  REQUEST_MOBILE_WAKE_UP: 592,
  CTRL_PUSH_CTRL: 601,
} as const;

// ---- request payload (cmd 590) ---------------------------------------------
export interface SyncQuery {
  partition: string;
  from: number;
  to: number;
  limit: number;
}
export interface SyncRequestData {
  syncId: string;
  syncType: number;
  ek: string;
  ik: string;
  toDevice: number;
  tempKey: string;
  deviceName: string;
  req: { type: DbType; priority: number; queries: SyncQuery[]; batchSize: number };
  ver: 1;
  ussidx: number;
}

const rndStr = (n: number) => {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  const r = crypto.getRandomValues(new Uint8Array(n));
  return Array.from(r, (x) => alphabet[x % alphabet.length]).join("");
};

/** One receiver-side sync session: owns the per-request EK, uses the long-lived IK. */
export class Sync2Receiver {
  readonly syncId = rndStr(32); // reads: Ko ctor, Ot.i.generateRandomString(32)
  private ek!: KeyPair;
  private sessionRecord: Uint8Array | null = null;

  constructor(private z: Zproto, private ik: KeyPair) {
    this.ek = z.generateKeyPair(); // reads: Ko ctor -> syncProto.genSyncKeyPair() => {ik, ek}
  }

  /** true once a MetadataEmbedded batch established the session, so Message batches can be decrypted */
  get ready() {
    return this.sessionRecord != null;
  }

  /** reads: Ko.requestSync (shared-worker.pp.js 12313). */
  buildRequest(opts: {
    type: DbType;
    priority: number;
    queries: SyncQuery[];
    tempKey?: string;
    deviceName?: string;
    batchSize?: number;
    ussidx?: number;
  }): SyncRequestData {
    return {
      syncId: this.syncId,
      syncType: SyncType.SyncFull,
      ek: b64(this.ek.publicKey),
      ik: b64(this.ik.publicKey),
      toDevice: FromDevice.Mobile,
      tempKey: opts.tempKey ?? "", // "" for manual sync; temp_key from transfer_after_login for auto sync
      deviceName: opts.deviceName ?? "ChatSpace",
      req: {
        type: opts.type,
        priority: opts.priority,
        queries: opts.queries,
        batchSize: opts.batchSize ?? 2000, // transfer_batch_size default
      },
      ver: 1,
      ussidx: opts.ussidx ?? 0,
    };
  }

  /** WS frame for cmd 590. reqId goes *inside* the JSON body next to `data` (PC requestAsyncV2). */
  buildRequestFrame(data: SyncRequestData, reqId: string): Uint8Array {
    return encodeFrame(CMD.SYNC_MESSAGE_REQUEST, 0, { data, reqId });
  }

  /** reads: Ko.decryptChunk (12281). */
  decryptRecord(record: Uint8Array, batchType: number): Uint8Array {
    if (batchType === BatchType.MetadataEmbedded) {
      const d = this.z.decryptMetadata(record, this.ik, this.ek.privateKey);
      this.sessionRecord = d.sessionRecord;
      return d.plaintext;
    }
    if (batchType === BatchType.Message) {
      if (!this.sessionRecord) throw new Error("message batch before metadata batch (app waits up to 60s)");
      return this.z.decryptMessage(record, this.sessionRecord).plaintext;
    }
    throw new Error("Unsupport batch type!");
  }

  /**
   * reads: uc.load (13164): for each record: decrypt -> zstd decompress -> protobuf deserialize.
   * `db` selects the chunk schema (message DB vs conversation DB).
   */
  loadBlob(blob: Uint8Array, batchType: number, db: DbType) {
    const out: Array<{ partition: string; data: any[] }> = [];
    for (const rec of readRecords(blob)) {
      const plain = this.decryptRecord(rec, batchType);
      const raw = zstdDecompress(plain);
      if (db === "message") {
        const chunk = decode(MessageSyncChunk, raw);
        for (const p of chunk.partitionsList) out.push({ partition: p.id, data: p.messagesList });
      } else {
        const chunk = decode(ConversationSyncChunk, raw);
        out.push({ partition: "", data: chunk.conversationsList });
      }
    }
    return out;
  }
}

// ---- control events (cmd 601) ----------------------------------------------
// reads: main-startup SyncEventsHandler (lines 116990-117206) + compact _processCtrlMessages (576493).
// A cmd-601 frame body is { controls: [{ content: { act_type, act, data, ts? } }], saveActionId? };
// `data` is a JSON string for sync controls (parsed when it starts with "{" or "[").
export interface CtrlContent {
  act_type: string;
  act: string;
  data: any;
  ts?: number;
}
export type SyncCtrl =
  | { kind: "tempKey"; tempKey: string; imei: string; ts: number }
  | { kind: "status"; status: number; syncId?: string; data: any }
  | { kind: "batch"; syncId: string; idx: number; isLast: boolean; batchType: number; msgUrl: string; scopes: Array<{ partition: string; from?: number; to?: number }> }
  | { kind: "error"; syncId?: string; idx?: number; errorCode: number; batchType?: number; data: any }
  | { kind: "ignored" };

export function classifyControl(c: CtrlContent, ownImei: string): SyncCtrl {
  const data = typeof c.data === "string" && /^[{[]/.test(c.data) ? JSON.parse(c.data) : c.data;
  if (c.act === "transfer_after_login" && c.act_type === "transfer_msg" && data?.temp_key && data.imei === ownImei)
    return { kind: "tempKey", tempKey: data.temp_key, imei: data.imei, ts: c.ts ?? 0 };
  if (c.act_type !== "transfer_sync2" && c.act_type !== "transfer_msg") return { kind: "ignored" };
  if (c.act === "transfer_status" && data?.fromDevice === FromDevice.Mobile)
    return { kind: "status", status: data.status, syncId: data.syncId, data };
  if (c.act === "upload_batch" && data?.err === 0)
    return { kind: "batch", syncId: data.syncId, idx: data.idx, isLast: !!data.isLast, batchType: data.batchType, msgUrl: data.msgUrl, scopes: data.scopes ?? [] };
  if ((c.act === "upload_batch" && data?.err !== 0) || c.act === "transfer_error")
    return { kind: "error", syncId: data?.syncId, idx: data?.idx, errorCode: data?.errorCode ?? data?.err ?? -1, batchType: data?.batchType, data };
  return { kind: "ignored" };
}

// ---- partition naming -------------------------------------------------------
// reads: compact module aSMs: `${isGroup ? "group" : "oneone"}/${id}`; id = "global noised id".
export const partitionOf = (globalId: string, isGroup: boolean) => `${isGroup ? "group" : "oneone"}/${globalId}`;

// ---- phone simulator (what the real phone does server-side; INFERRED from the receiver) -------
export class PhoneSimulator {
  private ik: KeyPair;
  private sessionRecord: Uint8Array | null = null;
  constructor(private z: Zproto) {
    this.ik = z.generateKeyPair();
  }
  get identityPublicKey() {
    return this.ik.publicKey;
  }

  private compress(db: DbType, chunkObj: any): Uint8Array {
    const raw = encode(db === "message" ? MessageSyncChunk : ConversationSyncChunk, chunkObj);
    return zstdCompress(raw);
  }

  /** batchType=2 blob: one record whose plaintext is the first zstd'd chunk; establishes the session. */
  makeMetadataBlob(req: SyncRequestData, firstChunk: any): Uint8Array {
    const db = req.req.type;
    const m = this.z.createMetadataCipher(this.compress(db, firstChunk), this.ik, unb64(req.ik), unb64(req.ek));
    this.sessionRecord = m.sessionRecord;
    return writeRecords([m.ciphertext]);
  }

  /** batchType=1 blob: N records, each zstd'd chunk encrypted with the session record. */
  makeMessageBlob(db: DbType, chunks: any[]): Uint8Array {
    if (!this.sessionRecord) throw new Error("create metadata blob first");
    return writeRecords(chunks.map((c) => this.z.encryptMessage(this.compress(db, c), this.sessionRecord!).ciphertext));
  }
}

export { PlatformType };
