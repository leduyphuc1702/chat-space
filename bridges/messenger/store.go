package main

// In-memory + on-disk (debounced) store of threads, contacts and messages.
// The Messenger web client keeps all of this server-side and streams it to us
// as LightSpeed tables; we need a local copy so chats.list / messages.list can
// answer instantly and so the app has something to show before the socket is up.

import (
	"bytes"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"sync"
	"time"

	"github.com/rs/zerolog"
)

const (
	maxPerThread  = 300 // messages kept per thread (on disk always; in memory unless paginated deeper)
	storeFileName = "store.json"
	saveDebounce  = 2 * time.Second
)

type threadRec struct {
	ID           string `json:"id"`
	Key          int64  `json:"key,omitempty"` // Facebook thread key (0 for E2EE-only chats)
	Type         int64  `json:"type"`
	Name         string `json:"name,omitempty"`
	Avatar       string `json:"avatar,omitempty"`
	LastActivity int64  `json:"lastActivity,omitempty"`
	LastRead     int64  `json:"lastRead,omitempty"` // my read watermark
	PeerRead     int64  `json:"peerRead,omitempty"` // the other side's read watermark (1:1 chats)
	Unread       int    `json:"unread,omitempty"`
	MuteUntil    int64  `json:"muteUntil,omitempty"` // ms epoch; -1 = forever
	MemberCount  int    `json:"memberCount,omitempty"`
	Snippet      string `json:"snippet,omitempty"`
	Folder       string `json:"folder,omitempty"`
	JID          string `json:"jid,omitempty"` // E2EE chat JID ("<fbid>@msgr", "<id>@g.us")
	E2EE         bool   `json:"e2ee,omitempty"`
	IsGroup      bool   `json:"isGroup,omitempty"`
	NoMoreBefore bool   `json:"noMoreBefore,omitempty"` // server has nothing older than our oldest message
	// OpenNoMoreBefore: the pre-encryption thread of an encrypted 1:1 chat has nothing
	// older than our oldest message from it (history.loadOpen).
	OpenNoMoreBefore bool `json:"openNoMoreBefore,omitempty"`
	// ArchiveNoMoreBefore: the encrypted backup has nothing older than our oldest
	// end-to-end encrypted message of the chat.
	ArchiveNoMoreBefore bool `json:"archiveNoMoreBefore,omitempty"`
}

type contactRec struct {
	ID     string `json:"id"`
	Name   string `json:"name,omitempty"`
	Avatar string `json:"avatar,omitempty"`
}

// waMediaRef is what we need to download+decrypt a media message that arrived
// over E2EE (whatsmeow DownloadFB). Plain data so the store stays whatsmeow-free.
type waMediaRef struct {
	Key        []byte `json:"key"`
	SHA256     []byte `json:"sha256"`
	EncSHA256  []byte `json:"encSha256"`
	DirectPath string `json:"directPath"`
	Type       string `json:"type"` // image | video | audio | document
}

type storedMsg struct {
	Message
	Sort      int64             `json:"sort,omitempty"`
	Actors    map[string]string `json:"actors,omitempty"` // reaction actor id -> emoji
	SenderJID string            `json:"senderJid,omitempty"`
	WAMedia   *waMediaRef       `json:"waMedia,omitempty"`
	WAReplyTo string            `json:"waReplyTo,omitempty"`
}

type storeFile struct {
	Version  int                     `json:"version"`
	Threads  []*threadRec            `json:"threads"`
	Contacts []*contactRec           `json:"contacts"`
	Messages map[string][]*storedMsg `json:"messages"`
	// Members: group chat id -> participant Facebook user ids (LSAddParticipantIdToGroupThread).
	// Absent in stores written before @mentions existed; it simply fills up on the next sync.
	Members map[string][]string `json:"members,omitempty"`
}

type store struct {
	mu       sync.RWMutex
	path     string
	log      zerolog.Logger
	selfID   string
	threads  map[string]*threadRec
	contacts map[string]*contactRec
	members  map[string]map[string]struct{} // chat id -> participant user ids
	msgs     map[string][]*storedMsg
	msgChat  map[string]string // message id -> chat id
	jidChat  map[string]string // E2EE JID user -> chat id
	deep     map[string]bool   // in-memory only: user paginated past the cap

	saveMu    sync.Mutex
	saveTimer *time.Timer
}

func newStore(dataDir string, log zerolog.Logger) *store {
	s := &store{path: filepath.Join(dataDir, storeFileName), log: log}
	s.resetLocked()
	return s
}

func (s *store) resetLocked() {
	s.threads = map[string]*threadRec{}
	s.contacts = map[string]*contactRec{}
	s.members = map[string]map[string]struct{}{}
	s.msgs = map[string][]*storedMsg{}
	s.msgChat = map[string]string{}
	s.jidChat = map[string]string{}
	s.deep = map[string]bool{}
}

func (s *store) load() error {
	data, err := os.ReadFile(s.path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	} else if err != nil {
		return err
	}
	var f storeFile
	if err := json.Unmarshal(data, &f); err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.resetLocked()
	for _, t := range f.Threads {
		s.threads[t.ID] = t
		if t.JID != "" {
			if user, _, ok := cutJID(t.JID); ok {
				s.jidChat[user] = t.ID
			}
		}
	}
	for _, c := range f.Contacts {
		s.contacts[c.ID] = c
	}
	for chat, ids := range f.Members {
		set := make(map[string]struct{}, len(ids))
		for _, id := range ids {
			set[id] = struct{}{}
		}
		s.members[chat] = set
	}
	for chat, list := range f.Messages {
		for _, m := range list {
			s.msgChat[m.ID] = chat
		}
		s.msgs[chat] = list
	}
	return nil
}

// reset forgets everything and removes the on-disk copy (logout).
func (s *store) reset() {
	s.saveMu.Lock()
	if s.saveTimer != nil {
		s.saveTimer.Stop()
		s.saveTimer = nil
	}
	s.saveMu.Unlock()
	s.mu.Lock()
	s.resetLocked()
	s.mu.Unlock()
	_ = os.Remove(s.path)
}

func (s *store) setSelf(id string) {
	s.mu.Lock()
	s.selfID = id
	s.mu.Unlock()
}

// Persistence ---------------------------------------------------------------

func (s *store) markDirty() {
	s.saveMu.Lock()
	defer s.saveMu.Unlock()
	if s.saveTimer != nil {
		return
	}
	s.saveTimer = time.AfterFunc(saveDebounce, func() {
		s.saveMu.Lock()
		s.saveTimer = nil
		s.saveMu.Unlock()
		if err := s.flush(); err != nil {
			s.log.Warn().Err(err).Msg("failed to save store")
		}
	})
}

func (s *store) flush() error {
	s.mu.RLock()
	f := storeFile{Version: 1, Messages: make(map[string][]*storedMsg, len(s.msgs))}
	for _, id := range sortedKeys(s.threads) {
		f.Threads = append(f.Threads, s.threads[id])
	}
	for _, id := range sortedKeys(s.contacts) {
		f.Contacts = append(f.Contacts, s.contacts[id])
	}
	if len(s.members) > 0 {
		f.Members = make(map[string][]string, len(s.members))
		for chat, set := range s.members {
			f.Members[chat] = sortedKeys(set)
		}
	}
	for chat, list := range s.msgs {
		if len(list) > maxPerThread {
			list = list[len(list)-maxPerThread:]
		}
		f.Messages[chat] = list
	}
	data, err := json.Marshal(&f)
	s.mu.RUnlock()
	if err != nil {
		return err
	}
	return atomicWrite(s.path, data, 0o600)
}

func (s *store) stopSaving() {
	s.saveMu.Lock()
	if s.saveTimer != nil {
		s.saveTimer.Stop()
		s.saveTimer = nil
	}
	s.saveMu.Unlock()
}

// Threads -------------------------------------------------------------------

func (s *store) thread(id string) (threadRec, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	t, ok := s.threads[id]
	if !ok {
		return threadRec{}, false
	}
	return *t, true
}

// updateThread applies fn to the thread, creating it first when needed.
func (s *store) updateThread(id string, fn func(t *threadRec)) (threadRec, bool) {
	s.mu.Lock()
	t, ok := s.threads[id]
	if !ok {
		t = &threadRec{ID: id}
		s.threads[id] = t
	}
	if fn != nil {
		fn(t)
	}
	out := *t
	s.mu.Unlock()
	s.markDirty()
	return out, !ok
}

func (s *store) threadList() []threadRec {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]threadRec, 0, len(s.threads))
	for _, t := range s.threads {
		out = append(out, *t)
	}
	return out
}

func (s *store) removeThread(id string) {
	s.mu.Lock()
	for _, m := range s.msgs[id] {
		delete(s.msgChat, m.ID)
	}
	delete(s.msgs, id)
	delete(s.threads, id)
	delete(s.members, id)
	delete(s.deep, id)
	for user, chat := range s.jidChat {
		if chat == id {
			delete(s.jidChat, user)
		}
	}
	s.mu.Unlock()
	s.markDirty()
}

// mapJID records that E2EE JID user `user` belongs to chat `chatID`, merging an
// E2EE-only chat that was created before the mapping was known.
func (s *store) mapJID(user, chatID string) (merged bool) {
	s.mu.Lock()
	prev := s.jidChat[user]
	s.jidChat[user] = chatID
	if prev == "" {
		prev = user // E2EE-only chats are keyed by the JID user
	}
	if prev != chatID {
		if old, ok := s.threads[prev]; ok {
			if dst, ok := s.threads[chatID]; ok {
				dst.LastActivity = max(dst.LastActivity, old.LastActivity)
			}
			for _, m := range s.msgs[prev] {
				m.ChatID = chatID
				s.msgChat[m.ID] = chatID
				s.insertLocked(chatID, m)
			}
			delete(s.msgs, prev)
			delete(s.threads, prev)
			merged = true
		}
	}
	s.mu.Unlock()
	s.markDirty()
	return merged
}

func (s *store) chatForJID(user string) string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if id := s.jidChat[user]; id != "" {
		return id
	}
	return user
}

// Members -------------------------------------------------------------------

// addMembers records participants of a group chat; it reports whether anything was new.
func (s *store) addMembers(chat string, ids ...string) (changed bool) {
	s.mu.Lock()
	set := s.members[chat]
	for _, id := range ids {
		if id == "" || id == "0" {
			continue
		}
		if set == nil {
			set = map[string]struct{}{}
			s.members[chat] = set
		}
		if _, ok := set[id]; !ok {
			set[id] = struct{}{}
			changed = true
		}
	}
	s.mu.Unlock()
	if changed {
		s.markDirty()
	}
	return changed
}

func (s *store) removeMember(chat, id string) (changed bool) {
	s.mu.Lock()
	if set := s.members[chat]; set != nil {
		if _, ok := set[id]; ok {
			delete(set, id)
			changed = true
		}
	}
	s.mu.Unlock()
	if changed {
		s.markDirty()
	}
	return changed
}

// resetMembers forgets the participant list of a chat (a full resync follows).
func (s *store) resetMembers(chat string) {
	s.mu.Lock()
	_, had := s.members[chat]
	delete(s.members, chat)
	s.mu.Unlock()
	if had {
		s.markDirty()
	}
}

// memberIDs returns the stored participant ids of a chat, sorted.
func (s *store) memberIDs(chat string) []string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return sortedKeys(s.members[chat])
}

// Contacts ------------------------------------------------------------------

func (s *store) setContact(id, name, avatar string) (changed bool) {
	if id == "" || id == "0" {
		return false
	}
	s.mu.Lock()
	c, ok := s.contacts[id]
	if !ok {
		c = &contactRec{ID: id}
		s.contacts[id] = c
		changed = true
	}
	if name != "" && c.Name != name {
		c.Name = name
		changed = true
	}
	if avatar != "" && c.Avatar != avatar {
		c.Avatar = avatar
		changed = true
	}
	s.mu.Unlock()
	if changed {
		s.markDirty()
	}
	return changed
}

func (s *store) contact(id string) (contactRec, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	c, ok := s.contacts[id]
	if !ok {
		return contactRec{}, false
	}
	return *c, true
}

// Messages ------------------------------------------------------------------

func msgLess(a, b *storedMsg) bool {
	if a.Timestamp != b.Timestamp {
		return a.Timestamp < b.Timestamp
	}
	if a.Sort != b.Sort {
		return a.Sort < b.Sort
	}
	return a.ID < b.ID
}

func snapshot(m *storedMsg) Message {
	out := m.Message
	if m.Media != nil {
		mc := *m.Media
		out.Media = &mc
	}
	if m.ReplyTo != nil {
		rc := *m.ReplyTo
		out.ReplyTo = &rc
	}
	if m.Reactions != nil {
		out.Reactions = slices.Clone(m.Reactions)
	}
	return out
}

func (s *store) insertLocked(chat string, m *storedMsg) {
	list := s.msgs[chat]
	i := sort.Search(len(list), func(i int) bool { return !msgLess(list[i], m) })
	s.msgs[chat] = slices.Insert(list, i, m)
}

func (s *store) trimLocked(chat string) {
	if s.deep[chat] {
		return
	}
	list := s.msgs[chat]
	if len(list) <= maxPerThread {
		return
	}
	for _, m := range list[:len(list)-maxPerThread] {
		delete(s.msgChat, m.ID)
	}
	s.msgs[chat] = slices.Clone(list[len(list)-maxPerThread:])
}

func (s *store) markDeep(chat string) {
	s.mu.Lock()
	s.deep[chat] = true
	s.mu.Unlock()
}

// putMessage inserts or merges a message. Reactions, local media paths and read
// status survive a re-delivery of the same message.
func (s *store) putMessage(m *storedMsg) (isNew, changed bool, snap Message) {
	s.mu.Lock()
	defer s.mu.Unlock()
	chat := m.ChatID
	if existingChat, ok := s.msgChat[m.ID]; ok {
		chat = existingChat
		list := s.msgs[chat]
		for i, old := range list {
			if old.ID != m.ID {
				continue
			}
			before, _ := json.Marshal(old)
			merged := *m
			merged.ChatID = chat
			if len(old.Actors) > 0 && len(merged.Actors) == 0 {
				merged.Actors = old.Actors
				merged.Reactions = old.Reactions
			}
			if old.Media != nil && merged.Media != nil && merged.Media.Path == "" {
				merged.Media.Path = old.Media.Path
			}
			if merged.WAMedia == nil {
				merged.WAMedia = old.WAMedia
			}
			if merged.Status == "" || (old.Status == "read" && merged.Status != "read") {
				merged.Status = old.Status
			}
			if old.Edited && !merged.Edited && !merged.Deleted {
				merged.Edited = true
				if merged.Text == "" {
					merged.Text = old.Text
				}
			}
			if old.Deleted {
				merged.Deleted = true
				merged.Text = ""
				merged.Media = nil
			}
			list[i] = &merged
			after, _ := json.Marshal(&merged)
			s.markDirty()
			return false, !bytes.Equal(before, after), snapshot(&merged)
		}
	}
	cp := *m
	s.msgChat[cp.ID] = chat
	s.insertLocked(chat, &cp)
	s.trimLocked(chat)
	s.markDirty()
	return true, true, snapshot(&cp)
}

// mutateMessage applies fn to a stored message; ok=false when it is unknown.
func (s *store) mutateMessage(id string, fn func(m *storedMsg)) (snap Message, changed, ok bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	chat, found := s.msgChat[id]
	if !found {
		return Message{}, false, false
	}
	for _, m := range s.msgs[chat] {
		if m.ID != id {
			continue
		}
		before, _ := json.Marshal(m)
		fn(m)
		after, _ := json.Marshal(m)
		if !bytes.Equal(before, after) {
			s.markDirty()
		}
		return snapshot(m), !bytes.Equal(before, after), true
	}
	return Message{}, false, false
}

func (s *store) message(chat, id string) (storedMsg, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, m := range s.msgs[chat] {
		if m.ID == id {
			cp := *m
			cp.Message = snapshot(m)
			return cp, true
		}
	}
	return storedMsg{}, false
}

func (s *store) removeMessage(chat, id string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	list := s.msgs[chat]
	for i, m := range list {
		if m.ID == id {
			s.msgs[chat] = slices.Delete(slices.Clone(list), i, i+1)
			delete(s.msgChat, id)
			s.markDirty()
			return true
		}
	}
	return false
}

func (s *store) chatOfMessage(id string) (string, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	c, ok := s.msgChat[id]
	return c, ok
}

func (s *store) messageCount(chat string) int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.msgs[chat])
}

// oldest returns the id and timestamp of the oldest stored real message.
func (s *store) oldest(chat string) (id string, ts int64, ok bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	list := s.msgs[chat]
	if len(list) == 0 {
		return "", 0, false
	}
	return list[0].ID, list[0].Timestamp, true
}

// oldestOpen is the oldest message of the chat that did not come over E2EE.
func (s *store) oldestOpen(chat string) (id string, ts int64, ok bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, m := range s.msgs[chat] {
		if m.SenderJID == "" {
			return m.ID, m.Timestamp, true
		}
	}
	return "", 0, false
}

// oldestEncrypted is the oldest message of the chat that came over E2EE.
func (s *store) oldestEncrypted(chat string) (ts int64, ok bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, m := range s.msgs[chat] {
		if m.SenderJID != "" {
			return m.Timestamp, true
		}
	}
	return 0, false
}

// newestOpen is the newest message of the chat that did not come over E2EE.
func (s *store) newestOpen(chat string) (id string, ok bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	list := s.msgs[chat]
	for i := len(list) - 1; i >= 0; i-- {
		if list[i].SenderJID == "" {
			return list[i].ID, true
		}
	}
	return "", false
}

func (s *store) newestTS(chat string) int64 {
	s.mu.RLock()
	defer s.mu.RUnlock()
	list := s.msgs[chat]
	if len(list) == 0 {
		return 0
	}
	return list[len(list)-1].Timestamp
}

func (s *store) lastMessage(chat string) *Message {
	s.mu.RLock()
	defer s.mu.RUnlock()
	list := s.msgs[chat]
	if len(list) == 0 {
		return nil
	}
	snap := snapshot(list[len(list)-1])
	return &snap
}

// listMessages returns up to `limit` messages strictly older than `before`
// (the newest ones when before == ""), ascending. found=false means `before`
// is not a known message.
func (s *store) listMessages(chat, before string, limit int) (out []Message, found bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	list := s.msgs[chat]
	end := len(list)
	if before != "" {
		idx := -1
		for i, m := range list {
			if m.ID == before {
				idx = i
				break
			}
		}
		if idx < 0 {
			return nil, false
		}
		end = idx
	}
	start := max(end-limit, 0)
	out = make([]Message, 0, end-start)
	for _, m := range list[start:end] {
		out = append(out, snapshot(m))
	}
	return out, true
}

// countBefore reports how many stored messages are older than `before`.
func (s *store) countBefore(chat, before string) int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	list := s.msgs[chat]
	if before == "" {
		return len(list)
	}
	for i, m := range list {
		if m.ID == before {
			return i
		}
	}
	return 0
}

// unreadIncoming counts stored incoming messages newer than the read watermark.
func (s *store) unreadIncoming(chat string, watermark int64) int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	n := 0
	for _, m := range s.msgs[chat] {
		if !m.FromMe && !m.Deleted && m.Timestamp > watermark {
			n++
		}
	}
	return n
}

// incomingIDsAfter returns ids (and sender JIDs) of incoming E2EE messages newer than ts.
func (s *store) incomingIDsAfter(chat string, ts int64) []*storedMsg {
	s.mu.RLock()
	defer s.mu.RUnlock()
	var out []*storedMsg
	for _, m := range s.msgs[chat] {
		if !m.FromMe && m.Timestamp > ts && m.SenderJID != "" {
			cp := *m
			out = append(out, &cp)
		}
	}
	return out
}

// ownMessagesUpTo returns ids of my messages not yet marked read up to ts.
func (s *store) markOwnRead(chat string, upTo int64) (changed []Message) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, m := range s.msgs[chat] {
		if m.FromMe && m.Timestamp <= upTo && m.Status != "read" && m.Status != "failed" {
			m.Status = "read"
			changed = append(changed, snapshot(m))
		}
	}
	if len(changed) > 0 {
		s.markDirty()
	}
	return changed
}

func recomputeReactions(m *storedMsg, selfID string) {
	if len(m.Actors) == 0 {
		m.Reactions = nil
		return
	}
	type agg struct {
		count  int
		fromMe bool
	}
	by := map[string]*agg{}
	for actor, emoji := range m.Actors {
		a := by[emoji]
		if a == nil {
			a = &agg{}
			by[emoji] = a
		}
		a.count++
		if actor == selfID {
			a.fromMe = true
		}
	}
	m.Reactions = m.Reactions[:0]
	for emoji, a := range by {
		m.Reactions = append(m.Reactions, Reaction{Emoji: emoji, Count: a.count, FromMe: a.fromMe})
	}
	sort.Slice(m.Reactions, func(i, j int) bool {
		if m.Reactions[i].Count != m.Reactions[j].Count {
			return m.Reactions[i].Count > m.Reactions[j].Count
		}
		return m.Reactions[i].Emoji < m.Reactions[j].Emoji
	})
}

// cutJID splits "user@server" without importing whatsmeow here.
func cutJID(jid string) (user, server string, ok bool) {
	for i := 0; i < len(jid); i++ {
		if jid[i] == '@' {
			return jid[:i], jid[i+1:], true
		}
	}
	return "", "", false
}
