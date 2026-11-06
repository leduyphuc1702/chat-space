package main

import (
	"encoding/json"
	"testing"

	"github.com/rs/zerolog"
)

func searchParamsOf(t *testing.T, raw string) searchParams {
	t.Helper()
	var p params
	if err := json.Unmarshal([]byte(raw), &p); err != nil {
		t.Fatal(err)
	}
	sp, err := parseSearch(p)
	if err != nil {
		t.Fatal(err)
	}
	return sp
}

func TestSearchMessages(t *testing.T) {
	s := newStore(t.TempDir(), zerolog.Nop())
	add := func(chat, id string, ts int64, kind, text string, media *Media) {
		s.insertLocked(chat, &storedMsg{Message: Message{ID: id, ChatID: chat, Timestamp: ts, Kind: kind, Text: text, Media: media}, Sort: ts})
	}
	add("A", "a1", 1, "text", "Gửi anh hợp đồng", nil)
	add("A", "a2", 5, "text", "hop dong da ky", nil)
	add("B", "b1", 3, "file", "", &Media{FileName: "Hợp đồng Q3.pdf"})
	add("B", "b2", 4, "text", "xem https://example.com", nil)
	add("B", "b3", 6, "system", "hợp đồng", nil)

	ids := func(ms []Message) (out []string) {
		for _, m := range ms {
			out = append(out, m.ID)
		}
		return
	}
	check := func(raw string, want ...string) {
		t.Helper()
		got := ids(s.searchMessages(searchParamsOf(t, raw)))
		if len(got) != len(want) {
			t.Fatalf("%s: got %v want %v", raw, got, want)
		}
		for i := range got {
			if got[i] != want[i] {
				t.Fatalf("%s: got %v want %v", raw, got, want)
			}
		}
	}
	check(`{"query":"Đồng HOP"}`, "a2", "b1", "a1")
	check(`{"query":"hop dong","limit":2}`, "a2", "b1")
	check(`{"query":"hop dong","before":5}`, "b1", "a1")
	check(`{"query":"hop","chatId":"B"}`, "b1")
	check(`{"kinds":["link"]}`, "b2")

	if _, err := parseSearch(params{}); err == nil {
		t.Fatal("empty search accepted")
	}
}

// The profile panel lists one member's media, files and links: empty query, kinds, senderId, chatId.
func TestSearchBySender(t *testing.T) {
	s := newStore(t.TempDir(), zerolog.Nop())
	add := func(chat, id, sender string, ts int64, kind, text string, media *Media, links []Link) {
		s.insertLocked(chat, &storedMsg{Message: Message{ID: id, ChatID: chat, SenderID: sender, Timestamp: ts, Kind: kind, Text: text, Media: media, Links: links}, Sort: ts})
	}
	add("A", "a1", "1", 1, "image", "", &Media{}, nil)
	add("A", "a2", "2", 2, "image", "", &Media{}, nil)
	add("A", "a3", "1", 3, "file", "", &Media{FileName: "plan.pdf"}, nil)
	add("A", "a4", "1", 4, "text", "Great article", nil, []Link{{Offset: 0, Length: 13, URL: "https://example.com/post"}})
	add("A", "a5", "1", 5, "text", "chat", nil, nil)
	add("A", "a6", "1", 6, "system", "joined", nil, nil)
	add("B", "b1", "1", 7, "image", "", &Media{}, nil)

	ids := func(raw string) (out []string) {
		for _, m := range s.searchMessages(searchParamsOf(t, raw)) {
			out = append(out, m.ID)
		}
		return
	}
	eq := func(got []string, want ...string) {
		t.Helper()
		if len(got) != len(want) {
			t.Fatalf("got %v want %v", got, want)
		}
		for i := range got {
			if got[i] != want[i] {
				t.Fatalf("got %v want %v", got, want)
			}
		}
	}
	eq(ids(`{"senderId":"1","chatId":"A","kinds":["image"]}`), "a1")
	eq(ids(`{"senderId":"1","chatId":"A","kinds":["image","video","file"]}`), "a3", "a1")
	eq(ids(`{"senderId":"1","chatId":"A","kinds":["link"]}`), "a4")  // a link card whose address is not in its text
	eq(ids(`{"senderId":"1","kinds":["image"]}`), "b1", "a1")        // all chats
	eq(ids(`{"senderId":"1","chatId":"A"}`), "a5", "a4", "a3", "a1") // everything but system messages
	eq(ids(`{"senderId":"2","chatId":"A"}`), "a2")
	eq(ids(`{"senderId":"1","chatId":"A","query":"example.com"}`), "a4") // the card's address is searchable
	eq(ids(`{"senderId":"nobody"}`))
	if _, err := parseSearch(params{}); err == nil {
		t.Fatal("empty search accepted")
	}
}
