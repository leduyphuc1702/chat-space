// Minimal schema-driven protobuf codec for the Sync2 messages.
//
// The schema tables below are the reconstruction from the generated code in
// pc-dist/shared-worker.*.js modules "1y5R" (Sync2.Message.*), "Ho1Y"
// (Sync2.Conversation.*) and "N3ek" (Sync2.Shared.PlatformType). See sync2.proto.
//
// Output object shapes deliberately mirror jspb's toObject() as used by the app
// (fooList for repeated fields, 64-bit "String" variants as decimal strings).

export type Scalar = "string" | "bytes" | "bool" | "enum" | "i32" | "u64" | "i64" | "u64s" | "i64s";
export interface FieldDef {
  no: number;
  name: string;
  type: Scalar | "message";
  repeated?: boolean;
  msg?: () => MsgDef;
}
export interface MsgDef {
  name: string;
  fields: FieldDef[];
}

// ---- schema ----------------------------------------------------------------
export const PlatformType = {
  UNSPECIFIED: 0,
  MOBILE: 1,
  WEB: 2,
  PC: 3,
  SYSTEM: 4,
} as const;
export const MessageStatus = {
  UNSPECIFIED: 0,
  FAIL: 1,
  SENDING: 2,
  SENT: 3,
  RECEIVED: 4,
  SEEN: 5,
} as const;
export const ConvType = { UNSPECIFIED: 0, ONEONE: 1, GROUP: 2 } as const;

const Mention: MsgDef = {
  name: "Sync2.Message.Mention",
  fields: [
    { no: 1, name: "uid", type: "string" },
    { no: 2, name: "pos", type: "i32" },
    { no: 3, name: "len", type: "i32" },
    { no: 4, name: "type", type: "i32" },
  ],
};
const Quote: MsgDef = {
  name: "Sync2.Message.Quote",
  fields: [
    { no: 1, name: "attach", type: "string" },
    { no: 2, name: "ownerId", type: "string" },
    { no: 3, name: "ts", type: "i64" },
    { no: 4, name: "fromD", type: "string" },
    { no: 5, name: "cliMsgId", type: "u64" },
    { no: 6, name: "globalMsgId", type: "u64s" },
    { no: 7, name: "msg", type: "string" },
    { no: 8, name: "cliMsgType", type: "i32" },
    { no: 9, name: "ttl", type: "u64" },
    { no: 10, name: "partialQuote", type: "string" },
  ],
};
const Attach: MsgDef = {
  name: "Sync2.Message.Attach",
  fields: [
    { no: 1, name: "type", type: "string" },
    { no: 2, name: "extInfo", type: "string" },
    { no: 3, name: "action", type: "string" },
    { no: 4, name: "params", type: "string" },
    { no: 5, name: "title", type: "string" },
    { no: 6, name: "href", type: "string" },
    { no: 7, name: "thumb", type: "string" },
    { no: 8, name: "description", type: "string" },
    { no: 9, name: "catId", type: "i32" },
    { no: 10, name: "id", type: "i32" },
    { no: 11, name: "childNumber", type: "i32" },
    { no: 12, name: "remains", type: "string" },
    { no: 13, name: "zinstantData", type: "string" },
    { no: 14, name: "zinstantMsg", type: "string" },
  ],
};
const Properties: MsgDef = {
  name: "Sync2.Message.Properties",
  fields: [
    { no: 1, name: "size", type: "i32" },
    { no: 2, name: "color", type: "i32" },
    { no: 3, name: "type", type: "i32" },
    { no: 4, name: "subType", type: "i32" },
    { no: 5, name: "ext", type: "string" },
  ],
};
const Reference: MsgDef = {
  name: "Sync2.Message.Reference",
  fields: [
    { no: 1, name: "type", type: "i32" },
    { no: 2, name: "data", type: "string" },
  ],
};
const MetaData: MsgDef = {
  name: "Sync2.Message.MetaData",
  fields: [
    { no: 1, name: "attachs", type: "message", repeated: true, msg: () => Attach },
    { no: 2, name: "mentions", type: "message", repeated: true, msg: () => Mention },
    { no: 3, name: "reference", type: "message", msg: () => Reference },
    { no: 4, name: "quote", type: "message", msg: () => Quote },
    { no: 5, name: "property", type: "message", msg: () => Properties },
    { no: 6, name: "ttl", type: "u64" },
  ],
};
const Message: MsgDef = {
  name: "Sync2.Message.Message",
  fields: [
    { no: 1, name: "senderId", type: "string" },
    { no: 2, name: "clientId", type: "u64" },
    { no: 3, name: "msgStatus", type: "enum" },
    { no: 4, name: "msgType", type: "i32" },
    { no: 5, name: "timestamp", type: "u64" },
    { no: 6, name: "globalId", type: "i64s" },
    { no: 7, name: "content", type: "string" },
    { no: 8, name: "meta", type: "message", msg: () => MetaData },
  ],
};
const Partition: MsgDef = {
  name: "Sync2.Message.Partition",
  fields: [
    { no: 1, name: "id", type: "string" },
    { no: 2, name: "messages", type: "message", repeated: true, msg: () => Message },
  ],
};
export const MessageSyncChunk: MsgDef = {
  name: "Sync2.Message.SyncChunk",
  fields: [
    { no: 1, name: "platform", type: "enum" },
    { no: 2, name: "partitions", type: "message", repeated: true, msg: () => Partition },
  ],
};
const Conversation: MsgDef = {
  name: "Sync2.Conversation.Conversation",
  fields: [
    { no: 1, name: "convId", type: "string" },
    { no: 2, name: "convType", type: "enum" },
    { no: 3, name: "respondedByMe", type: "bool" },
    { no: 4, name: "lastTs", type: "u64" },
    { no: 5, name: "lastGlobalId", type: "u64s" },
    { no: 6, name: "lastClientId", type: "u64" },
  ],
};
export const ConversationSyncChunk: MsgDef = {
  name: "Sync2.Conversation.SyncChunk",
  fields: [
    { no: 1, name: "platform", type: "enum" },
    { no: 2, name: "conversations", type: "message", repeated: true, msg: () => Conversation },
  ],
};

// ---- wire primitives -------------------------------------------------------
const U64 = (1n << 64n) - 1n;

class Writer {
  private chunks: number[] = [];
  bytes(): Uint8Array {
    return Uint8Array.from(this.chunks);
  }
  varint(v: bigint) {
    v &= U64;
    while (v > 0x7fn) {
      this.chunks.push(Number((v & 0x7fn) | 0x80n));
      v >>= 7n;
    }
    this.chunks.push(Number(v));
  }
  tag(no: number, wt: number) {
    this.varint(BigInt((no << 3) | wt));
  }
  lenDelim(b: Uint8Array) {
    this.varint(BigInt(b.length));
    for (const x of b) this.chunks.push(x);
  }
}

class Reader {
  pos = 0;
  constructor(private buf: Uint8Array, private end = buf.length) {}
  more() {
    return this.pos < this.end;
  }
  varint(): bigint {
    let r = 0n;
    let shift = 0n;
    for (;;) {
      if (this.pos >= this.end) throw new Error("proto: truncated varint");
      const b = this.buf[this.pos++];
      r |= BigInt(b & 0x7f) << shift;
      if (!(b & 0x80)) return r;
      shift += 7n;
      if (shift > 70n) throw new Error("proto: varint too long");
    }
  }
  take(n: number): Uint8Array {
    if (this.pos + n > this.end) throw new Error("proto: truncated field");
    const s = this.buf.subarray(this.pos, this.pos + n);
    this.pos += n;
    return s;
  }
  skip(wt: number) {
    if (wt === 0) this.varint();
    else if (wt === 1) this.take(8);
    else if (wt === 2) this.take(Number(this.varint()));
    else if (wt === 5) this.take(4);
    else throw new Error("proto: unsupported wire type " + wt);
  }
}

const isNumericScalar = (t: string) => t === "i32" || t === "u64" || t === "i64" || t === "enum" || t === "u64s" || t === "i64s";
const dec = new TextDecoder("utf-8", { fatal: true });
const enc = new TextEncoder();

function toBig(v: unknown): bigint {
  if (typeof v === "bigint") return v;
  if (typeof v === "string") return BigInt(v === "" ? "0" : v);
  return BigInt(Math.trunc(v as number));
}

function encodeScalar(w: Writer, f: FieldDef, v: any) {
  switch (f.type) {
    case "string": {
      w.tag(f.no, 2);
      w.lenDelim(enc.encode(v));
      return;
    }
    case "bytes": {
      w.tag(f.no, 2);
      w.lenDelim(v);
      return;
    }
    case "bool":
      w.tag(f.no, 0);
      w.varint(v ? 1n : 0n);
      return;
    default:
      w.tag(f.no, 0);
      w.varint(toBig(v)); // negative int32/int64 -> 10-byte two's complement varint
  }
}

function isDefault(f: FieldDef, v: any): boolean {
  switch (f.type) {
    case "string":
      return v === "" || v == null;
    case "bytes":
      return v == null || v.length === 0;
    case "bool":
      return !v;
    default:
      return v == null || toBig(v) === 0n;
  }
}

export function encode(def: MsgDef, obj: Record<string, any>): Uint8Array {
  const w = new Writer();
  encodeInto(w, def, obj);
  return w.bytes();
}

function encodeInto(w: Writer, def: MsgDef, obj: Record<string, any>) {
  for (const f of def.fields) {
    if (f.type === "message") {
      if (f.repeated) {
        for (const item of (obj[f.name + "List"] ?? []) as any[]) {
          w.tag(f.no, 2);
          w.lenDelim(encode(f.msg!(), item));
        }
      } else if (obj[f.name] != null) {
        w.tag(f.no, 2);
        w.lenDelim(encode(f.msg!(), obj[f.name]));
      }
    } else if (!isDefault(f, obj[f.name])) {
      encodeScalar(w, f, obj[f.name]);
    }
  }
}

function defaultFor(f: FieldDef): any {
  switch (f.type) {
    case "string":
      return "";
    case "bytes":
      return new Uint8Array(0);
    case "bool":
      return false;
    case "u64s":
    case "i64s":
      return "0";
    default:
      return 0;
  }
}

function readScalar(r: Reader, f: FieldDef, wt: number): any {
  if (f.type === "string") {
    if (wt !== 2) throw new Error("proto: wire type mismatch for " + f.name);
    return dec.decode(r.take(Number(r.varint())));
  }
  if (f.type === "bytes") {
    if (wt !== 2) throw new Error("proto: wire type mismatch for " + f.name);
    return r.take(Number(r.varint())).slice();
  }
  if (wt !== 0) throw new Error("proto: wire type mismatch for " + f.name);
  const raw = r.varint();
  switch (f.type) {
    case "bool":
      return raw !== 0n;
    case "i32":
    case "enum":
      return Number(BigInt.asIntN(32, raw));
    case "i64":
      return Number(BigInt.asIntN(64, raw)); // jspb readInt64 -> JS number
    case "u64":
      return Number(raw); // jspb readUint64 -> JS number (precision loss above 2^53, same as app)
    case "i64s":
      return BigInt.asIntN(64, raw).toString();
    case "u64s":
      return raw.toString();
  }
}

export function decode(def: MsgDef, buf: Uint8Array, r = new Reader(buf)): Record<string, any> {
  const out: Record<string, any> = {};
  for (const f of def.fields) {
    if (f.type === "message") {
      if (f.repeated) out[f.name + "List"] = [];
    } else out[f.name] = defaultFor(f);
  }
  const byNo = new Map(def.fields.map((f) => [f.no, f]));
  while (r.more()) {
    const key = Number(r.varint());
    const no = key >>> 3;
    const wt = key & 7;
    const f = byNo.get(no);
    if (!f) {
      r.skip(wt);
      continue;
    }
    if (f.type === "message") {
      if (wt !== 2) throw new Error("proto: wire type mismatch for " + f.name);
      const sub = r.take(Number(r.varint()));
      const val = decode(f.msg!(), sub);
      if (f.repeated) out[f.name + "List"].push(val);
      else out[f.name] = val;
    } else if (wt === 2 && isNumericScalar(f.type)) {
      // packed repeated is not used by this schema; tolerate by reading first element
      const sub = new Reader(r.take(Number(r.varint())));
      out[f.name] = readScalar(sub, f, 0);
    } else {
      out[f.name] = readScalar(r, f, wt);
    }
  }
  return out;
}
