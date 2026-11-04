// Telegram update sequencing (pts) without I/O: which updates carry a pts, and whether one continues the
// local state, repeats something already applied, or reveals a gap that updates.getDifference /
// updates.getChannelDifference must fill. See https://core.telegram.org/api/updates.
import { Api, utils } from "telegram";

/** What updates.getState / getDifference give back; persisted so a restart can resume where it left off. */
export interface UpdateState {
  pts: number;
  qts: number;
  date: number;
  seq: number;
}

export interface PtsStep {
  pts: number;
  ptsCount: number;
}

/** Marked id ("-100…") for a raw channel id. */
export const channelChatId = (channelId: Api.PeerChannel["channelId"]): string =>
  utils.getPeerId(new Api.PeerChannel({ channelId }));

const hasStep = (u: unknown): u is PtsStep =>
  typeof (u as { pts?: unknown })?.pts === "number" && typeof (u as { ptsCount?: unknown })?.ptsCount === "number";

/**
 * pts/pts_count of an update in the account-wide sequence (users, basic groups, read states, deletions…), or
 * undefined for updates that do not move it. UpdateShort{Sent,}Message / UpdateShortChatMessage are included:
 * GramJS hands them to raw handlers as they are.
 */
export function commonPts(u: unknown): PtsStep | undefined {
  if (!hasStep(u)) return undefined;
  if (
    u instanceof Api.UpdateNewMessage ||
    u instanceof Api.UpdateDeleteMessages ||
    u instanceof Api.UpdateReadHistoryInbox ||
    u instanceof Api.UpdateReadHistoryOutbox ||
    u instanceof Api.UpdateWebPage ||
    u instanceof Api.UpdateReadMessagesContents ||
    u instanceof Api.UpdateEditMessage ||
    u instanceof Api.UpdateFolderPeers ||
    u instanceof Api.UpdatePinnedMessages ||
    u instanceof Api.UpdateShortMessage ||
    u instanceof Api.UpdateShortChatMessage ||
    u instanceof Api.UpdateShortSentMessage
  ) {
    return { pts: u.pts, ptsCount: u.ptsCount };
  }
  return undefined;
}

/** pts/pts_count of a channel update plus the channel's chat id; channels each have their own sequence. */
export function channelPts(u: unknown): (PtsStep & { chatId: string }) | undefined {
  if (!hasStep(u)) return undefined;
  if (u instanceof Api.UpdateNewChannelMessage || u instanceof Api.UpdateEditChannelMessage) {
    const peer = (u.message as { peerId?: Api.TypePeer }).peerId;
    if (!(peer instanceof Api.PeerChannel)) return undefined;
    return { chatId: utils.getPeerId(peer), pts: u.pts, ptsCount: u.ptsCount };
  }
  if (
    u instanceof Api.UpdateDeleteChannelMessages ||
    u instanceof Api.UpdateChannelWebPage ||
    u instanceof Api.UpdatePinnedChannelMessages
  ) {
    return { chatId: channelChatId(u.channelId), pts: u.pts, ptsCount: u.ptsCount };
  }
  return undefined;
}

export type PtsVerdict = "applied" | "duplicate" | "gap";

/**
 * Compare an update against the local pts. "applied" moves the local pts to `step.pts`; "duplicate" means the
 * server already counted it (replays after getDifference); "gap" means at least one update between the local pts
 * and this one was never seen, which only getDifference can repair.
 */
export function judgePts(local: number, step: PtsStep): PtsVerdict {
  const from = step.pts - step.ptsCount;
  if (from === local) return "applied";
  if (from > local) return "gap";
  return "duplicate";
}

/** Serialised form of the state file (DIR/updates-state.json). */
export interface SavedUpdateState extends UpdateState {
  /** Owner of the state: a different account must not resume from it. */
  selfId: string;
  /** Per-channel pts, keyed by chat id ("-100…"). */
  channels: Record<string, number>;
}

export function parseSavedState(text: string, selfId: string): SavedUpdateState | undefined {
  let v: unknown;
  try {
    v = JSON.parse(text);
  } catch {
    return undefined;
  }
  const o = v as Partial<SavedUpdateState> | null;
  if (!o || o.selfId !== selfId) return undefined;
  const n = (x: unknown) => (typeof x === "number" && Number.isFinite(x) && x >= 0 ? x : undefined);
  const pts = n(o.pts);
  const date = n(o.date);
  if (pts === undefined || date === undefined) return undefined;
  const channels: Record<string, number> = {};
  for (const [k, c] of Object.entries(o.channels ?? {})) {
    const cp = n(c);
    if (cp !== undefined && /^-100\d+$/.test(k)) channels[k] = cp;
  }
  return { selfId, pts, qts: n(o.qts) ?? 0, date, seq: n(o.seq) ?? 0, channels };
}
