package main

import (
	"context"
	"encoding/json"
	"reflect"
	"testing"
	"unicode/utf16"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

// utf16Index is where `needle` starts in `s`, counted in UTF-16 code units (what the app sends).
func utf16Index(t *testing.T, s, needle string) int {
	t.Helper()
	hay, pat := utf16.Encode([]rune(s)), utf16.Encode([]rune(needle))
	for i := 0; i+len(pat) <= len(hay); i++ {
		if reflect.DeepEqual(hay[i:i+len(pat)], pat) {
			return i
		}
	}
	t.Fatalf("%q not found in %q", needle, s)
	return -1
}

func rawMentions(t *testing.T, v any) json.RawMessage {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestParseMentionsKeepsValidSortedSpans(t *testing.T) {
	text := "hi @Ann and @Bob!"
	got := parseMentions(rawMentions(t, []map[string]any{
		{"userId": "200", "offset": 12, "length": 4},
		{"userId": "100", "offset": 3, "length": 4},
	}), text)
	want := []mentionSpan{{"100", 3, 4}, {"200", 12, 4}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, want %+v", got, want)
	}
}

func TestParseMentionsDropsInvalid(t *testing.T) {
	text := "hi @Ann and @Bob!"
	got := parseMentions(rawMentions(t, []any{
		map[string]any{"userId": "", "offset": 3, "length": 4},
		map[string]any{"userId": "abc", "offset": 3, "length": 4},
		map[string]any{"userId": "-5", "offset": 3, "length": 4},
		map[string]any{"userId": "007", "offset": 3, "length": 4}, // not canonical
		map[string]any{"userId": "1", "offset": -1, "length": 4},
		map[string]any{"userId": "1", "offset": 3, "length": 0},
		map[string]any{"userId": "1", "offset": 1.5, "length": 2},
		map[string]any{"userId": "1", "offset": 15, "length": 10}, // runs past the end
		map[string]any{"userId": 42, "offset": 3, "length": 4},    // id must be a string
		"junk",
		nil,
		map[string]any{"userId": "100", "offset": 3, "length": 4},
		map[string]any{"userId": "300", "offset": 5, "length": 4}, // overlaps the previous one
	}), text)
	want := []mentionSpan{{"100", 3, 4}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, want %+v", got, want)
	}
	if parseMentions(nil, text) != nil || parseMentions(rawMentions(t, "x"), text) != nil ||
		parseMentions(rawMentions(t, []any{map[string]any{"userId": "1", "offset": 0, "length": 1}}), "") != nil {
		t.Fatal("missing / non-array mentions and empty text must give nothing")
	}
}

// Meta indexes UTF-16 code units, and so does the protocol: the numbers must go through
// untouched even when emoji (surrogate pairs) and diacritics precede the mention.
func TestMentionOffsetsAreUTF16(t *testing.T) {
	text := "Chào 😀 @Nguyễn Văn A, xem cái này"
	name := "@Nguyễn Văn A"
	off := utf16Index(t, text, name)
	length := len(utf16.Encode([]rune(name)))
	// Sanity: UTF-16 and byte offsets really differ here, so a byte-based implementation would fail.
	if byteOff := len("Chào 😀 "); byteOff == off {
		t.Fatalf("test text does not distinguish bytes from UTF-16 units")
	}

	spans := parseMentions(rawMentions(t, []map[string]any{{"userId": "61500000001", "offset": off, "length": length}}), text)
	if len(spans) != 1 || spans[0].Offset != off || spans[0].Length != length {
		t.Fatalf("span changed: %+v (want offset %d length %d)", spans, off, length)
	}
	md := buildMentionData(spans)
	if md == nil || md.MentionOffsets != itoa(off) || md.MentionLengths != itoa(length) {
		t.Fatalf("MentionData offsets not passed through unchanged: %+v", md)
	}
	units := utf16.Encode([]rune(text))
	if got := string(utf16.Decode(units[off : off+length])); got != name {
		t.Fatalf("span covers %q, want %q", got, name)
	}
}

func TestParseMentionsRejectsSplitSurrogatePair(t *testing.T) {
	text := "a😀b" // units: a, high, low, b
	for _, c := range []struct{ off, length int }{{2, 1}, {0, 2}} {
		got := parseMentions(rawMentions(t, []map[string]any{{"userId": "1", "offset": c.off, "length": c.length}}), text)
		if got != nil {
			t.Fatalf("span %+v splits a surrogate pair but was kept: %+v", c, got)
		}
	}
	if got := parseMentions(rawMentions(t, []map[string]any{{"userId": "1", "offset": 1, "length": 2}}), text); len(got) != 1 {
		t.Fatalf("a span over the whole pair must be kept, got %+v", got)
	}
}

func itoa(n int) string {
	b, _ := json.Marshal(n)
	return string(b)
}

func TestBuildMentionData(t *testing.T) {
	if buildMentionData(nil) != nil {
		t.Fatal("no spans must give nil MentionData")
	}
	got := buildMentionData([]mentionSpan{{"100", 3, 4}, {"200", 12, 5}})
	want := &socket.MentionData{MentionIDs: "100,200", MentionOffsets: "3,12", MentionLengths: "4,5", MentionTypes: "p,p"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v, want %+v", got, want)
	}
	// It must round-trip through the parser mautrix-meta uses for incoming mentions.
	ms, err := got.Parse()
	if err != nil || len(ms) != 2 || ms[1].ID != 200 || ms[1].Offset != 12 || ms[1].Length != 5 || ms[1].Type != socket.MentionTypePerson {
		t.Fatalf("round trip failed: %+v, %v", ms, err)
	}
	// The task carries it as mention_data.
	task := &socket.SendMessageTask{Text: "x", MentionData: got}
	raw, _ := json.Marshal(task)
	var back map[string]any
	_ = json.Unmarshal(raw, &back)
	md, _ := back["mention_data"].(map[string]any)
	if md["mention_ids"] != "100,200" || md["mention_types"] != "p,p" {
		t.Fatalf("mention_data missing from the task JSON: %s", raw)
	}
}

func TestRewriteE2EEMentions(t *testing.T) {
	text := "😀 @Ann Lee, meet @Bob"
	spans := []mentionSpan{
		{"100", utf16Index(t, text, "@Ann Lee"), len(utf16.Encode([]rune("@Ann Lee")))},
		{"200", utf16Index(t, text, "@Bob"), len("@Bob")},
	}
	got, jids := rewriteE2EEMentions(text, spans)
	if want := "😀 @100@msgr, meet @200@msgr"; got != want {
		t.Fatalf("text = %q, want %q", got, want)
	}
	if want := []string{"100@msgr", "200@msgr"}; !reflect.DeepEqual(jids, want) {
		t.Fatalf("jids = %v, want %v", jids, want)
	}

	// The same person twice is listed once.
	dup := []mentionSpan{{"100", 0, 2}, {"100", 3, 2}}
	if got, jids := rewriteE2EEMentions("@A @A", dup); got != "@100@msgr @100@msgr" || !reflect.DeepEqual(jids, []string{"100@msgr"}) {
		t.Fatalf("dup: %q %v", got, jids)
	}
	if got, jids := rewriteE2EEMentions("plain", nil); got != "plain" || jids != nil {
		t.Fatalf("no spans: %q %v", got, jids)
	}
}

func TestParamsMentions(t *testing.T) {
	p := params{"mentions": rawMentions(t, []map[string]any{{"userId": "7", "offset": 0, "length": 3}})}
	if got := p.mentions("@Al ok"); len(got) != 1 || got[0].UserID != "7" {
		t.Fatalf("got %+v", got)
	}
	if got := (params{}).mentions("@Al ok"); got != nil {
		t.Fatalf("absent param: %+v", got)
	}
}

// chats.members: stored participants, names/avatars from contacts, self excluded, full
// resyncs replace the list, removals apply, and everything survives a restart.
func TestChatMembers(t *testing.T) {
	h := newHarness(t)
	b := h.b
	b.state = stateLoggedIn
	b.self = &SelfInfo{ID: "1", Name: "Me"}
	const group = int64(500)
	const dm = int64(600)

	b.ingestTable(&table.LSTable{
		LSDeleteThenInsertContact: []*table.LSDeleteThenInsertContact{
			{Id: 2, Name: "Zed Zebra", ProfilePictureUrl: "https://cdn/zed.jpg"},
			{Id: 3, Name: "alice Adams"},
			{Id: dm, Name: "Dana Peer", ProfilePictureUrl: "https://cdn/dana.jpg"},
		},
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{
			{ThreadKey: group, ThreadType: table.GROUP_THREAD, ThreadName: "Team", MemberCount: 4, LastActivityTimestampMs: 10},
			{ThreadKey: dm, ThreadType: table.ONE_TO_ONE, LastActivityTimestampMs: 10},
		},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: group, ContactId: 1},
			{ThreadKey: group, ContactId: 2},
			{ThreadKey: group, ContactId: 3},
			{ThreadKey: group, ContactId: 4}, // no contact row: falls back to the id
		},
	}, modeInitial)

	ctx := context.Background()
	got, err := b.chatMembers(ctx, chatKey(group))
	if err != nil {
		t.Fatal(err)
	}
	// Case-insensitive by name; the member without a contact row (just its id) is last.
	want := []Member{{ID: "3", Name: "alice Adams"}, {ID: "2", Name: "Zed Zebra", Avatar: "https://cdn/zed.jpg"}, {ID: "4", Name: "4"}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("members = %+v, want %+v", got, want)
	}

	// 1:1 chat: the other participant only.
	got, err = b.chatMembers(ctx, chatKey(dm))
	if err != nil || !reflect.DeepEqual(got, []Member{{ID: "600", Name: "Dana Peer", Avatar: "https://cdn/dana.jpg"}}) {
		t.Fatalf("dm members = %+v, %v", got, err)
	}

	// A participant leaves; another joins.
	b.ingestTable(&table.LSTable{
		LSRemoveParticipantFromThread:   []*table.LSRemoveParticipantFromThread{{ThreadKey: group, ParticipantId: 2}},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{{ThreadKey: group, ContactId: 5}},
	}, modeLive)
	ids := b.st.memberIDs(chatKey(group))
	if !reflect.DeepEqual(ids, []string{"1", "3", "4", "5"}) {
		t.Fatalf("after leave/join: %v", ids)
	}

	// A complete resync (as many rows as MemberCount) replaces the list; a partial one only adds.
	b.ingestTable(&table.LSTable{
		LSDeleteThenInsertThread: []*table.LSDeleteThenInsertThread{{ThreadKey: group, ThreadType: table.GROUP_THREAD, MemberCount: 2}},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{
			{ThreadKey: group, ContactId: 1}, {ThreadKey: group, ContactId: 3},
		},
	}, modeInitial)
	if ids := b.st.memberIDs(chatKey(group)); !reflect.DeepEqual(ids, []string{"1", "3"}) {
		t.Fatalf("after full resync: %v", ids)
	}
	b.ingestTable(&table.LSTable{
		LSDeleteThenInsertThread:        []*table.LSDeleteThenInsertThread{{ThreadKey: group, ThreadType: table.GROUP_THREAD, MemberCount: 5}},
		LSAddParticipantIdToGroupThread: []*table.LSAddParticipantIdToGroupThread{{ThreadKey: group, ContactId: 9}},
	}, modeInitial)
	if ids := b.st.memberIDs(chatKey(group)); !reflect.DeepEqual(ids, []string{"1", "3", "9"}) {
		t.Fatalf("after partial resync: %v", ids)
	}

	// Persisted and reloaded.
	if err := b.st.flush(); err != nil {
		t.Fatal(err)
	}
	reloaded := newStore(b.dataDir, b.log)
	if err := reloaded.load(); err != nil {
		t.Fatal(err)
	}
	if ids := reloaded.memberIDs(chatKey(group)); !reflect.DeepEqual(ids, []string{"1", "3", "9"}) {
		t.Fatalf("after reload: %v", ids)
	}

	// Deleting the thread forgets its members.
	b.st.removeThread(chatKey(group))
	if ids := b.st.memberIDs(chatKey(group)); len(ids) != 0 {
		t.Fatalf("members of a removed thread: %v", ids)
	}

	if _, err := b.chatMembers(ctx, "999999"); err == nil {
		t.Fatal("unknown chat must be not_found")
	}
}
