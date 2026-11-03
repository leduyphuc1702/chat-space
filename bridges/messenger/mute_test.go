package main

import (
	"context"
	"encoding/json"
	"errors"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func zerologNop() zerolog.Logger { return zerolog.Nop() }

func bridgeCode(err error) string {
	var be *bridgeError
	if errors.As(err, &be) {
		return be.Code
	}
	if err != nil {
		return "other:" + err.Error()
	}
	return ""
}

func muteSetting(thread, until int64) *table.LSTable {
	return &table.LSTable{LSUpdateThreadMuteSetting: []*table.LSUpdateThreadMuteSetting{{ThreadKey: thread, MuteExpireTimeMS: until}}}
}

// A mute changed on the phone (LSUpdateThreadMuteSetting) reaches the app as chat.upsert,
// for Facebook threads and for encrypted ones (keyed by their hybrid thread key).
func TestMuteSettingFromMeta(t *testing.T) {
	h := newHarness(t)
	b := h.b
	now := time.Now().UnixMilli()
	b.ingestTable(&table.LSTable{
		LSVerifyThreadExists:       []*table.LSVerifyThreadExists{{ThreadKey: 100, ThreadType: table.ONE_TO_ONE}},
		LSVerifyHybridThreadExists: []*table.LSVerifyHybridThreadExists{{ThreadKey: 700, ThreadJID: 555}},
	}, modeLive)
	h.drain()
	if th := mustThread(t, b, "700"); !th.E2EE || th.Key != 700 {
		t.Fatalf("hybrid thread = %+v", th)
	}

	for _, chat := range []int64{100, 700} {
		id := chatKey(chat)
		until := now + 3_600_000
		steps := []struct {
			until int64
			muted bool
		}{{-1, true}, {until, true}, {0, false}}
		for _, s := range steps {
			b.ingestTable(muteSetting(chat, s.until), modeLive)
			c := lastChat(t, h.drain())
			if c["id"] != id || (c["muted"] == true) != s.muted {
				t.Fatalf("chat %s until %d: %v", id, s.until, c)
			}
			if s.muted && c["mutedUntil"] != float64(s.until) {
				t.Fatalf("chat %s until %d: mutedUntil = %v", id, s.until, c["mutedUntil"])
			}
			if _, has := c["mutedUntil"]; !s.muted && has {
				t.Fatalf("chat %s unmuted but mutedUntil = %v", id, c["mutedUntil"])
			}
		}
		// The same setting again changes nothing, so nothing is sent.
		b.ingestTable(muteSetting(chat, 0), modeLive)
		if fr := h.drain(); len(fr) != 0 {
			t.Fatalf("repeated mute setting emitted %v", fr)
		}
	}

	// A thread we do not know yet gets its mute with the full thread row.
	b.ingestTable(muteSetting(999, -1), modeLive)
	if fr := h.drain(); len(fr) != 0 {
		t.Fatalf("unknown thread emitted %v", fr)
	}
	if _, ok := b.st.thread("999"); ok {
		t.Fatalf("mute setting created a thread")
	}
}

func TestMuteThreadKey(t *testing.T) {
	cases := []struct {
		name string
		th   threadRec
		key  int64
		code string
	}{
		{"facebook thread by key", threadRec{ID: "100", Key: 100}, 100, ""},
		{"facebook thread by id", threadRec{ID: "123"}, 123, ""},
		{"facebook thread without a number", threadRec{ID: "abc"}, 0, codeUnsupported},
		{"encrypted with hybrid thread", threadRec{ID: "700", Key: 700, E2EE: true}, 700, ""},
		// The id of an encrypted-only chat is the JID user, i.e. the peer's id for a 1:1 chat:
		// the key of the unencrypted thread. It must not be used as the thread to mute.
		{"encrypted-only 1:1", threadRec{ID: "555", E2EE: true, JID: "555@msgr"}, 0, codeUnsupported},
		{"encrypted-only group", threadRec{ID: "888", E2EE: true, IsGroup: true, JID: "888@g.us"}, 0, codeUnsupported},
	}
	for _, c := range cases {
		key, err := muteThreadKey(c.th)
		if key != c.key || bridgeCode(err) != c.code {
			t.Errorf("%s: key %d err %v, want %d %q", c.name, key, err, c.key, c.code)
		}
	}
}

// Every refusal happens before anything is sent to Meta and leaves the chat as it was.
func TestMuteChatRefusals(t *testing.T) {
	h := newHarness(t)
	b := h.b
	ctx := context.Background()
	b.st.updateThread("100", func(th *threadRec) { th.Key = 100 })
	b.st.updateThread("555", func(th *threadRec) { th.E2EE, th.JID = true, "555@msgr" })
	b.st.updateThread("700", func(th *threadRec) { th.Key, th.E2EE = 700, true })

	if _, err := b.muteChat(ctx, "100", -2); bridgeCode(err) != codeInvalidParams {
		t.Fatalf("until -2: %v", err)
	}
	if _, err := b.muteChat(ctx, "100", -1); bridgeCode(err) != codeNotLoggedIn {
		t.Fatalf("logged out: %v", err)
	}
	b.state = stateLoggedIn
	if _, err := b.muteChat(ctx, "404", -1); bridgeCode(err) != codeNotFound {
		t.Fatalf("unknown chat: %v", err)
	}
	_, err := b.muteChat(ctx, "555", -1)
	if bridgeCode(err) != codeUnsupported || !strings.Contains(err.Error(), "end-to-end encrypted") {
		t.Fatalf("encrypted-only chat: %v", err)
	}
	// With a usable key the next gate is the live client, which this harness does not have.
	for _, chat := range []string{"100", "700"} {
		if _, err := b.muteChat(ctx, chat, -1); bridgeCode(err) != codeInternal || !strings.Contains(err.Error(), "reconnecting") {
			t.Fatalf("chat %s without a client: %v", chat, err)
		}
	}
	for _, chat := range []string{"100", "555", "700"} {
		if th := mustThread(t, b, chat); th.MuteUntil != 0 {
			t.Fatalf("refused mute changed chat %s: %+v", chat, th)
		}
	}
	if fr := h.drain(); len(fr) != 0 {
		t.Fatalf("refused mute emitted %v", fr)
	}
}

func TestIntegerParam(t *testing.T) {
	cases := []struct {
		raw string
		n   int64
		ok  bool
	}{
		{"-1", -1, true}, {"0", 0, true}, {"1762000000000", 1762000000000, true}, {"1.5", 0, false},
		{`"5"`, 0, false}, {"null", 0, false}, {"true", 0, false}, {"1e300", 0, false},
	}
	for _, c := range cases {
		n, ok := params{"until": json.RawMessage(c.raw)}.integer("until")
		if n != c.n || ok != c.ok {
			t.Errorf("integer(%s) = %d, %v; want %d, %v", c.raw, n, ok, c.n, c.ok)
		}
	}
	if _, ok := (params{}).integer("until"); ok {
		t.Errorf("a missing param must not be ok")
	}
}

func TestMuteAndPinsAreRouted(t *testing.T) {
	if !slices.Contains(capabilities, "mute") || !slices.Contains(capabilities, "pins") {
		t.Fatalf("capabilities = %v", capabilities)
	}
	h := newHarness(t)
	h.b.state = stateLoggedIn
	h.b.st.updateThread("100", func(th *threadRec) { th.Key = 100 })
	routes := (&server{b: h.b, out: h.b.out}).routes()
	ctx := context.Background()
	str := func(s string) json.RawMessage { b, _ := json.Marshal(s); return b }

	mute := routes["chats.mute"]
	for name, p := range map[string]params{
		"no chatId":   {"until": json.RawMessage("-1")},
		"no until":    {"chatId": str("100")},
		"text until":  {"chatId": str("100"), "until": str("soon")},
		"fraction":    {"chatId": str("100"), "until": json.RawMessage("1.5")},
		"until below": {"chatId": str("100"), "until": json.RawMessage("-5")},
	} {
		if _, err := mute(ctx, p); bridgeCode(err) != codeInvalidParams {
			t.Errorf("chats.mute %s: %v", name, err)
		}
	}

	res, err := routes["chats.pins"](ctx, params{"chatId": str("100")})
	if err != nil {
		t.Fatal(err)
	}
	if raw, _ := json.Marshal(res); string(raw) != `{"messages":[]}` {
		t.Fatalf("chats.pins = %s", raw)
	}
	if _, err := routes["chats.pins"](ctx, params{}); bridgeCode(err) != codeInvalidParams {
		t.Fatalf("chats.pins without chatId: %v", err)
	}
}
