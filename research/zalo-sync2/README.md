# zalo-sync-proto

Offline prototype of the Zalo "sync v2" receiver (phone -> client history transfer).
Nothing here talks to a network. Read `SPEC.md` first for the protocol; this file is only about running the code.

## Run

```sh
cd /tmp/zalo-sync-proto

# self-test against the extracted copy of the WASM
bun run src/demo.ts

# same, but read the WASM straight out of the installed app (read-only, nothing is written)
bun run src/demo.ts --from-app                # default /Applications/Zalo.app/Contents/Resources/app.asar
bun run src/demo.ts --from-app /path/to/app.asar

# or point at any copy of libzproto_wasm_bg.*.wasm
bun run src/demo.ts /path/to/libzproto_wasm_bg.<hash>.wasm
```

Requires Bun >= 1.3 (uses `Bun.zstdCompressSync/DecompressSync`). Exit code 0 means every check passed.
The only external import is `protobufjs` from `/Volumes/DATA/chat-space/bridges/node/node_modules` (absolute path, read-only),
used for an independent wire-format cross-check; if it is missing that single check prints `SKIP`.
No `npm install` is needed or was run.

## What the demo proves (all offline)

1. Identity/ephemeral key generation via the real WASM (33-byte public, 32-byte private) and a symmetric DH agreement.
2. Metadata cipher and message cipher round trips with the phone side simulated by the same WASM, including negative cases (wrong EK, wrong IK, tampered record, record order independence).
3. Protobuf encode/decode of synthetic `Sync2.Message.SyncChunk` and `Sync2.Conversation.SyncChunk` with nested meta, 64-bit ids kept as strings, negative int32; cross-checked in both directions against `protobufjs`.
4. zstd round trip.
5. `u32-LE` length-prefixed blob framing round trip, truncation detection.
6. End to end: build the cmd 590 frame -> simulated phone builds a batchType 2 blob and batchType 1 blob from that exact request -> `Sync2Receiver.loadBlob` yields the original partitions/messages; "message batch before metadata batch" is rejected.
7. Classification of synthetic cmd 601 control contents (`transfer_after_login`, `transfer_status`, `upload_batch` ok/error).

## Files

| Path | Purpose |
|---|---|
| `SPEC.md` | protocol spec with evidence tags and the list of live-server unknowns |
| `sync2.proto` | reconstructed protobuf schema |
| `src/zproto.ts` | typed port of the wasm-bindgen glue (module `Vox7`) and the `sync2-proto` wrapper |
| `src/asar.ts` | minimal read-only asar reader that returns the WASM bytes from an installed app |
| `src/proto.ts` | dependency-free schema-driven protobuf codec for the Sync2 messages |
| `src/sync2.ts` | framing, zstd, WS frame helpers, request builder, `Sync2Receiver`, `PhoneSimulator`, control classifier |
| `src/demo.ts` | the self-test above |
| `_inspect.mjs` | lists WASM imports/exports |
| `work/` | scratch: prettier-formatted bundle copies (`*.pp.js`) that SPEC line numbers refer to, `mod.py` module dumper, `machine.txt` state-machine dump |

## Using it later (not done here)

- Load the WASM at runtime from the user's own Zalo install (`readWasmFromAsar()`); do not ship or copy it into the repo.
- `Sync2Receiver` is transport-agnostic: feed it `buildRequestFrame()` output over the authenticated listener socket and call `loadBlob(await fetch(msgUrl), batchType, db)` for each `upload_batch` control.
- Keep one identity key pair in memory for a whole import (the official client regenerates it per run; the phone is expected to prompt for each new key).
- zca-js `Listener` hides `ws`/`cipherKey` behind `private`; see SPEC section 11.

## Safety notes

- The prototype never reads `~/Library/Application Support/{ZaloData,ChatSpace}`, the Keychain, or any session/cookie data.
- It never prints key material; the demo only prints lengths and booleans.
