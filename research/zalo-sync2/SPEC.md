# Zalo "sync v2" (phone -> client message history transfer): implementation spec

Basis: static analysis of Zalo PC 26.10.20 plus offline experiments against its `libzproto_wasm` module.
No network access was made to Zalo; nothing here has been observed on a live server.

## 0. Conventions

Evidence tags used on every non-trivial statement:

- **[R]** read directly in the shipped code.
- **[E]** verified empirically, offline, by running the shipped WASM (see `src/demo.ts`).
- **[I]** inferred from code structure; plausible but not proven.
- **[U]** unknown; needs the live server (collected in section 12).

Bundles (all under `/tmp/zalo-re/app_extracted/pc-dist/`):

| Short | File |
|---|---|
| CA | `compact-app-pc.06caa1ffa528545b8f43.js` (WS manager, cmd tables, API classes, state machine) |
| MS | `lazy/main-startup.4e81984d6f200154e824.js` (SyncController, SyncEventsHandler, config, request handlers) |
| SW | `shared-worker.06caa1ffa528545b8f43.js` (SyncSession, loaders, WASM glue, protobuf) |
| WASM | `libs/libzproto_wasm_bg.ecc0555ba9d0f8867a4699ce943ec745.wasm` |

`machine.txt` = dump of CA module `AS+M` (the sync state machine). Line numbers refer to the prettier-formatted copies in `/tmp/zalo-sync-proto/work/{compact,main-startup,shared-worker}.pp.js`
(`mod.py <file> <moduleId>` dumps a webpack module by id). Module ids are webpack ids, stable across reformatting.

The analysed client is Zalo **PC**. The user's real session is **Web** (zpw_type 30) and its server config exposes
`features.sync_2_web {manual_sync, auto_sync, sync_paused_timeout_ms}`, `transfer_when_login`, `ui_ux_transfer_revamp`.
Those keys match the PC remote-config schema `sync_2_pc` (MS 116395-116945), so the Web flow is assumed to be the same
protocol with different gating. No Web bundle was available, so every "Web does the same" statement is **[I]**.

## 1. Roles

- **Master device** = the phone. It owns the full history, encrypts it, uploads blobs, answers confirm prompts.
- **Client** = Web/PC (ChatSpace would be this). Requests ranges, downloads blobs, decrypts, stores.
- **Server** = relays requests (WS cmd 590) and pushes control events (WS cmd 601); hosts the blobs behind `msgUrl`.

## 2. Selecting v2 vs legacy v1 **[R]**

CA `_processCtrlMessages` (576493). Controls are grouped by `content.act_type`:

- `act_type = "transfer_sync2"`: always routed to `SyncController.handleReceiveServerResponse` (v2).
- `act_type = "transfer_msg"`: if `SyncController.isFeatureEnabled()` then v2, else legacy v1 (`SyncMessageController.handleTransferAfterLoginCtrlEvents`, HTTP `pull_mobile_msg` family).
- `isFeatureEnabled()` = remote config `<sync_2_pc>.enable` (MS 116410, 116555); schema default `false`, so it is server-enabled per account.
- `isFeatureEnabledBySyncSource(src)`: `SETTING` (manual) needs `enable_on_sync_source_setting` (default `true`); `AUTO_SYNC` needs `auto_sync.enable` (default `true`).
- Web equivalent keys **[I]**: `sync_2_web.manual_sync`, `sync_2_web.auto_sync`, `sync_2_web.sync_paused_timeout_ms`.

Defaults (MS 116723-116845, schema `Jhe/ege/sge`): `sync_paused_timeout_ms` 600000; `transfer_batch_size` 2000 (min 50, max 10000);
`batch_size` 2000; `conversations_batch_size` 1000; `manual_sync` and `auto_sync` each:
`awaiting_mobile_timeout_ms` 600000, `wakeup_timeout` 180000, `batch_timeout` 10000,
`sync_conv_list {max_time_range_d -1, max_number_of_conv -1}`,
`sync_last_chat {-1, 10 convs, 1 msg/conv}`, `sync_recent_chat {-1, 10 convs, 100 msg/conv}`,
`sync_all_remaining_msg {-1, -1, -1}`. `-1` means unbounded. `transfer_when_login.time_wait_for_temp_key` 3000 ms.
Server-supplied values override these.

## 3. Keys and secrets handled by the client

### 3.1 Identity key (IK) **[R]**
`IdentityKeyManager` (CA module `Bw1J`, 184142). `loadNewIdentityKeyPair()` calls the WASM key generator and keeps the
pair in memory only. The state machine's `checkingIdentityKeyPair` (CA `AS+M`, machine.txt:~120) calls
`loadNewIdentityKeyPair()` unconditionally and sets `isNewKeyGenerated = true`. So **a fresh IK is generated for every sync run**;
nothing is persisted and nothing is registered with any server other than via the request payload `ik`.
Consequence **[I]**: the phone sees an unknown device key each time, which is why `isNewKeyGenerated` selects the
`waiting_for_user_confirm` path (phone prompt) in manual sync.

### 3.2 Ephemeral key (EK) **[R]**
New per `SyncSession` (SW `Ko` ctor, 12133: `syncKey = syncProto.genSyncKeyPair()` -> `{ik, ek}`). One SyncSession = one WS request = one `syncId`.
A user-level sync run (`userSyncSessionId`, 16 random chars, MS `nme.a`/CA `qiiv`) typically creates several SyncSessions (one per phase / per ≤30 queries).

### 3.3 Key format **[E]**
`generateKeyPair()` -> public key **33 bytes** (leading `0x05`, Curve25519/"Djb" type), private key **32 bytes**.
Same generator for IK and EK. Base64 (standard, padded) is used on the wire.

### 3.4 tempKey **[R]**
String sent in the request as `tempKey`. SW `Zo.create` (12550): only the first SyncSession of a run carries it; later ones send `""`.
- Manual sync: `""` (CA `AS+M` `initNewMachineInfo`: `tempKey = e.tempKey ?? ""`).
- Auto sync after login: the `temp_key` from the `transfer_after_login` control (3.5/6.3); lets the phone confirm without a prompt (`BypassConfirmed`) **[I]**.

### 3.5 clientId / imei **[R]**
`transfer_after_login` is only accepted if `data.imei === ZaloClientID` (MS 117010). For zca-js this is the `imei` used in login.

## 4. End-to-end flow

### 4.1 Manual sync (button / "sync" suggestion) **[R]** unless tagged

1. `SyncController.manualSync(source)` (MS 117265): computes `lastSyncConvSuccessTs` and `fullResyncRound`; calls `machine.startManualSync({userSyncSessionId, source, lastSyncConvSuccessTs, fullResyncRound})`.
   `syncType` = FULL if `lastSyncConvSuccessTs == 0`, FULL_RESYNC if `fullResyncRound > 0`, else INCREMENTAL (CA `initNewMachineInfo`). This is only local bookkeeping; it is not sent.
2. Machine `init`: `manual_sync.checking_identity_key_pair` (new IK, 3.1) then `checking_message_readiness_state` (local DB check, irrelevant for ChatSpace) -> `sync_process` or `success_no_need_sync`.
3. `sync_process` runs two regions in parallel:
   - `handling_timeout` (confirm/idle timers, section 9);
   - `sync_full` (CA `AS+M`, machine.txt:192-372): `determining_initial_sync_state` -> `sync_conversations` -> `prepare_sync_preview_and_recent_messages` -> `sync_preview_messages` -> `sync_other_messages` -> success.
     If the local DB is already partially filled it can start at a later phase; for an empty client the order is the full sequence.
4. Each phase executes queries on a "remote database" (SW `zo`): `conversation` DB or `message` DB. A query that has no live SyncSession covering it calls `SyncSessionManager.useSession` -> `create` -> `Ko.start()` -> `requestSync()` (section 5).
5. The phone (via server) pushes `transfer_status` and `upload_batch` controls (section 6). The client downloads, decrypts, decodes (section 7) and imports.
6. When the last batch of every session arrives (`isLast` and all `idx` seen: SW `checkRequestfulfilled`, 12425) the session is fulfilled. On completion or abort the client sends cmd 591 (dispose) (5.4).

### 4.2 Auto sync after login **[R]**

1. After login the server pushes a control `act_type="transfer_msg"`, `act="transfer_after_login"`, `data={temp_key, imei}` inside a cmd 601 frame.
   MS `handleTransferAfterLoginEvent` (117195) picks the latest such event with `ts` within **600000 ms** of now and a non-empty `temp_key`, and a matching `imei`.
2. `SyncController.handleTransferAfterLoginTempKey` stores the key. `checkAndTriggerAutoSync` (MS 117506) starts auto sync only when **tempKey is present and `isMessageReady === false`** (a MESSAGE_READY signal cancels it).
3. `autoSyncWhenLogin(tempKey)` -> `machine.startAutoSyncFull({source: AUTO_SYNC, tempKey, lastSyncConvSuccessTs})`. Rest as 4.1 but timeouts come from `auto_sync.*` and the timeout machine uses `waiting_for_mobile_auto_confirm` instead of the user-confirm path.
4. `transfer_when_login.time_wait_for_temp_key` (3000 ms default) is how long other startup logic waits for that key **[I]**.

### 4.3 On-demand sync of one conversation **[I]**
`SyncType.SyncOnDemand = 1` and `SyncController.handleSyncSelectedConversation` exist; `syncClient.syncOnDemandWhenSyncFull(convId)` runs when the user opens a conversation during a sync. Request shape not analysed.

## 5. WebSocket requests

### 5.1 Frame **[R]**
CA WS manager `_send` (55871):

```
offset 0  u8   version  (always 1 here)
offset 1  u16  cmd      little endian
offset 3  u8   subCmd   (0 for all sync commands)
offset 4  ...  UTF-8 JSON body (the whole `data` object is the body; no envelope)
```

Request bodies are **plaintext JSON** (no AES). zca-js `sendWs` (`dist/apis/listen.js:366`) writes `cmd` as Int32LE at offset 1 and then overwrites byte 3 with `subCmd`;
for cmd < 65536 the resulting bytes are identical to the PC layout.

### 5.2 `requestAsyncV2` body shape **[R]**
CA API class (module `oQzU`, 710194-710240): `addCommonParams` copies the payload `data` and injects `reqId` into it:
`reqId = "cmd_<n>"` for a few special commands, else `"req_" + counter` (CA `XS0q`, 469769). Reply matching uses `reply.data.reqId` (710163).
Default request timeout 10 000 ms. `ede.withRetry` (MS 114971-115015): max 3 attempts, delay 1000 ms doubling to max 3000 ms, but errors with code `NO_NETWORK`, `TIMEOUT` or `211` are not retried for cmd 590.
Pre-check: socket must be `OPEN` and network up, else immediate `NO_NETWORK` (MS 115144).

**Difference from zca-js:** zca-js `sendWs(payload, requireId=true)` writes `req_id` (snake_case) into `payload.data` (top level of the body).
Zalo PC puts **`reqId` (camelCase)** at the top level of the body next to the `data` field. For cmd 590 use `requireId = false` and supply `reqId` yourself. **[U]** whether the server also accepts `req_id`.

### 5.3 cmd 590 SYNC_MESSAGE.REQUEST **[R]** (payload) **[U]** (server behaviour)

CA table `Qtro` (377135-377345): `SYNC_MESSAGE:{REQUEST:590, ACK_DELETE_SYNC_SESSION:591, REQUEST_MOBILE_WAKE_UP:592}`, `CTRL_PUSH_CTRL:601`.
Call: `requestSyncMessage(e) = requestAsyncV2({cmd:590, subCmd:0, data:{data:e}})` (710283). Body on the wire:

```json
{ "data": {
    "syncId":     "<32 random alnum chars>",
    "syncType":   0,
    "ek":         "<base64 33-byte EK public>",
    "ik":         "<base64 33-byte IK public>",
    "toDevice":   0,
    "tempKey":    "",
    "deviceName": "<os hostname / client name>",
    "req": {
      "type":      "conversation" | "message",
      "priority":  0 | 1 | 2,
      "queries":   [ { "partition": "...", "from": 0, "to": 9007199254740991, "limit": 2147483647 } ],
      "batchSize": 2000
    },
    "ver":    1,
    "ussidx": 0
  },
  "reqId": "req_N" }
```

Source: SW `Ko.requestSync` (12313-12337). Field notes:

- `syncId`: `Ot.i.generateRandomString(32)` **[R]** (charset not extracted; the prototype uses alphanumerics **[U]**).
- `syncType`: `SyncFull = 0` (`SyncOnDemand = 1`) (CA `SCpg`). Always 0 in `requestSync` **[R]**.
- `toDevice`: `Mobile = 0` (`Web = 1`, `PC = 2` are the `fromDevice` values of status events) **[R]**.
- `req.type`: the DB name: `"conversation"` or `"message"` (SW `Cc.createConversationDatabase/createMessageDatabase`, 13659-13674) **[R]**. One request = one type and one priority (`requestSync` throws otherwise).
- `req.priority`: `TaskPriority` HIGH 0 / NORMAL 1 / LOW 2 (CA `vuBx`, 799999). Used: conversation list **0**, preview+recent messages **0**, remaining messages **2** (MS 115519-115592 with CA `OzWg` constants) **[R]**.
- `req.queries[i]` = `{partition, from, to, limit}`: `from/to` are ms timestamps on the DB's index (`lastTs` for conversations, `timestamp` for messages), `to` defaults to `Number.MAX_SAFE_INTEGER`, `limit` `2147483647` means unbounded (SW `yo`, 11589) **[R]**. At most **30** queries are batched per request (SW `buildPrepareQuery`, 12981) **[R]**.
- `req.batchSize` = `transfer_batch_size` (default 2000) **[R]**.
- `ver = 1`, `ussidx` = per-`userSyncSessionId` counter starting at 0 (SW `jo.nextIndex`) **[R]**.
- `deviceName`: `Po.getDeviceName()` = `os.hostname()` (SW 16497-16501) **[R]**; shown on the phone prompt **[I]**.
- `tempKey`: 3.4.

Response **[R]** (shape) / **[U]** (content): `{error_code, error_message?, data:{reqId, ...}}`; MS `convertHTTPResponseToCommonResponsePayload` maps `error_code != 0` to failure.
SW `Ko.requestSync` handles codes: `Timeout`, `RequestSocketClosed`, **211** (special error class `Co.b`, meaning unknown **[U]**), anything else -> `Net:<code>`.
After a successful ack: start timer `connectTimeout` (= `awaiting_mobile_timeout_ms` = 600000 default) if non-zero, otherwise `batchTimeout`.

### 5.4 cmd 591 ACK_DELETE_SYNC_SESSION / cmd 592 REQUEST_MOBILE_WAKE_UP **[R]**

- 591: `requestAsyncV2({cmd:591, subCmd:0, data:{data:{syncId, toDevice, reason}}})`, reason enum (CA `SCpg`.a): `UserCancel 0, SyncSuccess 1, Error 2, OutDated 3`. Timeout 3000 ms, retry as above (MS 115017-115033).
- 592: `requestAsyncV2({cmd:592, subCmd:0, data:{syncId, toDevice}})` (note: **not** nested under `data`; the body is `{syncId, toDevice, reqId}`). Timeout 3000 ms. Sent by `Ko.wakeup()`; afterwards the batch timeout is set to `wakeup_timeout` (180000) (SW 12196-12206).

### 5.5 HTTP twin **[R]** (exists) / dead code **[R]**
`GET {fileDomain}/api/transfer-sync-v2/request-sync?{common params}&params=<encoded {reqId, data: JSON.stringify(req)}>` (CA 601307). Common params carry `zpw_ver`, `zpw_type`; encoding is the standard `_encodeParams` (AES-CBC `ParamsEncryptor`, CA 597277).
**No caller references it in this build**; the sync-v2 request handler (MS 115105-115198) uses WS only. Treat it as an unexercised fallback; do not rely on it.

## 6. Control events (cmd 601)

### 6.1 Envelope **[R]**
Server -> client frames with header `version=1, cmd=601, subCmd=0`. Body (after normal `encrypt` handling; zca-js `decodeEventData` handles encrypt 0-3 with the cipher key from the cmd 1 / sub 1 handshake):

```json
{ "controls": [ { "content": { "act_type": "...", "act": "...", "data": "<JSON string or object>", "ts": 1730000000000 } } ],
  "saveActionId": "..." }
```
`data` is parsed with `JSON.parse` if it starts with `{` or `[` (CA 576493 ff). zca-js 2.2.0 (`listen.js:213`) already receives these frames and silently ignores unknown `act_type`s.

### 6.2 Routing **[R]**
MS `SyncEventsHandler.handleControlEvents` (117077):

| Predicate | Action |
|---|---|
| `act=transfer_after_login`, `act_type=transfer_msg`, `data.temp_key` non-empty, `data.imei == own imei` | store temp key (4.2) |
| `act=transfer_status` and `data.fromDevice == 0` (Mobile) | `OnMobileStatusReceived(data)` for the statuses below |
| `act=upload_batch` and `data.err == 0` | `handleSyncBackupInfo`: batchType 1 -> `OnMessageBatchReceived`; batchType 2 -> `OnMetaBatchReceived` |
| `act=upload_batch` and `data.err != 0`, or `act=transfer_error` | `handleSyncError`: same two channels with `err = data.errorCode` |

### 6.3 `transfer_status` **[R]**
`data = {status, fromDevice, syncId, ...}`; SyncSession filters on `syncId` (SW 12107-12130). `status` (CA `SCpg`.b):

| Value | Name | Client reaction |
|---|---|---|
| 1 | Active | phone working; start `batchTimeout` (10 s) |
| 2 | Idle | phone paused/backgrounded; clear batch timer; machine goes to `mobile_idle` (fails after `sync_paused_timeout_ms` = 600000) |
| 3 | WaitingConfirm | phone prompt is showing; `connectTimeout` timer |
| 4 | Confirmed | user tapped confirm; step -> Preparing |
| 5 | BypassConfirmed | confirmation not needed (tempKey / trusted key) |
| 6 | UserReject | fail with `USER_REJECT_BACKUP` |
| 7 | UserCancel | phone cancelled |
| 8 | MasterDeviceBusy | fail `MOBILE_BUSY` |
| 9 | MasterDeviceLowStorage | fail `MOBILE_LOW_STORAGE` |
| 10 | MasterDeviceLogout | fail `MOBILE_LOGGED_OUT` |

### 6.4 `upload_batch` **[R]** (fields consumed), **[U]** (any additional fields)
`data = {syncId, idx, isLast, err, batchType, msgUrl, scopes:[{partition, from, to}], errorCode?}`.

- `batchType` (CA `PL82`): `Message = 1`, `MetadataEmbedded = 2`.
- `idx`: 0-based batch counter inside one `syncId`; duplicates are dropped (SW `dedupBatchInfo`, 12388). `isLast` fixes `totalBatch = idx + 1`; session fulfilled when `|received idx| == totalBatch`.
- `scopes`: ranges covered by the blob. For batchType 2 the client overwrites `scopes[*].partition` with `metadata_<syncId>` (SW 12097).
  `from` missing/0 -> 0; `to` missing/0 -> `MAX_SAFE_INTEGER`. First batch of a partition has `to == 0`; later `to` values are patched from neighbours (`expanseScopeIfNeeded`, 12431).
- Ordering: the client pre-fetches batches in `idx` order per partition; metadata batch must be decrypted first (message decrypt waits up to 60 s for the session record, SW 12496-12505).
- `msgUrl`: URL of the blob (section 7.1).

## 7. Receiving the data

### 7.1 Download **[R]**
SW `Gl` download service (16309-16495): plain `fetch(msgUrl, {signal, keepalive:true})`, **no custom headers, no cookies**, `r.ok` required (404 -> `DOWNLOAD_STATUS_404`, >=500 -> `DOWNLOAD_SERVER_ERROR`), body read as `blob`/`arrayBuffer`.
Per-download timeout 60 000 ms, 3 retries with 1000 ms delay, 2 concurrent downloads, results cached on disk. The URL is therefore a capability URL **[I]** (self-authenticating). **[U]** URL host/expiry.

### 7.2 Record framing **[R]** / **[E]**
SW class `Ql` (16290): blob = repeated `[u32 little-endian length][length bytes]` until end. Verified by `src/demo.ts` section 5.

### 7.3 Per-record pipeline **[R]**
SW `uc.load` (13164): `for each record: decryptChunk(record, batchType) -> zstd decompress -> protobuf deserialize -> hand partitions to storage`.
Both batch types go through the same decompress+deserialize; the chunk schema is chosen by the DB type of the request (`conversation` -> `Sync2.Conversation.SyncChunk`, `message` -> `Sync2.Message.SyncChunk`) (classes `Ic`, `hc`, 13611/13260).

### 7.4 Cryptographic handshake **[R]** (call order) / **[E]** (argument roles)

WASM exports used (Vox7 glue, SW 16970-17689; wrapper class `sync2-proto`, SW 10503-10616):

| Step | Who | Call | Inputs | Output |
|---|---|---|---|---|
| a | client | `zprotoEd25519GenerateKeyPair()` | none | 33B pub / 32B priv (IK once per run, EK per SyncSession) |
| b | client | send `ik`, `ek` (base64 pubs) in cmd 590 | | |
| c | phone | `zprotoSync2CreateMetadataCipher(plaintext, phoneIkPub, phoneIkPriv, rxIkPub, rxEkPub, [str])` | first zstd'd chunk, phone IK pair, **client IK pub, client EK pub** | `{sessionId, ciphertext, receiverIdentity, sessionRecord}` |
| d | client | `zprotoSync2DecryptMetadata(ciphertext, ikPub, ikPriv, ekPriv, [str])` for every record of a **batchType 2** blob | | `{sessionId, plaintext, senderIdentity, sessionRecord}` |
| e | phone | `zprotoSync2EncryptMessage(plaintext, sessionRecord)` per record of batchType 1 blobs | | `{sessionId, ciphertext}` |
| f | client | `zprotoSync2DecryptMessage(ciphertext, sessionRecord)` | `sessionRecord` from step d | `{sessionId, plaintext}` |

Facts established offline **[E]** (phone side simulated with the same WASM, `src/demo.ts` section 2):

- Roles of arguments in c/d are exactly as in the table; decrypt succeeds only with the matching client IK pair **and** EK private key; wrong IK or wrong EK -> `Decryption failed: aead::Error`.
- `senderIdentity` returned by d equals the phone IK public key; `receiverIdentity` returned by c equals the client IK public key. The client can therefore pin/log the phone identity.
- `sessionId` is a 44-char base64 string (32 raw bytes), identical across metadata and message ciphers of one session. It is random, not derived from the keys (`zprotoSync2GetSessionId(a,b,c)` is exported but unused by the app and its inputs are unknown; not needed).
- `sessionRecord` is 79 bytes: protobuf `{1: bytes(32) sessionId, 2: bytes(32) key, 3: varint}` (observed by hexdump; third field looks like a timestamp). Metadata ciphertext = protobuf `{1: bytes(32) sessionId, 2: bytes AEAD}`; 305 bytes of overhead for a 14-byte plaintext.
- Message encryption is **stateless and randomized**: any number of records can be decrypted in any order with one `sessionRecord`; flipping a bit is detected (AEAD).
- The optional trailing string argument is not authenticated by either side (mismatch still decrypts); the app never passes it.
- A message batch cannot be decrypted before its metadata record has produced the `sessionRecord`.
- One metadata batch per `syncId`: the client tracks `metadata_<syncId>` as the first item of its sequence tracker, before the data partitions (SW 13002) **[R]**. Its `idx` value is not asserted anywhere **[U]**.
- `generateKeyPair`'s output is also the format `zprotoEd25519CalculateAgreement(ownPrivate, peerPublic)` consumes (32-byte shared secret, symmetric) **[E]**.

Why batchType 2 is called "MetadataEmbedded" **[I]**: the record decrypted with `DecryptMetadata` is passed through the same decompress/deserialize path as data records (SW `buildMetadata`, 12912), so it carries the session key **and** the first data chunk.

### 7.5 zstd **[R]**
SW `Fi` codec (10622-10660): `zstd-codec` `ZstdStream.compress/decompress`, i.e. standard zstd frames (magic `28 b5 2f fd`). Bun's built-in `Bun.zstdDecompressSync` decodes them **[E]** (round-trip only; real phone-produced frames untested **[U]**, but the format is the standard one).

### 7.6 Protobuf schema **[R]**
Full reconstructed schema: `sync2.proto` (field numbers and kinds read from the generated `deserializeBinaryFromReader`/`serializeBinaryToWriter`, SW modules `1y5R` line 264, `Ho1Y` 16508, `N3ek` 16710).
Summary:

```
Message.SyncChunk   { platform=1 enum; partitions=2 [Partition] }
Message.Partition   { id=1 string; messages=2 [Message] }
Message.Message     { senderId=1 string; clientId=2 u64; msgStatus=3 enum; msgType=4 i32; timestamp=5 u64;
                      globalId=6 i64 (decimal string in JS); content=7 string; meta=8 MetaData }
Message.MetaData    { attachs=1 [Attach]; mentions=2 [Mention]; reference=3; quote=4; property=5; ttl=6 u64 }
Conversation.SyncChunk   { platform=1 enum; conversations=2 [Conversation] }
Conversation.Conversation{ convId=1 string; convType=2 enum(1 oneone, 2 group); respondedByMe=3 bool;
                           lastTs=4 u64; lastGlobalId=5 u64 (string); lastClientId=6 u64 }
```
Verified byte-compatible with `protobufjs` in both directions for the fields exercised **[E]**. The jspb quirk to preserve: `clientId`, `timestamp`, `lastTs` are lossy JS numbers in the app; `globalId`, `lastGlobalId`, `Quote.globalMsgId` are decimal strings. In Bun use BigInt if exact 64-bit values matter.
`platform` is written by the producer; the receiver ignores it (the app's own serializer writes `PLATFORM_TYPE_PC = 3`). The value the phone writes is **[U]**.

### 7.7 Partitions and ids **[R]** / **[U]**
- Message partition string: `` `${isGroup ? "group" : "oneone"}/${id}` `` (CA module `aSMs`, 530025). The same string is used as `queries[].partition` and as `Partition.id` in returned chunks (SW `hc.paritionData`: `{partition: e.id, data: e.messagesList}`).
- `id` is the **"global noised id"** (`globalId`), not the plain Zalo uid. App mapping (MS 38194-38275, CA 805400-805555): for 1:1, friend profile `globalId` <-> `userId`; for groups, group `globalId` <-> `groupId`. Both are fields of the normal profile / group-info payloads (zca-js models: `User.globalId`, `Group.globalId`, `getGroupMembersInfo` -> `globalId`). Server fallbacks exist: `getGlobalNoiseId` / `getUidFromGlobalNoiseId` **[U: endpoints and params not extracted]**.
- Conversation chunk: `Conversation.convId` is the **global noised id** and `convType` says oneone vs group (SW `normalizeConversations`, 14741: `{globalId: e.convId, isGroup: e.convType === Group}`).
  The app's own conversation ids use a `g` prefix for groups (`convId.startsWith("g")`, 13995) which does not appear on the wire.
- The app drops partitions whose global id has no known local mapping (`scope-dropped-no-global-id` reporter, SW 9949-9959). ChatSpace must have `globalId` for every friend/group it wants (zca-js `getAllFriends`/`getAllGroups`/`getGroupInfo` provide them).

### 7.8 Message field mapping **[R]** (shape) / **[I]** (semantics)

| sync2 field | Meaning (inferred from names and the app's `convertToNormalizedMessages` caller) | Zalo web equivalent |
|---|---|---|
| `senderId` | sender's id, **[U]** whether global noised id or uid (the app maps with `gNoised2PCNoised`) | `uidFrom` |
| `clientId` | client message id | `cliMsgId` |
| `globalId` | server message id (string, > 2^53 possible) | `msgId` |
| `timestamp` | ms | `ts` |
| `msgType` | client message type | `cliMsgType` / webchat msg type |
| `msgStatus` | 0 unspecified, 1 fail, 2 sending, 3 sent, 4 received, 5 seen | derived |
| `content` | text, or JSON string for rich types | `content` |
| `meta.attachs[]` | `{type, extInfo, action, params, title, href, thumb, description, catId, id, childNumber, remains, zinstantData, zinstantMsg}` | `content` params of photo/file/link |
| `meta.mentions[]` | `{uid, pos, len, type}` | `mentions` |
| `meta.quote` | `{ownerId, ts, fromD, cliMsgId, globalMsgId, msg, cliMsgType, ttl, attach, partialQuote}` | `quote` |
| `meta.reference`, `meta.property`, `meta.ttl` | reference, text style, self-destruct ttl | |

Real values for `msgType` and the exact encoding of `content`/`attachs[].params` for each type require sample data **[U]**.

## 8. Which queries a full sync sends **[R]** (structure) / **[I]** (empty-client concrete values)

Order for an empty client (CA `AS+M` `sync_full`, machine.txt:192-372), each step a separate request type/priority:

1. `conversation` DB, `priority 0`: one query `{partition:"", from: conversationRange.fromTs (0 if unbounded), to: MAX_SAFE_INTEGER, limit: limitConversationsToSync or conversations_batch_size (1000)}` (SW 14818). Paged by the client: next page uses `to = lastRow.lastTs - 1` while a page is full (SW `executeGetAllQuery`, 11948). Ordered by `lastTs` descending.
2. Prepare step (`prepare_sync_preview_and_recent_messages`): `executePrepareMessageQueries`: for each conversation in the list to sync, `{partition, from:fromTs, to:toTs, limit}`, grouped into requests of ≤30 queries (SW 12976-12988, 14079), `type:"message"`. Pre-announces ranges so the phone can start encoding while the client consumes.
3. Preview+recent (`sync_preview_messages`), `message` DB, `priority 0`: per conversation, limited by `max_number_of_conv` (`sync_last_chat` 10 convs, 1 msg; `sync_recent_chat` 10 convs, 100 msgs by default) (MS 115519-115592, SW 14530-14640).
4. Remaining history (`sync_other_messages`), `message` DB, **`priority 2`**: `max_number_of_conv -1`, `max_number_of_msg_per_conv -1`, `max_time_range_d -1` -> unbounded; `batch_size` 2000.
`sync_conv_list` / `sync_last_chat` etc. above are remote-config names; the numbers are the schema defaults, the server may change them.

For ChatSpace the minimal viable request set is steps 1 and 4 (with `from:0`, `limit:2147483647` per partition) **[I]**; steps 2-3 are UX optimisations of the official client.

## 9. Phone confirmation, timeouts, errors **[R]**

Timeout machine (CA `AS+M`, 500-725):

- `confirming`: overall limit `confirmTimeout` = `<manual|auto>_sync.awaiting_mobile_timeout_ms` (600000). Sub-states: `waiting_for_mobile_open` (initial, not auto, key not new) / `waiting_for_user_confirm` (new IK: always true here) / `waiting_for_mobile_auto_confirm` (auto sync; shows an "idle" banner after `auto_sync.show_idle_banner_after_not_receive_user_confirm_event_in_ms` = 3000).
  Leaves on `BypassConfirmed` (-> no_timeout) or `USER_CONFIRM`. On timeout: `USER_DONT_CONFIRM` (manual) / `MOBILE_DONT_AUTO_CONFIRM` (auto).
- `running`: `mobile_active` <-> `mobile_idle`. Idle longer than `sync_paused_timeout_ms` (600000) -> `MOBILE_IDLE_TIMEOUT`.
- Per-batch watchdog `batch_timeout` 10 000 ms between batches while Active; `wakeup_timeout` 180 000 after a wake-up (cmd 592); `rePrepareMaxRounds` 0 by default (no re-prepare).
- Client-side error codes (CA `o7eC`): EncryptMeta 101, DecryptMeta 102, EncryptMessage 201, DecryptMessage 202, Serialize 301, Deserialize 302, Compress 401, Decompress 402, IO 501, PartitionNotFound 502, NoiseId 601, UploadError 701, DownloadError 702, Unknow 999, ClientNotSupport 1000, Timeout 1100, ConnectTimeout 1200, WakeupTimeout 1300, BatchTimeout 1400, Network 1500, BackupRangeError 1600, DatabaseClosed 1700, RequestSocketClosed 1800.

## 10. Differences between the analysed client (PC) and ChatSpace (Web session)

| Topic | PC (read) | Web / ChatSpace |
|---|---|---|
| Config key | `sync_2_pc` | `sync_2_web` **[I]** |
| Transport | WS cmd 590/591/592 + cmd 601 | same **[I]** (the user's web session receives the same feature flags) |
| Identity key | fresh per run, memory | same is enough **[I]** |
| `toDevice` | 0 (Mobile) | 0 **[I]** |
| Storage | snowflake DB + search index | ChatSpace's own store |
| Device label | `os.hostname()` | any string |
| WASM | shipped in bundle | load from `/Applications/Zalo.app` at runtime (no redistribution) |
| WS header/body | `reqId` | zca-js uses `req_id`; send `reqId` (5.2) |

## 11. Integration pitfalls

- zca-js 2.2.0 `Listener` keeps `ws` and `cipherKey` as private fields (`listen.d.ts`); both are plain properties at runtime. To receive cmd 601 `transfer_*` controls and the cmd 590 reply you must attach an extra `message` handler on the same socket and decode with the same `decodeEventData` logic (AES-GCM `encrypt` 1-3 + inflate). zca-js emits `cipher_key` once.
- The `transfer_after_login` imei check compares against the client id used at login; ChatSpace must pass the same imei it logged in with.
- Use BigInt (or strings) for `globalId`/64-bit ids; do not round-trip through Number.
- Never log `ik`/`ek` private keys, `tempKey` or `msgUrl`.

## 12. Unknowns that need the live server

**Live results, 2026-10-30** (`bridges/node/scripts/zalo-sync2-probe.ts`, web session type 30, steps 1-5 of section 13):
- (1) Accepted. Reply is cmd 590 `{error_code:0, data:{reqId, err, ts}}`, AES-GCM `encrypt` like other frames; `reqId` is echoed camelCase. cmd 591 replies `{err, ts}`.
- (3) `transfer_status` data = `{syncId, fromDevice:0, status}`. `upload_batch` data = `{err, reqId, ts, fromDevice, msgUrl, syncId, ik, batchType, idx, isLast (0/1), scopes}`; one scope each.
- (4) First request: `WaitingConfirm` at +0.3 s, `Confirmed` after the user tapped (~6 s). Second request in the same run with the same IK: `BypassConfirmed`, no prompt.
- (6) Blobs fetched plain; record framing, decryption and zstd exactly as in sections 7-8. A batchType 2 blob (324 bytes) carried 0 rows; the rows came in the batchType 1 blob.
- (7) `convId` is the global noised id (0 matches against uids). `senderId` of incoming 1:1 messages is the peer's global id; the user's own messages carry another id (the self global id, presumably). Friends (504/504) and groups (208/208) all have `globalId`, but only 11 of 39 recent 1:1 conversations were friends; the rest need `getUserInfo` or the server lookup.
- Ids: `clientId` == zca-js `cliMsgId` for 20/20 messages already in the ChatSpace store; `globalId` == `msgId` for 16/20 (4 messages had no `globalId`).
- Seen in 20 messages: `msgType` 0, 4 and 19; `msgStatus` 4; every message has `meta` with `attachsList` and `property`.
- cmd 601 bodies did not inflate with a zlib-wrapper-only inflate ("incorrect header check"); `src/zalo-sync2/ws.ts` falls back to gzip/raw inflate, which decodes them.

1. Does the server accept cmd 590 from a Web session (zpw_type 30) with the exact PC payload, and what are the `data` and `error_code` values of a successful reply? Does it echo `reqId` or `req_id`?
2. Meaning of error 211 and of any other server codes on cmd 590.
3. Exact JSON of `transfer_status` / `upload_batch` events as seen by a **Web** session (additional fields, whether `syncId` always present, whether `scopes` can have more than one entry in practice).
4. Whether the phone shows a confirmation prompt for a Web client and its text (expected yes on every manual sync, since the IK is new each time).
5. Whether `transfer_after_login` / `temp_key` is pushed to Web sessions (`features.transfer_when_login` suggests yes) and its lifetime.
6. `msgUrl` host, expiry, size limits; whether real blobs are plain zstd frames per record (expected) or add framing.
7. What `Partition.id` / `Message.senderId` contain on real data (global noised ids vs uids) and `platform` value written by the phone.
8. Concrete `msgType`/`content`/`attachs` encodings per message kind.
9. Server rate limits (the app caps at `max_number_of_concurrent_sync_items` 4, `transfer_batch_size` 50..10000).
10. Behaviour when the phone is offline (`wakeup` cmd 592 semantics, `MasterDeviceBusy` conditions).
11. Endpoints `getGlobalNoiseId` / `getUidFromGlobalNoiseId` (only needed if `globalId` fields are missing from the profile/group payloads).

## 13. Minimum live sequence (and prompts)

1. (no prompt) Log in as today; capture `cipher_key`; attach a raw WS observer. Log every cmd 601 frame whose `act_type` is `transfer_*` (read-only; this alone answers unknowns 3 and 5 if the server pushes anything).
2. (no prompt) Ensure `globalId` is stored for friends/groups (existing zca-js calls).
3. **(phone prompt, expected)** Send one cmd 590: `type:"conversation"`, `priority:0`, one query `{partition:"", from:0, to:MAX_SAFE_INTEGER, limit:50}`, `tempKey:""`. Observe the reply, `transfer_status` sequence, `upload_batch` events. Answers unknowns 1-4, 6.
4. (no prompt) Download the `msgUrl` blobs, run `Sync2Receiver.loadBlob`. Answers 6-8.
5. Request one `message` partition for a single test conversation with `limit:20`, **reusing the same IK** (new EK/`syncId`). Expect no second prompt **[I]**. Then send cmd 591 `{syncId, toDevice:0, reason:1}` for each finished session.
6. Only after 1-5 look right: request the full history (`priority 2`, 30 queries per request).

Within one run the IK is constant and only the EK changes per SyncSession **[R]** (SW `Ko` ctor reuses the `identityKey` given to `sync2-proto.init`). Whether the phone prompts once per run or once per `syncId` is **[U]**. A new IK (new process or a deliberately regenerated key) is expected to prompt again **[I]**; keep the IK in memory for the whole history import.
