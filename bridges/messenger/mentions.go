package main

// @mentions of messages.send (docs/PROTOCOL.md, "Mentions").
//
// The app sends spans over the `text` param: offset/length are UTF-16 code units and
// cover the visible "@Name". Meta counts the same way: its incoming mention offsets are
// indexed as UTF-16 (textfmt.parseMetaMentions in mautrix-meta), so for ordinary
// Facebook threads the numbers go into socket.MentionData unchanged. (mautrix-meta's
// own *sending* side, textfmt/from-matrix.go, computes byte offsets with strings.Index
// and len(), which is only right for ASCII text; it is deliberately not copied.)

import (
	"encoding/json"
	"math"
	"slices"
	"sort"
	"strconv"
	"unicode/utf16"

	"go.mau.fi/mautrix-meta/pkg/messagix/socket"
	"go.mau.fi/whatsmeow/types"
)

type mentionSpan struct {
	UserID string // Facebook user id (digits)
	Offset int    // UTF-16 code units
	Length int    // UTF-16 code units
}

// mentions decodes and validates the "mentions" param against text.
func (p params) mentions(text string) []mentionSpan {
	return parseMentions(p["mentions"], text)
}

func isNumericID(s string) bool {
	if s == "" || len(s) > 19 {
		return false
	}
	n, err := strconv.ParseInt(s, 10, 64)
	return err == nil && n > 0 && strconv.FormatInt(n, 10) == s
}

func intOf(f float64) (int, bool) {
	if f != math.Trunc(f) || f < 0 || f > math.MaxInt32 {
		return 0, false
	}
	return int(f), true
}

func isLowSurrogate(u uint16) bool { return u >= 0xDC00 && u <= 0xDFFF }

// parseMentions keeps the valid spans, sorted by offset. Malformed entries, entries
// outside the text, entries that start or end inside a surrogate pair, non-numeric
// user ids and spans overlapping an earlier one are dropped: the message is still
// sent, just without that highlight.
func parseMentions(raw json.RawMessage, text string) []mentionSpan {
	if len(raw) == 0 || text == "" {
		return nil
	}
	var items []json.RawMessage
	if json.Unmarshal(raw, &items) != nil {
		return nil
	}
	units := utf16.Encode([]rune(text))
	var spans []mentionSpan
	for _, it := range items {
		var m struct {
			UserID string  `json:"userId"`
			Offset float64 `json:"offset"`
			Length float64 `json:"length"`
		}
		if json.Unmarshal(it, &m) != nil || !isNumericID(m.UserID) {
			continue
		}
		off, ok1 := intOf(m.Offset)
		length, ok2 := intOf(m.Length)
		if !ok1 || !ok2 || length == 0 || off+length > len(units) {
			continue
		}
		if isLowSurrogate(units[off]) || (off+length < len(units) && isLowSurrogate(units[off+length])) {
			continue
		}
		spans = append(spans, mentionSpan{UserID: m.UserID, Offset: off, Length: length})
	}
	sort.SliceStable(spans, func(i, j int) bool { return spans[i].Offset < spans[j].Offset })
	out := spans[:0]
	end := 0
	for _, s := range spans {
		if s.Offset < end {
			continue
		}
		out = append(out, s)
		end = s.Offset + s.Length
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

// buildMentionData is the MentionData of a SendMessageTask: comma-separated ids,
// offsets, lengths and types ("p" = person). Nil when there is nothing to mention.
func buildMentionData(spans []mentionSpan) *socket.MentionData {
	if len(spans) == 0 {
		return nil
	}
	ms := make(socket.Mentions, 0, len(spans))
	for _, s := range spans {
		id, err := strconv.ParseInt(s.UserID, 10, 64)
		if err != nil {
			continue
		}
		ms = append(ms, socket.Mention{ID: id, Offset: s.Offset, Length: s.Length, Type: socket.MentionTypePerson})
	}
	if len(ms) == 0 {
		return nil
	}
	return ms.ToData()
}

// rewriteE2EEMentions prepares an end-to-end encrypted text. Like mautrix-meta's
// ParseWhatsApp, the text carries a literal "@<fbid>@msgr" token per mention and the
// same JIDs go into MessageText.MentionedJID; the receiving client swaps the token for
// the person's name. Spans are replaced from last to first so offsets stay valid.
func rewriteE2EEMentions(text string, spans []mentionSpan) (string, []string) {
	if len(spans) == 0 {
		return text, nil
	}
	units := utf16.Encode([]rune(text))
	var jids []string
	for i := len(spans) - 1; i >= 0; i-- {
		s := spans[i]
		jid := types.NewJID(s.UserID, types.MessengerServer).String()
		units = slices.Concat(units[:s.Offset], utf16.Encode([]rune("@"+jid)), units[s.Offset+s.Length:])
		jids = append(jids, jid)
	}
	// jids were collected last-to-first; report them in text order, once each.
	slices.Reverse(jids)
	return string(utf16.Decode(units)), uniqueStrings(jids)
}

// uniqueStrings drops later duplicates while keeping first-seen order.
func uniqueStrings(in []string) []string {
	seen := make(map[string]struct{}, len(in))
	out := make([]string, 0, len(in))
	for _, s := range in {
		if _, ok := seen[s]; ok {
			continue
		}
		seen[s] = struct{}{}
		out = append(out, s)
	}
	return out
}
