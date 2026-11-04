# ChatSpace bridge protocol (v1)

Every platform runs in its own sidecar process, spawned by the macOS app:

| Platform  | Command                                                         |
|-----------|-----------------------------------------------------------------|
| zalo      | `bun bridges/node/src/main.ts --platform zalo --data-dir DIR`     |
| whatsapp  | `bun bridges/node/src/main.ts --platform whatsapp --data-dir DIR` |
| telegram  | `bun bridges/node/src/main.ts --platform telegram --data-dir DIR` |
| messenger | `bridges/messenger/chatspace-messenger --data-dir DIR`           |

(In the packaged `.app`, the Node bridge is a single binary produced by `bun build --compile`
and the commands become `chatspace-bridge --platform …`.)

Transport: **newline-delimited JSON over stdin/stdout**, UTF-8, one object per line.
stderr is free-form logs (the app writes it to `DIR/bridge.log`). A bridge must never print
anything that is not a protocol frame to stdout (route library loggers to stderr).

## Frames

App → bridge (request):
```json
{"id":"17","method":"messages.send","params":{"chatId":"…","text":"hi"}}
```
Bridge → app (response, exactly one per request):
```json
{"id":"17","result":{…}}
{"id":"17","error":{"code":"not_logged_in","message":"…"}}
```
Bridge → app (event, unsolicited):
```json
{"event":"message.new","data":{…}}
```

Error codes: `not_logged_in`, `not_found`, `invalid_params`, `unsupported`, `internal`.

## Data model

All timestamps are **milliseconds since epoch** (number). All IDs are strings.

```ts
type AuthState = "logged_out" | "connecting" | "awaiting_qr" | "awaiting_password"
               | "awaiting_code" | "awaiting_input" | "logged_in" | "error";

// Sent with state "awaiting_input". The UI renders `fields`, then calls
// auth.submit { inputs: { [field.id]: value } }.
interface AuthStep {
  stepId: string;
  kind: "input" | "wait";          // "wait": show instructions + spinner, bridge keeps polling
  instructions?: string;
  fields?: { id: string; name: string;
             type: "username" | "password" | "code" | "captcha" | "select" | "text";
             options?: string[]; default?: string }[];
  images?: string[];               // data: URLs (captcha image etc.)
  canCancel?: boolean;
  error?: string;                  // why the previous submission was rejected
}

interface SelfInfo { id: string; name: string; avatar?: string; phone?: string }

interface Chat {
  id: string;
  title: string;
  avatar?: string;        // http(s) URL or absolute local file path
  kind: "direct" | "group" | "channel" | "bot" | "self";
  unread: number;         // server-authoritative unread message count; must drop to 0
                          // (via chat.upsert) when the chat is read on another device
  markedUnread?: boolean; // manually "mark as unread" (shown as a dot/badge with unread 0)
  pinned?: boolean;
  muted?: boolean;        // notifications currently muted
  mutedUntil?: number;    // ms epoch; -1 = forever; absent = not muted
  timestamp: number;      // last activity
  lastMessage?: Message;
  memberCount?: number;
  online?: boolean;
  lastSeen?: number;
  isContact?: boolean;    // peer is in the contact list (folder rules)
  archived?: boolean;     // chat is archived
  encrypted?: boolean;    // end-to-end encrypted; history before this device joined
                          // may be unavailable, so an empty messages.list is normal
  encryptedAfter?: string; // id of the last message from before the chat switched to
                          // encryption; encrypted messages after it may be missing.
                          // Such a message is never sent as lastMessage.
}

// Telegram dialog filter. The app evaluates membership against its chat list so
// unread/mute changes re-sort folders without another round trip.
// A chat is in the folder if it is not excluded and (included or pinned or matches a flag).
interface Folder {
  id: string; title: string;
  emoticon?: string;      // Telegram folder icon emoticon ("🤖","📢","⭐","💬","👤","👥",…)
  includeChatIds: string[]; excludeChatIds: string[]; pinnedChatIds: string[];
  contacts?: boolean; nonContacts?: boolean; groups?: boolean; channels?: boolean; bots?: boolean;
  excludeMuted?: boolean; excludeRead?: boolean; excludeArchived?: boolean;
}

// Pushed by the app (Settings → Dữ liệu và bộ nhớ), at start and whenever it changes.
interface StorageConfig {
  keepMediaDays: number;            // delete cached media unused for N days; 0 = forever
  maxCacheMB: number;               // media cache cap, least-recently-used first; 0 = unlimited
  compressImages: boolean;          // re-encode downloaded photos (JPEG q≈0.6, ≤1600 px)
  maxStoredMessagesPerChat: number; // older messages are dropped locally and refetched; 0 = unlimited
}

type MessageKind = "text" | "image" | "video" | "audio" | "voice" | "file" | "sticker"
                 | "gif" | "location" | "contact" | "link" | "call" | "system" | "unsupported";

interface Media {
  mime?: string;
  url?: string;           // remote URL the app can fetch directly
  path?: string;          // local file (after media.download)
  thumbnail?: string;     // data: URL or path, optional
  width?: number; height?: number;
  duration?: number;      // seconds
  fileName?: string;
  size?: number;          // bytes
}

// A bot button (capability `bot_buttons`, see [Bot buttons](#bot-buttons)).
interface Button {
  text: string;
  kind: "callback" | "url" | "login" | "switch_inline" | "web_app" | "game" | "text" | "copy"
      | "user_profile" | "unsupported";
  url?: string;           // url / login / user_profile: what to open; web_app: the bot's page
  insert?: string;        // switch_inline: "@bot query" for the composer
  samePeer?: boolean;     // switch_inline: meant for this same chat
  copyText?: string;      // copy: what goes on the clipboard
}

// A bot's custom keyboard shown in place of the usual composer keyboard (ReplyKeyboardMarkup).
interface ReplyKeyboard {
  rows: Button[][];
  resize?: boolean; oneTime?: boolean; persistent?: boolean; placeholder?: string; selective?: boolean;
}

interface Message {
  id: string;
  chatId: string;
  senderId: string;
  senderName?: string;
  senderAvatar?: string;
  fromMe: boolean;
  timestamp: number;
  kind: MessageKind;
  text?: string;          // body or caption; for system messages the rendered sentence
  media?: Media;
  replyTo?: { id: string; text?: string; senderName?: string; kind?: MessageKind };
  forwarded?: boolean;
  edited?: boolean;
  deleted?: boolean;      // recalled / unsent
  reactions?: { emoji: string; count: number; fromMe: boolean }[];
  status?: "pending" | "sent" | "delivered" | "read" | "failed";  // only for fromMe
  buttons?: Button[][];           // inline keyboard under the bubble (bots)
  replyKeyboard?: ReplyKeyboard;  // this message installs a bot keyboard for the chat …
  hideKeyboard?: boolean;         // … or removes it (ReplyKeyboardHide)
  forceReply?: { placeholder?: string; oneTime?: boolean };  // the bot asks for a reply to this message
}
```

## Methods

| Method | Params | Result |
|---|---|---|
| `bridge.hello` | `{ config?: object }` | `{ platform, version, capabilities: string[] }` |
| `auth.status` | – | `{ state: AuthState, self?: SelfInfo, step?: AuthStep /* current screen while awaiting_input */ }` |
| `auth.login` | `{ method?: "qr" \| "cookies" \| "phone" \| "password", cookies?: string, phone?: string, username?: string, password?: string }` | `{ state }` — progress is reported via events |
| `auth.submit` | `{ password?: string, code?: string, inputs?: Record<string,string> }` | `{ state }` (Telegram 2FA / login code; `inputs` answers the current `auth.step`) |
| `auth.cancelStep` | – | `{ state }` — go back from the current `auth.step` (only if `canCancel`) |
| `auth.logout` | – | `{}` |
| `chats.list` | `{ limit?: number }` | `{ chats: Chat[] }` (sorted: pinned first, then timestamp desc) |
| `chats.avatar` | `{ chatId }` | `{ avatar?: string }` (lazy avatar resolution) |
| `chats.members` | `{ chatId }` | `{ members: { id: string, name: string, avatar?: string }[] }` (capability `mentions`, see [Mentions](#mentions)) |
| `messages.list` | `{ chatId, limit?: number, before?: string /* messageId */ }` | `{ messages: Message[] }` (ascending by time) |
| `messages.search` | `{ query?: string, chatId?: string, kinds?: string[], before?: number /* ms */, limit?: number }` | `{ messages: Message[] }` (newest first; capability `search`, see [Search](#search)) |
| `messages.send` | `{ chatId, text?, replyTo?: string, filePath?: string, mentions?: { userId: string, offset: number, length: number }[] }` | `{ message: Message }` |
| `messages.forward` | `{ fromChatId, messageId, toChatId }` | `{ message: Message }` (capability `forward`, see [Forwarding](#forwarding)) |
| `messages.markRead` | `{ chatId }` | `{}` |
| `chats.mute` | `{ chatId, until: number /* ms epoch; -1 = forever; 0 = unmute */ }` | `{ chat: Chat }` (capability `mute`, see [Muting](#muting)) |
| `chats.pins` | `{ chatId }` | `{ messages: Message[] }` (most recently pinned first; capability `pins`, see [Pinned messages](#pinned-messages)) |
| `messages.react` | `{ chatId, messageId, emoji }` (empty emoji = remove) | `{}` |
| `messages.delete` | `{ chatId, messageId, forEveryone?: boolean }` | `{}` |
| `messages.edit` | `{ chatId, messageId, text, mentions? }` | `{ message: Message }` (capability `edit`, see [Editing](#editing)) |
| `media.download` | `{ chatId, messageId }` | `{ path: string }` |
| `typing.send` | `{ chatId, typing: boolean }` | `{}` |
| `messages.clickButton` | `{ chatId, messageId, row: number, column: number }` | `{ message?: string, alert?: boolean, url?: string }` (capability `bot_buttons`, see [Bot buttons](#bot-buttons)) |
| `connection.resume` | – | `{}` — the computer woke up or changed networks (see [Keeping up after a disconnect](#keeping-up-after-a-disconnect)) |
| `chats.folders` | – | `{ folders: Folder[] }` (`[]` if the platform has none; capability `folders`) |
| `storage.configure` | `StorageConfig` | `{}` |
| `storage.usage` | – | `{ mediaBytes, mediaFiles, databaseBytes }` |
| `storage.clear` | `{ olderThanDays?: number }` | `{ freedBytes }` |
| `history.status` | – | `{ available: boolean, restored: boolean }` (capability `history_pin`, see [Encrypted history](#encrypted-history)) |
| `history.restore` | `{ pin: string /* 6 digits */ }` | `{ restored: true }`, `{ restored: false, wrongPin: true, attemptsRemaining?: number }` or `{ restored: false, noBackup: true }` |
| `history.loadOpen` | `{ chatId }` | `{ added: number, done: boolean }` (capability `open_history`, see [Encrypted history](#encrypted-history)) |
| `history.phoneSync` | `{ action: "start" \| "cancel" \| "status" }` (default `"status"`) | `PhoneSyncProgress` (capability `phone_sync`, see [Zalo phone sync](#zalo-phone-sync)) |

### Editing

`messages.edit` replaces the text (or caption) of one of our own messages and returns it with
`edited: true`; the bridge also emits `message.update`. `mentions` works as in `messages.send`.
Platform rules, enforced by the server and mirrored by the app before offering "Edit":

- **Telegram**: text and media captions, 48 h (no limit in Saved Messages and channels).
- **WhatsApp**: text messages, 15 min. Edits received from other devices arrive without quote or
  media context; bridges lay the new text over the stored original.
- **Messenger**: text messages, 15 min, at most 5 edits; E2EE chats send a ConsumerApplication
  `EditMessage`. Meta may answer the edit task with the old text first; bridges wait briefly for
  the corrected value before reporting a failure.
- **Zalo**: no editing (no capability).

Rejections (window expired, not our message) are `invalid_params` with a user-readable message.

Media is **cloud-first**: when the server exposes a directly fetchable URL (Zalo CDN, Telegram
never), bridges put it in `media.url` and do not download. Files are only written under
`DIR/media` by `media.download`, which must honour `StorageConfig`.

QR login: while in `awaiting_qr` a bridge keeps emitting a fresh `auth.qr` (with `expiresAt`)
whenever the previous code expires. If the platform gives up (too many unscanned codes) the bridge
emits `auth.state {state:"logged_out", error:"qr_expired"}`; the app then calls `auth.login`
again automatically while the QR screen is visible.

`capabilities` tells the UI which actions to show, e.g.
`["reactions","reply","delete","edit","typing","read_receipts","media_send","voice","mentions","search","bot_buttons"]`.

### Bot buttons

Capability `bot_buttons` (Telegram) means messages carry their bot keyboards and the bridge implements
`messages.clickButton`. An **inline keyboard** comes as `Message.buttons` (rows, drawn under the bubble); a **custom
keyboard** as `Message.replyKeyboard` (drawn under the composer; the chat's current keyboard is the newest incoming
message that set one, until a message with `hideKeyboard` removes it — the app keeps track). `forceReply` asks the
app to put the composer into reply mode for that message. The app acts on a button by its `kind`:

- `text` — send the label as a plain message (custom keyboards; a `oneTime` keyboard is folded away after that).
- `url`, `login`, `user_profile` — open `url` (login buttons open the plain link; the Telegram login handshake is
  not performed).
- `switch_inline` — put `insert` (`@bot query`) into the composer; absent when the bot's username is unknown.
- `copy` — put `copyText` on the clipboard.
- `callback`, `game`, `web_app` — call `messages.clickButton` with the button's row/column; the bridge reads the
  message again (bots edit their keyboards constantly), presses that button (`messages.getBotCallbackAnswer`, or
  `messages.requestWebView` for web apps) and returns the bot's answer: `message` to show as a toast, or as an
  alert when `alert` is set, and/or `url` to open (games, web apps). A button that is gone answers `not_found`;
  a callback that requires the account's 2FA password answers `unsupported`.
- `unsupported` — payments, sharing a phone number / location / poll / chat with the bot: shown dimmed, no action.

The bot's reaction (an edited keyboard, a new message) arrives as the usual `message.update` / `message.new`.

### Keeping up after a disconnect

The platforms push messages over long-lived connections and keep little or nothing for a client that was away. What
the official apps do when the computer wakes up or changes networks, bridges do too:

- The app calls `connection.resume` on every bridge after a wake-from-sleep and after a network change (at most once
  every few seconds). A bridge checks that its socket still answers (a ping with a short timeout) and reconnects at
  once instead of waiting for the library's own keep-alive to fail (GramJS pings every 9 s, Baileys every 30 s plus
  5 s grace, both followed by the bridge's reconnect backoff); a reconnect already waiting out its backoff is started
  immediately. Bridges without the method answer `unsupported`, which the app ignores.
- **Telegram** keeps the position in the update stream (`updates.getState`: pts/qts/date, plus one pts per channel)
  in step with every update and in `DIR/updates-state.json`. On connect, after every reconnect, on `updatesTooLong`,
  when an update's pts reveals a gap and on `connection.resume`, it calls `updates.getDifference`
  (`updates.getChannelDifference` for a channel) and delivers what the server queued as `message.new` — `live` when
  the message is fresh, so a message that arrived while the laptop was asleep still notifies, as in Telegram's own
  apps — followed by `chat.upsert` with the exact unread counts (`messages.getPeerDialogs`). Past 500 pending
  updates (`differenceTooLong`) it re-reads the chat list instead and announces every chat. Without this, GramJS
  never asks for missed updates (its `catchUp` is a stub) and a message that arrived during a disconnect showed up
  only when its chat was opened.
- **WhatsApp** and **Zalo** servers replay what was missed on reconnect (offline messages, `requestOldMessages`);
  only the reconnect itself had to be made fast. **Messenger** answers `{}`: messagix reconnects on its own and Meta
  replays the missed tables.

### Muting

Capability `mute` means the bridge implements `chats.mute`, which turns a chat's notifications off on the
platform itself (so the phone and other devices follow) until `until`, or forever (`-1`), or back on (`0`).
The result is the chat with its new `muted`/`mutedUntil`; the bridge also emits `chat.upsert`. A mute
changed on another device arrives as `chat.upsert` like any other chat change. Each platform keeps its own
granularity: the app offers the durations of the official app, and bridges pass them through as given.

- **Telegram**: `account.updateNotifySettings` (`muteUntil` in seconds; forever = 2^31−1).
- **WhatsApp**: app-state `mute` (`muteEndTimestamp` = the end as an absolute ms epoch; forever = −1; unmute
  sends no timestamp).
- **Zalo**: `setmute` with the remaining duration in seconds, or forever.
- **Messenger**: `MuteThreadTask` (`mute_expire_time_ms`; −1 forever, 0 unmute). Sent only when the user
  asks; it is a single task with no retry. An end-to-end encrypted chat whose Facebook thread is not known
  yet answers `unsupported`.

### Pinned messages

Capability `pins` means the bridge implements `chats.pins` and emits `pins.update`. `chats.pins` returns the
messages pinned in a chat, most recently pinned first, as full `Message` objects with the id of the original
message (so the app can scroll to it). When the original is not stored locally the bridge fills what the
platform gives with the pin (sender, text or kind); `text` may then be a preview. An unpinned chat returns `[]`.

- **Telegram**: `messages.search` with `inputMessagesFilterPinned` (up to 100). Telegram does not say when a
  message was pinned, so the order is newest message first, as in its own pinned bar. `pins.update` on
  `updatePinnedMessages` / `updatePinnedChannelMessages`, on the "pinned a message" service message, and when a
  pinned message is edited or deleted.
- **WhatsApp**: there is no server query: the bridge records `pinInChatMessage` (pin/unpin, with expiry) from
  live, offline and history-sync messages, at most 3 per chat. `pins.update` on every change and when a pin
  lapses. Pins made before the bridge first synced the chat may be missing.
- **Zalo**: the group board (`board/list`, first 50 items, pinned-message ones) and the 1:1 friend board, newest
  pin first. A pin carries the message ids, sender and a title or thumbnail but not the send time, so a message
  that is not stored comes back with the pin time as `timestamp`. `pins.update` on the group's pin/unpin/reorder
  topic events (and board changes to a message topic) and on the 1:1 pin/unpin events.
- **Messenger**: `LSSetPinnedMessage` / `LSClearPinnedMessages` as they arrive with thread sync, kept per
  thread in the bridge's store; the bridge never asks Meta for pins, so pins made before the bridge first
  synced the chat may be missing. A pin without a timestamp counts as an unpin. A pin whose message is no
  longer stored comes back as a placeholder (`kind: "unsupported"`, no sender, `timestamp` = pin time).

`pins.update { chatId }` tells the app the pins of a chat changed (on any device); the app calls `chats.pins`
again if it shows that chat.

### Mentions

Capability `mentions` means the bridge implements `chats.members` and the `mentions` param of
`messages.send`. Bridges without it answer `chats.members` with `unsupported` and ignore `mentions`.

**`chats.members { chatId }`** returns the people who can be mentioned in that chat:

```json
{"members":[{"id":"1234567890","name":"Nguyễn Văn A","avatar":"https://…"}]}
```

- Group chat: its members. 1:1 chat: the other participant. The logged-in user is normally left out;
  clients must not rely on either.
- `id` is the platform user id **exactly as the bridge wants it back** in `mentions[].userId`:
  Zalo uid, Telegram user id (positive integer), WhatsApp participant JID as Baileys reports it
  (`…@s.whatsapp.net` or `…@lid`), Messenger Facebook user id.
- `name` is never empty (falls back to a phone number or the id). `avatar` is a URL or an absolute
  local path, absent when not known yet; resolve it lazily with `chats.avatar` if the platform allows.
- Bridges cache the answer per chat for a few minutes, so calling it on every "@" keystroke is cheap.
  Very large Telegram supergroups return only the first 200 participants; Telegram broadcast channels
  return `[]`.

**`messages.send { …, text, mentions }`** — `mentions` is optional:

```json
{"chatId":"g:42","text":"hi @Ann Lee, lunch?","mentions":[{"userId":"1234567890","offset":3,"length":8}]}
```

- `offset` and `length` are in **UTF-16 code units** (JavaScript / Swift `utf16` view) over the `text`
  param, and each span covers the visible `@Name` **including the `@`**.
- Spans must lie inside `text`, must not overlap and must not split a surrogate pair. Invalid spans are
  dropped silently: the message is still sent, just without that highlight.
- `userId` is an `id` returned by `chats.members` for the same chat.
- Zalo honours mentions in groups only (1:1 chats ignore them). The returned `Message.text` is the
  text as the user typed it, except on WhatsApp where each span is rewritten to `@<number>` (the form
  WhatsApp renders from `mentionedJid`) and the message echoes the text that was actually sent.
- Telegram sends a message that carries mentions as plain text plus the mention entities (no Markdown
  parsing, unlike messages without mentions, because explicit entities replace the parser).
- Messenger end-to-end encrypted chats carry the mention as an `@<fbid>@msgr` token plus `MentionedJID`
  (as mautrix-meta does); the stored/returned message keeps the typed `@Name`.

### Search

Capability `search` means the bridge implements `messages.search`, which finds messages by text, caption
or file name across all chats (or one chat with `chatId`).

- Every word of `query` must appear, in any order; matching ignores case, Vietnamese accents and đ/d
  (`hop dong` finds `Hợp đồng`). An empty `query` with `kinds` lists messages of those kinds.
- `kinds` filters by `Message.kind`; `link` also matches text messages that contain a URL. Without
  `kinds`, deleted, system and call messages are left out.
- `limit` defaults to 50 (max 200). The next page passes `before` = the oldest returned `timestamp`.
- Zalo, WhatsApp and Messenger search the bridge's local store only (what was synced to this
  computer); Messenger never contacts Meta for a search. Telegram asks the server
  (`messages.search` / `messages.searchGlobal`) and merges matches from recently received messages.
- Text inside images is not searched by bridges; the app keeps its own OCR index for that.

### Forwarding

Capability `forward` (WhatsApp, Telegram) means the bridge implements `messages.forward`: the platform's own
forward of one message to another chat of the **same account**, with the platform's "Forwarded" marking and
without downloading or re-uploading media. WhatsApp leaves the marking off when you forward your own message,
like the phone app. The result is the new message in `toChatId`.

Bridges without it answer `unsupported`. The app then forwards on its own, which is also how every forward to
another platform works: it sends the text with `messages.send`, or gets the media file (`media.url`, or
`media.download` on the source bridge), copies it under the original file name (photos re-encoded to JPEG/PNG
when they are HEIC/WebP/…) and sends it with `messages.send { filePath, text: caption }`.

### Encrypted history

Messenger only (capability `history_pin`). Messages of end-to-end encrypted chats sent before the
bridge's E2EE device was registered are not delivered to it; they live in the account's secure
storage backup, unlocked by the 6-digit PIN the user set in Messenger. `history.status` answers from
local state only (no request to Meta): `available` = there are encrypted chats, `restored` = the
bridge already holds access to the backup. `history.restore` contacts Meta only when the bridge has no
history device yet (otherwise it answers `restored: true` at once); a wrong PIN or a missing backup
(`noBackup`) is a normal result, not an error, and Meta allows only a few attempts
(`attemptsRemaining`). The PIN is never stored; the bridge keeps only the history device it enrols
(`DIR/e2ee-history.json`, removed on logout or when Meta reports the backup gone, after which
`history.status` asks for the PIN again).

With the backup, `messages.list` on an encrypted chat fetches backup pages (30 messages each, at most
6 per call) until the requested page no longer reaches past the oldest E2EE message, the way Messenger
restores a thread when it is opened or scrolled. Pre-encryption messages (`history.loadOpen`) are
older than the backup's range and do not count. Nothing is fetched in the background: no bulk restore
after the PIN and none on connect, after Meta locked an account right after a restore that loaded 80
chats at once.

Without the backup, `messages.list` answers encrypted chats from local storage only. Messages sent in a
1:1 chat before it switched to end-to-end encryption are still on Facebook, in the old unencrypted thread
keyed by the other person's user id (capability `open_history`). `history.loadOpen` loads the page of that
thread before the oldest one already loaded, filed under the encrypted chat (`added` messages; then
re-read them with `messages.list`). `done` = nothing older. It contacts Meta with one request and no retry,
so the app calls it only on an explicit user action, never automatically or for many chats at once.

### Zalo phone sync

Zalo only (capability `phone_sync`, advertised when Zalo PC is installed at `/Applications/Zalo.app`,
because the bridge loads Zalo PC's own decryption library from it, read only). Zalo keeps chat
history on the phone and does not let a new computer download old messages from the server. Zalo PC
gets it by asking the phone to transfer it ("Đồng bộ tin nhắn"); the bridge does the same over the
logged-in connection: it asks the phone, which shows a confirmation prompt, then downloads and
decrypts the conversation list and every conversation's messages and stores them like other
messages (no `message.new`; the app re-reads the chats when the sync is done).

`history.phoneSync {action:"start"}` returns at once with the current `PhoneSyncProgress`; progress
then arrives as `history.phoneSync` events. `"cancel"` stops a running sync, `"status"` only
reports. A second `"start"` while one runs is a no-op.

```
PhoneSyncProgress {
  state: "idle" | "preparing" | "waiting" | "paused" | "chats" | "messages" | "done" | "failed" | "cancelled",
  chats?: number,       // conversations listed by the phone
  chatsDone?: number,   // conversations whose messages are in
  messages?: number,    // messages added
  skipped?: number,     // conversations Zalo would not identify (dropped)
  code?: "rejected" | "busy" | "timeout" | "disconnected" | "server" | "decode" | "unavailable",
  error?: string,
  finishedAt?: number   // ms, on "done"
}
```

`waiting` means the phone is showing the prompt: the user must open Zalo on the phone and accept.
The phone remembers the bridge's key for the rest of the bridge process, so later requests of the
same run (one per 30 conversations) are accepted without a new prompt. `paused` means Zalo went to
the background on the phone, which stops sending until it is opened again (the bridge waits up to
15 minutes). Like Zalo PC, the bridge wakes the phone up when it goes quiet mid-transfer and retries a
failed request twice. Messages are stored batch by batch as they arrive (`messages` grows during a
request) and finished conversations are remembered, so `"start"` after a failure or cancel resumes
where the previous run stopped; `chatsDone` then starts above 0.

Telegram reactions: the bridge reports the red heart as `❤️` (U+2764 U+FE0F) and generally appends
U+FE0F to emoji that are a single text-presentation scalar (❤ ☺ ☹ ✌ ☝ ✍ ❣ …); `messages.react` accepts
either form and strips U+FE0F before talking to Telegram, whose reaction set uses the bare form.

## Events

| Event | Data |
|---|---|
| `auth.state` | `{ state: AuthState, self?: SelfInfo, error?: string }` |
| `auth.qr` | `{ qr: string /* raw payload to encode */, image?: string /* data:image/png;base64 */, expiresAt?: number }` |
| `auth.step` | `AuthStep` — next screen of a multi-step native login (Messenger email/password) |
| `connection` | `{ state: "connecting" \| "online" \| "offline", detail?: string }` |
| `chat.upsert` | `{ chat: Chat }` |
| `folders.update` | `{ folders: Folder[] }` — full list, after login and whenever folders change |
| `message.new` | `{ message: Message, live?: boolean }` — `live: true` only for messages that just arrived in real time (never for history sync, backfill or replays); the app notifies only for live, incoming, unmuted messages. Bridges must also emit `chat.upsert` with the new `unread` right after. |
| `message.update` | `{ message: Message }` (full replacement; edits, reactions, status, recall) |
| `message.delete` | `{ chatId, messageId }` |
| `typing` | `{ chatId, userId, name?, typing: boolean }` |
| `presence` | `{ chatId, online: boolean, lastSeen?: number }` |
| `history.phoneSync` | `PhoneSyncProgress` — Zalo phone sync progress (see [Zalo phone sync](#zalo-phone-sync)) |
| `pins.update` | `{ chatId }` — the chat's pinned messages changed (see [Pinned messages](#pinned-messages)) |
| `log` | `{ level: "info" \| "warn" \| "error", message }` |

## Session storage

Each bridge persists credentials/session under `--data-dir` (the app passes
`~/Library/Application Support/ChatSpace/<platform>/`). On start a bridge must try to
restore the saved session automatically and emit `auth.state` accordingly.
