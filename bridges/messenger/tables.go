package main

// LightSpeed table ingestion. Every table the Messenger web client would see
// (the initial inbox page, thread pagination, realtime publishes, task responses)
// goes through ingestTable, which updates the store and emits protocol events:
// chat.upsert, message.new, message.update, typing.
//
// Mirrors pkg/connector/handlemeta.go (parseTable) of mautrix-meta.

import (
	"context"
	"fmt"
	"net/url"
	"slices"
	"sort"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

type ingestMode int

const (
	// modeInitial is for the initial inbox page, thread pagination and task
	// responses: store everything, only emit chat.upsert for what changed.
	modeInitial ingestMode = iota
	// modeLive is for tables pushed over the realtime socket.
	modeLive
)

type batch struct {
	b        *bridge
	mode     ingestMode
	selfID   int64
	selfName string
	dirty    map[string]bool
	order    []string
	created  map[string]bool
	msgEv    []eventFrame
	ranges   map[string]bool
	active   map[int64]bool
	// live is true only for tables pushed over an established realtime socket. The
	// initial sync of a (re)connect arrives in modeLive too, but before the socket
	// is flagged up, so what it contains is never "live".
	live bool
	// serverUnread holds chats whose unread count was set from the server's own
	// count in this batch; local increments would double count them.
	serverUnread map[string]bool
}

// liveMaxAge bounds how old a message may be and still count as "just arrived".
const liveMaxAge = 15 * time.Minute

// isFresh reports whether a message timestamp (ms epoch) is recent enough for the
// message to have just been delivered. It is a second line of defence against
// replays that slip past the socket-state tracking.
func isFresh(tsMs int64) bool {
	return tsMs <= 0 || time.Since(time.UnixMilli(tsMs)) < liveMaxAge
}

func (b *bridge) selfInfo() (int64, string) {
	b.mu.Lock()
	defer b.mu.Unlock()
	name := ""
	if b.self != nil {
		name = b.self.Name
	}
	return b.selfID, name
}

func (b *bridge) ingestTable(tbl *table.LSTable, mode ingestMode) {
	if tbl == nil {
		return
	}
	b.tblMu.Lock()
	defer b.tblMu.Unlock()
	id, name := b.selfInfo()
	x := &batch{
		b: b, mode: mode, selfID: id, selfName: name,
		dirty: map[string]bool{}, created: map[string]bool{},
		ranges: map[string]bool{}, active: map[int64]bool{},
		live:         mode == modeLive && b.socketUp.Load(),
		serverUnread: map[string]bool{},
	}
	x.contacts(tbl)
	x.mappings(tbl)
	x.deletions(tbl)
	x.threads(tbl)
	x.participants(tbl)
	x.messages(tbl)
	x.pins(tbl)
	x.updates(tbl)
	x.flush()
}

func chatKey(key int64) string { return strconv.FormatInt(key, 10) }

func (x *batch) markDirty(chat string) {
	if !x.dirty[chat] {
		x.dirty[chat] = true
		x.order = append(x.order, chat)
	}
}

func (x *batch) touchThread(chat string, fn func(t *threadRec)) threadRec {
	before, _ := x.b.st.thread(chat)
	after, created := x.b.st.updateThread(chat, fn)
	if created {
		x.created[chat] = true
	}
	if created || after != before {
		x.markDirty(chat)
	}
	return after
}

// Contacts -------------------------------------------------------------------

func (x *batch) contact(id int64, name, avatar string) {
	sid := chatKey(id)
	if x.b.st.setContact(sid, name, avatar) {
		if _, ok := x.b.st.thread(sid); ok {
			x.markDirty(sid)
		}
	}
}

func (x *batch) contacts(tbl *table.LSTable) {
	for _, c := range tbl.LSDeleteThenInsertContact {
		avatar := c.ProfilePictureUrl
		if avatar == "" {
			avatar = c.ProfilePictureLargeUrl
		}
		x.contact(c.Id, c.Name, avatar)
	}
	for _, c := range tbl.LSVerifyContactRowExists {
		x.contact(c.ContactId, c.Name, c.ProfilePictureUrl)
	}
}

// Participants ---------------------------------------------------------------

// participants keeps the member list of group threads (chats.members, @mentions).
// Same tables mautrix-meta feeds its chat member list from.
func (x *batch) participants(tbl *table.LSTable) {
	adds := map[int64][]string{}
	for _, a := range tbl.LSAddParticipantIdToGroupThread {
		adds[a.ThreadKey] = append(adds[a.ThreadKey], chatKey(a.ContactId))
	}
	// A delete-then-insert thread row that comes with all of its member rows is a full
	// resync (mautrix-meta: IsFull when the row count equals MemberCount): forget the
	// old list first so people who left in the meantime disappear.
	for _, th := range tbl.LSDeleteThenInsertThread {
		if ids := adds[th.ThreadKey]; len(ids) > 0 && (th.MemberCount <= 0 || int64(len(ids)) >= int64(th.MemberCount)) {
			x.b.st.resetMembers(chatKey(th.ThreadKey))
		}
	}
	for key, ids := range adds {
		x.b.st.addMembers(chatKey(key), ids...)
	}
	for _, r := range tbl.LSRemoveParticipantFromThread {
		x.b.st.removeMember(chatKey(r.ThreadKey), chatKey(r.ParticipantId))
	}
}

// Hybrid (E2EE) thread mapping ----------------------------------------------

func (x *batch) mapHybrid(key, jid int64, group, groupKnown bool) {
	chat := chatKey(key)
	jidUser := chatKey(jid)
	if x.b.st.mapJID(jidUser, chat) {
		x.markDirty(chat)
	}
	x.touchThread(chat, func(t *threadRec) {
		t.Key = key
		t.E2EE = true
		if groupKnown {
			t.IsGroup = group
		}
		if t.IsGroup {
			t.JID = jidUser + "@g.us"
		} else {
			t.JID = jidUser + "@msgr"
		}
	})
}

// openChat is the encrypted 1:1 chat that replaced the pre-encryption ("open")
// thread with this key; Meta keys that thread by the peer's user id. Its rows are
// filed under the encrypted chat instead of showing up as a second chat.
func (x *batch) openChat(key int64) (string, bool) {
	id := chatKey(key)
	chat := x.b.st.chatForJID(id)
	if chat == id {
		return "", false
	}
	t, ok := x.b.st.thread(chat)
	if !ok || !t.E2EE || t.IsGroup {
		return "", false
	}
	return chat, true
}

func (x *batch) mappings(tbl *table.LSTable) {
	for _, m := range tbl.LSVerifyHybridThreadExists {
		x.mapHybrid(m.ThreadKey, m.ThreadJID, m.IsGroupThread, true)
	}
	for _, m := range tbl.LSUpdateThreadAuthorityAndMappingWithOTIDFromJID {
		x.mapHybrid(m.ThreadKey, m.ThreadJID, false, false)
	}
}

// Deletions ------------------------------------------------------------------

func (x *batch) deletions(tbl *table.LSTable) {
	for _, t := range tbl.LSVerifyThreadExists {
		x.active[t.ThreadKey] = true
	}
	for _, t := range tbl.LSUpdateOrInsertThread {
		x.active[t.ThreadKey] = true
	}
	for _, t := range tbl.LSDeleteThenInsertThread {
		x.active[t.ThreadKey] = true
	}
	for _, r := range tbl.LSInsertNewMessageRange {
		x.active[r.ThreadKey] = true
	}
	del := func(key int64) {
		if x.active[key] {
			return // delete-then-insert combination
		}
		chat := chatKey(key)
		if _, ok := x.b.st.thread(chat); ok {
			x.b.st.removeThread(chat)
			delete(x.dirty, chat)
		}
	}
	for _, d := range tbl.LSDeleteThread {
		del(d.ThreadKey)
	}
	for _, d := range tbl.LSDeletePartialThread {
		del(d.ThreadKey)
	}
	for _, d := range tbl.LSDeleteMessageRequest {
		del(d.ThreadKey)
	}
	for _, d := range tbl.LSRemoveParticipantFromThread {
		if d.ParticipantId == x.selfID {
			del(d.ThreadKey)
		}
	}
}

// Threads --------------------------------------------------------------------

func skipThreadType(tt table.ThreadType) bool {
	switch tt {
	case table.FOLDER, table.COMMUNITY_FOLDER, table.COMMUNITY_CHANNEL_CATEGORY:
		return true
	}
	return false
}

func applyThreadType(t *threadRec, key int64, tt table.ThreadType) {
	if tt != table.UNKNOWN_THREAD_TYPE {
		t.Type = int64(tt)
	}
	t.Key = key
	if tt.IsWhatsApp() {
		t.E2EE = true
		t.IsGroup = tt == table.ENCRYPTED_OVER_WA_GROUP
		if t.JID == "" {
			if t.IsGroup {
				t.JID = chatKey(key) + "@g.us"
			} else {
				t.JID = chatKey(key) + "@msgr"
			}
		}
	}
}

// settledUnread works out the unread count after my read watermark moved to
// lastRead (it never moves backwards). t is the thread as it was before.
//
//   - the watermark reaches the last activity: nothing is unread;
//   - otherwise the stored messages decide what was read: when no stored incoming
//     message is newer than the watermark nothing is unread, else the count drops
//     by the stored messages that just became read (the local list can be shorter
//     than the server's count, so it is not used as the new total).
func (b *bridge) settledUnread(chat string, t threadRec, lastRead int64) int {
	lastRead = max(lastRead, t.LastRead)
	if t.Unread == 0 || lastRead >= t.LastActivity {
		return 0
	}
	if b.st.newestTS(chat) == 0 {
		return t.Unread // no local messages to judge by
	}
	now := b.st.unreadIncoming(chat, lastRead)
	if now == 0 {
		return 0
	}
	was := b.st.unreadIncoming(chat, t.LastRead)
	return min(t.Unread, max(now, t.Unread-(was-now)))
}

// readTo moves my read watermark forward (the chat was read here or on another
// device) and settles the unread count; a changed chat is re-sent by flush.
func (x *batch) readTo(chat string, ts int64) {
	if ts <= 0 {
		return
	}
	cur, ok := x.b.st.thread(chat)
	if !ok {
		return
	}
	unread := x.b.settledUnread(chat, cur, ts)
	x.touchThread(chat, func(t *threadRec) {
		t.LastRead = max(t.LastRead, ts)
		t.Unread = unread
	})
}

func (x *batch) threads(tbl *table.LSTable) {
	skip := func(key int64, tt table.ThreadType) bool {
		_, open := x.openChat(key)
		return open || skipThreadType(tt)
	}
	for _, th := range tbl.LSVerifyThreadExists {
		if skip(th.ThreadKey, th.ThreadType) {
			continue
		}
		x.touchThread(chatKey(th.ThreadKey), func(t *threadRec) {
			applyThreadType(t, th.ThreadKey, th.ThreadType)
			if th.FolderName != "" {
				t.Folder = th.FolderName
			}
		})
	}
	for _, th := range tbl.LSDeleteThenInsertThread {
		if skip(th.ThreadKey, th.ThreadType) {
			continue
		}
		chat := chatKey(th.ThreadKey)
		x.touchThread(chat, func(t *threadRec) {
			applyThreadType(t, th.ThreadKey, th.ThreadType)
			if th.ThreadName != "" {
				t.Name = th.ThreadName
			}
			if th.ThreadPictureUrl != "" {
				t.Avatar = th.ThreadPictureUrl
			}
			t.LastActivity = max(th.LastActivityTimestampMs, t.LastActivity)
			t.LastRead = max(th.LastReadWatermarkTimestampMs, t.LastRead)
			t.Snippet = th.Snippet
			t.Folder = th.FolderName
			// A full thread row: the server's unread count and mute setting are
			// authoritative (0 = not muted, -1 = muted forever, else expiry in ms).
			t.MuteUntil = th.MuteExpireTimeMs
			if th.MemberCount > 0 {
				t.MemberCount = int(th.MemberCount)
			}
			t.Unread = int(max(th.UnreadMessageCount, 0))
		})
		x.serverUnread[chat] = true
	}
	for _, th := range tbl.LSUpdateOrInsertThread {
		if skip(th.ThreadKey, th.ThreadType) {
			continue
		}
		chat := chatKey(th.ThreadKey)
		x.touchThread(chat, func(t *threadRec) {
			applyThreadType(t, th.ThreadKey, th.ThreadType)
			if th.ThreadName != "" {
				t.Name = th.ThreadName
			}
			if th.ThreadPictureUrl != "" {
				t.Avatar = th.ThreadPictureUrl
			}
			t.LastActivity = max(th.LastActivityTimestampMs, t.LastActivity)
			if th.Snippet != "" {
				t.Snippet = th.Snippet
			}
			if th.FolderName != "" {
				t.Folder = th.FolderName
			}
			// An update row leaves unrelated columns empty, so a missing expiry
			// is not read as "unmuted"; unmuting arrives as LSUpdateThreadMuteSetting
			// or in the next full thread row.
			if th.MuteExpireTimeMs != 0 {
				t.MuteUntil = th.MuteExpireTimeMs
			}
		})
		// The row's read watermark may have caught up with the last activity:
		// the chat was read on another device.
		x.readTo(chat, th.LastReadWatermarkTimestampMs)
	}
}

// Messages -------------------------------------------------------------------

func (x *batch) messages(tbl *table.LSTable) {
	upsert, insert := tbl.WrapMessages()

	keys := make([]int64, 0, len(upsert))
	for k := range upsert {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool { return keys[i] < keys[j] })
	for _, tk := range keys {
		um := upsert[tk]
		chat, open := x.openChat(tk)
		if !open {
			chat = chatKey(tk)
			x.ensureThread(chat, tk)
		}
		prevNewest := x.b.st.newestTS(chat)
		_, prevOldest, hadMsgs := x.b.st.oldest(chat)
		if open {
			_, prevOldest, hadMsgs = x.b.st.oldestOpen(chat)
		}
		msgs := um.Messages
		sort.SliceStable(msgs, func(i, j int) bool { return msgs[i].TimestampMs < msgs[j].TimestampMs })
		for _, w := range msgs {
			x.putWrapped(chat, w, prevNewest)
		}
		if um.Range != nil {
			x.ranges[chat] = true
			if !hadMsgs || um.Range.MinTimestampMs <= prevOldest {
				noMore := !um.Range.HasMoreBefore
				x.touchThread(chat, func(t *threadRec) {
					if open {
						t.OpenNoMoreBefore = noMore
					} else {
						t.NoMoreBefore = noMore
					}
				})
			}
		}
	}

	sort.SliceStable(insert, func(i, j int) bool { return insert[i].TimestampMs < insert[j].TimestampMs })
	for _, w := range insert {
		chat, open := x.openChat(w.ThreadKey)
		if !open {
			chat = chatKey(w.ThreadKey)
			x.ensureThread(chat, w.ThreadKey)
		}
		x.putWrapped(chat, w, x.b.st.newestTS(chat))
	}
}

func (x *batch) ensureThread(chat string, key int64) {
	if _, ok := x.b.st.thread(chat); ok {
		return
	}
	x.touchThread(chat, func(t *threadRec) { t.Key = key })
}

// putWrapped stores a message (and its extra attachment parts). A message is
// "new" for the app when it is newer than everything we had before this table;
// older messages (history sync, backfill) are stored silently.
//
// message.new carries live=true only when the table came over the established
// realtime socket and the message is new and recent. Messages caught up during a
// (re)connect sync are still announced (the open chat must show them) but with
// live=false, so the app does not notify for them.
func (x *batch) putWrapped(chat string, w *table.WrappedMessage, prevNewest int64) {
	parts := x.b.convertWrapped(w, chat)
	for i, p := range parts {
		isNew, changed, snap := x.b.st.putMessage(p)
		fresh := x.mode == modeLive && isNew && p.Timestamp > prevNewest
		switch {
		case fresh:
			live := x.live && isFresh(p.Timestamp)
			x.msgEv = append(x.msgEv, eventFrame{Event: "message.new", Data: map[string]any{"message": snap, "live": live}})
		case i == 0 && x.mode == modeLive && !isNew && changed:
			x.msgEv = append(x.msgEv, eventFrame{Event: "message.update", Data: map[string]any{"message": snap}})
		}
		if isNew || changed {
			x.markDirty(chat)
		}
		if !isNew {
			continue
		}
		x.touchThread(chat, func(t *threadRec) {
			if snap.Timestamp >= t.LastActivity {
				t.LastActivity = snap.Timestamp
				if txt := previewText(snap); txt != "" {
					t.Snippet = txt
				}
			}
			if !fresh || i > 0 {
				return // history or backfill, or an extra attachment of a counted message
			}
			switch {
			case snap.FromMe:
				// Sent from another device (or echoed back): sending reads the chat.
				t.LastRead = max(t.LastRead, snap.Timestamp)
				t.Unread = 0
			case !snap.Deleted && snap.Timestamp > t.LastRead && !x.serverUnread[chat]:
				t.Unread++
			}
		})
	}
}

func previewText(m Message) string {
	if m.Deleted {
		return ""
	}
	if m.Text != "" {
		return m.Text
	}
	switch m.Kind {
	case "image":
		return "Photo"
	case "video":
		return "Video"
	case "gif":
		return "GIF"
	case "voice", "audio":
		return "Voice message"
	case "sticker":
		return "Sticker"
	case "file":
		return "File"
	case "location":
		return "Location"
	}
	return ""
}

// Pins -----------------------------------------------------------------------

// pins keeps the pinned messages of each thread from the only two tables Meta sends
// for them with thread sync: LSSetPinnedMessage and LSClearPinnedMessages (delete by
// thread key). A table keeps no order between the two, so clears run first: a clear
// plus sets for one thread replaces its pins. No pin is made at time 0, so a set
// without a timestamp is read as an unpin of that message (and could not be ordered
// anyway). pins.update goes out only for threads whose list really changed, so a
// sync that repeats the current state stays silent.
func (x *batch) pins(tbl *table.LSTable) {
	if len(tbl.LSClearPinnedMessages) == 0 && len(tbl.LSSetPinnedMessage) == 0 {
		return
	}
	before := map[string][]pinRec{}
	var order []string
	// Pins of the pre-encryption thread of an encrypted 1:1 chat belong to the encrypted
	// chat, where its messages are filed (openChat).
	chatOf := func(key int64) string {
		chat, open := x.openChat(key)
		if !open {
			chat = chatKey(key)
		}
		if _, seen := before[chat]; !seen {
			before[chat] = x.b.st.pinList(chat)
			order = append(order, chat)
		}
		return chat
	}
	for _, c := range tbl.LSClearPinnedMessages {
		if c.ThreadKey != 0 {
			x.b.st.clearPins(chatOf(c.ThreadKey))
		}
	}
	for _, p := range tbl.LSSetPinnedMessage {
		if p.ThreadKey == 0 || p.MessageId == "" {
			continue
		}
		chat := chatOf(p.ThreadKey)
		if p.PinnedTimestampMs <= 0 {
			x.b.st.removePin(chat, p.MessageId)
		} else {
			x.b.st.setPin(chat, p.MessageId, p.PinnedTimestampMs)
		}
	}
	for _, chat := range order {
		if !slices.Equal(before[chat], x.b.st.pinList(chat)) {
			x.msgEv = append(x.msgEv, eventFrame{Event: "pins.update", Data: map[string]any{"chatId": chat}})
		}
	}
}

// Message conversion ----------------------------------------------------------

type attachment struct {
	kind  string
	media Media
	text  string // extra text carried by the attachment (link shares, locations)
	// link is the address a link card opens. It is not written into text: the message shows
	// the card's title and Message.links makes the title open the address (see addAttachmentText).
	link string
}

func kindFromAttachment(at table.AttachmentType, mime, fileName string) string {
	switch at {
	case table.AttachmentTypeSticker, table.AttachmentTypeSelfieSticker, table.AttachmentTypeThirdPartySticker:
		return "sticker"
	case table.AttachmentTypeImage, table.AttachmentTypeEphemeralImage:
		return "image"
	case table.AttachmentTypeAnimatedImage:
		return "gif"
	case table.AttachmentTypeVideo, table.AttachmentTypeEphemeralVideo:
		return "video"
	case table.AttachmentTypeAudio, table.AttachmentTypeSoundBite:
		if strings.HasPrefix(strings.ToLower(fileName), "audioclip") {
			return "voice"
		}
		return "audio"
	case table.AttachmentTypeFile:
		return "file"
	}
	switch {
	case strings.HasPrefix(mime, "image/gif"):
		return "gif"
	case strings.HasPrefix(mime, "image/"):
		return "image"
	case strings.HasPrefix(mime, "video/"):
		return "video"
	case strings.HasPrefix(mime, "audio/"):
		if strings.HasPrefix(strings.ToLower(fileName), "audioclip") {
			return "voice"
		}
		return "audio"
	}
	return "file"
}

func firstNonEmpty(vals ...string) string {
	for _, v := range vals {
		if v != "" {
			return v
		}
	}
	return ""
}

func removeLPHP(addr string) string {
	parsed, err := url.Parse(addr)
	if err == nil && parsed.Path == "/l.php" {
		if u := parsed.Query().Get("u"); u != "" {
			return u
		}
	}
	return addr
}

func (b *bridge) convertWrapped(w *table.WrappedMessage, chat string) []*storedMsg {
	selfID, selfName := b.selfInfo()
	sender := chatKey(w.SenderId)
	fromMe := w.SenderId == selfID
	base := &storedMsg{
		Message: Message{
			ID:        w.MessageId,
			ChatID:    chat,
			SenderID:  sender,
			FromMe:    fromMe,
			Timestamp: w.TimestampMs,
			Kind:      "text",
			Text:      w.Text,
			Forwarded: w.IsForwarded,
			Edited:    w.EditCount > 0,
			Deleted:   w.IsUnsent,
		},
		Sort: w.PrimarySortKey,
	}
	if c, ok := b.st.contact(sender); ok {
		base.SenderName, base.SenderAvatar = c.Name, c.Avatar
	}
	base.Text = resolveOpenMentions(w.Text, w.MentionIds, w.MentionOffsets, w.MentionLengths,
		func(user string) string { return b.mentionName(chat, user) })
	if fromMe {
		base.SenderName = selfName
		base.Status = "sent"
		if t, ok := b.st.thread(chat); ok && t.PeerRead >= w.TimestampMs && w.TimestampMs > 0 {
			base.Status = "read"
		}
	}
	if w.ReplySourceId != "" {
		rep := &Reply{ID: w.ReplySourceId, Text: firstNonEmpty(w.ReplyMessageText, w.ReplySnippet)}
		if w.ReplyToUserId != 0 {
			if c, ok := b.st.contact(chatKey(w.ReplyToUserId)); ok {
				rep.SenderName = c.Name
			}
		}
		if rep.Text == "" && w.ReplyMediaUrl != "" {
			rep.Kind = "image"
		}
		base.ReplyTo = rep
	}
	if len(w.Reactions) > 0 {
		base.Actors = map[string]string{}
		for _, r := range w.Reactions {
			base.Actors[chatKey(r.ActorId)] = r.Reaction
		}
		recomputeReactions(base, chatKey(selfID))
	}
	if w.IsUnsent {
		base.Text = ""
		return []*storedMsg{base}
	}

	var atts []attachment
	for _, a := range w.BlobAttachments {
		atts = append(atts, blobAttachment(a))
	}
	for _, a := range w.Attachments {
		atts = append(atts, legacyAttachment(a))
	}
	for _, s := range w.Stickers {
		url := firstNonEmpty(s.PlayableUrl, s.PreviewUrl, s.ImageUrl)
		mime := s.PlayableUrlMimeType
		if s.PlayableUrl == "" {
			mime = firstNonEmpty(s.PreviewUrlMimeType, s.ImageUrlMimeType)
		}
		atts = append(atts, attachment{kind: "sticker", media: Media{
			Mime: mime, URL: url, Width: int(s.PreviewWidth), Height: int(s.PreviewHeight),
		}})
	}
	for _, xma := range w.XMAAttachments {
		atts = append(atts, xmaAttachment(xma))
	}

	switch {
	case len(atts) > 0:
		first := atts[0]
		base.Kind = first.kind
		if first.media != (Media{}) {
			m := first.media
			base.Media = &m
		}
		addAttachmentText(&base.Message, first)
	case w.IsAdminMessage:
		base.Kind = "system"
	case base.Text == "":
		if w.StickerId != 0 {
			base.Kind = "sticker"
		} else {
			base.Kind = "unsupported"
			base.Text = unsupportedE2EEText
		}
	}

	out := []*storedMsg{base}
	for i := 1; i < len(atts); i++ {
		a := atts[i]
		extra := &storedMsg{
			Message: Message{
				ID:           fmt.Sprintf("%s:%d", w.MessageId, i),
				ChatID:       chat,
				SenderID:     sender,
				SenderName:   base.SenderName,
				SenderAvatar: base.SenderAvatar,
				FromMe:       fromMe,
				// One millisecond apart so the parts keep their order and stay together.
				Timestamp: w.TimestampMs + int64(i),
				Kind:      a.kind,
				Status:    base.Status,
			},
			Sort: w.PrimarySortKey,
		}
		addAttachmentText(&extra.Message, a)
		if a.media != (Media{}) {
			m := a.media
			extra.Media = &m
		}
		out = append(out, extra)
	}
	if len(out) > 1 {
		// What Messenger sent as one message (several photos, a photo and its caption) is
		// shown as one album: every part carries the id of the message.
		for _, m := range out {
			m.GroupID = w.MessageId
		}
	}
	return out
}

// addAttachmentText adds what an attachment says to the text of the message part that shows
// it. A link card contributes its title, which then opens the card's address through
// Message.links; the address is only written out when there is no title or when the text
// already shows it.
func addAttachmentText(m *Message, a attachment) {
	if a.link == "" {
		m.Text = joinText(m.Text, a.text)
		return
	}
	switch {
	case strings.Contains(strings.ToLower(m.Text), normURL(a.link)):
		m.Text = joinText(m.Text, a.text) // the address is already in the text
	case a.text == "":
		m.Text = joinText(m.Text, a.link)
	default:
		m.Text = joinText(m.Text, a.text)
		if off := utf16Offset(m.Text, a.text); off >= 0 {
			m.Links = append(m.Links, Link{Offset: off, Length: utf16Len(a.text), URL: a.link})
		}
	}
}

func joinText(a, b string) string {
	switch {
	case a == "":
		return b
	case b == "" || strings.Contains(a, b):
		return a
	}
	return a + "\n\n" + b
}

func blobAttachment(a *table.LSInsertBlobAttachment) attachment {
	url, mime := a.PlayableUrl, firstNonEmpty(a.PlayableUrlMimeType, a.AttachmentMimeType)
	if url == "" {
		url, mime = a.PreviewUrl, firstNonEmpty(a.PreviewUrlMimeType, a.AttachmentMimeType)
	}
	return attachment{
		kind: kindFromAttachment(a.AttachmentType, mime, a.Filename),
		media: Media{
			Mime: mime, URL: url, FileName: a.Filename, Size: a.Filesize,
			Width: int(a.PreviewWidth), Height: int(a.PreviewHeight),
			Duration: float64(a.PlayableDurationMs) / 1000,
		},
	}
}

func legacyAttachment(a *table.LSInsertAttachment) attachment {
	url, mime := a.PlayableUrl, firstNonEmpty(a.PlayableUrlMimeType, a.AttachmentMimeType)
	if url == "" {
		url, mime = firstNonEmpty(a.PreviewUrl, a.ImageUrl), firstNonEmpty(a.PreviewUrlMimeType, a.ImageUrlMimeType, a.AttachmentMimeType)
	}
	at := attachment{
		kind: kindFromAttachment(a.AttachmentType, mime, a.Filename),
		media: Media{
			Mime: mime, URL: url, FileName: a.Filename, Size: a.Filesize,
			Width: int(a.PreviewWidth), Height: int(a.PreviewHeight),
			Duration: float64(a.PlayableDurationMs) / 1000,
		},
	}
	if a.ActionUrl != "" && url == "" {
		at.kind = "link"
		at.text, at.link = a.TitleText, removeLPHP(a.ActionUrl)
	}
	return at
}

func xmaAttachment(x *table.WrappedXMA) attachment {
	if x.CTA != nil && (x.CTA.Type_ == "xma_live_location_sharing" || strings.HasPrefix(x.CTA.NativeUrl, "messenger://location_share")) {
		at := attachment{kind: "location", text: firstNonEmpty(strings.TrimSpace(x.TitleText+"\n"+x.SubtitleText), "Location")}
		if lat, lon, ok := locationFromNativeURL(x.CTA.NativeUrl); ok {
			if l := mapLinks(at.text, lat, lon); len(l) > 0 {
				at.link = l[0].URL
			}
		}
		return at
	}
	url, mime := x.PlayableUrl, x.PlayableUrlMimeType
	w, h := int64(0), int64(0)
	if url == "" {
		url, mime = x.PreviewUrl, x.PreviewUrlMimeType
		w, h = x.PreviewWidth, x.PreviewHeight
	}
	link := ""
	if x.CTA != nil && x.CTA.ActionUrl != "" {
		link = removeLPHP(x.CTA.ActionUrl)
	}
	at := attachment{media: Media{Mime: mime, URL: url, FileName: x.Filename, Size: x.Filesize, Width: int(w), Height: int(h)}}
	switch {
	case link != "":
		at.kind = "link"
		at.text, at.link = x.TitleText, link
	case url != "":
		at.kind = kindFromAttachment(x.AttachmentType, mime, x.Filename)
		if x.ShouldAutoplayVideo {
			at.kind = "gif"
		}
		at.text = x.CaptionBodyText
	case x.TitleText != "":
		at.kind = "unsupported"
		at.text = x.TitleText
	default:
		at.kind = "unsupported"
		at.text = unsupportedE2EEText
	}
	return at
}

// Other realtime updates ---------------------------------------------------------

func (x *batch) updateMessage(id string, fn func(m *storedMsg)) {
	snap, changed, ok := x.b.st.mutateMessage(id, fn)
	if !ok || !changed {
		return
	}
	x.markDirty(snap.ChatID)
	if x.mode == modeLive {
		x.msgEv = append(x.msgEv, eventFrame{Event: "message.update", Data: map[string]any{"message": snap}})
	}
}

func (x *batch) markDeleted(id string) {
	x.updateMessage(id, func(m *storedMsg) {
		m.Deleted = true
		m.Text = ""
		m.Media = nil
		m.Actors = nil
		m.Reactions = nil
	})
}

func (x *batch) readWatermark(key int64, ts int64, mine bool) {
	chat := chatKey(key)
	if _, ok := x.b.st.thread(chat); !ok {
		return
	}
	if mine {
		x.readTo(chat, ts)
		return
	}
	x.touchThread(chat, func(t *threadRec) { t.PeerRead = max(t.PeerRead, ts) })
	for _, snap := range x.b.st.markOwnRead(chat, ts) {
		x.markDirty(chat)
		if x.mode == modeLive {
			x.msgEv = append(x.msgEv, eventFrame{Event: "message.update", Data: map[string]any{"message": snap}})
		}
	}
}

func (x *batch) updates(tbl *table.LSTable) {
	b := x.b
	selfStr := chatKey(x.selfID)

	for _, e := range tbl.LSEditMessage {
		x.updateMessage(e.MessageID, func(m *storedMsg) {
			m.Text = e.Text
			m.Edited = true
		})
	}
	for _, d := range tbl.LSDeleteMessage {
		x.markDeleted(d.MessageId)
	}
	for _, d := range tbl.LSDeleteThenInsertMessage {
		if d.IsUnsent {
			x.markDeleted(d.MessageId)
		}
	}
	for _, r := range tbl.LSUpsertReaction {
		x.updateMessage(r.MessageId, func(m *storedMsg) {
			if m.Actors == nil {
				m.Actors = map[string]string{}
			}
			m.Actors[chatKey(r.ActorId)] = r.Reaction
			recomputeReactions(m, selfStr)
		})
	}
	for _, r := range tbl.LSDeleteReaction {
		x.updateMessage(r.MessageId, func(m *storedMsg) {
			delete(m.Actors, chatKey(r.ActorId))
			recomputeReactions(m, selfStr)
		})
	}
	for _, r := range tbl.LSMarkThreadReadV2 {
		x.readWatermark(r.ThreadKey, r.LastReadWatermarkTimestampMs, true)
	}
	for _, r := range tbl.LSMarkThreadRead {
		x.readWatermark(r.ThreadKey, r.LastReadWatermarkTimestampMs, true)
	}
	for _, r := range tbl.LSUpdateReadReceipt {
		x.readWatermark(r.ThreadKey, r.ReadWatermarkTimestampMs, r.ContactId == x.selfID)
	}
	for _, n := range tbl.LSSyncUpdateThreadName {
		if _, open := x.openChat(n.ThreadKey); n.ThreadName != "" && !open {
			x.touchThread(chatKey(n.ThreadKey), func(t *threadRec) { t.Name = n.ThreadName })
		}
	}
	for _, n := range tbl.LSSetThreadImageURL {
		if _, open := x.openChat(n.ThreadKey); !open {
			x.touchThread(chatKey(n.ThreadKey), func(t *threadRec) { t.Avatar = n.ImageURL })
		}
	}
	for _, n := range tbl.LSUpdateThreadMuteSetting {
		chat := chatKey(n.ThreadKey)
		if _, ok := b.st.thread(chat); !ok {
			continue // the full thread row (with its mute setting) follows
		}
		// 0 = unmuted, -1 = muted forever, else the expiry (ms epoch).
		x.touchThread(chat, func(t *threadRec) { t.MuteUntil = n.MuteExpireTimeMS })
	}
	for _, r := range tbl.LSUpdateExistingMessageRange {
		chat, open := x.openChat(r.ThreadKey)
		var oldest int64
		var ok bool
		if open {
			_, oldest, ok = b.st.oldestOpen(chat)
		} else {
			chat = chatKey(r.ThreadKey)
			_, oldest, ok = b.st.oldest(chat)
		}
		if r.UnknownBool2 && !r.UnknownBool3 && (!ok || r.TimestampMS <= oldest) {
			x.touchThread(chat, func(t *threadRec) {
				if open {
					t.OpenNoMoreBefore = true
				} else {
					t.NoMoreBefore = true
				}
			})
		}
		x.ranges[chat] = true
	}
	if x.mode == modeLive {
		for _, ti := range tbl.LSUpdateTypingIndicator {
			if ti.SenderId == x.selfID {
				continue
			}
			uid := chatKey(ti.SenderId)
			data := map[string]any{"chatId": chatKey(ti.ThreadKey), "userId": uid, "typing": ti.IsTyping}
			if c, ok := b.st.contact(uid); ok && c.Name != "" {
				data["name"] = c.Name
			}
			x.msgEv = append(x.msgEv, eventFrame{Event: "typing", Data: data})
		}
	}
}

// flush emits the collected events: chat.upsert for brand-new chats first (so
// the app knows the chat before it sees its messages), then message events,
// then chat.upsert for everything else that changed.
func (x *batch) flush() {
	b := x.b
	var late []string
	for _, chat := range x.order {
		if x.created[chat] {
			b.emitChat(chat)
		} else {
			late = append(late, chat)
		}
	}
	for _, ev := range x.msgEv {
		b.out.write(ev)
	}
	for _, chat := range late {
		b.emitChat(chat)
	}
	for chat := range x.ranges {
		b.signalWaiters(chat)
	}
}

func (b *bridge) emitChat(chat string) {
	t, ok := b.st.thread(chat)
	if !ok {
		return
	}
	if hiddenThread(t) {
		return
	}
	b.emit("chat.upsert", map[string]any{"chat": b.chatView(t)})
}

// Chat view -----------------------------------------------------------------------

func hiddenThread(t threadRec) bool {
	switch t.Folder {
	case "spam", "hidden", "e2ee_cutover_archived":
		return true
	}
	return false
}

func jidUser(jid string) string {
	u, _, _ := cutJID(jid)
	return u
}

// peerID is the Facebook user ID of the other side of a 1:1 chat.
func peerID(t threadRec) string {
	if t.JID != "" {
		if u := jidUser(t.JID); u != "" {
			return u
		}
	}
	return t.ID
}

func (b *bridge) chatKind(t threadRec, selfID int64) string {
	tt := table.ThreadType(t.Type)
	switch {
	case tt == table.AI_BOT:
		return "bot"
	case t.IsGroup && t.E2EE:
		return "group"
	case tt.IsOneToOne() || (t.E2EE && !t.IsGroup && tt == table.UNKNOWN_THREAD_TYPE):
		if peerID(t) == chatKey(selfID) {
			return "self"
		}
		return "direct"
	case tt == table.UNKNOWN_THREAD_TYPE:
		if t.MemberCount > 2 || (t.Name != "" && t.MemberCount == 0 && t.Key != 0 && t.Key < 1_000_000_000_000_000) {
			return "group"
		}
		return "direct"
	}
	return "group"
}

func (b *bridge) chatView(t threadRec) Chat {
	selfID, selfName := b.selfInfo()
	kind := b.chatKind(t, selfID)
	c := Chat{
		ID:          t.ID,
		Title:       t.Name,
		Avatar:      t.Avatar,
		Kind:        kind,
		Unread:      t.Unread,
		Timestamp:   t.LastActivity,
		MemberCount: t.MemberCount,
		Encrypted:   t.E2EE,
	}
	// Muted while the mute is forever (-1) or has not expired yet; an expired mute is
	// reported as not muted. mutedUntil lets the app notice the expiry by itself.
	if t.MuteUntil < 0 || t.MuteUntil > time.Now().UnixMilli() {
		c.Muted, c.MutedUntil = true, t.MuteUntil
	}
	if kind == "direct" || kind == "self" || kind == "bot" {
		co, _ := b.st.contact(peerID(t))
		c.Title = firstNonEmpty(co.Name, c.Title)
		// The same key as the sender avatar of that person's messages, so one download
		// serves both; a fresher signed URL on either side wins over an expired one.
		c.Avatar = b.st.avatarFor(peerID(t), co.Avatar, t.Avatar)
		if kind == "self" {
			c.Title = firstNonEmpty(selfName, c.Title)
		}
	} else {
		c.Avatar = b.st.avatarFor("chat-"+t.ID, t.Avatar)
	}
	if c.Title == "" {
		c.Title = t.ID
	}
	last := b.st.lastMessage(t.ID)
	if t.E2EE {
		if id, ok := b.st.newestOpen(t.ID); ok {
			c.EncryptedAfter = id
			if last != nil && last.ID == id {
				last = nil // the chat went on encrypted; its real latest message is not here
			}
		}
	}
	if last != nil {
		c.LastMessage = last
		c.Timestamp = max(c.Timestamp, last.Timestamp)
	} else if t.Snippet != "" {
		c.LastMessage = &Message{
			ID: "snippet:" + t.ID, ChatID: t.ID, Kind: "text", Text: t.Snippet, Timestamp: t.LastActivity,
		}
	}
	return c
}

// Thread pagination ----------------------------------------------------------------

func (b *bridge) liveClient() *messagix.Client {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.state != stateLoggedIn {
		return nil
	}
	return b.client
}

func (b *bridge) threadCount() int {
	b.st.mu.RLock()
	defer b.st.mu.RUnlock()
	return len(b.st.threads)
}

// fetchMoreThreads loads the next page of the inbox. more=false means the
// server has no further threads.
func (b *bridge) fetchMoreThreads(ctx context.Context, client *messagix.Client) (more bool, err error) {
	keyStore, tbl, err := client.FetchMoreThreads(ctx, 1)
	if err != nil {
		return false, err
	}
	if keyStore == nil && tbl == nil {
		b.threadsExhausted.Store(true)
		return false, nil
	}
	b.ingestTable(tbl, modeInitial)
	return true, nil
}

// prefetchThreads grabs a few extra inbox pages right after connecting so the
// first chats.list is already reasonably full.
func (b *bridge) prefetchThreads(ctx context.Context, gen int, client *messagix.Client) {
	for i := 0; i < 4 && b.isCurrent(gen) && ctx.Err() == nil; i++ {
		cctx, cancel := context.WithTimeout(ctx, 20*time.Second)
		more, err := b.fetchMoreThreads(cctx, client)
		cancel()
		if err != nil {
			b.log.Debug().Err(err).Msg("thread prefetch stopped")
			return
		}
		if !more {
			return
		}
		select {
		case <-time.After(400 * time.Millisecond):
		case <-ctx.Done():
			return
		}
	}
}

// ensureThreads paginates the inbox until `want` threads are known.
func (b *bridge) ensureThreads(ctx context.Context, want int) {
	client := b.liveClient()
	if client == nil {
		return
	}
	for i := 0; i < 3 && b.threadCount() < want && !b.threadsExhausted.Load(); i++ {
		cctx, cancel := context.WithTimeout(ctx, 15*time.Second)
		more, err := b.fetchMoreThreads(cctx, client)
		cancel()
		if err != nil {
			b.log.Debug().Err(err).Msg("could not load more threads")
			return
		}
		if !more {
			return
		}
	}
}

// Backfill waiters -----------------------------------------------------------------

func (b *bridge) addWaiter(chat string) (ch chan struct{}, remove func()) {
	ch = make(chan struct{}, 1)
	b.wmu.Lock()
	if b.waiters[chat] == nil {
		b.waiters[chat] = map[chan struct{}]struct{}{}
	}
	b.waiters[chat][ch] = struct{}{}
	b.wmu.Unlock()
	return ch, func() {
		b.wmu.Lock()
		delete(b.waiters[chat], ch)
		b.wmu.Unlock()
	}
}

func (b *bridge) signalWaiters(chat string) {
	b.wmu.Lock()
	defer b.wmu.Unlock()
	for ch := range b.waiters[chat] {
		select {
		case ch <- struct{}{}:
		default:
		}
	}
}
