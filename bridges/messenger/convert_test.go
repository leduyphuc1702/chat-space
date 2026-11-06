package main

import (
	"testing"

	"go.mau.fi/mautrix-meta/pkg/messagix/table"
)

func wrapped(text string) *table.WrappedMessage {
	return &table.WrappedMessage{LSInsertMessage: &table.LSInsertMessage{
		ThreadKey: 9, MessageId: "mid.1", SenderId: 7, TimestampMs: 5000, PrimarySortKey: 5000, Text: text,
	}}
}

func photo(url string) *table.LSInsertBlobAttachment {
	return &table.LSInsertBlobAttachment{PlayableUrl: url, PlayableUrlMimeType: "image/jpeg"}
}

// One Messenger message with three photos and a caption was shown as three unrelated
// bubbles at the same instant; the parts now share the message id as groupId.
func TestConvertWrappedGroupsPartsOfOneMessage(t *testing.T) {
	h := newHarness(t)
	w := wrapped("holiday")
	w.BlobAttachments = []*table.LSInsertBlobAttachment{photo("https://x/1.jpg"), photo("https://x/2.jpg"), photo("https://x/3.jpg")}
	parts := h.b.convertWrapped(w, "9")
	if len(parts) != 3 {
		t.Fatalf("parts = %d", len(parts))
	}
	for i, p := range parts {
		if p.GroupID != "mid.1" || p.Kind != "image" || p.Timestamp != 5000+int64(i) {
			t.Fatalf("part %d: %+v", i, p.Message)
		}
	}
	if parts[0].Text != "holiday" || parts[1].Text != "" {
		t.Fatalf("caption belongs to the first part: %q / %q", parts[0].Text, parts[1].Text)
	}
	if parts[1].ID != "mid.1:1" || parts[2].ID != "mid.1:2" {
		t.Fatalf("ids: %s %s", parts[1].ID, parts[2].ID)
	}

	// A message with one attachment is not a group.
	w = wrapped("one")
	w.BlobAttachments = []*table.LSInsertBlobAttachment{photo("https://x/1.jpg")}
	if p := h.b.convertWrapped(w, "9"); len(p) != 1 || p[0].GroupID != "" {
		t.Fatalf("single: %+v", p)
	}
}

func TestConvertWrappedExtraPartsKeepSenderName(t *testing.T) {
	h := newHarness(t)
	h.b.st.setContact("7", "Ann", "https://x.fbcdn.net/a.jpg")
	w := wrapped("")
	w.BlobAttachments = []*table.LSInsertBlobAttachment{photo("https://x/1.jpg"), photo("https://x/2.jpg")}
	parts := h.b.convertWrapped(w, "9")
	for _, p := range parts {
		h.b.st.putMessage(p)
	}
	for _, id := range []string{"mid.1", "mid.1:1"} {
		if m, _ := h.b.st.message("9", id); m.SenderName != "Ann" || m.SenderAvatar != "https://x.fbcdn.net/a.jpg" {
			t.Fatalf("%s: %+v", id, m)
		}
	}
}

func TestConvertWrappedMentionShowsNameNotID(t *testing.T) {
	h := newHarness(t)
	h.b.st.setContact("55555", "Bob", "")
	w := wrapped("hey @55555 look")
	w.MentionIds, w.MentionOffsets, w.MentionLengths, w.MentionTypes = "55555", "4", "6", "p"
	if p := h.b.convertWrapped(w, "9"); p[0].Text != "hey @Bob look" {
		t.Fatalf("text = %q", p[0].Text)
	}
	// Already a name on the wire: untouched.
	w = wrapped("hey @Bob look")
	w.MentionIds, w.MentionOffsets, w.MentionLengths, w.MentionTypes = "55555", "4", "4", "p"
	if p := h.b.convertWrapped(w, "9"); p[0].Text != "hey @Bob look" {
		t.Fatalf("text = %q", p[0].Text)
	}
}

func TestConvertWrappedLinkCardOpensItsAddress(t *testing.T) {
	h := newHarness(t)
	card := func() *table.WrappedXMA {
		return &table.WrappedXMA{
			LSInsertXmaAttachment: &table.LSInsertXmaAttachment{TitleText: "Great article"},
			CTA:                   &table.LSInsertAttachmentCta{ActionUrl: "https://l.facebook.com/l.php?u=https%3A%2F%2Fexample.com%2Fa%3Fx%3D1"},
		}
	}
	// The address is not in the text: the title shows and links to it.
	w := wrapped("")
	w.XMAAttachments = []*table.WrappedXMA{card()}
	p := h.b.convertWrapped(w, "9")[0]
	if p.Kind != "link" || p.Text != "Great article" || len(p.Links) != 1 ||
		p.Links[0] != (Link{Offset: 0, Length: 13, URL: "https://example.com/a?x=1"}) {
		t.Fatalf("card: %+v", p.Message)
	}
	// Typed text before the card: the title's offset follows it.
	w = wrapped("😀 look")
	w.XMAAttachments = []*table.WrappedXMA{card()}
	p = h.b.convertWrapped(w, "9")[0]
	if p.Text != "😀 look\n\nGreat article" || len(p.Links) != 1 || p.Links[0].Offset != utf16Len("😀 look\n\n") {
		t.Fatalf("card with text: %q %+v", p.Text, p.Links)
	}
	// The sender pasted the address: nothing to add, the app finds it itself.
	w = wrapped("https://example.com/a?x=1")
	w.XMAAttachments = []*table.WrappedXMA{card()}
	p = h.b.convertWrapped(w, "9")[0]
	if len(p.Links) != 0 {
		t.Fatalf("address already in text but links = %+v", p.Links)
	}
	// No title: the address itself is the text.
	w = wrapped("")
	c := card()
	c.TitleText = ""
	w.XMAAttachments = []*table.WrappedXMA{c}
	p = h.b.convertWrapped(w, "9")[0]
	if p.Text != "https://example.com/a?x=1" || len(p.Links) != 0 {
		t.Fatalf("untitled card: %q %+v", p.Text, p.Links)
	}
}
