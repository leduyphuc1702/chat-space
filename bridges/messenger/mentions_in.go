package main

// Incoming mentions.
//
// A mention reaches us in one of two shapes, and neither carries the person's name:
//
//   - end-to-end encrypted text has a token "@<fbid>@msgr" in place of the name, plus the
//     JID in MentionedJID (the receiving Messenger client swaps it for the name);
//   - plain Messenger text has the name already, with MentionIds/Offsets/Lengths marking
//     where; when the sender's client wrote the id instead (or the span covers one) the id
//     shows.
//
// Message.text must read "@Name" and never an id, so both are rewritten here from the
// contact table, falling back to a generic label for people we know nothing about.
// Offsets are UTF-16 code units, as everywhere in the protocol.

import (
	"regexp"
	"slices"
	"sort"
	"strconv"
	"strings"
	"unicode/utf16"

	"go.mau.fi/whatsmeow/proto/waCommon"
)

var (
	mentionTokenRe = regexp.MustCompile(`@(\d{5,20})@(?:msgr|s\.whatsapp\.net|c\.us)`)
	bareIDRe       = regexp.MustCompile(`^@?(\d{5,20})(?:@[a-z.]+)?$`)
)

// inMention is one mention span of an incoming message.
type inMention struct {
	offset, length int // UTF-16 code units
	user           string
}

func jidUserOf(jid string) string {
	if u, _, ok := cutJID(jid); ok {
		return u
	}
	return jid
}

// resolveE2EEMentions makes the text of an encrypted message readable.
func resolveE2EEMentions(text string, jids []string, spans []*waCommon.Mention, nameOf func(user string) string) string {
	var ms []inMention
	for _, sp := range spans {
		user := jidUserOf(sp.GetMentionedJID())
		if !isNumericID(user) {
			user = ""
		}
		ms = append(ms, inMention{int(sp.GetOffset()), int(sp.GetLength()), user})
	}
	return renderMentions(text, ms, jids, nameOf)
}

// resolveOpenMentions does the same for the comma-separated MentionIds / Offsets /
// Lengths columns of a plain Messenger message. Malformed data leaves the text alone.
func resolveOpenMentions(text, ids, offsets, lengths string, nameOf func(user string) string) string {
	if text == "" || ids == "" {
		return text
	}
	idl, ofl, lel := strings.Split(ids, ","), strings.Split(offsets, ","), strings.Split(lengths, ",")
	var ms []inMention
	for i, id := range idl {
		id = strings.TrimSpace(id)
		var off, n int
		var err1, err2 error
		if i < len(ofl) && i < len(lel) {
			off, err1 = strconv.Atoi(strings.TrimSpace(ofl[i]))
			n, err2 = strconv.Atoi(strings.TrimSpace(lel[i]))
		}
		if !isNumericID(id) {
			continue
		}
		if i >= len(ofl) || i >= len(lel) || err1 != nil || err2 != nil {
			ms = append(ms, inMention{0, 0, id}) // no usable span: ids are still replaced where written out
			continue
		}
		ms = append(ms, inMention{off, n, id})
	}
	return renderMentions(text, ms, idl, nameOf)
}

// renderMentions rewrites spans whose text is a bare user id and the "@<id>@msgr" /
// "@<id>" forms of the listed ids into "@Name".
func renderMentions(text string, spans []inMention, ids []string, nameOf func(user string) string) string {
	if text == "" {
		return text
	}
	label := func(user string) string {
		n := ""
		if nameOf != nil {
			n = strings.TrimSpace(nameOf(user))
		}
		if n == "" || n == user {
			n = unknownMentionName
		}
		return "@" + n
	}

	if len(spans) > 0 {
		units := utf16.Encode([]rune(text))
		type rep struct {
			off, end int
			user     string
		}
		var reps []rep
		for _, sp := range spans {
			off, end := sp.offset, sp.offset+sp.length
			if sp.length <= 0 || off < 0 || end > len(units) {
				continue
			}
			m := bareIDRe.FindStringSubmatch(string(utf16.Decode(units[off:end])))
			if m == nil {
				continue // already reads as a name
			}
			user := m[1]
			if sp.user != "" {
				user = sp.user
			}
			reps = append(reps, rep{off, end, user})
		}
		sort.Slice(reps, func(i, j int) bool { return reps[i].off > reps[j].off })
		prevStart := len(units) + 1
		for _, r := range reps {
			if r.end > prevStart {
				continue // overlaps one already replaced
			}
			units = slices.Concat(units[:r.off], utf16.Encode([]rune(label(r.user))), units[r.end:])
			prevStart = r.off
		}
		text = string(utf16.Decode(units))
	}

	text = mentionTokenRe.ReplaceAllStringFunc(text, func(tok string) string {
		return label(mentionTokenRe.FindStringSubmatch(tok)[1])
	})
	for _, id := range uniqueStrings(ids) {
		user := jidUserOf(strings.TrimSpace(id))
		if !isNumericID(user) {
			continue
		}
		if id != user {
			text = strings.ReplaceAll(text, "@"+id, label(user))
		}
		text = regexp.MustCompile(`@`+regexp.QuoteMeta(user)+`\b`).ReplaceAllLiteralString(text, label(user))
	}
	return text
}

// mentionName is how a mentioned person is called: us by our own name, then the contact
// table, then the name their messages in this chat carry (end-to-end encrypted senders
// only have a push name). "" when unknown.
func (b *bridge) mentionName(chat, user string) string {
	if selfID, selfName := b.selfInfo(); selfID != 0 && user == chatKey(selfID) && selfName != "" {
		return selfName
	}
	if c, ok := b.st.contact(user); ok && c.Name != "" {
		return c.Name
	}
	return b.st.senderNameIn(chat, user)
}
