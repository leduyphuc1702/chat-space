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
