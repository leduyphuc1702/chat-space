package main

import (
	"context"
	"strings"
	"testing"
	"time"

	armadillo "go.mau.fi/whatsmeow/proto"
	"go.mau.fi/whatsmeow/proto/waArmadilloApplication"
	"go.mau.fi/whatsmeow/proto/waArmadilloXMA"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	"github.com/rs/zerolog"
)

func e2eeFixture(t *testing.T) (*harness, *e2eeState) {
	t.Helper()
	h := newHarness(t)
	h.b.self = &SelfInfo{ID: "1", Name: "Me"}
	return h, &e2eeState{b: h.b, ctx: context.Background()}
}

func fbEvent(id, chatUser, sender string, ts int64, app any) *events.FBMessage {
	chat := types.JID{User: chatUser, Server: types.MessengerServer}
	evt := &events.FBMessage{Info: types.MessageInfo{
		MessageSource: types.MessageSource{Chat: chat, Sender: types.JID{User: sender, Server: types.MessengerServer}},
		ID:            id,
		Timestamp:     time.UnixMilli(ts),
		PushName:      "Push " + sender,
	}}
	switch v := app.(type) {
	case *waConsumerApplication.ConsumerApplication:
		evt.Message = v
	case *waArmadilloApplication.Armadillo:
		evt.Message = v
	case *armadillo.Unsupported_Voip:
		evt.Message = v
	default:
		panic("unknown payload")
	}
	return evt
}

func consumerContent(c *waConsumerApplication.ConsumerApplication_Content) *waConsumerApplication.ConsumerApplication {
	return wrapContent(c)
}

func consumerText(text string, jids ...string) *waConsumerApplication.ConsumerApplication {
	return consumerContent(&waConsumerApplication.ConsumerApplication_Content{
		Content: &waConsumerApplication.ConsumerApplication_Content_MessageText{
			MessageText: &waCommon.MessageText{Text: proto.String(text), MentionedJID: jids},
		},
	})
}

func armadilloContent(c *waArmadilloApplication.Armadillo_Content) *waArmadilloApplication.Armadillo {
	return &waArmadilloApplication.Armadillo{Payload: &waArmadilloApplication.Armadillo_Payload{
		Payload: &waArmadilloApplication.Armadillo_Payload_Content{Content: c},
	}}
}

func xma(x *waArmadilloXMA.ExtendedContentMessage) *waArmadilloApplication.Armadillo {
	return armadilloContent(&waArmadilloApplication.Armadillo_Content{
		Content: &waArmadilloApplication.Armadillo_Content_ExtendedContentMessage{ExtendedContentMessage: x},
	})
}

func imageTransport(path string) *waMediaTransport.ImageTransport {
	return &waMediaTransport.ImageTransport{
		Integral: &waMediaTransport.ImageTransport_Integral{Transport: &waMediaTransport.WAMediaTransport{
			Integral:  &waMediaTransport.WAMediaTransport_Integral{DirectPath: proto.String(path), MediaKey: []byte{1}},
			Ancillary: &waMediaTransport.WAMediaTransport_Ancillary{Mimetype: proto.String("image/jpeg")},
		}},
		Ancillary: &waMediaTransport.ImageTransport_Ancillary{Width: proto.Uint32(30), Height: proto.Uint32(20)},
	}
}

// Everything that only carries protocol state must not produce a bubble, and the rest must
// map to the right kind instead of the old "Unsupported encrypted message".
func TestClassifyProtocolOnlyPayloadsMakeNoBubble(t *testing.T) {
	_, e := e2eeFixture(t)
	revoke := &waConsumerApplication.ConsumerApplication{Payload: &waConsumerApplication.ConsumerApplication_Payload{
		Payload: &waConsumerApplication.ConsumerApplication_Payload_ApplicationData{
			ApplicationData: &waConsumerApplication.ConsumerApplication_ApplicationData{
				ApplicationContent: &waConsumerApplication.ConsumerApplication_ApplicationData_Revoke{
					Revoke: &waConsumerApplication.ConsumerApplication_RevokeMessage{Key: &waCommon.MessageKey{ID: proto.String("gone")}},
				}}}}}
	if act := e.classify(fbEvent("m", "9", "9", 1, revoke), "9", "Ann"); act.revokeID != "gone" || len(act.parts) != 0 {
		t.Fatalf("revoke: %+v", act)
	}
	reaction := consumerContent(&waConsumerApplication.ConsumerApplication_Content{Content: &waConsumerApplication.ConsumerApplication_Content_ReactionMessage{
		ReactionMessage: &waConsumerApplication.ConsumerApplication_ReactionMessage{Text: proto.String("x")}}})
	if act := e.classify(fbEvent("m", "9", "9", 1, reaction), "9", "Ann"); act.reaction == nil || len(act.parts) != 0 {
		t.Fatalf("reaction: %+v", act)
	}
	edit := consumerContent(&waConsumerApplication.ConsumerApplication_Content{Content: &waConsumerApplication.ConsumerApplication_Content_EditMessage{
		EditMessage: &waConsumerApplication.ConsumerApplication_EditMessage{}}})
	if act := e.classify(fbEvent("m", "9", "9", 1, edit), "9", "Ann"); act.edit == nil || len(act.parts) != 0 {
		t.Fatalf("edit: %+v", act)
	}
	vote := consumerContent(&waConsumerApplication.ConsumerApplication_Content{Content: &waConsumerApplication.ConsumerApplication_Content_PollUpdateMessage{
		PollUpdateMessage: &waConsumerApplication.ConsumerApplication_PollUpdateMessage{}}})
	signal := &waConsumerApplication.ConsumerApplication{Payload: &waConsumerApplication.ConsumerApplication_Payload{
		Payload: &waConsumerApplication.ConsumerApplication_Payload_Signal{Signal: &waConsumerApplication.ConsumerApplication_Signal{}}}}
	notif := armadilloContent(&waArmadilloApplication.Armadillo_Content{Content: &waArmadilloApplication.Armadillo_Content_RavenActionNotifMessage_{
		RavenActionNotifMessage: &waArmadilloApplication.Armadillo_Content_RavenActionNotifMessage{}}})
	for name, app := range map[string]any{"poll vote": vote, "signal": signal, "raven notif": notif, "voip": &armadillo.Unsupported_Voip{}} {
		if act := e.classify(fbEvent("m", "9", "9", 1, app), "9", "Ann"); len(act.parts) != 0 || act.unsupported != "" {
			t.Fatalf("%s must be silent, got %+v", name, act)
		}
	}
}

func TestClassifyMapsRichPayloads(t *testing.T) {
	_, e := e2eeFixture(t)
	one := func(app any) waContent {
		t.Helper()
		act := e.classify(fbEvent("m", "9", "9", 1, app), "9", "Ann")
		if len(act.parts) != 1 {
			t.Fatalf("parts = %+v", act.parts)
		}
		return act.parts[0]
	}

	// Link preview whose text does not contain the address.
	et := consumerContent(&waConsumerApplication.ConsumerApplication_Content{Content: &waConsumerApplication.ConsumerApplication_Content_ExtendedTextMessage{
		ExtendedTextMessage: &waConsumerApplication.ConsumerApplication_ExtendedTextMessage{
			Text:         &waCommon.MessageText{Text: proto.String("xem cái này")},
			CanonicalURL: proto.String("https://example.com/post/1"), Title: proto.String("Post"),
		}}})
	c := one(et)
	if c.kind != "text" || c.text != "xem cái này" || len(c.links) != 1 || c.links[0].URL != "https://example.com/post/1" ||
		c.links[0].Offset != 0 || c.links[0].Length != utf16Len("xem cái này") {
		t.Fatalf("extended text: %+v", c)
	}

	poll := consumerContent(&waConsumerApplication.ConsumerApplication_Content{Content: &waConsumerApplication.ConsumerApplication_Content_PollCreationMessage{
		PollCreationMessage: &waConsumerApplication.ConsumerApplication_PollCreationMessage{
			Name: proto.String("Lunch?"), Options: []*waConsumerApplication.ConsumerApplication_Option{{OptionName: proto.String("Pho")}, {OptionName: proto.String("Bun")}},
		}}})
	if c := one(poll); c.kind != "text" || c.text != "Poll: Lunch?\n- Pho\n- Bun" {
		t.Fatalf("poll: %+v", c)
	}

	cm := &waConsumerApplication.ConsumerApplication_ContactMessage{}
	if err := cm.Set(&waMediaTransport.ContactTransport{
		Integral:  &waMediaTransport.ContactTransport_Integral{Contact: &waMediaTransport.ContactTransport_Integral_Vcard{Vcard: "BEGIN:VCARD\nFN:Binh Nguyen\nTEL;TYPE=CELL:+84 90 000 0000\nEND:VCARD"}},
		Ancillary: &waMediaTransport.ContactTransport_Ancillary{DisplayName: proto.String("Binh")},
	}); err != nil {
		t.Fatal(err)
	}
	contact := consumerContent(&waConsumerApplication.ConsumerApplication_Content{Content: &waConsumerApplication.ConsumerApplication_Content_ContactMessage{ContactMessage: cm}})
	if c := one(contact); c.kind != "contact" || c.text != "Binh\n+84 90 000 0000" {
		t.Fatalf("contact: %+v", c)
	}

	// The Messenger "like" button.
	like := armadilloContent(&waArmadilloApplication.Armadillo_Content{Content: &waArmadilloApplication.Armadillo_Content_CommonSticker_{
		CommonSticker: &waArmadilloApplication.Armadillo_Content_CommonSticker{}}})
	if c := one(like); c.kind != "text" || c.text != "\U0001F44D" {
		t.Fatalf("like: %+v", c)
	}

	// Link card: the title is the text and opens the address.
	card := xma(&waArmadilloXMA.ExtendedContentMessage{
		TargetType: waArmadilloXMA.ExtendedContentMessage_MSG_EXTERNAL_LINK_SHARE.Enum(),
		TitleText:  proto.String("Great article"),
		Ctas:       []*waArmadilloXMA.ExtendedContentMessage_CTA{{ActionURL: proto.String("https://example.com/a?x=1")}},
	})
	c = one(card)
	if c.kind != "link" || c.text != "Great article" || len(c.links) != 1 || c.links[0].URL != "https://example.com/a?x=1" || c.links[0].Length != utf16Len("Great article") {
		t.Fatalf("link card: %+v", c)
	}

	call := xma(&waArmadilloXMA.ExtendedContentMessage{
		TargetType:   waArmadilloXMA.ExtendedContentMessage_RTC_MISSED_VIDEO_CALL.Enum(),
		SubtitleText: proto.String("0:00"),
	})
	if c := one(call); c.kind != "call" || c.text != "Cuộc gọi video bị nhỡ \u00B7 0:00" {
		t.Fatalf("call: %+v", c)
	}

	loc := xma(&waArmadilloXMA.ExtendedContentMessage{
		TargetType: waArmadilloXMA.ExtendedContentMessage_MSG_LOCATION_SHARING.Enum(),
		TitleText:  proto.String("Hanoi"),
		Ctas:       []*waArmadilloXMA.ExtendedContentMessage_CTA{{NativeURL: proto.String("messenger://location_share?lat=21.03&long=105.85")}},
	})
	c = one(loc)
	if c.kind != "location" || c.text != "Hanoi" || len(c.links) != 1 || !strings.Contains(c.links[0].URL, "21.030000,105.850000") {
		t.Fatalf("location: %+v", c)
	}

	// A card with nothing readable says what it is, briefly, and is the only unsupported case.
	empty := xma(&waArmadilloXMA.ExtendedContentMessage{TargetType: waArmadilloXMA.ExtendedContentMessage_IG_CLIPS_SHARE.Enum()})
	act := e.classify(fbEvent("m", "9", "9", 1, empty), "9", "Ann")
	if len(act.parts) != 1 || act.parts[0].kind != "unsupported" || act.parts[0].text != "Bài chia sẻ từ Instagram (mở ứng dụng Messenger để xem)" || act.unsupported == "" {
		t.Fatalf("empty card: %+v", act)
	}

	shot := armadilloContent(&waArmadilloApplication.Armadillo_Content{Content: &waArmadilloApplication.Armadillo_Content_ScreenshotAction_{
		ScreenshotAction: &waArmadilloApplication.Armadillo_Content_ScreenshotAction{}}})
	if c := one(shot); c.kind != "system" || c.text != "Ann đã chụp màn hình" {
		t.Fatalf("screenshot: %+v", c)
	}
}

func TestIngestAlbumSharesGroupIDAndKeepsOrder(t *testing.T) {
	h, e := e2eeFixture(t)
	gallery := &waArmadilloApplication.Armadillo_Content_ImageGalleryMessage{}
	if err := gallery.Set([]*waMediaTransport.ImageTransport{imageTransport("/a"), imageTransport("/b"), imageTransport("/c")}); err != nil {
		t.Fatal(err)
	}
	app := armadilloContent(&waArmadilloApplication.Armadillo_Content{Content: &waArmadilloApplication.Armadillo_Content_ImageGalleryMessage_{ImageGalleryMessage: gallery}})
	const ts = int64(1_700_000_000_000)
	e.ingest(fbEvent("alb", "77", "77", ts, app), false)

	frames := h.drain()
	var got []map[string]any
	for _, d := range events_(frames, "message.new") {
		got = append(got, d["message"].(map[string]any))
	}
	if len(got) != 3 {
		t.Fatalf("got %d messages: %v", len(got), frames)
	}
	for i, m := range got {
		wantID := "alb"
		if i > 0 {
			wantID = "alb:" + string(rune('0'+i))
		}
		if m["id"] != wantID || m["groupId"] != "alb" || m["kind"] != "image" || m["timestamp"] != float64(ts+int64(i)) {
			t.Fatalf("part %d = %v", i, m)
		}
	}
	// One album is one unread, not three.
	if th := mustThread(t, h.b, "77"); th.Unread != 1 {
		t.Fatalf("unread = %d, want 1", th.Unread)
	}

	// Unsending the first part unsends the album.
	e.onRevoke("77", "alb")
	for _, id := range []string{"alb", "alb:1", "alb:2"} {
		if m, _ := h.b.st.message("77", id); !m.Deleted {
			t.Fatalf("%s not deleted", id)
		}
	}
}

func TestIngestUsesWireGroupIDOfSeparateAlbumMessages(t *testing.T) {
	h, e := e2eeFixture(t)
	for i, id := range []string{"p1", "p2"} {
		evt := fbEvent(id, "78", "78", int64(1000+i), consumerText("x"))
		evt.FBApplication = &waMsgApplication.MessageApplication{Payload: nil, Metadata: &waMsgApplication.MessageApplication_Metadata{
			GroupID: proto.String("album-9"), GroupSize: proto.Uint32(2), GroupIndex: proto.Uint32(uint32(i)),
		}}
		e.ingest(evt, false)
	}
	_ = h.drain()
	for _, id := range []string{"p1", "p2"} {
		if m, ok := h.b.st.message("78", id); !ok || m.GroupID != "album-9" {
			t.Fatalf("%s: %+v ok=%v", id, m, ok)
		}
	}
}

func TestIngestMentionsReadAsNames(t *testing.T) {
	h, e := e2eeFixture(t)
	h.b.st.setContact("200", "Ann Lee", "")
	e.ingest(fbEvent("t1", "300", "300", 1000, consumerText("hi @200@msgr and @1@msgr and @999999@msgr", "200@msgr", "1@msgr", "999999@msgr")), false)
	m, ok := h.b.st.message("300", "t1")
	if !ok {
		t.Fatal("message not stored")
	}
	if want := "hi @Ann Lee and @Me and @Người dùng Facebook"; m.Text != want {
		t.Fatalf("text = %q, want %q", m.Text, want)
	}
}

func TestIngestEditResolvesMentionsAndDropsStaleLinks(t *testing.T) {
	h, e := e2eeFixture(t)
	h.b.st.setContact("200", "Ann Lee", "")
	e.ingest(fbEvent("t1", "300", "300", 1000, consumerText("first")), false)
	e.onEdit("300", &waConsumerApplication.ConsumerApplication_EditMessage{
		Key:     &waCommon.MessageKey{ID: proto.String("t1")},
		Message: &waCommon.MessageText{Text: proto.String("now @200@msgr"), MentionedJID: []string{"200@msgr"}},
	})
	if m, _ := h.b.st.message("300", "t1"); m.Text != "now @Ann Lee" || !m.Edited {
		t.Fatalf("edited = %+v", m)
	}
}

func TestIngestFillsSenderAvatarFromContact(t *testing.T) {
	h, e := e2eeFixture(t)
	url := "https://scontent.fbcdn.net/v/ann.jpg?oe=" + hexExpiry(30*24*time.Hour)
	h.b.st.setContact("400", "Ann", url)
	e.ingest(fbEvent("av1", "400", "400", 1000, consumerText("hello")), false)
	var got map[string]any
	for _, d := range events_(h.drain(), "message.new") {
		got = d["message"].(map[string]any)
	}
	if got["senderAvatar"] != url || got["senderName"] != "Ann" {
		t.Fatalf("message = %v", got)
	}
}

func TestUnsupportedFallbackIsClearAndSpecific(t *testing.T) {
	if got := unsupportedText(""); got != unsupportedE2EEText || !strings.Contains(got, "mở ứng dụng Messenger để xem") {
		t.Fatalf("%q", got)
	}
	if got := unsupportedText("Payment of 5 USD"); got != "Payment of 5 USD (mở ứng dụng Messenger để xem)" {
		t.Fatalf("%q", got)
	}
}

func TestLegacyUnsupportedEncryptedTextIsMigrated(t *testing.T) {
	dir := t.TempDir()
	st := newStore(dir, zerolog.Nop())
	st.putMessage(&storedMsg{Message: Message{ID: "old", ChatID: "5", Kind: "text", Text: legacyUnsupportedE2EEText, Timestamp: 1}, Sort: 1, SenderJID: "5@msgr"})
	st.putMessage(&storedMsg{Message: Message{ID: "plain", ChatID: "5", Kind: "text", Text: legacyUnsupportedE2EEText, Timestamp: 2}, Sort: 2})
	if err := st.flush(); err != nil {
		t.Fatal(err)
	}
	st2 := newStore(dir, zerolog.Nop())
	if err := st2.load(); err != nil {
		t.Fatal(err)
	}
	if m, _ := st2.message("5", "old"); m.Kind != "unsupported" || m.Text != unsupportedE2EEText {
		t.Fatalf("e2ee fallback not migrated: %+v", m)
	}
	if m, _ := st2.message("5", "plain"); m.Kind != "text" {
		t.Fatalf("an open-path message must be left alone: %+v", m)
	}
}

func TestPreviewLinks(t *testing.T) {
	cases := []struct {
		name, text, matched, canonical, title string
		wantText                              string
		wantLinks                             []Link
	}{
		{"address already written", "see https://example.com/a now", "https://example.com/a", "https://example.com/a", "T", "see https://example.com/a now", nil},
		{"short form of the address", "see example.com/a now", "example.com/a", "https://example.com/a?utm=1", "T", "see example.com/a now", []Link{{Offset: 4, Length: 13, URL: "https://example.com/a?utm=1"}}},
		{"address missing from text", "look 😀", "", "https://example.com/a", "T", "look 😀", []Link{{Offset: 0, Length: 7, URL: "https://example.com/a"}}},
		{"no text, title", "", "", "https://example.com/a", "Title", "Title", []Link{{Offset: 0, Length: 5, URL: "https://example.com/a"}}},
		{"no text, no title", "", "", "https://example.com/a", "", "https://example.com/a", nil},
		{"no preview", "plain", "", "", "", "plain", nil},
	}
	for _, c := range cases {
		text, links := previewLinks(c.text, c.matched, c.canonical, c.title)
		if text != c.wantText || len(links) != len(c.wantLinks) {
			t.Fatalf("%s: got %q %+v", c.name, text, links)
		}
		for i := range links {
			if links[i] != c.wantLinks[i] {
				t.Fatalf("%s: link %d = %+v, want %+v", c.name, i, links[i], c.wantLinks[i])
			}
		}
	}
}

func TestRenderMentions(t *testing.T) {
	names := map[string]string{"111111": "Ann", "222222": "Bob Smith"}
	nameOf := func(u string) string { return names[u] }

	// A span over a bare id becomes the name; UTF-16 offsets after an emoji.
	text := "😀 @111111 hi"
	got := renderMentions(text, []inMention{{offset: 3, length: 7, user: "111111"}}, nil, nameOf)
	if got != "😀 @Ann hi" {
		t.Fatalf("span: %q", got)
	}
	// A span that already shows a name is left alone.
	if got := renderMentions("hi @Ann there", []inMention{{offset: 3, length: 4, user: "111111"}}, nil, nameOf); got != "hi @Ann there" {
		t.Fatalf("name span: %q", got)
	}
	// Two spans, replaced from the end so offsets stay valid.
	got = renderMentions("@111111 and @222222", []inMention{{0, 7, "111111"}, {12, 7, "222222"}}, nil, nameOf)
	if got != "@Ann and @Bob Smith" {
		t.Fatalf("two spans: %q", got)
	}
	// Unknown people never show their id.
	if got := renderMentions("@333333@msgr yo", nil, []string{"333333@msgr"}, nameOf); got != "@Người dùng Facebook yo" || strings.Contains(got, "333333") {
		t.Fatalf("unknown: %q", got)
	}
	// A name that is just the id is not a name.
	if got := renderMentions("@444444@msgr", nil, nil, func(string) string { return "444444" }); strings.Contains(got, "444444") {
		t.Fatalf("id as name: %q", got)
	}
	// Garbage spans are ignored instead of panicking.
	if got := renderMentions("short", []inMention{{offset: 50, length: 5, user: "111111"}, {offset: -1, length: 2, user: "111111"}}, nil, nameOf); got != "short" {
		t.Fatalf("garbage: %q", got)
	}
}

func TestResolveOpenMentions(t *testing.T) {
	nameOf := func(u string) string {
		if u == "111111" {
			return "Ann"
		}
		return ""
	}
	// Sender's client wrote the id where the name belongs.
	if got := resolveOpenMentions("hey @111111!", "111111", "4", "7", nameOf); got != "hey @Ann!" {
		t.Fatalf("got %q", got)
	}
	// Already a name: untouched.
	if got := resolveOpenMentions("hey @Ann!", "111111", "4", "4", nameOf); got != "hey @Ann!" {
		t.Fatalf("got %q", got)
	}
	// Malformed columns leave the text alone.
	if got := resolveOpenMentions("hey", "111111,222222", "1", "", nameOf); got != "hey" {
		t.Fatalf("got %q", got)
	}
	if got := resolveOpenMentions("hey", "", "", "", nameOf); got != "hey" {
		t.Fatalf("got %q", got)
	}
}
