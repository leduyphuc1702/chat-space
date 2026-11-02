// Live probe of Zalo "sync v2" (phone -> this client history transfer). Run with ChatSpace quit, so the
// saved web session has only this one socket. It sends exactly two sync requests (conversation list, then
// one conversation's latest 20 messages) and closes both sessions. Output is shapes and counts only:
// no message text, no keys, no msgUrl.
//
//   bun run scripts/zalo-sync2-probe.ts [--observe-only]

import { readFileSync } from "node:fs";
import { gunzipSync } from "node:zlib";
import { homedir } from "node:os";
import { join } from "node:path";
import { Zalo } from "zca-js";
import { readWasmFromAsar } from "../src/zalo-sync2/asar";
import { Zproto } from "../src/zalo-sync2/zproto";
import { BatchType, CMD, MobileStatus, Sync2Receiver, encodeFrame, partitionOf, type DbType } from "../src/zalo-sync2/sync2";
import { decodeBody, frameHeader, frameJson } from "../src/zalo-sync2/ws";

const DATA = join(homedir(), "Library/Application Support/ChatSpace/zalo");
const observeOnly = process.argv.includes("--observe-only");
const KNOWN = new Set([1, 2, 501, 502, 510, 511, 521, 522, 602, 610, 611, 612, 3000]);
const statusName = (s: number) => Object.entries(MobileStatus).find(([, v]) => v === s)?.[0] ?? `#${s}`;
const t0 = Date.now();
const log = (...a: unknown[]) => console.log(`[${((Date.now() - t0) / 1000).toFixed(1)}s]`, ...a);
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const short = (s?: string) => (s ? s.slice(0, 6) + "…" : s);
const shape = (o: any): any =>
  o && typeof o === "object" && !Array.isArray(o)
    ? Object.fromEntries(Object.entries(o).map(([k, v]) => [k, Array.isArray(v) ? `array(${v.length})` : typeof v]))
    : typeof o;

// ---------------------------------------------------------------- sessions
interface Batch { idx: number; isLast: boolean; batchType: number; msgUrl: string; scopes: number }
class Session {
  recv: Sync2Receiver;
  pending: Batch[] = [];
  rows: Array<{ partition: string; data: any[] }> = [];
  statuses: string[] = [];
  lastSeen = false;
  processed = 0;
  failed?: string;
  busy = false;
  done!: Promise<void>;
  private finish!: () => void;
  constructor(z: Zproto, ik: any, readonly db: DbType) {
    this.recv = new Sync2Receiver(z, ik);
    this.done = new Promise((r) => (this.finish = r));
  }
  fail(why: string) {
    this.failed ??= why;
    this.finish();
  }
  async pump() {
    if (this.busy) return;
    this.busy = true;
    try {
      for (;;) {
        const i = this.pending.findIndex((b) => b.batchType === BatchType.MetadataEmbedded || this.recv.ready);
        if (i < 0) break;
        const [b] = this.pending.splice(i, 1);
        const res = await fetch(b.msgUrl);
        if (!res.ok) throw new Error(`blob HTTP ${res.status}`);
        const blob = new Uint8Array(await res.arrayBuffer());
        const out = this.recv.loadBlob(blob, b.batchType, this.db);
        const n = out.reduce((s, p) => s + p.data.length, 0);
        log(`  batch idx=${b.idx} type=${b.batchType} bytes=${blob.length} rows=${n}`);
        this.rows.push(...out);
        this.processed++;
      }
    } catch (e) {
      this.fail("decode: " + (e as Error).message);
    } finally {
      this.busy = false;
    }
    if (this.lastSeen && this.pending.length === 0 && !this.busy) this.finish();
  }
}

// ---------------------------------------------------------------- main
const creds = JSON.parse(readFileSync(join(DATA, "credentials.json"), "utf8"));
const wasm = readWasmFromAsar();
const z = await Zproto.load(wasm.bytes);
const ik = z.generateKeyPair(); // one identity for the whole run, memory only
log("wasm", wasm.name, "loaded; logging in");

const api = await new Zalo({ selfListen: false, checkUpdate: false, logging: false }).login({
  cookie: creds.cookie,
  imei: creds.imei,
  userAgent: creds.userAgent,
  language: creds.language,
});
const selfId = String(api.getOwnId());
const listener: any = api.listener;
let cipherKey: string | undefined;
const sessions = new Map<string, Session>();
const replies: Record<string, (body: any) => void> = {};

function onControl(c: any) {
  const ct = c?.content;
  if (!ct || typeof ct.act_type !== "string" || !/^transfer/.test(ct.act_type)) return;
  const data = typeof ct.data === "string" && /^[{[]/.test(ct.data) ? JSON.parse(ct.data) : ct.data;
  const s = data?.syncId ? sessions.get(data.syncId) : undefined;
  const tag = `${ct.act_type}/${ct.act} sync=${short(data?.syncId)}${data?.syncId && !s ? " (not ours)" : ""}`;
  if (ct.act === "transfer_status") {
    log(`ctrl ${tag} status=${statusName(data?.status)} fromDevice=${data?.fromDevice} keys=${Object.keys(data ?? {})}`);
    const target = s ?? (sessions.size ? [...sessions.values()].at(-1) : undefined);
    target?.statuses.push(statusName(data?.status));
    if (target && [6, 7, 8, 9, 10].includes(data?.status)) target.fail("phone status " + statusName(data.status));
  } else if (ct.act === "upload_batch") {
    log(`ctrl ${tag} idx=${data?.idx} isLast=${data?.isLast} err=${data?.err} errorCode=${data?.errorCode} batchType=${data?.batchType} scopes=${data?.scopes?.length} keys=${Object.keys(data ?? {})}`);
    if (!s) return;
    if (data.err !== 0) return s.fail(`upload_batch err=${data.err} code=${data.errorCode}`);
    s.pending.push({ idx: data.idx, isLast: !!data.isLast, batchType: data.batchType, msgUrl: data.msgUrl, scopes: data.scopes?.length ?? 0 });
    if (data.isLast) s.lastSeen = true;
    void s.pump();
  } else {
    log(`ctrl ${tag} keys=${Object.keys(data ?? {})}`);
    if (s && ct.act === "transfer_error") s.fail("transfer_error " + JSON.stringify(shape(data)));
  }
}

async function onFrame(raw: Buffer) {
  const h = frameHeader(raw);
  if (!h) return;
  if (KNOWN.has(h.cmd) && h.cmd !== 601) return;
  try {
    const parsed = frameJson(raw);
    if (h.cmd === 601) {
      const body = await decodeBody(parsed, cipherKey);
      for (const c of body?.data?.controls ?? body?.controls ?? []) onControl(c);
      return;
    }
    let body = parsed;
    try {
      body = await decodeBody(parsed, cipherKey);
    } catch (e) {
      log(`frame cmd=${h.cmd} body not decodable: ${(e as Error).message} encrypt=${parsed?.encrypt}`);
    }
    log(`frame cmd=${h.cmd} sub=${h.subCmd} raw=${JSON.stringify(shape(parsed))} decoded=${JSON.stringify(shape(body))} error_code=${body?.error_code ?? parsed?.error_code} inner=${JSON.stringify(shape(body?.data))}`);
    const reqId = body?.data?.reqId ?? body?.reqId ?? body?.data?.req_id ?? parsed?.reqId;
    if (reqId && replies[reqId]) replies[reqId](body);
    else if (h.cmd === CMD.SYNC_MESSAGE_REQUEST) Object.values(replies).forEach((r) => r(body));
  } catch (e) {
    log(`frame cmd=${h.cmd} undecodable: ${(e as Error).message} encrypt=${frameJson(raw)?.encrypt} dataLen=${frameJson(raw)?.data?.length} haveKey=${!!cipherKey}`);
  }
}

listener.on("error", (e: any) => log("listener error", e?.message ?? e));
listener.on("closed", (code: number, reason: string) => log("listener closed", code, reason));
const keyReady = new Promise<void>((resolve) =>
  listener.on("cipher_key", (k: string) => {
    cipherKey = k;
    resolve();
  }),
);
listener.on("connected", () => listener.ws?.on("message", (d: Buffer) => void onFrame(d)));
listener.start({ retryOnClose: false });
await Promise.race([keyReady, sleep(30_000)]);
if (!cipherKey) throw new Error("no cipher key within 30s");
log("socket ready; observing pushes for 8s");
await sleep(8_000);
if (observeOnly) {
  listener.stop();
  process.exit(0);
}

let reqN = 9000;
async function request(db: DbType, queries: any[], waitMs: number) {
  const s = new Session(z, ik, db);
  sessions.set(s.recv.syncId, s);
  const data = s.recv.buildRequest({ type: db, priority: 0, queries, deviceName: "ChatSpace" });
  const reqId = `req_${reqN++}`;
  const reply = new Promise<any>((r) => (replies[reqId] = r));
  listener.ws.send(encodeFrame(CMD.SYNC_MESSAGE_REQUEST, 0, { data, reqId }));
  log(`sent cmd 590 ${db} sync=${short(s.recv.syncId)} queries=${queries.length} limit=${queries[0].limit} ${reqId}`);
  const r = await Promise.race([reply, sleep(15_000).then(() => "timeout")]);
  delete replies[reqId];
  log(`reply: ${r === "timeout" ? "none within 15s" : JSON.stringify({ error_code: r?.error_code, error_message: r?.error_message, data: shape(r?.data) })}`);
  if (r !== "timeout" && r?.error_code) s.fail(`cmd 590 error_code=${r.error_code}`);
  await Promise.race([s.done, sleep(waitMs).then(() => s.fail("timed out"))]);
  log(`session ${db}: statuses=[${s.statuses}] batches=${s.processed} failed=${s.failed ?? "no"}`);
  return s;
}
function close(s: Session) {
  listener.ws?.send(encodeFrame(CMD.ACK_DELETE_SYNC_SESSION, 0, { data: { data: { syncId: s.recv.syncId, toDevice: 0, reason: 1 } } }));
}
const day = (ts: number) => (ts ? new Date(ts).toISOString().slice(0, 10) : "-");
const hist = (xs: any[]) => xs.reduce((m: Record<string, number>, x) => ((m[String(x)] = (m[String(x)] ?? 0) + 1), m), {});

// 1) conversation list. The phone asks the user to confirm; allow 10 minutes like the official client.
log(">>> waiting for the phone: please confirm the sync request on your phone");
const conv = await request("conversation", [{ partition: "", from: 0, to: Number.MAX_SAFE_INTEGER, limit: 50 }], 600_000);
close(conv);
const convs = conv.rows.flatMap((p) => p.data);
if (!convs.length) {
  log("no conversations received; stopping");
  listener.stop();
  process.exit(conv.failed ? 1 : 0);
}
const ts = convs.map((c) => Number(c.lastTs)).filter(Boolean);
log(`conversations=${convs.length} convType=${JSON.stringify(hist(convs.map((c) => c.convType)))} respondedByMe=${convs.filter((c) => c.respondedByMe).length} lastTs ${day(Math.min(...ts))}..${day(Math.max(...ts))} fields=${JSON.stringify(shape(convs[0]))}`);

// 2) map global noised ids to Zalo ids with the profile data the bridge already loads
const friends: any[] = await api.getAllFriends(20000, 1);
const fByGlobal = new Map(friends.filter((f) => f.globalId).map((f) => [String(f.globalId), String(f.userId)]));
const gIds = Object.keys((await api.getAllGroups()).gridVerMap ?? {});
const gByGlobal = new Map<string, string>();
for (let i = 0; i < gIds.length; i += 40) {
  const info = (await api.getGroupInfo(gIds.slice(i, i + 40))).gridInfoMap ?? {};
  for (const [id, g] of Object.entries<any>(info)) if (g?.globalId) gByGlobal.set(String(g.globalId), id);
}
const one = convs.filter((c) => c.convType === 1);
const grp = convs.filter((c) => c.convType === 2);
log(`friends=${friends.length} (with globalId ${fByGlobal.size}) groups=${gIds.length} (with globalId ${gByGlobal.size})`);
log(`mapped: oneone ${one.filter((c) => fByGlobal.has(c.convId)).length}/${one.length}, group ${grp.filter((c) => gByGlobal.has(c.convId)).length}/${grp.length}; convId looks like uid: ${convs.filter((c) => fByGlobal.has(c.convId) === false && friends.some((f) => String(f.userId) === c.convId)).length}`);

// 3) one conversation's latest 20 messages, same identity key (expected: no second prompt)
const pick = one.find((c) => fByGlobal.has(c.convId)) ?? one[0] ?? grp[0];
const isGroup = pick.convType === 2;
const peerUid = isGroup ? gByGlobal.get(pick.convId) : fByGlobal.get(pick.convId);
const msgS = await request("message", [{ partition: partitionOf(pick.convId, isGroup), from: 0, to: Number.MAX_SAFE_INTEGER, limit: 20 }], 300_000);
close(msgS);
const msgs = msgS.rows.flatMap((p) => p.data);
const partitions = [...new Set(msgS.rows.map((p) => p.partition))];
log(`messages=${msgs.length} partitions=${partitions.length} matchesRequested=${partitions.every((p) => p === partitionOf(pick.convId, isGroup))} (${isGroup ? "group" : "oneone"})`);
if (msgs.length) {
  const mts = msgs.map((m) => Number(m.timestamp)).filter(Boolean);
  const sender = (id: string) =>
    id === selfId ? "selfUid" : id === peerUid ? "peerUid" : id === pick.convId ? "peerGlobal" : fByGlobal.has(id) ? "otherGlobal" : /^\d+$/.test(id) ? "otherNumeric" : "other";
  log(`msgType=${JSON.stringify(hist(msgs.map((m) => m.msgType)))} msgStatus=${JSON.stringify(hist(msgs.map((m) => m.msgStatus)))} sender=${JSON.stringify(hist(msgs.map((m) => sender(String(m.senderId)))))}`);
  log(`timestamp ${day(Math.min(...mts))}..${day(Math.max(...mts))} withGlobalId=${msgs.filter((m) => m.globalId && m.globalId !== "0").length} withContent=${msgs.filter((m) => m.content).length} withMeta=${msgs.filter((m) => m.meta).length} metaFields=${JSON.stringify(hist(msgs.flatMap((m) => Object.keys(m.meta ?? {}).filter((k) => { const v = m.meta[k]; return Array.isArray(v) ? v.length : v; }))))}`);
  log(`fields=${JSON.stringify(shape(msgs[0]))}`);
  // cross-check ids against what ChatSpace already stored from the web listener (read only)
  try {
    const store = JSON.parse(gunzipSync(readFileSync(join(DATA, "store.json.gz"))).toString("utf8"));
    const ids = new Set<string>();
    const cli = new Set<string>();
    for (const list of Object.values<any[]>(store.messages ?? {})) for (const e of list) (ids.add(String(e.z?.msgId)), cli.add(String(e.z?.cliMsgId)));
    log(`store cross-check: globalId==msgId ${msgs.filter((m) => ids.has(String(m.globalId))).length}/${msgs.length}, clientId==cliMsgId ${msgs.filter((m) => cli.has(String(m.clientId))).length}/${msgs.length}`);
  } catch (e) {
    log("store cross-check skipped:", (e as Error).message);
  }
}

await sleep(1500);
listener.stop();
log("done");
process.exit(0);
