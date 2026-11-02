import { expect, test } from "bun:test";
import { existsSync } from "node:fs";
import { DEFAULT_ASAR, readWasmFromAsar } from "./asar";
import { PhoneSyncClient, SyncError, type SyncPhase } from "./client";
import { CMD, PhoneSimulator, decodeFrame } from "./sync2";
import { Zproto } from "./zproto";

const haveZalo = existsSync(DEFAULT_ASAR);

async function setup(opts: { reject?: boolean; reorder?: boolean; pause?: boolean; sleepy?: boolean; dead?: boolean } = {}) {
  const z = await Zproto.load(readWasmFromAsar().bytes);
  const phone = new PhoneSimulator(z);
  const blobs = new Map<string, Uint8Array>();
  const sent: any[] = [];
  let client!: PhoneSyncClient;
  const ctrl = (act: string, data: any) => client.handleControl({ act_type: "transfer_sync2", act, data: JSON.stringify(data) });
  let sendBatches = () => {};
  client = new PhoneSyncClient(z, {
    send: (frame) => {
      const f = decodeFrame(frame);
      sent.push(f);
      if (f.cmd === CMD.REQUEST_MOBILE_WAKE_UP && !opts.dead) queueMicrotask(() => sendBatches());
      if (f.cmd !== CMD.SYNC_MESSAGE_REQUEST) return true;
      const req = f.body.data;
      const syncId = req.syncId;
      queueMicrotask(() => {
        ctrl("transfer_status", { syncId, fromDevice: 0, status: 3 });
        if (opts.reject) return ctrl("transfer_status", { syncId, fromDevice: 0, status: 6 });
        ctrl("transfer_status", { syncId, fromDevice: 0, status: 4 });
        if (opts.pause) {
          ctrl("transfer_status", { syncId, fromDevice: 0, status: 2 });
          ctrl("transfer_status", { syncId, fromDevice: 0, status: 1 });
        }
        const db = req.req.type;
        const chunk =
          db === "conversation"
            ? { platform: 1, conversationsList: [{ convId: "111", convType: 1, lastTs: 5, lastGlobalId: "9", lastClientId: 7, respondedByMe: true }] }
            : { platform: 1, partitionsList: [{ id: req.req.queries[0].partition, messagesList: [{ senderId: "111", clientId: 1, msgStatus: 4, msgType: 0, timestamp: 10, globalId: "77", content: "hi" }] }] };
        blobs.set(`u0${syncId}`, phone.makeMetadataBlob(req, { platform: 1 }));
        blobs.set(`u1${syncId}`, phone.makeMessageBlob(db, [chunk]));
        const b1 = { syncId, idx: 1, isLast: 1, err: 0, batchType: 1, msgUrl: `u1${syncId}`, scopes: [] };
        const b0 = { syncId, idx: 0, isLast: 0, err: 0, batchType: 2, msgUrl: `u0${syncId}`, scopes: [] };
        sendBatches = () => {
          for (const b of opts.reorder ? [b1, b0] : [b0, b1]) ctrl("upload_batch", b);
        };
        if (!opts.sleepy && !opts.dead) sendBatches();
      });
      return true;
    },
    fetchBlob: async (url) => blobs.get(url)!,
    idleMs: 30,
    wakeupMs: 30,
  });
  return { client, sent };
}

test.skipIf(!haveZalo)("conversation and message requests decode end to end", async () => {
  const { client, sent } = await setup();
  const phases: SyncPhase[] = [];
  const conv = await client.request("conversation", [{ partition: "", from: 0, to: Number.MAX_SAFE_INTEGER, limit: 50 }], 0, (p) => phases.push(p));
  expect(conv.flatMap((r) => r.data).map((c) => c.convId)).toEqual(["111"]);
  expect(phases).toContain("waiting");
  expect(phases.at(-1)).toBe("confirmed");
  const msgs = await client.request("message", [{ partition: "oneone/111", from: 0, to: Number.MAX_SAFE_INTEGER, limit: 20 }], 2);
  expect(msgs[0]!.partition).toBe("oneone/111");
  expect(msgs[0]!.data[0].content).toBe("hi");
  // one close (cmd 591) per finished session, identity key reused across requests
  expect(sent.filter((f) => f.cmd === CMD.ACK_DELETE_SYNC_SESSION).length).toBe(2);
  const reqs = sent.filter((f) => f.cmd === CMD.SYNC_MESSAGE_REQUEST).map((f) => f.body.data);
  expect(reqs[0].ik).toBe(reqs[1].ik);
  expect(reqs[0].ek).not.toBe(reqs[1].ek);
  expect(client.active).toBe(false);
});

test.skipIf(!haveZalo)("message batch arriving before the metadata batch waits for it", async () => {
  const { client } = await setup({ reorder: true });
  const conv = await client.request("conversation", [{ partition: "", from: 0, to: 1, limit: 1 }], 0);
  expect(conv.flatMap((r) => r.data).length).toBe(1);
});

test.skipIf(!haveZalo)("declining on the phone rejects the request", async () => {
  const { client } = await setup({ reject: true });
  const err = await client.request("conversation", [{ partition: "", from: 0, to: 1, limit: 1 }], 0).catch((e) => e);
  expect(err).toBeInstanceOf(SyncError);
  expect((err as SyncError).code).toBe("rejected");
});

test.skipIf(!haveZalo)("a server refusal of cmd 590 fails the request", async () => {
  const z = await Zproto.load(readWasmFromAsar().bytes);
  let client!: PhoneSyncClient;
  client = new PhoneSyncClient(z, {
    send: (frame) => {
      if (decodeFrame(frame).cmd === CMD.SYNC_MESSAGE_REQUEST) queueMicrotask(() => client.handleReply({ error_code: 211 }));
      return true;
    },
  });
  const err = await client.request("conversation", [{ partition: "", from: 0, to: 1, limit: 1 }], 0).catch((e) => e);
  expect((err as SyncError).code).toBe("server");
});

test.skipIf(!haveZalo)("a sink gets each batch, and a paused phone is reported", async () => {
  const { client } = await setup({ pause: true });
  const phases: SyncPhase[] = [];
  const got: string[] = [];
  const rows = await client.request(
    "message",
    [{ partition: "oneone/111", from: 0, to: Number.MAX_SAFE_INTEGER, limit: 20 }],
    2,
    (p) => phases.push(p),
    async (batch) => {
      await Bun.sleep(5);
      for (const r of batch) for (const m of r.data) got.push(m.content);
    },
  );
  expect(rows).toEqual([]);
  expect(got).toEqual(["hi"]);
  expect(phases).toEqual(["waiting", "confirmed", "paused", "confirmed", "confirmed", "confirmed"]);
});

test.skipIf(!haveZalo)("a failing sink fails the request", async () => {
  const { client } = await setup();
  const err = await client
    .request("message", [{ partition: "oneone/111", from: 0, to: 1, limit: 1 }], 2, () => {}, () => {
      throw new SyncError("cancelled", "cancelled");
    })
    .catch((e) => e);
  expect((err as SyncError).code).toBe("cancelled");
});

test.skipIf(!haveZalo)("a phone that goes quiet is woken up (cmd 592) and continues", async () => {
  const { client, sent } = await setup({ sleepy: true });
  const rows = await client.request("conversation", [{ partition: "", from: 0, to: 1, limit: 1 }], 0);
  expect(rows.flatMap((r) => r.data).length).toBe(1);
  const wake = sent.find((f) => f.cmd === CMD.REQUEST_MOBILE_WAKE_UP);
  expect(wake.body.syncId).toBe(sent.find((f) => f.cmd === CMD.SYNC_MESSAGE_REQUEST).body.data.syncId);
  expect(wake.body.toDevice).toBe(0);
});

test.skipIf(!haveZalo)("a phone that stays quiet after the wake-ups times out", async () => {
  const { client, sent } = await setup({ dead: true });
  const err = await client.request("conversation", [{ partition: "", from: 0, to: 1, limit: 1 }], 0).catch((e) => e);
  expect((err as SyncError).code).toBe("timeout");
  expect(sent.filter((f) => f.cmd === CMD.REQUEST_MOBILE_WAKE_UP).length).toBe(3);
});
