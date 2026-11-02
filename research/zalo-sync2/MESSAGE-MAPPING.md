# Zalo PC "sync v2" Message -> web-shape message mapping (static analysis)

Scope: read-only analysis of the extracted PC bundle `/tmp/zalo-re/app_extracted/pc-dist/`. Nothing was executed, no network.
Offsets are JS string character indexes (Python `str.find`), not byte offsets. `~` = approximate.

Legend: **READ** = seen in code (location cited). **INFERRED** = deduced, not directly visible.

File aliases:
- **SW** = `shared-worker.06caa1ffa528545b8f43.js`
- **LZ** = `lazy/default-login-main-startup-shared-worker-znotification.d9141cdf23123a5c2330.js`
- **MS** = `lazy/main-startup.4e81984d6f200154e824.js`

---

## 0. Pipeline (READ)

```
DB rows from phone (protobuf Sync2.Message.Message, decoded via toObject)
  -> kl.convertToNormalizedMessages(globalNoisedId, convId, rows, cfg)      SW @357836 (class kl @357769)
       pre-pass: batch-resolve every global id (conv, senders, mentions, quote owner, contact param)
       -> Rl.convertToNormalizedMessage -> El.syncMessage2SVMessage         SW @352726 (class El)
            = "server-message" (web-like) object                            <-- the shape you want
       -> jl.transformMessageFromServer(svMsg, convId)                      SW @362483 (class jl)
            = PC DB message model (MSG_* numeric types, message payload normalisation)
```

`El.syncMessage2SVMessage` output is already the web/server shape. `jl` is only needed to learn how the PC consumes it.
Messages for which a step returns `null` are silently dropped; `kl.logDroppedMessages` only logs counts per msgType (SW @359264).

### 0.1 Protobuf schema as compiled in the bundle (READ, SW)

| Message | Fields (number: name type) | toObject offset |
|---|---|---|
| Message | 1 senderId string, 2 clientId uint64, 3 msgStatus enum, 4 msgType int32, 5 timestamp uint64, 6 globalId int64-as-string, 7 content string, 8 meta MetaData | @12989 |
| MetaData | 1 attachs[] Attach, 2 mentions[] Mention, 3 reference Reference, 4 quote Quote, 5 property Properties, 6 ttl uint64 (optional) | @38059 |
| Attach | 1 type s, 2 extInfo s, 3 action s, 4 params s, 5 title s, 6 href s, 7 thumb s, 8 description s, 9 catId i32, 10 id i32, 11 childNumber i32, 12 remains s, 13 zinstantData s, 14 zinstantMsg s (all optional -> `undefined` when absent) | @24054 |
| Mention | 1 uid s, 2 pos i32, 3 len i32, 4 type i32 | @17091 |
| Quote | 1 attach s, 2 ownerId s, 3 ts int64, 4 fromD s, 5 cliMsgId uint64, 6 globalMsgId uint64-as-string, 7 msg s, 8 cliMsgType i32, 9 ttl uint64 (opt), 10 partialQuote s (opt) | @19274 |
| Reference | 1 type i32, 2 data s | @36444 |
| Properties | 1 size i32, 2 color i32, 3 type i32, 4 subType i32, 5 ext s (all optional) | @32761 |
| MessageStatus | 0 UNSPECIFIED, 1 FAIL, 2 SENDING, 3 SENT, 4 RECEIVED, 5 SEEN | same module |

Note `Attach.childNumber` is camelCase in the proto object (matters in section 3.4).

---

## 1. Numeric Sync2 msgType -> internal type -> web strings

Enum `Ai` (= Sync2 msgType values) SW @209941. Map `ji` (Ai -> web msgType string) SW @210840. `ji` is referenced exactly twice in the file (definition + `El`), so it is not shadowed. Web string -> PC `MSG_*` is `XITH` export `a` (LZ @9901156). Constants are in LZ module `1pet` (@1478420).

| Sync2 msgType | `Ai` name | web `msgType` string (`ji`) | PC `MSG_*` (value) | web `cliMsgType` (`CLI_MSG_TYPE_*`) | Result |
|---|---|---|---|---|---|
| 0 | Text | `webchat` | MSG_TEXT (1) | 1 | kept |
| 2 | Doodle | `chat.doodle` | MSG_DOODLE (5) | 37 | kept |
| 3 | Photo | `chat.photo` | MSG_PHOTO (2) | 32 | kept |
| 4 | Photo_V2 | `chat.photo` | MSG_PHOTO (2) | 32 | kept (real data seen) |
| 31 | PhotoV2 | `chat.photo` | MSG_PHOTO (2) | 32 | kept, but attach NOT copied (see 3.4) |
| 6 | Voice | `chat.voice` | MSG_VOICE (3) | 31 | kept |
| 10 | Sticker | `chat.sticker` | MSG_STICKER (4) | 36 | kept |
| 12 | Contact | `chat.recommended` | MSG_CONTACT (6) | 38 (user card); link variant = 40 | kept |
| 15 | OA | `chat.list.action` | MSG_OA (21) | none defined | kept |
| 18 | Location | `chat.location.new` | MSG_LOCATION (17) | 43 | kept |
| 19 | Video | `chat.video.msg` | MSG_VIDEO (18) | 44 | kept (real data seen) |
| 22 | File | `share.file` | MSG_FILE (19) | 46 | kept |
| 23 | Gift | `chat.gif` | MSG_GIF (7) | 49 | kept |
| 24 | Webcontent | `chat.webcontent` | MSG_ZINSTANT (52) | 52 (ZINSTANT_V1) | kept only if `attach.params` contains `"pcItem"`, else dropped |
| 53 | WebcontentV2 | `chat.webcontent.v2` | MSG_ZINSTANT_V2 (53) | 61 (ZINSTANT_V2) | kept |
| 25 | VideoLive | `chat.video.live.msg` | not in `XITH.a` -> MSG_UNKNOWN (-3) | none | dropped in `jl` |
| 33 | ChatDelete | `chat.delete` | not in `XITH.a` -> MSG_UNKNOWN | none | dropped in `jl` |
| 20 | ChatInfo | none | n/a | n/a | dropped in `El` (`if(!i)return null`) |
| 21 | ECard | none | n/a | n/a | dropped in `El` |
| 26 | Poll | none | n/a | n/a | dropped in `El` |
| 36 | Undo | none | n/a | n/a | dropped in `El` (see section 6) |
| other | - | none | n/a | n/a | dropped |

Status of the table columns:
- Columns 1-3: READ (SW @209941-211296; the drop is `let i=ji[t.msgType];if(!i)return null;` at SW @352976).
- Column 4: READ, `XITH.a` switch at LZ @9901156 (also knows `chat.undo` -> MSG_UNDO 20, `chat.delete.everyone` -> MSG_DEL_EVERYONE -27, `chat.ecard` 25, `group.poll` 26, `chat.todo` 2506, but `El` never emits those four except the delete-everyone branch, which is dead code).
- Column 5: constants READ in `1pet`. The pairing MSG_* <-> CLI_MSG_TYPE_* is READ for text/contact/doodle/photo/file/gif/sticker/voice/location/video in the pin-message helper `he` (LZ @9912067, switch on `t.msgType` setting `i.msg_type=l.CLI_MSG_TYPE_*`). The pairing for webcontent (52 / 61) and the link variant (40) is INFERRED from names. `El` itself never emits a numeric cliMsgType for the top-level message.
- Reverse direction (PC -> Sync2, used by the backup encoder `un`, SW @218088): map `xi` SW @210492 turns PC types into Sync2 types: 1->0, 2->3, 11->3, 3->6, 4->10, 13->10, 5->2, 6->12, 7->23 (gift/gif), 17->18, 18->19, 19->22, 20->36 (undo), 21->15, 25->21, 26->26, 52->24, 53->53, -1909->20, -27->33. The PC never writes 4, 31 or 25; those come from the phone. READ.
- Observed real values 0, 4, 19 = Text, Photo_V2, Video. Consistent. INFERRED (from the user's observation).

---

## 2. Output object built by `El.syncMessage2SVMessage` (READ, SW @352935-357600)

```js
{
  msgId:       String(msg.globalId),                       // "0"/<=0 -> synthetic id, see 7.3
  cliMsgId:    String(msg.clientId),
  msgType:     ji[msg.msgType],                            // web string, table above
  status:      uidFrom == "0" ? MSG_DELIVERED(2) : MSG_READ(4),   // msgStatus NOT used
  notify:      "1",
  mentions:    meta.mentionsList,                          // uids rewritten, section 4.2
  quote:       meta.quote,                                 // ownerId rewritten, section 4.1
  content:     <string | attach object | array | {...}>,   // section 3
  ts:          String(msg.timestamp),
  uidFrom:     sender == selfPcUid ? "0" : senderPcUid,
  uidTo:       gNoised2PCNoised(partitionGlobalId, isGroup),   // conversation PC id, ALWAYS the conversation
  dName:       "",
  propertyExt: meta.property,
  ttl:         meta.ttl,
  reference:   meta.reference  (only if present),
  src:         MSG_SRC.SYNC_MOBILE_DB (7),
  uidSenderDel: ""  or deleter uid (dead path, section 6)
}
```

Mapping to zca-js `TMessage`: `uidTo` = `idTo`; `uidFrom` "0" = self; `dName` empty (the converter never fills sender display name); `ts` is a decimal string.

---

## 3. `content` and `attachsList[0]` composition

Let `d = meta.attachsList[0]` (only the first attach is ever used, except OA). READ SW @353380-356500.

### 3.1 Rule per msgType

| Sync2 type | `content` in output | Notes |
|---|---|---|
| Text (0) | `msg.content` (plain string) | If `d.action == "rtf"`: `if(!d.title) d.title = msg.content; content = d` (object with `action:"rtf"`, `params`, `title`). READ. |
| Photo (3), Photo_V2 (4) | `d` (or `{}`) | Defaults applied, see 3.2. |
| Sticker (10) | `d` (or `{}`) unchanged | No defaults. |
| Gift (23) | `d` | Same defaults as photo. |
| Voice (6) | `d` | Defaults for childnumber/action/description/params/thumb/title/type. |
| Video (19) | `d` | Defaults for childnumber/action/description/type (NOT params/href/thumb/title). |
| Location (18) | `d` | Defaults for childnumber/action/href/thumb/type. |
| Doodle (2) | `d` | Defaults for childnumber/action/description/thumb/title. |
| Contact (12) | `d` | Defaults for description/thumb. If `d.action` is `recommened.user` or `recommened.vip` and `d.params` set: `d.params = gNoised2PCNoised(d.params)` (global id -> uid). |
| File (22) | `d` | Defaults for childnumber/action/description/thumb/type. |
| OA (15) | `meta.attachsList` (the whole array) | READ. |
| Webcontent (24) | `d` | Requires `d.params` containing `"pcItem"`, else null. Defaults to `""` for action/description/title/href/thumb/params; `delete c.childNumber`. |
| WebcontentV2 (53) | `{ zinstant_msg: d.zinstantMsg||"", zinstant_data: d.zinstantData||"", action: d.action||"" }` | snake_case keys. READ. |
| PhotoV2 (31), VideoLive (25) | `msg.content` (switch has no case) | The attach is ignored. READ (no `case` in the switch). |
| Undo, ECard, Poll | switch branches exist but unreachable | `ji` has no entry, so `El` returned earlier. |

Default helper: `normalizeParams(v, dflt="") = (v is Object with keys) ? dflt : (v || dflt)`. So `undefined`/`null`/`""` -> `""` (or `0` for childnumber). Fields that are not normalised stay `undefined` (omitted in JSON). READ SW @357700.

### 3.2 Attach field roles (types are strings in the proto)

Roles come from the decode code plus the exact reverse mapping in the encoder (`en` @214075, `tn` @214725, through ~215600), which is the most explicit documentation of what the phone expects.

| Attach field | Meaning | Source |
|---|---|---|
| `title` | photo: caption; file: file name; text/rtf: the text; location: place title; link: link title | READ (jl file branch; he(); rtf branch) |
| `description` | free text; for contact cards may carry JSON with `gUid` (fallback to resolve the uid, section 5.3) | READ (`Rn`, SW @221107) |
| `href` | photo: normal URL; video: video URL; file: download URL; doodle: image URL; location: map link | READ (`tn`: Photo href=normalUrl\|\|oriUrl; Video href=oriUrl\|\|normalUrl; Doodle href=oriUrl) |
| `thumb` | thumbnail URL | READ (`tn`: thumb=thumbUrl) |
| `params` | **JSON string** (not an object); `en` does `typeof params=="string" ? params : JSON.stringify(params)` | READ |
| `extInfo`, `remains` | opaque strings, forwarded only if present | READ (`en`) |
| `catId`, `id` | sticker category id / sticker id (int32) | READ (`en`: `catId`, `id` via Number) |
| `type` | string (sticker: numeric as string in the proto) | READ (`en`: `String(type)`) |
| `action` | e.g. `rtf`, `recommened.user`, `recommened.vip`, `recommened.link`, `recommened.stickerset`, `recommened.misscall`, `recommened.calling`, `recommened.receivecall`, `recommened.calltime` | READ (`jl._normalizeRecommendMsgSubType`) |
| `childNumber` | album child count | READ (see quirk 3.4) |
| `zinstantData`, `zinstantMsg` | zinstant payload strings | READ |

### 3.3 `params` JSON per type (what the PC reads / writes)

- **Photo/Gif**: JSON `{hd, rawUrl, href, convertible, rotation, flip, ...}`. The PC reads `hd` (HD url), `rawUrl`, `href`, `convertible`, writes `rotation`/`flip`. If `params` is a bare URL string starting with `http` it is NOT parsed. READ (`jl._normalizeMessageJSON`, `_parsePhotoParams`, SW @364977). Encoder adds `{"hd": hdUrl}` when absent. READ (`nn`). Width/height keys: INFERRED, not seen.
- **Video**: JSON with `duration` (read as `t.duration`). READ. Other keys INFERRED.
- **Location**: JSON `{latitude, longitude, placeId, isUserLocation, srcId}`. READ (`rn` / `jl` location branch).
- **File**: JSON with `fileExt` (lowercased; the PC appends `.<ext>` to `title` if missing). Other keys (size, checksum) INFERRED.
- **Voice**: JSON (duration etc.), contents not inspected. INFERRED.
- **Contact**: for `recommened.user`/`recommened.vip`, `params` is the contact's (global) user id; for `recommened.link`, `params` is JSON with `mediaTitle`, `redirect_url`, `streamUrl`, `artist`, `stream_icon`, `type`. READ (`he()` in LZ, `El`).
- **Text rtf**: `params` JSON carries the style spans. Exact keys not inspected. INFERRED.
- **Undo**: JSON `{custom_message: "<JSON string>"}`, see section 6.

### 3.4 Quirks that affect a faithful replica (all READ)

1. `c.childnumber` (all lowercase) is normalised, but the proto field is `childNumber` (camelCase). Result: output has `childnumber: 0` AND the original `childNumber` (if the phone sent it) untouched. For web-shape use `childNumber`.
2. PhotoV2 (31) and VideoLive (25) never copy the attach (no `case`), so content is the plain string.
3. `El` mutates the incoming `meta.mentionsList`, `meta.quote` and `attachsList[0]` in place.
4. Pre-pass bug: for Contact messages it does `c.set(t.senderId, {globalId: attach.params})`, overwriting the sender's own entry in the batch map. Harmless (single-id fallback lookups still happen), only a perf quirk.

---

## 4. quote, mentions, reference, property, ttl

### 4.1 Quote (reply) - READ SW @353380, @217369 (`ln`), LZ @5305676, SW `jl._normalizeQuoteMessageJSON` @367800

- Passed through as `quote: meta.quote` with the Sync2 field names: `{attach, ownerId, ts, fromD, cliMsgId, globalMsgId, msg, cliMsgType, ttl?, partialQuote?}` which are the same names as the web `quote`.
- Only `quote.ownerId` is rewritten: `gNoised2PCNoised(ownerId)`. It is NOT mapped to `"0"` for self (unlike `uidFrom`), so a quoted self message carries your real uid.
- `attach` is a **JSON string** (encoder: `typeof attach=="string"?attach:JSON.stringify(attach??"")`). Contents of `attach` are not touched by `El`. In the Sync2 wire format its `mentions[].uid` and (for undo) `params.custom_message.highLightsV2[].uid` are global ids (encoder `Nn` converts PC uids to global ids), so they stay global ids after import. INFERRED consequence: a replica must denoise them itself if it wants uids.
- `cliMsgType` is a web cliMsgType (1, 31, 32, 36, 37, 38, 43, 44, 46, 49, ...). PC compares it with `CLI_MSG_TYPE_PHOTO (32)` and maps `45 (CLI_MSG_TYPE_UNDO)` to PC-internal `MSG_UNDO (20)`; the encoder does the reverse via `dn(20)=45`. A quote of a recalled message therefore has `cliMsgType 45`. READ.
- `partialQuote` is a JSON string `{"pos":..,"len":..}`; the PC converts it to an object `{pos:Number,len:Number}` (LZ `HOQz`). `El` leaves it a string. READ.
- `globalMsgId` is a uint64 string; `cliMsgId` and `ts` are JS numbers. `ts` units: ms, INFERRED.

### 4.2 Mentions - READ SW @353281, constants LZ @1496733

- `mentionsList` passes through as `mentions: [{uid, pos, len, type}]` (identical to web).
- `uid` is `gNoised2PCNoised(uid)`; the literal `"-1"` is kept as-is (`MENTION_ALL_ID = "-1"`). `type`: `0 = MENTION_TYPE_USER`, `1 = MENTION_TYPE_ALL`. If a uid cannot be resolved, `El` yields `undefined` for it (no fallback), whereas the encoder writes `"-1"` for unresolved ids.
- `pos`/`len` are int32 offsets into the message text (`content`).

### 4.3 Reference - READ SW `jl._transformMessageReferenceFromServerMessage` (SW @368748), LZ `0Na3`, `Ubns`, `XITH.c`

- `meta.reference` `{type:int, data:string}` is copied to `reference` only when present. It is **not** the reply mechanism (that is `quote`) and **not** how undo is signalled.
- `jl` parses `data` (JSON string) and branches on `type`: `1` = story reference (keys like `ownerId, type, id, width, height, thumb, url, urlHls, delayTime, viewCount, createTime, storyId, ownerInfo, displayName, avatar, privacy, location, status, bg_color, ...`); `3` = forward log (keys `id, ts, logSrcType, fwLvl, cmi, gmi, srcId, tId, rootMsgRef{id,ts,logSrcType}`). Other types: `data` replaced by `{}`.
- So `reference` = forward/story metadata. Forwarded messages keep their normal msgType.

### 4.4 Property (text style / urgency) - READ

- `meta.property` `{size, color, type, subType, ext}` is passed through as `propertyExt` unchanged (encoder `an` keeps only finite numbers; `ext` is a string, JSON-stringified if object). Web shape is the same.
- `propertyExt.type == 3` (`MSG_BUBBLE_LAYOUT_TYPE_NO_BORDER`) is used for borderless sticker-like photos (LZ `he()`); `2 = SMALL`.
- Text styling lives in the `rtf` attach (section 3.1), not in `property`.
- Urgency: `jl` sets `urgency` from `e.specialMsgType`, which `El` never provides. So sync v2 does not carry urgency. READ (absence).

### 4.5 ttl - `ttl: msg.meta.ttl` (number or undefined). READ.

---

## 5. Identity: senderId, global ids, self

### 5.1 What "global noised id" is (READ)

- Sync2 `senderId`, `mentions[].uid`, `quote.ownerId`, contact `params` and the partition id are **global noised ids** (opaque per-user ids), not Zalo uids. The partition name is `"oneone/<globalId>"` or `"group/<globalId>"` (SW @234231, class `Vn.noise`).
- `gNoised2PCNoised(globalId, isGroup=false)` (SW @325277, class `Hc`): cached (`idCache`, key `${id}-${isGroup}`), delegates to `syncRendererTaskManager.getPcNoisedIds([{globalId,isGroup}])` (renderer handler `GetPcNoisedIdsHandler`, MS @2457790 -> `NoiseIdConverter.gNoised2PCNoised`, MS @817148). `gNoised2PCNoisedMulti` batches 100 and pre-fills the cache in both directions.
- Resolution: `isGroup` -> `groupGNoised2PCNoised` (group info cache keyed by the group's `globalId` field; no server fallback seen in this path); otherwise `oneToOneGNoised2PCNoised` -> friend store `_getUserIdsFromGlobalIds` (LZ @8964269): in-memory `_cacheGlobalIdAndUserId` (filled from profiles' `globalId` field) -> DB table `GlobalNoiseid` -> server.
- Server lookups (LZ @12139229; command-id table at LZ @2638575 lists 12053 = /api/gid/encrypt, 12054 = /api/gid/decrypt; module `wcaV` @15771689 = cache wrapper `GlobalNoiseId`):
  - uid -> globalId: `getGlobalNoiseId`, GET `<profileDomain>/api/gid/encrypt`, AES-encrypted `params` = `{noiseUids:[uids]}`; response `.data` = `{uid: globalId}`.
  - globalId -> uid: `getUidFromGlobalNoiseId`, GET `<profileDomain>/api/gid/decrypt`, `params` = `{globalUids: JSON.stringify([ids])}`; response `.data` = `{globalId: uid}`.
  - The wrapper pre-seeds `"-1" -> "-1"` (mention-all). Results are persisted via `addGlobalNoiseId`.
- Group ids on the PC side are prefixed with `g` (`convId.startsWith("g")` = group; todo code slices the first char for the real group id, LZ @9906272, module XOIG @9904800). So `uidTo` for groups is `"g<groupId>"`; strip the `g` for a web `idTo`/threadId. READ for the prefix convention; the exact `g<digits>` form is INFERRED.

### 5.2 Sender and "self" - READ SW @353035

```
n = gNoised2PCNoised(conversationGlobalId, isGroup)     // uidTo
a = gNoised2PCNoised(msg.senderId)                       // sender uid (PC form)
uidFrom = (a == this.userId) ? "0" : a                  // this.userId = session user id passed to new El(userId)
```

- There is **no explicit "self global id" on the decode side**. Self is detected only because the sender's global id resolves to the account's own uid (which is in the profile cache / `GlobalNoiseid` table / server).
- On the encode side the self global id is `pcNoised2gNoised(sessionUserId)` (`resolveMyGlobalNoisedId`, SW @228289), i.e. `getGlobalIdsFromUserIds` -> own profile `globalId` (cache, else `_doGetFullProfileByIds(..., FETCH_MINI_INFO)`). READ.
- Encode-side convention: sender `"0"`, `"-1"` or an id starting with `"-"` is treated as self/special and replaced with the self global id (`kn`, SW @221594). READ.
- INFERRED shortcut for a replica: in a `oneone/<peerGid>` partition, a `senderId != <peerGid>` is the self account. In `group/<gid>` partitions you need the self global id from `/api/gid/encrypt` (or any other source of the own `globalId`).

### 5.3 Contact card ids

For `recommened.user` / `recommened.vip` the attach `params` is a global id and is denoised. Encoder fallback (`Rn`): if the map misses, it tries `JSON.parse(attach.description).gUid`. READ. So `description` of a contact card may contain `{"gUid": ...}`.

---

## 6. Recall / delete (undo)

- **READ:** In this bundle version, sync v2 Undo (Sync2 msgType 36) and ChatDelete (33) messages are **not imported**: 36 has no `ji` entry (dropped in `El`), 33 maps to `chat.delete`, which `XITH.a` does not recognise (dropped in `jl`).
- **READ (dead code in `El`, SW @356538):** the intended handling for Undo is
  1. `content = ""`,
  2. `JSON.parse(attach.params).custom_message` -> `JSON.parse(...)` -> `highLightsV2[0].uid` = the global id of the user who deleted,
  3. `uidSenderDel = gNoised2PCNoised(uid)` (`"0"` if self), `msgType = "chat.delete.everyone"`.
  The identical logic is live in the older v1 "cross message" converter (SW @66200-68300) where the input type is `chat.undo`. So the wire shape of a recall in Sync2 is: msgType 36, `attachsList[0].params` = `'{"custom_message":"{\"highLightsV2\":[{\"uid\":\"<gid>\",...}],\"msg\":{...}}"}'`.
- The encoder emits Undo (Ai 36) for PC messages with msgType 20 and applies the same custom_message structure (`On`, SW @223291). A quote pointing at a recalled message gets `cliMsgType 45`. READ.
- INFERRED: a replica that wants recalls should implement the dead branch itself (web uses `chat.undo`/`delete` events rather than messages anyway).

---

## 7. msgStatus, timestamps, ids

1. **msgStatus**: not read at all on decode (only `setMsgStatus` in the encoder, SW @248815). Output `status` is `MSG_DELIVERED (2)` for self (`uidFrom "0"`), `MSG_READ (4)` otherwise. READ. Encoder PC->Sync2 map `Li` (SW @210760): -2->1 FAIL, 1->3 SENT, 2->4 RECEIVED, 3->5 SEEN, 4->5, 10->2 SENDING, -20/-21/-23/-24/-25->2, -22->1, unmapped->0. READ.
2. **timestamp**: `ts = String(msg.timestamp)`; no unit conversion, no clock adjustment. `jl` stores it as `sendDttm` and `serverTime = e.serverTs||e.ts`; `localDttm = Date.now()` (ms). So the unit is ms. READ (no scaling) / INFERRED (ms).
3. **globalId / synthetic ids**: if `MessageIdComparator.greaterThan(globalId, 0)` the id is used as-is. Otherwise the message is queued and, once the next message with a real id is seen, gets `generateMsgIdWithIndex(nextRealGlobalId, indexInQueue)`; for trailing pending messages the base is `queue[0].clientId`. Format is `<base>_<index>` (`MessageIdBuilder.build`: `${prefix}${id}_${index}`; `extractStructure` splits on `_`). READ for the algorithm (SW @357836-359900; `processPendingMessages` @359584) / INFERRED for the precise string.
4. **clientId**: `cliMsgId = String(clientId)`. uint64 read as JS number, so ids above 2^53 would lose precision (the encoder validates `Number.isSafeInteger` and logs "precision-unsafe uids"). READ.

---

## 8. Reference converter (INFERRED reconstruction of `El`, for zca-js shape)

```ts
const JI: Record<number,string> = {0:"webchat",2:"chat.doodle",3:"chat.photo",4:"chat.photo",31:"chat.photo",
  6:"chat.voice",10:"chat.sticker",12:"chat.recommended",15:"chat.list.action",18:"chat.location.new",
  19:"chat.video.msg",22:"share.file",23:"chat.gif",24:"chat.webcontent",53:"chat.webcontent.v2",
  25:"chat.video.live.msg",33:"chat.delete"};
function toWeb(m, ctx /* gid->uid map, selfUid, convId */) {
  const msgType = JI[m.msgType]; if (!msgType) return null;       // 20,21,26,36 dropped by the app
  const a = m.meta?.attachsList?.[0];
  let content: any = m.content;
  switch (m.msgType) {
    case 0: if (a?.action === "rtf") content = { ...a, title: a.title || m.content }; break;
    case 2: case 3: case 4: case 6: case 10: case 12: case 18: case 19: case 22: case 23:
      content = a ?? {}; /* + defaults ("" / childNumber 0) */ break;
    case 15: content = m.meta.attachsList; break;
    case 53: content = { zinstant_msg: a?.zinstantMsg ?? "", zinstant_data: a?.zinstantData ?? "", action: a?.action ?? "" }; break;
  }
  const from = ctx.uid(m.senderId);
  return {
    msgId: m.globalId, cliMsgId: String(m.clientId), msgType,
    uidFrom: from === ctx.selfUid ? "0" : from, idTo: ctx.convUid,
    ts: String(m.timestamp), content, ttl: m.meta?.ttl,
    mentions: m.meta?.mentionsList?.map(x => ({ ...x, uid: x.uid === "-1" ? "-1" : ctx.uid(x.uid) })),
    quote: m.meta?.quote && { ...m.meta.quote, ownerId: ctx.uid(m.meta.quote.ownerId) },
    propertyExt: m.meta?.property, reference: m.meta?.reference,
  };
}
```

---

## 9. Open points not resolved by static reading

1. Exact keys inside `params` for voice, rtf styles and file (besides `fileExt`): not inspected (INFERRED from zca-js).
2. Whether newer app versions add Undo/Poll/ECard to `ji` (this bundle: no).
3. Group global id -> `g<gid>` resolution relies on the local group cache (`dc.default.getGroupIdsFromGlobalIdsAsync`, not opened); no server fallback was seen.
4. Rows reach `kl` already decoded by the protobuf `toObject` shape (camelCase names); the transport (`executeMessageQuery`) was not traced. INFERRED from the user's stated field list matching `toObject`.
5. Sticker `type` is a string in the proto while web uses a number. INFERRED, convert with `Number()` if needed.
