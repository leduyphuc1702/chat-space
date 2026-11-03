package main

import (
	"encoding/json"
	"reflect"
	"slices"
	"strings"
	"testing"
	"time"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func pinIDs(msgs []Message) string {
	ids := make([]string, 0, len(msgs))
	for _, m := range msgs {
		ids = append(ids, m.ID)
	}
	return strings.Join(ids, ",")
}

func setPin(thread int64, id string, at int64) *table.LSSetPinnedMessage {
	return &table.LSSetPinnedMessage{ThreadKey: thread, MessageId: id, PinnedTimestampMs: at, AuthorityLevel: 1}
}

func mustPins(t *testing.T, b *bridge, chat string) []Message {
	t.Helper()
	msgs, err := b.chatPins(chat)
	if err != nil {
		t.Fatalf("chatPins(%s): %v", chat, err)
	}
	return msgs
}

// pinUpdates returns the chatIds of the pins.update frames in frames.
func pinUpdates(frames []map[string]any) []string {
	var out []string
	for _, ev := range events_(frames, "pins.update") {
		out = append(out, ev["chatId"].(string))
	}
	return out
}

func TestPinsFromSyncTables(t *testing.T) {
	h := newHarness(t)
	b := h.b
	b.state = stateLoggedIn
	now := time.Now().UnixMilli()
	const chat = int64(100)

	b.ingestTable(&table.LSTable{
		LSVerifyThreadExists: []*table.LSVerifyThreadExists{{ThreadKey: chat, ThreadType: table.ONE_TO_ONE}},
		LSInsertMessage:      []*table.LSInsertMessage{msg(chat, "m1", 7, now-2000), msg(chat, "m2", 7, now-1000)},
	}, modeLive)
	h.drain()
	if got := mustPins(t, b, "100"); len(got) != 0 {
		t.Fatalf("unpinned chat must return no pins: %v", got)
	}

	// 1. Newest pin first (by pin time, not message time); a pin whose message is not
	// stored is a minimal placeholder at its pin time.
	b.ingestTable(&table.LSTable{LSSetPinnedMessage: []*table.LSSetPinnedMessage{
		setPin(chat, "m2", 1000), setPin(chat, "m1", 3000), setPin(chat, "gone", 2000),
	}}, modeLive)
	fr := h.drain()
	if got := pinUpdates(fr); !slices.Equal(got, []string{"100"}) {
		t.Fatalf("pins.update = %v", got)
	}
	pins := mustPins(t, b, "100")
	if pinIDs(pins) != "m1,gone,m2" {
		t.Fatalf("order = %s", pinIDs(pins))
	}
	if pins[0].Text != "hi m1" || pins[0].SenderID != "7" {
		t.Fatalf("stored message not resolved: %+v", pins[0])
	}
	if want := (Message{ID: "gone", ChatID: "100", Timestamp: 2000, Kind: "unsupported"}); !reflect.DeepEqual(pins[1], want) {
		t.Fatalf("placeholder = %+v", pins[1])
	}
	raw, _ := json.Marshal(pins[1])
	if !strings.Contains(string(raw), `"senderId":""`) || !strings.Contains(string(raw), `"fromMe":false`) {
		t.Fatalf("placeholder json = %s", raw)
	}

	// 2. A sync that repeats the current state (clear + the same sets) is silent.
	b.ingestTable(&table.LSTable{
		LSClearPinnedMessages: []*table.LSClearPinnedMessages{{ThreadKey: chat}},
		LSSetPinnedMessage:    []*table.LSSetPinnedMessage{setPin(chat, "m2", 1000), setPin(chat, "m1", 3000), setPin(chat, "gone", 2000)},
	}, modeLive)
	if got := pinUpdates(h.drain()); len(got) != 0 {
		t.Fatalf("unchanged pins emitted pins.update: %v", got)
	}

	// 3. A set without a timestamp unpins that message.
	b.ingestTable(&table.LSTable{LSSetPinnedMessage: []*table.LSSetPinnedMessage{setPin(chat, "m1", 0)}}, modeLive)
	if got := pinUpdates(h.drain()); !slices.Equal(got, []string{"100"}) {
		t.Fatalf("unpin pins.update = %v", got)
	}
	if got := pinIDs(mustPins(t, b, "100")); got != "gone,m2" {
		t.Fatalf("after unpin = %s", got)
	}
	b.ingestTable(&table.LSTable{LSSetPinnedMessage: []*table.LSSetPinnedMessage{setPin(chat, "never-pinned", 0)}}, modeLive)
	if got := pinUpdates(h.drain()); len(got) != 0 {
		t.Fatalf("unpinning an unpinned message emitted pins.update: %v", got)
	}

	// 4. Clear and sets in one table replace the thread's pins, whatever the field order.
	b.ingestTable(&table.LSTable{
		LSSetPinnedMessage:    []*table.LSSetPinnedMessage{setPin(chat, "m1", 5000)},
		LSClearPinnedMessages: []*table.LSClearPinnedMessages{{ThreadKey: chat}},
	}, modeLive)
	if got := pinIDs(mustPins(t, b, "100")); got != "m1" {
		t.Fatalf("replacement = %s", got)
	}
	h.drain()

	// 5. Pins survive a restart.
	if err := b.st.flush(); err != nil {
		t.Fatal(err)
	}
	reloaded := newStore(b.dataDir, zerologNop())
	if err := reloaded.load(); err != nil {
		t.Fatal(err)
	}
	if got := reloaded.pinList("100"); !slices.Equal(got, []pinRec{{ID: "m1", At: 5000}}) {
		t.Fatalf("reloaded pins = %v", got)
	}

	// 6. Clearing empties the list (once), and clearing again says nothing.
	clearTbl := &table.LSTable{LSClearPinnedMessages: []*table.LSClearPinnedMessages{{ThreadKey: chat}}}
	b.ingestTable(clearTbl, modeLive)
	if got := pinUpdates(h.drain()); !slices.Equal(got, []string{"100"}) {
		t.Fatalf("clear pins.update = %v", got)
	}
	if got := mustPins(t, b, "100"); got == nil || len(got) != 0 {
		t.Fatalf("cleared chat must return an empty list, got %#v", got)
	}
	b.ingestTable(clearTbl, modeLive)
	if got := pinUpdates(h.drain()); len(got) != 0 {
		t.Fatalf("clearing nothing emitted pins.update: %v", got)
	}

	// 7. Unknown chat.
	if _, err := b.chatPins("404"); bridgeCode(err) != codeNotFound {
		t.Fatalf("unknown chat err = %v", err)
	}
}

func TestPinsFollowThreadLifecycle(t *testing.T) {
	h := newHarness(t)
	b := h.b
	b.state = stateLoggedIn
	b.ingestTable(&table.LSTable{
		LSVerifyThreadExists: []*table.LSVerifyThreadExists{{ThreadKey: 100, ThreadType: table.ONE_TO_ONE}},
		LSSetPinnedMessage:   []*table.LSSetPinnedMessage{setPin(100, "m1", 10), setPin(0, "x", 10), setPin(100, "", 10)},
	}, modeLive)
	if got := b.st.pinList("100"); len(got) != 1 {
		t.Fatalf("pins = %v (rows without a thread key or message id must be ignored)", got)
	}
	b.ingestTable(&table.LSTable{LSDeleteThread: []*table.LSDeleteThread{{ThreadKey: 100}}}, modeLive)
	if got := b.st.pinList("100"); len(got) != 0 {
		t.Fatalf("deleted thread kept pins: %v", got)
	}
}

// Pins of the pre-encryption thread of an encrypted 1:1 chat belong to the encrypted
// chat, like its messages (openChat).
func TestPinsOfOpenThreadAreFiledUnderEncryptedChat(t *testing.T) {
	h := newHarness(t)
	b := h.b
	b.state = stateLoggedIn
	b.st.updateThread("900", func(th *threadRec) { th.Key, th.E2EE = 900, true })
	b.st.mapJID("500", "900") // 500 = the peer, whose id keys the unencrypted thread
	b.ingestTable(&table.LSTable{LSSetPinnedMessage: []*table.LSSetPinnedMessage{setPin(500, "old", 10)}}, modeLive)
	if got := pinUpdates(h.drain()); !slices.Equal(got, []string{"900"}) {
		t.Fatalf("pins.update = %v", got)
	}
	if got := b.st.pinList("900"); !slices.Equal(got, []pinRec{{ID: "old", At: 10}}) {
		t.Fatalf("encrypted chat pins = %v", got)
	}
	if got := b.st.pinList("500"); len(got) != 0 {
		t.Fatalf("pins left under the open thread: %v", got)
	}
}
