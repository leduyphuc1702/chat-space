package main

import (
	"bufio"
	"bytes"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

type harness struct {
	t   *testing.T
	b   *bridge
	buf *bytes.Buffer
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	buf := &bytes.Buffer{}
	b := newBridge(t.TempDir(), zerolog.Nop(), &frameWriter{w: bufio.NewWriter(buf)})
	b.selfID = 1
	return &harness{t: t, b: b, buf: buf}
}

// drain returns the frames written since the last call.
func (h *harness) drain() []map[string]any {
	var out []map[string]any
	for _, line := range strings.Split(strings.TrimSpace(h.buf.String()), "\n") {
		if line == "" {
			continue
		}
		var m map[string]any
		if err := json.Unmarshal([]byte(line), &m); err != nil {
			h.t.Fatalf("bad frame %q: %v", line, err)
		}
		out = append(out, m)
	}
	h.buf.Reset()
	return out
}

func events_(frames []map[string]any, name string) []map[string]any {
	var out []map[string]any
	for _, f := range frames {
		if f["event"] == name {
			out = append(out, f["data"].(map[string]any))
		}
	}
	return out
}

// lastChat returns the chat of the last chat.upsert frame.
func lastChat(t *testing.T, frames []map[string]any) map[string]any {
	t.Helper()
	ups := events_(frames, "chat.upsert")
	if len(ups) == 0 {
		t.Fatalf("no chat.upsert in %v", frames)
	}
	return ups[len(ups)-1]["chat"].(map[string]any)
}

func msg(thread int64, id string, sender, ts int64) *table.LSInsertMessage {
	return &table.LSInsertMessage{ThreadKey: thread, MessageId: id, SenderId: sender, TimestampMs: ts, PrimarySortKey: ts, Text: "hi " + id}
}

func TestLiveFlagUnreadMuteAndReads(t *testing.T) {
	h := newHarness(t)
	b := h.b
	now := time.Now().UnixMilli()
	const chat = int64(100)

	// 1. Initial sync over the socket, before it is flagged up: server count wins,
	// the (newer-than-stored) message is announced but not live, mute is forever.
	b.ingestTable(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{
			ThreadKey: chat, ThreadType: table.ONE_TO_ONE, ThreadName: "Alice",
			LastActivityTimestampMs: now - 1000, LastReadWatermarkTimestampMs: now - 5000,
			UnreadMessageCount: 3, MuteExpireTimeMs: -1,
		}},
		LSInsertMessage: []*table.LSInsertMessage{msg(chat, "m1", 7, now-1000)},
	}, modeLive)
	fr := h.drain()
	c := lastChat(t, fr)
	if c["unread"] != float64(3) || c["muted"] != true || c["mutedUntil"] != float64(-1) {
		t.Fatalf("initial chat wrong: %v", c)
	}
	mn := events_(fr, "message.new")
	if len(mn) != 1 || mn[0]["live"] != false {
		t.Fatalf("initial sync message must be live:false: %v", mn)
	}

	// 2. Socket is up: a fresh incoming message is live and followed by an upsert with unread+1.
	b.socketUp.Store(true)
	b.ingestTable(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{msg(chat, "m2", 7, now)}}, modeLive)
	fr = h.drain()
	mn = events_(fr, "message.new")
	if len(mn) != 1 || mn[0]["live"] != true {
		t.Fatalf("live message must be live:true: %v", mn)
	}
	// message.new must come before its chat.upsert
	var order []string
	for _, f := range fr {
		order = append(order, f["event"].(string))
	}
	if strings.Join(order, ",") != "message.new,chat.upsert" {
		t.Fatalf("order = %v", order)
	}
	if c = lastChat(t, fr); c["unread"] != float64(4) {
		t.Fatalf("unread after live message = %v", c["unread"])
	}

	// 3. Stale message (older than liveMaxAge, but newer than what we stored) is not live.
	old := now - int64(time.Hour/time.Millisecond)
	h2 := newHarness(t)
	h2.b.socketUp.Store(true)
	h2.b.ingestTable(&table.LSTable{
		LSVerifyThreadExists: []*table.LSVerifyThreadExists{{ThreadKey: 5, ThreadType: table.ONE_TO_ONE}},
		LSInsertMessage:      []*table.LSInsertMessage{msg(5, "old", 7, old)},
	}, modeLive)
	if mn := events_(h2.drain(), "message.new"); len(mn) != 1 || mn[0]["live"] != false {
		t.Fatalf("stale message must be live:false: %v", mn)
	}

	// 4. Read on another device (MarkThreadReadV2 up to the last activity): unread 0 right away.
	b.ingestTable(&table.LSTable{
		LSMarkThreadReadV2: []*table.LSMarkThreadReadV2{{ThreadKey: chat, LastReadWatermarkTimestampMs: now}},
	}, modeLive)
	if c = lastChat(t, h.drain()); c["unread"] != float64(0) {
		t.Fatalf("unread after read elsewhere = %v", c["unread"])
	}

	// 5. New message, then a read receipt of ours (UpdateReadReceipt, ContactId == self).
	b.ingestTable(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{msg(chat, "m3", 7, now+10)}}, modeLive)
	if c = lastChat(t, h.drain()); c["unread"] != float64(1) {
		t.Fatalf("unread after m3 = %v", c["unread"])
	}
	b.ingestTable(&table.LSTable{
		LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{{ThreadKey: chat, ContactId: 1, ReadWatermarkTimestampMs: now + 10}},
	}, modeLive)
	if c = lastChat(t, h.drain()); c["unread"] != float64(0) {
		t.Fatalf("unread after own read receipt = %v", c["unread"])
	}

	// 6. Peer's read receipt must not touch unread.
	b.ingestTable(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{msg(chat, "m4", 7, now+20)}}, modeLive)
	h.drain()
	b.ingestTable(&table.LSTable{
		LSUpdateReadReceipt: []*table.LSUpdateReadReceipt{{ThreadKey: chat, ContactId: 7, ReadWatermarkTimestampMs: now + 20}},
	}, modeLive)
	if th, _ := b.st.thread("100"); th.Unread != 1 {
		t.Fatalf("peer receipt changed unread: %d", th.Unread)
	}

	// 7. Own message from another device: fromMe, not an unread, resets unread to 0.
	b.ingestTable(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{msg(chat, "m5", 1, now+30)}}, modeLive)
	fr = h.drain()
	mn = events_(fr, "message.new")
	if len(mn) != 1 || mn[0]["message"].(map[string]any)["fromMe"] != true {
		t.Fatalf("own message: %v", mn)
	}
	if c = lastChat(t, fr); c["unread"] != float64(0) {
		t.Fatalf("unread after own message = %v", c["unread"])
	}

	// 8. Mute changes emit chat.upsert; an expiry in the past is "not muted".
	until := now + 3_600_000
	b.ingestTable(&table.LSTable{LSUpdateThreadMuteSetting: []*table.LSUpdateThreadMuteSetting{{ThreadKey: chat, MuteExpireTimeMS: until}}}, modeLive)
	if c = lastChat(t, h.drain()); c["muted"] != true || c["mutedUntil"] != float64(until) {
		t.Fatalf("mute until: %v", c)
	}
	b.ingestTable(&table.LSTable{LSUpdateThreadMuteSetting: []*table.LSUpdateThreadMuteSetting{{ThreadKey: chat, MuteExpireTimeMS: 0}}}, modeLive)
	c = lastChat(t, h.drain())
	if _, has := c["mutedUntil"]; has || c["muted"] == true {
		t.Fatalf("unmute: %v", c)
	}
	b.ingestTable(&table.LSTable{LSUpdateThreadMuteSetting: []*table.LSUpdateThreadMuteSetting{{ThreadKey: chat, MuteExpireTimeMS: now - 1000}}}, modeLive)
	h.drain()
	if th, _ := b.st.thread("100"); th.MuteUntil != now-1000 {
		t.Fatalf("expired mute not stored")
	}
	if v := b.chatView(mustThread(t, b, "100")); v.Muted || v.MutedUntil != 0 {
		t.Fatalf("expired mute shown as muted: %+v", v)
	}

	// 9. Non-live paths never carry live:true: initial mode and a disconnected socket.
	b.ingestTable(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{msg(chat, "m6", 7, now+40)}}, modeInitial)
	if len(events_(h.drain(), "message.new")) != 0 {
		t.Fatalf("modeInitial must not announce messages")
	}
	b.socketUp.Store(false)
	b.ingestTable(&table.LSTable{LSInsertMessage: []*table.LSInsertMessage{msg(chat, "m7", 7, now+50)}}, modeLive)
	if mn = events_(h.drain(), "message.new"); len(mn) != 1 || mn[0]["live"] != false {
		t.Fatalf("socket down: %v", mn)
	}
}

func mustThread(t *testing.T, b *bridge, id string) threadRec {
	t.Helper()
	th, ok := b.st.thread(id)
	if !ok {
		t.Fatalf("thread %s missing", id)
	}
	return th
}

// A server count in the same table as its messages must not be counted twice.
func TestServerUnreadNotDoubleCounted(t *testing.T) {
	h := newHarness(t)
	h.b.socketUp.Store(true)
	now := time.Now().UnixMilli()
	h.b.ingestTable(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{
			ThreadKey: 9, ThreadType: table.ONE_TO_ONE, LastActivityTimestampMs: now,
			LastReadWatermarkTimestampMs: now - 100, UnreadMessageCount: 1,
		}},
		LSInsertMessage: []*table.LSInsertMessage{msg(9, "a", 7, now)},
	}, modeLive)
	if c := lastChat(t, h.drain()); c["unread"] != float64(1) {
		t.Fatalf("unread = %v, want 1", c["unread"])
	}
}

// The E2EE self-read receipt used to take the store lock twice and hang.
func TestE2EEReadSelfDoesNotDeadlock(t *testing.T) {
	h := newHarness(t)
	b := h.b
	now := time.Now().UnixMilli()
	b.st.updateThread("200", func(th *threadRec) { th.Key, th.E2EE, th.LastActivity, th.Unread = 200, true, now, 2 })
	b.st.putMessage(&storedMsg{Message: Message{ID: "x1", ChatID: "200", SenderID: "8", Timestamp: now - 10}})
	b.st.putMessage(&storedMsg{Message: Message{ID: "x2", ChatID: "200", SenderID: "8", Timestamp: now}})
	e := &e2eeState{b: b}
	done := make(chan struct{})
	go func() {
		defer close(done)
		e.onReceipt(&events.Receipt{
			MessageSource: types.MessageSource{Chat: types.JID{User: "200", Server: types.MessengerServer}},
			MessageIDs:    []types.MessageID{"x2"},
			Type:          types.ReceiptTypeReadSelf,
		})
	}()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("onReceipt deadlocked")
	}
	if c := lastChat(t, h.drain()); c["unread"] != float64(0) {
		t.Fatalf("unread after e2ee read-self = %v", c["unread"])
	}
}
