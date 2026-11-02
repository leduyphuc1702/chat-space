// Receiver side of Zalo "sync v2" on top of an existing web-session socket: sends cmd 590 requests,
// follows the phone's cmd 601 transfer_sync2 controls, downloads and decrypts the batches.
// The identity key lives as long as this object: the phone asks the user to confirm a new key once,
// later requests with the same key come back BypassConfirmed.

import { Zproto, type KeyPair } from "./zproto";
import { BatchType, CMD, MobileStatus, Sync2Receiver, encodeFrame, type DbType, type SyncQuery } from "./sync2";

export interface SyncRows {
  partition: string;
  data: any[];
}

export type SyncPhase = "waiting" | "confirmed" | "paused";

/** Called with each decoded batch as it arrives; the next batch waits for it. */
export type RowsSink = (rows: SyncRows[]) => void | Promise<void>;

export class SyncError extends Error {
  constructor(
    readonly code: "rejected" | "busy" | "timeout" | "disconnected" | "cancelled" | "server" | "decode",
    message: string,
  ) {
    super(message);
  }
}

/** Wake-ups per request before it fails (and the caller may retry it). */
const MAX_WAKES = 3;

interface Pending {
  idx: number;
  batchType: number;
  msgUrl: string;
}

class Session {
  readonly recv: Sync2Receiver;
  readonly rows: SyncRows[] = [];
  private pending: Pending[] = [];
  private lastSeen = false;
  private busy = false;
  private timer?: ReturnType<typeof setTimeout>;
  private wakes = 0;
  private resolve!: (rows: SyncRows[]) => void;
  private reject!: (e: Error) => void;
  readonly done: Promise<SyncRows[]>;
  settled = false;

  constructor(
    z: Zproto,
    ik: KeyPair,
    readonly db: DbType,
    private onPhase: (p: SyncPhase) => void,
    private fetchBlob: (url: string) => Promise<Uint8Array>,
    private confirmMs: number,
    private idleMs: number,
    private pausedMs: number,
    private wakeupMs: number,
    private wake: () => void,
    private sink?: RowsSink,
  ) {
    this.recv = new Sync2Receiver(z, ik);
    this.done = new Promise((res, rej) => ((this.resolve = res), (this.reject = rej)));
    this.done.catch(() => {});
    this.arm(confirmMs);
  }

  private arm(ms: number) {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.fail(new SyncError("timeout", "the phone did not answer in time")), ms);
  }

  /** While transferring: after `ms` of silence wake the phone up (cmd 592) like Zalo PC, a few times, then give up. */
  private watch(ms: number) {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      if (this.settled) return;
      if (this.wakes >= MAX_WAKES) return this.fail(new SyncError("timeout", "the phone stopped sending"));
      this.wakes++;
      this.wake();
      this.watch(this.wakeupMs);
    }, ms);
  }

  fail(e: Error) {
    if (this.settled) return;
    this.settled = true;
    if (this.timer) clearTimeout(this.timer);
    this.reject(e);
  }

  private finish() {
    if (this.settled) return;
    this.settled = true;
    if (this.timer) clearTimeout(this.timer);
    this.resolve(this.rows);
  }

  status(s: number) {
    if (this.settled) return;
    if (s === MobileStatus.WaitingConfirm) {
      this.onPhase("waiting");
      this.arm(this.confirmMs);
    } else if (s === MobileStatus.Idle) {
      // Zalo went to the background on the phone: it resumes (Active) when reopened.
      this.onPhase("paused");
      this.arm(this.pausedMs);
    } else if (s === MobileStatus.Confirmed || s === MobileStatus.BypassConfirmed || s === MobileStatus.Active) {
      this.onPhase("confirmed");
      this.watch(this.idleMs);
    } else if (s === MobileStatus.UserReject || s === MobileStatus.UserCancel) {
      this.fail(new SyncError("rejected", "the sync was declined on the phone"));
    } else if (s === MobileStatus.MasterDeviceBusy) {
      this.fail(new SyncError("busy", "the phone is busy with another transfer"));
    } else if (s === MobileStatus.MasterDeviceLowStorage) {
      this.fail(new SyncError("busy", "the phone is low on storage"));
    } else if (s === MobileStatus.MasterDeviceLogout) {
      this.fail(new SyncError("busy", "Zalo is logged out on the phone"));
    }
  }

  batch(b: Pending & { isLast: boolean }) {
    if (this.settled) return;
    this.onPhase("confirmed");
    this.wakes = 0;
    this.watch(this.idleMs);
    this.pending.push(b);
    if (b.isLast) this.lastSeen = true;
    void this.pump();
  }

  private async pump() {
    if (this.busy) return;
    this.busy = true;
    try {
      for (;;) {
        // The metadata batch establishes the session; message batches that overtake it wait for it.
        const i = this.pending.findIndex((b) => b.batchType === BatchType.MetadataEmbedded || this.recv.ready);
        if (i < 0) break;
        const [b] = this.pending.splice(i, 1);
        const blob = await this.fetchBlob(b!.msgUrl);
        if (this.settled) return;
        let rows: SyncRows[];
        try {
          rows = this.recv.loadBlob(blob, b!.batchType, this.db);
        } catch (e) {
          throw new SyncError("decode", `batch ${b!.idx}: ${(e as Error).message}`);
        }
        if (this.sink) {
          if (rows.length) await this.sink(rows);
          if (this.settled) return;
        } else this.rows.push(...rows);
        this.watch(this.idleMs);
      }
    } catch (e) {
      this.fail(e instanceof SyncError ? e : new SyncError("server", (e as Error).message));
    } finally {
      this.busy = false;
    }
    if (this.lastSeen && this.pending.length === 0) this.finish();
  }
}

export interface SyncClientOptions {
  send: (frame: Uint8Array) => boolean;
  log?: (...a: unknown[]) => void;
  fetchBlob?: (url: string) => Promise<Uint8Array>;
  deviceName?: string;
  /** how long the phone may take to show and confirm the prompt (Zalo's awaiting_mobile_timeout_ms) */
  confirmMs?: number;
  /** silence between batches after which the phone is woken up (Zalo PC: batch_timeout 10 s) */
  idleMs?: number;
  /** how long to wait after a wake-up (Zalo PC: wakeup_timeout) */
  wakeupMs?: number;
  /** how long the phone may stay paused (Zalo in the background); Zalo's sync_paused_timeout_ms */
  pausedMs?: number;
}

export class PhoneSyncClient {
  private readonly ik: KeyPair;
  private sessions = new Map<string, Session>();
  private reqN = 1;

  constructor(
    private z: Zproto,
    private opts: SyncClientOptions,
  ) {
    this.ik = z.generateKeyPair();
  }

  /**
   * One sync request; resolves once the phone sent its last batch, with every decoded row, or with none when
   * `sink` took them batch by batch.
   */
  async request(
    db: DbType,
    queries: SyncQuery[],
    priority: number,
    onPhase: (p: SyncPhase) => void = () => {},
    sink?: RowsSink,
  ): Promise<SyncRows[]> {
    const s = new Session(
      this.z,
      this.ik,
      db,
      onPhase,
      this.opts.fetchBlob ?? defaultFetch,
      this.opts.confirmMs ?? 600_000,
      this.opts.idleMs ?? 20_000,
      this.opts.pausedMs ?? 900_000,
      this.opts.wakeupMs ?? 180_000,
      () => {
        this.opts.log?.("no batch for a while, waking the phone up");
        this.opts.send(encodeFrame(CMD.REQUEST_MOBILE_WAKE_UP, 0, { syncId, toDevice: 0, reqId: `req_cs${this.reqN++}` }));
      },
      sink,
    );
    const syncId = s.recv.syncId;
    this.sessions.set(syncId, s);
    try {
      const data = s.recv.buildRequest({ type: db, priority, queries, deviceName: this.opts.deviceName ?? "ChatSpace" });
      if (!this.opts.send(encodeFrame(CMD.SYNC_MESSAGE_REQUEST, 0, { data, reqId: `req_cs${this.reqN++}` }))) {
        throw new SyncError("disconnected", "Zalo is not connected");
      }
      return await s.done;
    } finally {
      this.sessions.delete(syncId);
      this.opts.send(
        encodeFrame(CMD.ACK_DELETE_SYNC_SESSION, 0, { data: { data: { syncId, toDevice: 0, reason: 1 } } }),
      );
    }
  }

  /** Fails every running request (socket closed, logout, user cancel). */
  abortAll(e: SyncError) {
    for (const s of this.sessions.values()) s.fail(e);
  }

  get active() {
    return this.sessions.size > 0;
  }

  /** Feed every decoded cmd 601 control here; returns true when it belonged to sync v2. */
  handleControl(content: any): boolean {
    if (!content || (content.act_type !== "transfer_sync2" && content.act_type !== "transfer_msg")) return false;
    let data = content.data;
    if (typeof data === "string" && /^[{[]/.test(data)) {
      try {
        data = JSON.parse(data);
      } catch {
        return true;
      }
    }
    const s = data?.syncId ? this.sessions.get(String(data.syncId)) : undefined;
    if (!s) return true;
    if (content.act === "transfer_status") {
      if (data.fromDevice === undefined || data.fromDevice === 0) {
        this.opts.log?.(`phone status ${data.status}`);
        s.status(Number(data.status));
      }
    } else if (content.act === "upload_batch") {
      if (data.err !== 0) {
        this.opts.log?.(`batch ${data.idx} upload failed (${data.errorCode ?? data.err})`);
        s.fail(new SyncError("server", `the phone could not upload batch ${data.idx} (${data.errorCode ?? data.err})`));
      } else {
        s.batch({ idx: Number(data.idx), isLast: !!data.isLast, batchType: Number(data.batchType), msgUrl: String(data.msgUrl) });
      }
    } else if (content.act === "transfer_error") {
      s.fail(new SyncError("server", `transfer error ${data?.errorCode ?? ""}`.trim()));
    }
    return true;
  }

  /** Reply to our cmd 590 (`{error_code, data:{reqId, err}}`); a refusal fails the newest open session. */
  handleReply(body: any) {
    const code = Number(body?.error_code ?? 0) || Number(body?.data?.err ?? 0);
    if (!code) return;
    this.opts.log?.(`sync request refused (${code})`);
    const last = [...this.sessions.values()].at(-1);
    last?.fail(new SyncError("server", `Zalo refused the sync request (${code})`));
  }
}

async function defaultFetch(url: string): Promise<Uint8Array> {
  const res = await fetch(url);
  if (!res.ok) throw new SyncError("server", `download failed (HTTP ${res.status})`);
  return new Uint8Array(await res.arrayBuffer());
}
