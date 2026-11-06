package main

// Implementations of the protocol methods on top of the store, the messagix
// task API (LightSpeed) and the E2EE layer (e2ee.go).

import (
	"context"
	"errors"
	"fmt"
	"io"
	"mime"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/mautrix-meta/pkg/messagix/useragent"
)

// partSep separates the id of an extra attachment part from its message id
// ("<mid>:<n>", see convertWrapped). Facebook and WhatsApp ids never contain it.
const partSep = ":"

func baseMessageID(id string) string {
	base, _, _ := strings.Cut(id, partSep)
	return base
}

func (b *bridge) lookupThread(chat string) (threadRec, error) {
	t, ok := b.st.thread(chat)
	if !ok {
		return threadRec{}, newErr(codeNotFound, "chat %s not found", chat)
	}
	return t, nil
}

func threadKeyOf(t threadRec) (int64, error) {
	if t.Key != 0 {
		return t.Key, nil
	}
	k, err := strconv.ParseInt(t.ID, 10, 64)
	if err != nil || k == 0 {
		return 0, newErr(codeUnsupported, "chat %s has no Facebook thread (end-to-end encrypted only)", t.ID)
	}
	return k, nil
}

// chats ----------------------------------------------------------------------

func (b *bridge) listChats(ctx context.Context, limit int) ([]Chat, error) {
	if err := b.requireRead(); err != nil {
		return nil, err
	}
	b.ensureThreads(ctx, limit)
	threads := b.st.threadList()
	chats := make([]Chat, 0, len(threads))
	for _, t := range threads {
		if hiddenThread(t) {
			continue
		}
		if t.LastActivity == 0 && t.Snippet == "" && b.st.messageCount(t.ID) == 0 {
			continue // placeholder thread without any content
		}
		chats = append(chats, b.chatView(t))
	}
	sort.SliceStable(chats, func(i, j int) bool {
		if chats[i].Pinned != chats[j].Pinned {
			return chats[i].Pinned
		}
		return chats[i].Timestamp > chats[j].Timestamp
	})
	if len(chats) > limit {
		chats = chats[:limit]
	}
	return chats, nil
}

func (b *bridge) chatAvatar(chat string) (string, error) {
	if err := b.requireRead(); err != nil {
		return "", err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return "", err
	}
	return b.chatView(t).Avatar, nil
}

// chats.members --------------------------------------------------------------

// chatMembers lists who can be @mentioned in a chat: the other side of a 1:1 chat,
// or the stored participants of a group (LSAddParticipantIdToGroupThread; for E2EE
// groups without such rows, whatsmeow's group info). Ids are Facebook user ids; the
// logged-in user is left out. Everything but the E2EE lookup is served from memory.
func (b *bridge) chatMembers(ctx context.Context, chat string) ([]Member, error) {
	if err := b.requireRead(); err != nil {
		return nil, err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return nil, err
	}
	selfID, selfName := b.selfInfo()
	self := chatKey(selfID)
	var pushNames map[string]string // names carried by messages, for people without a contact row
	view := func(id string) Member {
		m := Member{ID: id, Name: id}
		co, ok := b.st.contact(id)
		if ok {
			m.Name = firstNonEmpty(co.Name, id)
		}
		if !ok || co.Name == "" {
			if pushNames == nil {
				pushNames = b.st.senderNames()
			}
			m.Name = firstNonEmpty(pushNames[id], m.Name)
		}
		cands := []string{co.Avatar}
		if !t.IsGroup && id == peerID(t) {
			cands = append(cands, t.Avatar)
		}
		m.Avatar = b.st.avatarFor(id, cands...)
		if id == self {
			m.Name = firstNonEmpty(selfName, m.Name)
		}
		return m
	}
	switch b.chatKind(t, selfID) {
	case "direct", "bot", "self":
		return []Member{view(peerID(t))}, nil
	}
	ids := b.st.memberIDs(chat)
	if len(ids) == 0 && t.E2EE && t.IsGroup {
		ids = b.e2eeGroupMembers(ctx, t)
	}
	members := make([]Member, 0, len(ids))
	for _, id := range ids {
		if id != self {
			members = append(members, view(id))
		}
	}
	sort.SliceStable(members, func(i, j int) bool {
		// People we know a name for come first; bare ids (no contact row yet) go last.
		if ui, uj := members[i].Name == members[i].ID, members[j].Name == members[j].ID; ui != uj {
			return uj
		}
		a, c := strings.ToLower(members[i].Name), strings.ToLower(members[j].Name)
		if a != c {
			return a < c
		}
		return members[i].ID < members[j].ID
	})
	return members, nil
}

// chats.mute -----------------------------------------------------------------

// muteThreadKey is the LightSpeed thread a chat is muted on. An encrypted chat has
// one only once Meta told us its hybrid thread (Key, as markRead relies on). Before
// that its id is the WhatsApp-side JID user, which for a 1:1 chat is the peer's id:
// the key of the unencrypted thread, so muting it would mute the wrong thread.
func muteThreadKey(t threadRec) (int64, error) {
	if !t.E2EE {
		return threadKeyOf(t)
	}
	if t.Key == 0 {
		return 0, newErr(codeUnsupported, "chat %s is end-to-end encrypted and Messenger has not sent its thread yet, so it cannot be muted from here", t.ID)
	}
	return t.Key, nil
}

// muteChat sends one MuteThreadTask (no retry; it only ever runs for an explicit
// chats.mute) and stores the new setting the way LSUpdateThreadMuteSetting does.
// until is the ms epoch the mute ends, -1 = forever, 0 = unmute.
func (b *bridge) muteChat(ctx context.Context, chat string, until int64) (Chat, error) {
	if until < -1 {
		return Chat{}, newErr(codeInvalidParams, "until must be a ms epoch, -1 (forever) or 0 (unmute)")
	}
	if err := b.requireRead(); err != nil {
		return Chat{}, err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return Chat{}, err
	}
	key, err := muteThreadKey(t)
	if err != nil {
		return Chat{}, err
	}
	client, err := b.requireClient()
	if err != nil {
		return Chat{}, err
	}
	resp, err := client.ExecuteTasks(ctx, &socket.MuteThreadTask{ThreadKey: key, MailboxType: 0, MuteExpireTimeMS: until, SyncGroup: 1})
	if err != nil {
		return Chat{}, fmt.Errorf("failed to mute chat: %w", err)
	}
	if resp != nil {
		for _, f := range resp.LSHandleFailedTask {
			return Chat{}, fmt.Errorf("Messenger rejected the mute: %s", f.Message)
		}
	}
	snap, _ := b.st.updateThread(chat, func(t *threadRec) { t.MuteUntil = until })
	b.emitChat(chat)
	return b.chatView(snap), nil
}

// chats.pins ------------------------------------------------------------------

// chatPins lists the pinned messages of a chat, newest pin first, from the pins the
// sync tables delivered (tables.go pins). A pin whose message is not stored (older
// than what we keep) is a bare placeholder at its pin time, so it is still listed.
func (b *bridge) chatPins(chat string) ([]Message, error) {
	if err := b.requireRead(); err != nil {
		return nil, err
	}
	if _, err := b.lookupThread(chat); err != nil {
		return nil, err
	}
	pins := b.st.pinList(chat)
	out := make([]Message, 0, len(pins))
	for _, p := range pins {
		if m, ok := b.st.message(chat, p.ID); ok {
			out = append(out, m.Message)
		} else {
			out = append(out, Message{ID: p.ID, ChatID: chat, Timestamp: p.At, Kind: "unsupported"})
		}
	}
	return out, nil
}

// messages.list --------------------------------------------------------------

func (b *bridge) listMessages(ctx context.Context, chat string, limit int, before string) ([]Message, error) {
	if err := b.requireRead(); err != nil {
		return nil, err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return nil, err
	}
	if before != "" {
		if _, found := b.st.listMessages(chat, before, 1); !found {
			return nil, newErr(codeNotFound, "message %s not found in chat %s", before, chat)
		}
	}
	if t.E2EE {
		// Encrypted threads keep their history only in the secure backup; the
		// LightSpeed fetch answers with an empty range after a long wait. Messages
		// from the pre-encryption thread sit below the backup's range, so the page
		// is short of history whenever it reaches past our oldest E2EE message.
		for round := 0; round < 6 && b.hasHistoryDevice(); round++ {
			if cur, _ := b.st.thread(chat); cur.ArchiveNoMoreBefore {
				break
			}
			page, _ := b.st.listMessages(chat, before, limit)
			frontier := b.archiveFrontier(chat)
			if len(page) == limit && page[0].Timestamp >= frontier {
				break
			}
			if before != "" && len(page) > 0 && page[len(page)-1].Timestamp < frontier {
				break // the page is all pre-encryption history, below the backup's range
			}
			b.st.markDeep(chat)
			cur, _ := b.st.thread(chat)
			if !b.archiveBackfillOnce(ctx, chat, cur) {
				break
			}
		}
		msgs, _ := b.st.listMessages(chat, before, limit)
		if msgs == nil {
			msgs = []Message{}
		}
		return msgs, nil
	}
	// Page older history from the server when the local store runs dry.
	for round := 0; round < 6 && b.st.countBefore(chat, before) < limit; round++ {
		cur, _ := b.st.thread(chat)
		if cur.NoMoreBefore {
			break
		}
		if t.Key == 0 {
			break
		}
		b.st.markDeep(chat)
		if !b.backfillOnce(ctx, chat, cur) {
			break
		}
	}
	msgs, _ := b.st.listMessages(chat, before, limit)
	if msgs == nil {
		msgs = []Message{}
	}
	return msgs, nil
}

// backfillOnce asks the server for the page of messages preceding our oldest
// known message (same approach as FetchMessages in pkg/connector/backfill.go:
// FetchMessagesTask, then the answer arrives as LSInsertNewMessageRange +
// LSUpsertMessage and is ingested by the table handler). Returns whether the
// store grew.
func (b *bridge) backfillOnce(ctx context.Context, chat string, t threadRec) bool {
	client := b.liveClient()
	if client == nil {
		return false
	}
	before := b.st.messageCount(chat)
	refID, refTS, ok := b.st.oldest(chat)
	if !ok {
		// Never-opened thread: ask for the page ending at its last activity.
		refTS = max(t.LastActivity, time.Now().UnixMilli())
		refID = ""
	}
	ended := func(t threadRec) bool { return t.NoMoreBefore }
	return b.fetchOlder(ctx, client, chat, t.Key, refTS, refID, ended, true) > before
}

// fetchOlder sends FetchMessagesTask for the page of threadKey before (refTS, refID)
// and waits until chat grows, ended reports the start of history or the server
// stops answering. The answer arrives as LSInsertNewMessageRange + LSUpsertMessage
// and is ingested by the table handler. retry re-sends a request that got no answer
// after 8 s. Returns the message count of chat afterwards.
func (b *bridge) fetchOlder(ctx context.Context, client *messagix.Client, chat string, threadKey, refTS int64, refID string, ended func(threadRec) bool, retry bool) int {
	before := b.st.messageCount(chat)
	ch, remove := b.addWaiter(chat)
	defer remove()

	request := func() error {
		resp, err := client.ExecuteTasks(ctx, &socket.FetchMessagesTask{
			ThreadKey:            threadKey,
			Direction:            0,
			ReferenceTimestampMs: refTS,
			ReferenceMessageId:   refID,
			SyncGroup:            1,
			Cursor:               client.GetCursor(1),
		})
		if err != nil {
			return err
		}
		b.ingestTable(resp, modeInitial)
		return nil
	}
	if err := request(); err != nil {
		b.log.Warn().Err(err).Str("chat", chat).Msg("could not request older messages")
		return before
	}
	deadline := time.NewTimer(25 * time.Second)
	resend := time.NewTimer(8 * time.Second)
	defer deadline.Stop()
	defer resend.Stop()
	if !retry {
		resend.Stop()
	}
	emptySignals := 0
	for {
		if n := b.st.messageCount(chat); n > before {
			return n
		}
		if cur, _ := b.st.thread(chat); ended(cur) {
			return before
		}
		select {
		case <-ch:
			if emptySignals++; emptySignals >= 2 {
				return before
			}
		case <-resend.C:
			if err := request(); err != nil {
				return before
			}
		case <-deadline.C:
			b.log.Warn().Str("chat", chat).Msg("timed out waiting for older messages")
			return before
		case <-ctx.Done():
			return before
		}
	}
}

// history.loadOpen -----------------------------------------------------------------

// loadOpenHistory loads the page of an encrypted 1:1 chat's pre-encryption ("open")
// thread that precedes the oldest message we have from it. Meta keys that thread by
// the peer's user id; its messages are filed under the encrypted chat (openChat in
// tables.go). Only called on an explicit user action: one request, no retry.
func (b *bridge) loadOpenHistory(ctx context.Context, chat string) (map[string]any, error) {
	if err := b.requireRead(); err != nil {
		return nil, err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return nil, err
	}
	selfID, _ := b.selfInfo()
	if !t.E2EE || b.chatKind(t, selfID) != "direct" {
		return nil, newErr(codeUnsupported, "chat %s is not an encrypted one-to-one chat", chat)
	}
	peer, err := strconv.ParseInt(peerID(t), 10, 64)
	if err != nil || peer == 0 || chatKey(peer) == chat {
		return nil, newErr(codeUnsupported, "chat %s has no earlier unencrypted conversation", chat)
	}
	if t.OpenNoMoreBefore {
		return map[string]any{"added": 0, "done": true}, nil
	}
	client, err := b.requireClient()
	if err != nil {
		return nil, err
	}
	refID, refTS, ok := b.st.oldestOpen(chat)
	if !ok {
		refID, refTS = "", time.Now().UnixMilli()
	}
	b.st.markDeep(chat)
	before := b.st.messageCount(chat)
	ended := func(t threadRec) bool { return t.OpenNoMoreBefore }
	added := b.fetchOlder(ctx, client, chat, peer, refTS, refID, ended, false) - before
	cur, _ := b.st.thread(chat)
	if added == 0 && !cur.OpenNoMoreBefore {
		return nil, newErr(codeInternal, "Facebook returned no earlier messages for this chat")
	}
	if added > 0 {
		b.emitChat(chat)
	}
	return map[string]any{"added": added, "done": cur.OpenNoMoreBefore}, nil
}

// messages.send -----------------------------------------------------------------

func (b *bridge) sendMessage(ctx context.Context, chat, text, replyTo, filePath string, mentions []mentionSpan) (Message, error) {
	if err := b.requireRead(); err != nil {
		return Message{}, err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return Message{}, err
	}
	if filePath != "" {
		if st, err := os.Stat(filePath); err != nil || st.IsDir() {
			return Message{}, newErr(codeInvalidParams, "filePath %q is not a readable file", filePath)
		}
	}
	if t.E2EE {
		return b.e2eeSend(ctx, t, text, replyTo, filePath, mentions)
	}
	client, err := b.requireClient()
	if err != nil {
		return Message{}, err
	}
	threadKey, err := threadKeyOf(t)
	if err != nil {
		return Message{}, err
	}
	selfID, selfName := b.selfInfo()

	otid := methods.GenerateEpochID()
	task := &socket.SendMessageTask{
		ThreadId:         threadKey,
		Otid:             otid,
		Source:           table.MESSENGER_INBOX_IN_THREAD,
		InitiatingSource: table.FACEBOOK_INBOX,
		SendType:         table.TEXT,
		SyncGroup:        1,
		Text:             text,
		// Offsets/lengths are UTF-16 code units over Text, exactly what Meta indexes by.
		MentionData: buildMentionData(mentions),
	}
	if t.Folder == "pending" && client.Platform.IsMessenger() {
		task.Source = table.MESSENGER_INBOX_PENDING_REQUESTS
	}
	var replyView *Reply
	if replyTo != "" {
		base := baseMessageID(replyTo)
		rm := &socket.ReplyMetaData{ReplyMessageId: base, ReplySourceType: 1, ReplyType: 0}
		replyView = &Reply{ID: replyTo}
		if orig, ok := b.st.message(chat, replyTo); ok {
			rm.ReplySender, _ = strconv.ParseInt(orig.SenderID, 10, 64)
			replyView.Text, replyView.SenderName, replyView.Kind = orig.Text, orig.SenderName, orig.Kind
		}
		task.ReplyMetaData = rm
	}

	var media *Media
	kind := "text"
	if filePath != "" {
		data, err := os.ReadFile(filePath)
		if err != nil {
			return Message{}, newErr(codeInvalidParams, "cannot read %q: %v", filePath, err)
		}
		mimeType := mime.TypeByExtension(strings.ToLower(filepath.Ext(filePath)))
		if mimeType == "" {
			mimeType = http.DetectContentType(data)
		}
		resp, err := client.GetHTTP().SendMercuryUploadRequest(ctx, threadKey, &httpclient.MercuryUploadMedia{
			Filename:  filepath.Base(filePath),
			MimeType:  mimeType,
			MediaData: data,
		})
		if err != nil {
			return Message{}, fmt.Errorf("media upload failed: %w", err)
		}
		fbid := int64(0)
		if resp != nil && resp.Payload.RealMetadata != nil {
			fbid = resp.Payload.RealMetadata.GetFbId()
		}
		if fbid == 0 {
			return Message{}, errors.New("media upload failed: Facebook returned no attachment id")
		}
		task.SendType = table.MEDIA
		task.AttachmentFBIds = []int64{fbid}
		kind = kindFromAttachment(table.AttachmentTypeNone, mimeType, filepath.Base(filePath))
		media = &Media{Mime: mimeType, FileName: filepath.Base(filePath), Size: int64(len(data)), Path: filePath}
	}
	readTask := &socket.ThreadMarkReadTask{ThreadId: threadKey, SyncGroup: 1, LastReadWatermarkTs: time.Now().UnixMilli()}

	otidStr := strconv.FormatInt(otid, 10)
	var resp *table.LSTable
	var sendErr error
	for attempt := 0; attempt < 3; attempt++ {
		if sendErr = client.WaitUntilCanSendMessages(ctx, 15*time.Second); sendErr == nil {
			resp, sendErr = client.ExecuteTasks(ctx, task, readTask)
			if sendErr == nil {
				break
			}
		}
		if ctx.Err() != nil {
			return Message{}, ctx.Err()
		}
		b.log.Warn().Err(sendErr).Int("attempt", attempt+1).Msg("send failed, retrying")
		time.Sleep(time.Duration(attempt+1) * time.Second)
	}
	if sendErr != nil {
		return Message{}, fmt.Errorf("failed to send message: %w", sendErr)
	}
	msgID := ""
	if resp != nil {
		for _, r := range resp.LSReplaceOptimsiticMessage {
			if r.OfflineThreadingId == otidStr {
				msgID = r.MessageId
			}
		}
		if msgID == "" {
			for _, f := range resp.LSMarkOptimisticMessageFailed {
				return Message{}, fmt.Errorf("Facebook rejected the message: %s", f.Message)
			}
			for _, f := range resp.LSHandleFailedTask {
				return Message{}, fmt.Errorf("Facebook rejected the message: %s", f.Message)
			}
		}
	}
	if msgID == "" {
		return Message{}, errors.New("Facebook did not confirm the message (no message id in the response)")
	}
	ts := time.Now().UnixMilli()
	if parsed, err := methods.ParseMessageID(msgID); err == nil && parsed > 0 {
		ts = parsed
	}
	sm := &storedMsg{Message: Message{
		ID: msgID, ChatID: chat, SenderID: chatKey(selfID), SenderName: selfName, FromMe: true,
		Timestamp: ts, Kind: kind, Text: text, Media: media, ReplyTo: replyView, Status: "sent",
	}, Sort: ts}
	_, _, snap := b.st.putMessage(sm)
	b.touchAfterSend(chat, snap)
	return snap, nil
}

// touchAfterSend bumps the thread after a message of ours and tells the app.
func (b *bridge) touchAfterSend(chat string, m Message) {
	b.st.updateThread(chat, func(t *threadRec) {
		t.LastActivity = max(t.LastActivity, m.Timestamp)
		if txt := previewText(m); txt != "" {
			t.Snippet = txt
		}
		t.LastRead = max(t.LastRead, m.Timestamp)
		t.Unread = 0
	})
	b.emitChat(chat)
}

// markRead / react / delete / typing -------------------------------------------------

func (b *bridge) markRead(ctx context.Context, chat string) error {
	if err := b.requireRead(); err != nil {
		return err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return err
	}
	watermark := max(t.LastActivity, b.st.newestTS(chat))
	if watermark == 0 {
		watermark = time.Now().UnixMilli()
	}
	var e2eeErr error
	if t.E2EE {
		e2eeErr = b.e2eeMarkRead(ctx, t)
	}
	if t.Key != 0 || !t.E2EE {
		client, err := b.requireClient()
		if err != nil {
			if !t.E2EE {
				return err
			}
		} else if key, kerr := threadKeyOf(t); kerr == nil {
			_, err := client.ExecuteTasks(ctx, &socket.ThreadMarkReadTask{
				ThreadId: key, LastReadWatermarkTs: watermark, SyncGroup: 1,
			})
			if err != nil && !t.E2EE {
				return fmt.Errorf("failed to mark chat as read: %w", err)
			}
		}
	}
	if e2eeErr != nil {
		b.log.Debug().Err(e2eeErr).Str("chat", chat).Msg("e2ee mark-read failed")
	}
	b.st.updateThread(chat, func(t *threadRec) {
		t.LastRead = max(t.LastRead, watermark)
		t.Unread = 0
	})
	b.emitChat(chat)
	return nil
}

func (b *bridge) react(ctx context.Context, chat, msgID, emoji string) error {
	if err := b.requireRead(); err != nil {
		return err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return err
	}
	m, ok := b.st.message(chat, msgID)
	if !ok {
		return newErr(codeNotFound, "message %s not found in chat %s", msgID, chat)
	}
	selfID, _ := b.selfInfo()
	selfStr := chatKey(selfID)
	if isE2EEMsg(m) {
		if err := b.e2eeReact(ctx, t, m, emoji); err != nil {
			return err
		}
	} else {
		client, err := b.requireClient()
		if err != nil {
			return err
		}
		key, err := threadKeyOf(t)
		if err != nil {
			return err
		}
		_, err = client.ExecuteTasks(ctx, &socket.SendReactionTask{
			ThreadKey:       key,
			TimestampMs:     time.Now().UnixMilli(),
			MessageID:       baseMessageID(msgID),
			Reaction:        strings.ReplaceAll(emoji, "\ufe0f", ""),
			ActorID:         selfID,
			SyncGroup:       1,
			SendAttribution: table.MESSENGER_INBOX_IN_THREAD,
		})
		if err != nil {
			return fmt.Errorf("failed to send reaction: %w", err)
		}
	}
	snap, changed, ok := b.st.mutateMessage(msgID, func(m *storedMsg) {
		if emoji == "" {
			delete(m.Actors, selfStr)
		} else {
			if m.Actors == nil {
				m.Actors = map[string]string{}
			}
			m.Actors[selfStr] = emoji
		}
		recomputeReactions(m, selfStr)
	})
	if ok && changed {
		b.emit("message.update", map[string]any{"message": snap})
	}
	return nil
}

func (b *bridge) deleteMessage(ctx context.Context, chat, msgID string, forEveryone bool) error {
	if err := b.requireRead(); err != nil {
		return err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return err
	}
	m, ok := b.st.message(chat, msgID)
	if !ok {
		return newErr(codeNotFound, "message %s not found in chat %s", msgID, chat)
	}
	isE2EE := isE2EEMsg(m)
	switch {
	case isE2EE && forEveryone:
		if err := b.e2eeRevoke(ctx, t, m); err != nil {
			return err
		}
	case isE2EE:
		// WhatsApp-style multi-device has no "delete for me" we can send; forget it locally.
	default:
		client, err := b.requireClient()
		if err != nil {
			return err
		}
		if forEveryone {
			if _, err := client.ExecuteTasks(ctx, &socket.DeleteMessageTask{MessageId: baseMessageID(msgID)}); err != nil {
				return fmt.Errorf("failed to unsend message: %w", err)
			}
		} else {
			key, err := threadKeyOf(t)
			if err != nil {
				return err
			}
			if _, err := client.ExecuteTasks(ctx, &socket.DeleteMessageMeOnlyTask{ThreadKey: key, MessageId: baseMessageID(msgID)}); err != nil {
				return fmt.Errorf("failed to delete message: %w", err)
			}
		}
	}
	if forEveryone {
		snap, _, ok := b.st.mutateMessage(msgID, func(m *storedMsg) {
			m.Deleted, m.Text, m.Media, m.Actors, m.Reactions = true, "", nil, nil, nil
		})
		if ok {
			b.emit("message.update", map[string]any{"message": snap})
		}
	} else if b.st.removeMessage(chat, msgID) {
		b.emit("message.delete", map[string]any{"chatId": chat, "messageId": msgID})
	}
	b.emitChat(chat)
	return nil
}

// editMessage changes the text of one of our messages. Meta allows it for 15 minutes
// and 5 edits per message and enforces both on its side.
func (b *bridge) editMessage(ctx context.Context, chat, msgID, text string, mentions []mentionSpan) (Message, error) {
	if err := b.requireRead(); err != nil {
		return Message{}, err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return Message{}, err
	}
	m, ok := b.st.message(chat, msgID)
	switch {
	case !ok:
		return Message{}, newErr(codeNotFound, "message %s not found in chat %s", msgID, chat)
	case !m.FromMe:
		return Message{}, newErr(codeInvalidParams, "only your own messages can be edited")
	case m.Deleted:
		return Message{}, newErr(codeInvalidParams, "an unsent message cannot be edited")
	}
	if isE2EEMsg(m) {
		if err := b.e2eeEdit(ctx, t, m, text, mentions); err != nil {
			return Message{}, err
		}
	} else {
		client, err := b.requireClient()
		if err != nil {
			return Message{}, err
		}
		task := &socket.EditMessageTask{MessageID: baseMessageID(msgID), Text: text}
		resp, err := client.ExecuteTasks(ctx, task)
		if err != nil {
			return Message{}, fmt.Errorf("failed to edit message: %w", err)
		}
		if resp != nil {
			for _, f := range resp.LSHandleFailedTask {
				return Message{}, fmt.Errorf("Messenger rejected the edit: %s", f.Message)
			}
			for _, e := range resp.LSEditMessage {
				// Meta answers a refused edit (too late, too many edits) with the old text, but sometimes
				// sends that answer for an edit it then confirms in a separate update (see mautrix-meta
				// HandleMatrixEdit), which the live ingest stores.
				if e.MessageID == task.MessageID && e.Text != task.Text && !b.awaitText(ctx, chat, msgID, text, 5*time.Second) {
					return Message{}, newErr(codeInvalidParams, "Messenger did not accept the edit (messages can be edited for 15 minutes, at most 5 times)")
				}
			}
		}
	}
	snap, _, ok := b.st.mutateMessage(msgID, func(m *storedMsg) {
		m.Text, m.Edited = text, true
	})
	if !ok {
		return Message{}, newErr(codeNotFound, "message %s not found in chat %s", msgID, chat)
	}
	b.emit("message.update", map[string]any{"message": snap})
	if last := b.st.lastMessage(chat); last != nil && last.ID == msgID {
		b.st.updateThread(chat, func(t *threadRec) { t.Snippet = previewText(snap) })
		b.emitChat(chat)
	}
	return snap, nil
}

// awaitText waits until the stored message has the given text.
func (b *bridge) awaitText(ctx context.Context, chat, msgID, text string, timeout time.Duration) bool {
	deadline := time.Now().Add(timeout)
	for {
		if m, ok := b.st.message(chat, msgID); ok && m.Text == text {
			return true
		}
		if time.Now().After(deadline) {
			return false
		}
		select {
		case <-ctx.Done():
			return false
		case <-time.After(200 * time.Millisecond):
		}
	}
}

func (b *bridge) sendTyping(ctx context.Context, chat string, typing bool) error {
	if err := b.requireRead(); err != nil {
		return err
	}
	t, err := b.lookupThread(chat)
	if err != nil {
		return err
	}
	if t.E2EE {
		return b.e2eeTyping(ctx, t, typing)
	}
	client, err := b.requireClient()
	if err != nil {
		return err
	}
	key, err := threadKeyOf(t)
	if err != nil {
		return err
	}
	selfID, _ := b.selfInfo()
	isGroup, isTyping := int64(1), int64(0)
	if b.chatKind(t, selfID) != "group" {
		isGroup = 0
	}
	if typing {
		isTyping = 1
	}
	return client.ExecuteStatelessTask(ctx, &socket.UpdatePresenceTask{
		ThreadKey:     key,
		IsGroupThread: isGroup,
		IsTyping:      isTyping,
		Attribution:   0,
		SyncGroup:     1,
		ThreadType:    t.Type,
	})
}

// media.download ----------------------------------------------------------------------

var extByMime = map[string]string{
	"image/jpeg": ".jpg", "image/jpg": ".jpg", "image/png": ".png", "image/gif": ".gif",
	"image/webp": ".webp", "image/heic": ".heic", "video/mp4": ".mp4", "video/quicktime": ".mov",
	"video/webm": ".webm", "audio/mp4": ".m4a", "audio/mpeg": ".mp3", "audio/ogg": ".ogg",
	"audio/aac": ".aac", "audio/wav": ".wav", "application/pdf": ".pdf",
}

func mediaExt(m *Media) string {
	if ext := filepath.Ext(m.FileName); ext != "" && len(ext) <= 8 {
		return ext
	}
	mt, _, _ := mime.ParseMediaType(m.Mime)
	if ext, ok := extByMime[mt]; ok {
		return ext
	}
	if mt != "" {
		if exts, _ := mime.ExtensionsByType(mt); len(exts) > 0 {
			return exts[len(exts)-1]
		}
	}
	if u, err := url.Parse(m.URL); err == nil {
		if ext := filepath.Ext(u.Path); ext != "" && len(ext) <= 6 {
			return ext
		}
	}
	return ""
}

func safeFileName(s string) string {
	return strings.Map(func(r rune) rune {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_', r == '.':
			return r
		}
		return '_'
	}, s)
}

func (b *bridge) downloadMedia(ctx context.Context, chat, msgID string) (string, error) {
	if err := b.requireRead(); err != nil {
		return "", err
	}
	if _, err := b.lookupThread(chat); err != nil {
		return "", err
	}
	m, ok := b.st.message(chat, msgID)
	if !ok {
		return "", newErr(codeNotFound, "message %s not found in chat %s", msgID, chat)
	}
	if m.Media == nil {
		return "", newErr(codeNotFound, "message %s has no media", msgID)
	}
	if p := m.Media.Path; p != "" {
		if st, err := os.Stat(p); err == nil && !st.IsDir() {
			return p, nil
		}
	}
	dir := filepath.Join(b.dataDir, "media", safeFileName(chat))
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return "", err
	}
	dest := filepath.Join(dir, safeFileName(msgID)+mediaExt(m.Media))
	if st, err := os.Stat(dest); err == nil && st.Size() > 0 {
		b.recordMediaPath(msgID, dest)
		return dest, nil
	}
	var err error
	if m.WAMedia != nil {
		err = b.e2eeDownload(ctx, m.WAMedia, dest)
	} else {
		err = b.httpDownload(ctx, m.Media, dest)
	}
	if err != nil {
		return "", err
	}
	b.recordMediaPath(msgID, dest)
	return dest, nil
}

func (b *bridge) recordMediaPath(msgID, path string) {
	b.st.mutateMessage(msgID, func(m *storedMsg) {
		if m.Media != nil {
			m.Media.Path = path
		}
	})
}

// httpDownload GETs an attachment URL. fbcdn URLs are signed, but the session
// cookies are attached for facebook.com / messenger.com hosts.
func (b *bridge) httpDownload(ctx context.Context, media *Media, dest string) error {
	if media.URL == "" {
		return newErr(codeNotFound, "this attachment has no downloadable URL")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, media.URL, nil)
	if err != nil {
		return newErr(codeInvalidParams, "bad media URL: %v", err)
	}
	req.Header.Set("User-Agent", useragent.UserAgent)
	req.Header.Set("Accept", "*/*")
	host := strings.ToLower(req.URL.Hostname())
	if host == "facebook.com" || strings.HasSuffix(host, ".facebook.com") || host == "messenger.com" || strings.HasSuffix(host, ".messenger.com") {
		b.mu.Lock()
		ck := b.ck
		b.mu.Unlock()
		if ck != nil {
			req.Header.Set("Cookie", ck.String())
		}
	}
	httpc := &http.Client{Timeout: 5 * time.Minute}
	resp, err := httpc.Do(req)
	if err != nil {
		return fmt.Errorf("media download failed: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusForbidden || resp.StatusCode == http.StatusGone {
		return fmt.Errorf("media download failed: the attachment URL expired (HTTP %d); reopen the chat to refresh it", resp.StatusCode)
	} else if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("media download failed: HTTP %d", resp.StatusCode)
	}
	tmp, err := os.CreateTemp(filepath.Dir(dest), ".dl-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	_, copyErr := io.Copy(tmp, resp.Body)
	closeErr := tmp.Close()
	if copyErr != nil || closeErr != nil {
		os.Remove(tmpName)
		return fmt.Errorf("media download failed: %w", errors.Join(copyErr, closeErr))
	}
	if err := os.Chmod(tmpName, 0o600); err != nil {
		os.Remove(tmpName)
		return err
	}
	if err := os.Rename(tmpName, dest); err != nil {
		os.Remove(tmpName)
		return err
	}
	return nil
}
