// Shared helpers for a bridge's on-disk media cache (`DIR/media`), driven by StorageConfig.
//
//   usage(dir)                  -> { mediaBytes, mediaFiles }
//   prune(dir, cfg, reserve?)   -> drop files unused for keepMediaDays, then least-recently-accessed ones until
//                                  the cache fits maxCacheMB (minus `reserve` bytes about to be written)
//   clear(dir, olderThanDays?)  -> delete everything, or only files not accessed for N days
//   touch(path)                 -> mark a file as used now (atime), keeping its mtime
//   compressImage(path, cfg)    -> JPEG/PNG -> HEIC (q60, longest side capped at 1600 px, never upscaled) via macOS
//                                  `sips`; original is kept unless the result is smaller. Returns the path that
//                                  now holds the media.
//   treeSize(path...)           -> bytes under files/directories (for databaseBytes)
//   inflight(map, key, fn)      -> share one running promise per key (two media.download of the same message)
//
// "Last used" is the file's atime. Reads by the app may or may not bump it (APFS is lazy), so adapters call touch()
// on every cache hit of media.download. Files ending in .part/.tmp are in-flight downloads/conversions and are never
// pruned or cleared unless they are stale (older than an hour).
import { execFile } from "node:child_process";
import { readdirSync, renameSync, rmSync, statSync, utimesSync } from "node:fs";
import { extname, join } from "node:path";
import type { StorageConfig } from "./types.ts";

export type CacheConfig = Pick<StorageConfig, "keepMediaDays" | "maxCacheMB" | "compressImages">;

export interface CacheResult {
  freedBytes: number;
  /** absolute paths of the removed files, so the caller can forget them in its message store */
  deleted: string[];
}

const DAY_MS = 86_400_000;
const STALE_TEMP_MS = 3_600_000;
const MB = 1024 * 1024;

/** Below this a photo is not worth re-encoding (and spawning sips). */
const MIN_COMPRESS_BYTES = 24 * 1024;
const COMPRESSIBLE = new Set([".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp"]);
const SIPS = "/usr/bin/sips";
const SIPS_TIMEOUT_MS = 30_000;
const MAX_SIDE = 1600;
const HEIC_QUALITY = 60;

interface Info {
  path: string;
  size: number;
  atimeMs: number;
  mtimeMs: number;
  temp: boolean;
}

function walk(dir: string, out: Info[] = []): Info[] {
  let names: string[];
  try {
    names = readdirSync(dir);
  } catch {
    return out;
  }
  for (const name of names) {
    const p = join(dir, name);
    try {
      const st = statSync(p);
      if (st.isDirectory()) walk(p, out);
      else if (st.isFile()) {
        out.push({
          path: p,
          size: st.size,
          atimeMs: st.atimeMs,
          mtimeMs: st.mtimeMs,
          temp: /\.(part|tmp)$/.test(name),
        });
      }
    } catch {}
  }
  return out;
}

/** Files that may be deleted: everything except a download/conversion that is still running. */
function removable(files: Info[], now: number): Info[] {
  return files.filter((f) => !f.temp || now - f.mtimeMs > STALE_TEMP_MS);
}

function remove(f: Info, res: CacheResult): boolean {
  try {
    rmSync(f.path);
  } catch {
    return false;
  }
  res.freedBytes += f.size;
  res.deleted.push(f.path);
  return true;
}

export function usage(dir: string): { mediaBytes: number; mediaFiles: number } {
  let mediaBytes = 0;
  let mediaFiles = 0;
  for (const f of walk(dir)) {
    mediaBytes += f.size;
    if (!f.temp) mediaFiles++;
  }
  return { mediaBytes, mediaFiles };
}

/**
 * Enforce keepMediaDays (0 = forever) and maxCacheMB (0 = unlimited). `reserveBytes` is room to leave for a file
 * that is about to be written, so the cache does not overshoot the cap by the size of the next download.
 */
export function prune(dir: string, cfg: CacheConfig, reserveBytes = 0, now = Date.now()): CacheResult {
  const res: CacheResult = { freedBytes: 0, deleted: [] };
  let live = removable(walk(dir), now);

  const days = Number(cfg.keepMediaDays);
  if (days > 0) {
    const cutoff = now - days * DAY_MS;
    live = live.filter((f) => !(f.atimeMs < cutoff && remove(f, res)));
  }

  const capMB = Number(cfg.maxCacheMB);
  if (capMB > 0) {
    const budget = Math.max(0, capMB * MB - Math.max(0, reserveBytes));
    let total = live.reduce((n, f) => n + f.size, 0);
    live.sort((a, b) => a.atimeMs - b.atimeMs); // least recently used first
    for (const f of live) {
      if (total <= budget) break;
      if (remove(f, res)) total -= f.size;
    }
  }
  return res;
}

/** Delete all cached media, or only files whose last use is older than `olderThanDays`. */
export function clear(dir: string, olderThanDays?: number, now = Date.now()): CacheResult {
  const res: CacheResult = { freedBytes: 0, deleted: [] };
  let cutoff = Infinity; // undefined / 0 -> everything
  if (olderThanDays !== undefined && olderThanDays !== 0) {
    // A garbage value must never turn into "delete everything".
    if (!Number.isFinite(olderThanDays) || olderThanDays < 0) return res;
    cutoff = now - olderThanDays * DAY_MS;
  }
  for (const f of removable(walk(dir), now)) if (f.atimeMs < cutoff) remove(f, res);
  return res;
}

/** Mark a cached file as used just now (atime) without touching its mtime. */
export function touch(path: string): void {
  try {
    utimesSync(path, new Date(), statSync(path).mtime);
  } catch {}
}

/** Total bytes of the given files / directories (missing ones count 0). */
export function treeSize(...paths: string[]): number {
  let n = 0;
  for (const p of paths) {
    try {
      const st = statSync(p);
      if (st.isDirectory()) for (const f of walk(p)) n += f.size;
      else n += st.size;
    } catch {}
  }
  return n;
}

/** One running promise per key: concurrent callers share the same download. */
export function inflight<T>(map: Map<string, Promise<T>>, key: string, fn: () => Promise<T>): Promise<T> {
  const running = map.get(key);
  if (running) return running;
  const p = fn().finally(() => map.delete(key));
  map.set(key, p);
  return p;
}

function runSips(args: string[]): Promise<{ ok: boolean; out: string }> {
  return new Promise((resolve) => {
    try {
      execFile(SIPS, args, { timeout: SIPS_TIMEOUT_MS }, (err, stdout) => resolve({ ok: !err, out: String(stdout ?? "") }));
    } catch {
      resolve({ ok: false, out: "" });
    }
  });
}

/** Longest side in pixels, or undefined when sips cannot read the file. */
async function longestSide(path: string): Promise<number | undefined> {
  const r = await runSips(["-g", "pixelWidth", "-g", "pixelHeight", path]);
  if (!r.ok) return undefined;
  const w = Number(/pixelWidth:\s*(\d+)/.exec(r.out)?.[1]);
  const h = Number(/pixelHeight:\s*(\d+)/.exec(r.out)?.[1]);
  return w > 0 && h > 0 ? Math.max(w, h) : undefined;
}

/**
 * Re-encode a downloaded photo as HEIC (quality 60, longest side <= 1600 px) with `sips`. The original is replaced
 * only when the HEIC is strictly smaller; any failure (no sips, unsupported file, timeout) keeps the original.
 * Returns the path that holds the media afterwards (`<name>.heic` when compressed, else `path`).
 * GIF/WebP/HEIC are left alone: animated or already efficient. Videos are not touched.
 */
export async function compressImage(path: string, cfg: Pick<CacheConfig, "compressImages">): Promise<string> {
  if (!cfg.compressImages || process.platform !== "darwin") return path;
  const ext = extname(path).toLowerCase();
  if (!COMPRESSIBLE.has(ext)) return path;
  let before: number;
  try {
    before = statSync(path).size;
  } catch {
    return path;
  }
  if (before < MIN_COMPRESS_BYTES) return path;

  // `sips -Z` also UPSCALES smaller images, so only pass it when the picture really is larger than the cap.
  const side = await longestSide(path);
  if (side === undefined) return path; // not an image sips understands

  const final = path.slice(0, path.length - ext.length) + ".heic";
  const tmp = final + ".part"; // ends in .part: invisible to prune() while sips runs
  const args = ["-s", "format", "heic", "-s", "formatOptions", String(HEIC_QUALITY)];
  if (side > MAX_SIDE) args.push("-Z", String(MAX_SIDE));
  const { ok } = await runSips([...args, path, "--out", tmp]);
  try {
    const after = ok ? statSync(tmp).size : Infinity;
    if (after > 0 && after < before) {
      renameSync(tmp, final);
      rmSync(path, { force: true });
      touch(final);
      return final;
    }
  } catch {}
  rmSync(tmp, { force: true });
  return path;
}
