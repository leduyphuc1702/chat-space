package main

import (
	"encoding/json"
	"regexp"
	"sort"
	"strings"
	"unicode"

	"golang.org/x/text/runes"
	"golang.org/x/text/transform"
	"golang.org/x/text/unicode/norm"
)

// searchParams mirrors parseSearch in the Node bridge (docs/PROTOCOL.md, Search).
type searchParams struct {
	query  string
	chatID string
	kinds  map[string]bool
	before int64
	limit  int
}

var searchKinds = map[string]bool{"text": true, "image": true, "video": true, "audio": true, "voice": true,
	"file": true, "sticker": true, "gif": true, "location": true, "contact": true, "link": true}

func parseSearch(p params) (searchParams, error) {
	sp := searchParams{query: strings.TrimSpace(p.str("query")), chatID: p.str("chatId"), limit: min(p.intOr("limit", 50), 200)}
	var kinds []string
	if raw, ok := p["kinds"]; ok {
		_ = json.Unmarshal(raw, &kinds)
	}
	for _, k := range kinds {
		if searchKinds[k] {
			if sp.kinds == nil {
				sp.kinds = map[string]bool{}
			}
			sp.kinds[k] = true
		}
	}
	if sp.query == "" && sp.kinds == nil {
		return sp, newErr(codeInvalidParams, "query or kinds required")
	}
	if raw, ok := p["before"]; ok {
		var f float64
		if json.Unmarshal(raw, &f) == nil && f > 0 {
			sp.before = int64(f)
		}
	}
	return sp, nil
}

var stripMarks = transform.Chain(norm.NFD, runes.Remove(runes.In(unicode.Mn)), norm.NFC)

// fold is what people type when searching Vietnamese: lowercase, no accents, đ as d.
func fold(s string) string {
	out, _, err := transform.String(stripMarks, s)
	if err != nil {
		out = s
	}
	return strings.ToLower(strings.NewReplacer("đ", "d", "Đ", "d").Replace(out))
}

var urlRe = regexp.MustCompile(`(?i)https?://|www\.`)

func (sp searchParams) kindMatches(m *storedMsg) bool {
	if sp.kinds == nil {
		return m.Kind != "system" && m.Kind != "call" && m.Kind != "unsupported"
	}
	if sp.kinds[m.Kind] {
		return true
	}
	return sp.kinds["link"] && urlRe.MatchString(m.Text)
}

// searchMessages scans the local store only, newest first: searching must never
// make Messenger fetch anything.
func (s *store) searchMessages(sp searchParams) []Message {
	words := strings.Fields(fold(sp.query))
	s.mu.RLock()
	defer s.mu.RUnlock()
	var hits []*storedMsg
	for chat, list := range s.msgs {
		if sp.chatID != "" && chat != sp.chatID {
			continue
		}
		found := 0
		for i := len(list) - 1; i >= 0 && found < sp.limit; i-- {
			m := list[i]
			if m.Deleted || !sp.kindMatches(m) || (sp.before > 0 && m.Timestamp >= sp.before) {
				continue
			}
			hay := m.Text
			if m.Media != nil && m.Media.FileName != "" {
				hay += "\n" + m.Media.FileName
			}
			if !containsAll(fold(hay), words) {
				continue
			}
			hits = append(hits, m)
			found++
		}
	}
	sort.SliceStable(hits, func(i, j int) bool { return hits[i].Timestamp > hits[j].Timestamp })
	if len(hits) > sp.limit {
		hits = hits[:sp.limit]
	}
	out := make([]Message, 0, len(hits))
	for _, m := range hits {
		out = append(out, snapshot(m))
	}
	return out
}

func containsAll(hay string, words []string) bool {
	for _, w := range words {
		if !strings.Contains(hay, w) {
			return false
		}
	}
	return true
}
