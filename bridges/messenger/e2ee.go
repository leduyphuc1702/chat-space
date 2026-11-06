package main

// End-to-end encrypted (E2EE) Messenger chats.
//
// Facebook delivers E2EE conversations over a WhatsApp-protocol socket (Signal
// double ratchet, "Armadillo"/"ConsumerApplication" payloads). messagix wires this
// to whatsmeow: PrepareE2EEClient() returns a *whatsmeow.Client authenticated
// with a crypto auth token (CAT) fetched through the normal Facebook session, and
// RegisterE2EE() registers a new "linked device" (identity keys + ICDC identity
// list) for the account. whatsmeow keeps that device's keys, Signal sessions,
// sender keys and app state in an SQL store, which here is a pure-Go (modernc)
// SQLite file: ${dataDir}/e2ee.sqlite. The only other persisted bit is
// ${dataDir}/e2ee-state.json (which device id of the account is ours).
//
// This file is the ONLY place that imports whatsmeow, so everything E2EE can be
// reviewed (or cut) here. It mirrors pkg/connector/{client,handlewhatsapp,
// handlematrix}.go and pkg/msgconv/{from,to}-whatsapp.go of mautrix-meta.
//
// What works:   receive and send text (with replies), receive images / videos /
//               audio / documents / stickers (download + decrypt on demand),
//               send images / video / audio / files, reactions both ways, edits and
//               unsends received, unsend of our own messages, read receipts both
//               ways, typing both ways, group chats (name + message flow).
// History:      messages from before this device was registered are only in the
//               account's encrypted backup, readable once the user enters their
//               secure storage PIN (history.go).
// Payloads:     e2ee_content.go maps every ConsumerApplication / Armadillo payload: text
//               with mentions and link previews, media, albums, polls, contacts, locations,
//               link cards, story replies, call logs. Reactions, edits, unsends and
//               protocol-only payloads never create a bubble; what has no sensible text
//               is kind "unsupported" with a short sentence.
// What doesn't: Editing our own E2EE messages from ChatSpace isn't exposed by the protocol.

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"image"
	"image/color"
	"image/draw"
	_ "image/gif"
	_ "image/jpeg"
	"image/png"
	"mime"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMediaTransport"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	wastore "go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/store/sqlstore"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	waLog "go.mau.fi/whatsmeow/util/log"
	"google.golang.org/protobuf/proto"
	_ "modernc.org/sqlite" // pure-Go SQLite driver, registered as "sqlite"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/methods"
	mtypes "go.mau.fi/mautrix-meta/pkg/messagix/types"
)

const (
	e2eeDBName    = "e2ee.sqlite"
	e2eeStateName = "e2ee-state.json"

	e2eeMaxRetryDelay = 10 * time.Minute
	fullRestartGap    = 2 * time.Minute

	// offlineGrace is how long after connecting E2EE messages are treated as a
	// possible replay when the server sends no explicit offline-sync markers.
	offlineGrace = 20 * time.Second
)

// e2eeStateFile records which device of the account is ours.
type e2eeStateFile struct {
	FBID     int64  `json:"fbid"`
	DeviceID uint16 `json:"deviceId"`
}

type e2eeState struct {
	b     *bridge
	ctx   context.Context
	gen   int
	fbid  int64
	plain *messagix.Client

	stopped atomic.Bool
	ready   atomic.Bool // the WhatsApp socket is up
	// offline is true while the server replays what we missed (between
	// OfflineSyncPreview and OfflineSyncCompleted): those messages are not live.
	offline atomic.Bool
	// epoch invalidates the fallback timer that ends the assumed replay window of an
	// earlier connection once a newer connection or an explicit preview took over.
	epoch atomic.Int64

	mu   sync.Mutex
	db   *sql.DB
	dev  *wastore.Device
	cli  *whatsmeow.Client
	self types.JID // our non-AD JID
}

// isE2EEMsg tells whether a stored message came from / went through the E2EE
// transport (these are the only ones that carry a sender JID).
func isE2EEMsg(m storedMsg) bool { return m.SenderJID != "" }

func (e *e2eeState) client() *whatsmeow.Client {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.cli
}

// stop disconnects and closes the device store. Safe to call more than once.
func (e *e2eeState) stop() {
	if e == nil || !e.stopped.CompareAndSwap(false, true) {
		return
	}
	e.ready.Store(false)
	e.mu.Lock()
	cli, db := e.cli, e.db
	e.cli, e.db = nil, nil
	e.mu.Unlock()
	if cli != nil {
		cli.Disconnect()
	}
	if db != nil {
		_ = db.Close()
	}
}

// startE2EE brings up the E2EE socket for the current session. Idempotent: it
// does nothing when E2EE is already running (or starting) for this session.
// Failures never affect the plain Messenger session; registration is retried
// with a growing delay for as long as the session lives.
func (b *bridge) startE2EE(ctx context.Context, gen int, plain *messagix.Client) {
	b.mu.Lock()
	if b.gen != gen || b.e2ee != nil || b.selfID == 0 {
		b.mu.Unlock()
		return
	}
	e := &e2eeState{b: b, ctx: ctx, gen: gen, fbid: b.selfID, plain: plain}
	b.e2ee = e
	b.mu.Unlock()

	delay := time.Duration(0)
	for attempt := 0; ; attempt++ {
		if delay > 0 {
			select {
			case <-time.After(delay):
			case <-ctx.Done():
				b.dropE2EE(e)
				return
			}
		}
		if e.stopped.Load() || !b.isCurrent(gen) || ctx.Err() != nil {
			b.dropE2EE(e)
			return
		}
		err := e.connect(ctx)
		if err == nil {
			return
		}
		if e.stopped.Load() {
			return
		}
		if attempt == 0 {
			b.notify("warn", "End-to-end encrypted chats are unavailable for now: "+err.Error())
		} else {
			b.log.Warn().Err(err).Int("attempt", attempt+1).Msg("e2ee connect failed")
		}
		if cli := e.client(); cli != nil {
			cli.Disconnect()
		}
		switch {
		case delay == 0:
			delay = 10 * time.Second
		default:
			delay = min(delay*2, e2eeMaxRetryDelay)
		}
	}
}

func (b *bridge) dropE2EE(e *e2eeState) {
	b.mu.Lock()
	if b.e2ee == e {
		b.e2ee = nil
	}
	b.mu.Unlock()
	e.stop()
}

func (e *e2eeState) dbDSN() string {
	// Plain path + query (modernc.org/sqlite). whatsmeow's sqlstore requires
	// foreign keys; WAL + busy_timeout avoid "database is locked" under load.
	return filepath.Join(e.b.dataDir, e2eeDBName) +
		"?_pragma=busy_timeout(10000)&_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)"
}

func (e *e2eeState) statePath() string { return filepath.Join(e.b.dataDir, e2eeStateName) }

func (e *e2eeState) loadState() (s e2eeStateFile) {
	data, err := os.ReadFile(e.statePath())
	if err == nil {
		_ = json.Unmarshal(data, &s)
	}
	return s
}

func (e *e2eeState) saveState(s e2eeStateFile) error {
	data, _ := json.Marshal(s)
	return atomicWrite(e.statePath(), data, 0o600)
}

// connect opens the device store, restores or registers our device, and
// connects the WhatsApp socket.
func (e *e2eeState) connect(ctx context.Context) error {
	b := e.b
	if err := os.MkdirAll(b.dataDir, 0o700); err != nil {
		return err
	}
	e.mu.Lock()
	db := e.db
	e.mu.Unlock()
	if db == nil {
		var err error
		db, err = sql.Open("sqlite", e.dbDSN())
		if err != nil {
			return fmt.Errorf("could not open the E2EE key store: %w", err)
		}
		e.mu.Lock()
		if e.stopped.Load() {
			e.mu.Unlock()
			_ = db.Close()
			return context.Canceled
		}
		e.db = db
		e.mu.Unlock()
	}
	wlog := waLog.Zerolog(b.log.With().Str("component", "whatsmeow").Logger())
	container := sqlstore.NewWithDB(db, "sqlite", wlog)
	if err := container.Upgrade(ctx); err != nil {
		return fmt.Errorf("could not initialise the E2EE key store: %w", err)
	}

	fbidStr := strconv.FormatInt(e.fbid, 10)
	saved := e.loadState()
	var dev *wastore.Device
	if saved.FBID == e.fbid && saved.DeviceID != 0 {
		var err error
		dev, err = container.GetDevice(ctx, types.JID{User: fbidStr, Device: saved.DeviceID, Server: types.MessengerServer})
		if err != nil {
			return fmt.Errorf("could not load the E2EE device: %w", err)
		}
		if dev == nil {
			b.log.Warn().Uint16("device_id", saved.DeviceID).Msg("saved E2EE device is missing from the store, registering a new one")
		}
	}
	isNew := dev == nil
	if isNew {
		dev = container.NewDevice()
		// Messenger Lite Android logins have a device identity of their own (nil-safe
		// for every other platform); the connector registers E2EE under the same one.
		if sess := e.plain.MessengerLite.GetNativeSession(); sess != nil {
			dev.FacebookUUID = sess.DeviceID
		}
	}
	e.plain.SetDevice(dev)
	if isNew {
		b.log.Info().Msg("registering a new end-to-end encryption device")
		if err := e.plain.RegisterE2EE(ctx, e.fbid); err != nil {
			return fmt.Errorf("could not register an E2EE device: %w", err)
		}
		if err := dev.Save(ctx); err != nil {
			return fmt.Errorf("could not save the E2EE device: %w", err)
		}
		if err := e.saveState(e2eeStateFile{FBID: e.fbid, DeviceID: dev.ID.Device}); err != nil {
			b.log.Warn().Err(err).Msg("could not persist the E2EE device id")
		}
	}

	cli, err := e.plain.PrepareE2EEClient()
	if err != nil {
		return fmt.Errorf("could not prepare the E2EE client: %w", err)
	}
	// Same as the mautrix-meta connector when no portal event buffer is used:
	// events are acknowledged to the server only after we handled them, and are
	// delivered in decrypted order.
	cli.SynchronousAck = true
	cli.EnableDecryptedEventBuffer = true
	cli.AddEventHandlerWithSuccessStatus(e.handle)

	e.mu.Lock()
	if e.stopped.Load() {
		e.mu.Unlock()
		return context.Canceled
	}
	e.dev, e.cli = dev, cli
	e.self = dev.ID.ToNonAD()
	e.mu.Unlock()

	if err := cli.Connect(); err != nil {
		return fmt.Errorf("could not connect the E2EE socket: %w", err)
	}
	return nil
}

// recover handles permanent E2EE failures, like the connector does: optionally
// forget the device (so a fresh one is registered), then either restart only the
// E2EE client or the whole Facebook session (which re-fetches the CAT).
func (e *e2eeState) recoverAsync(why string, resetDevice, fullReconnect bool) {
	b := e.b
	b.notify("warn", "End-to-end encryption disconnected ("+why+"), reconnecting")
	b.go_("e2ee-recover", func() {
		if resetDevice {
			e.resetDevice()
		}
		if fullReconnect {
			b.mu.Lock()
			ck, native, cur := b.ck, b.native, b.gen == e.gen
			b.mu.Unlock()
			if !cur || ck == nil || !allowFullRestart() {
				return
			}
			b.startSession(ck, native)
			return
		}
		b.dropE2EE(e)
		select {
		case <-time.After(3 * time.Second):
		case <-e.ctx.Done():
			return
		}
		b.startE2EE(e.ctx, e.gen, e.plain)
	})
}

var (
	restartMu   sync.Mutex
	lastRestart time.Time
)

// allowFullRestart rate-limits full session restarts triggered by E2EE errors.
func allowFullRestart() bool {
	restartMu.Lock()
	defer restartMu.Unlock()
	if time.Since(lastRestart) < fullRestartGap {
		return false
	}
	lastRestart = time.Now()
	return true
}

func (e *e2eeState) resetDevice() {
	e.mu.Lock()
	dev := e.dev
	e.mu.Unlock()
	if dev != nil && dev.ID != nil {
		if err := dev.Delete(e.ctx); err != nil {
			e.b.log.Warn().Err(err).Msg("could not delete the E2EE device")
		}
	}
	_ = os.Remove(e.statePath())
}

// Event handling ---------------------------------------------------------------------

func (e *e2eeState) handle(raw any) (ok bool) {
	if e.stopped.Load() {
		return true
	}
	defer func() {
		if r := recover(); r != nil {
			e.b.log.Error().Interface("panic", r).Type("event", raw).Msg("panic while handling an E2EE event")
			ok = true // acknowledge, retrying a crashing event would loop forever
		}
	}()
	switch evt := raw.(type) {
	case *events.FBMessage:
		e.onMessage(evt)
	case *events.Receipt:
		e.onReceipt(evt)
	case *events.ChatPresence:
		e.onChatPresence(evt)
	case *events.Connected:
		e.ready.Store(true)
		e.b.log.Info().Msg("connected to the end-to-end encryption socket")
		// Anything delivered right after connecting may be the replay of what we
		// missed. Assume so until the server says otherwise (OfflineSyncPreview /
		// OfflineSyncCompleted), with a timeout in case it never does.
		n := e.epoch.Add(1)
		e.offline.Store(true)
		time.AfterFunc(offlineGrace, func() {
			if e.epoch.Load() == n {
				e.offline.Store(false)
			}
		})
		// WhatsApp only pushes typing notifications to clients that are online.
		if cli := e.client(); cli != nil {
			go func() {
				if err := cli.SendPresence(e.ctx, types.PresenceAvailable); err != nil {
					e.b.log.Debug().Err(err).Msg("could not set E2EE presence")
				}
			}()
		}
	case *events.OfflineSyncPreview:
		e.epoch.Add(1) // from here on only OfflineSyncCompleted ends the replay
		e.offline.Store(evt.Total > 0 || evt.Messages > 0)
	case *events.OfflineSyncCompleted:
		e.epoch.Add(1)
		e.offline.Store(false)
	case *events.Disconnected:
		e.ready.Store(false)
		e.epoch.Add(1)
		e.offline.Store(false)
		e.b.log.Debug().Msg("disconnected from the end-to-end encryption socket")
	case *events.CATRefreshError:
		e.ready.Store(false)
		if errors.Is(evt.Error, mtypes.ErrPleaseReloadPage) {
			e.recoverAsync("the encryption token needs a page reload", false, true)
		} else {
			e.recoverAsync(evt.PermanentDisconnectDescription(), false, true)
		}
	case events.PermanentDisconnect:
		e.ready.Store(false)
		switch v := evt.(type) {
		case *events.LoggedOut:
			if v.Reason == events.ConnectFailureLoggedOut && !v.OnConnect {
				e.recoverAsync("device logged out", true, true)
				return true
			}
		case *events.ConnectFailure:
			switch v.Reason {
			case events.ConnectFailureNotFound:
				e.recoverAsync("device not found", true, false)
				return true
			case events.ConnectFailureClientUnknown:
				e.recoverAsync("unknown client", true, true)
				return true
			case events.ConnectFailureGeneric:
				e.recoverAsync("connection rejected", false, true)
				return true
			}
		}
		e.b.notify("warn", "End-to-end encryption stopped: "+evt.PermanentDisconnectDescription())
	}
	return true
}

func (e *e2eeState) selfJID() types.JID {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.self
}

// chatIDFor maps a WhatsApp-side chat JID to our chat id (the Facebook thread key
// when the hybrid mapping is known, else the JID user).
func (b *bridge) chatIDFor(jid types.JID) string { return b.st.chatForJID(jid.ToNonAD().User) }

func (e *e2eeState) displayName(user, pushName string) string {
	if c, ok := e.b.st.contact(user); ok && c.Name != "" {
		return c.Name
	}
	return pushName
}

func (e *e2eeState) replyView(chat, id string) *Reply {
	if id == "" {
		return nil
	}
	r := &Reply{ID: id}
	if q, ok := e.b.st.message(chat, id); ok {
		r.Text, r.SenderName, r.Kind = q.Text, q.SenderName, q.Kind
	}
	return r
}

func waMediaKind(typ string) string {
	switch typ {
	case "image":
		return "image"
	case "video":
		return "video"
	case "audio":
		return "voice"
	}
	return "file"
}

func defaultMime(typ string) string {
	switch typ {
	case "image":
		return "image/jpeg"
	case "video":
		return "video/mp4"
	case "audio":
		return "audio/ogg"
	}
	return "application/octet-stream"
}

// transportInfo extracts download info + basic metadata from a media transport.
func transportInfo(tr *waMediaTransport.WAMediaTransport, typ string) (*Media, *waMediaRef) {
	integral, anc := tr.GetIntegral(), tr.GetAncillary()
	m := &Media{Mime: firstNonEmpty(anc.GetMimetype(), defaultMime(typ)), Size: int64(anc.GetFileLength())}
	ref := &waMediaRef{
		Key:        integral.GetMediaKey(),
		SHA256:     integral.GetFileSHA256(),
		EncSHA256:  integral.GetFileEncSHA256(),
		DirectPath: integral.GetDirectPath(),
		Type:       typ,
	}
	return m, ref
}

func (e *e2eeState) onMessage(evt *events.FBMessage) { e.ingest(evt, false) }

// ingest stores one decrypted message. history marks messages restored from the
// encrypted backup (history.go): they never count as unread and are returned by
// messages.list instead of being pushed as message.new.
//
// classify (e2ee_content.go) decides what the payload is: a reaction, edit or unsend
// (applied to an existing message, no bubble), a protocol-only payload (nothing), or one
// or more parts that become messages. Parts of one payload (an album, a photo with its
// caption) share a groupId, the id of the first.
func (e *e2eeState) ingest(evt *events.FBMessage, history bool) {
	b := e.b
	if evt.Message == nil {
		return // e.g. disappearing-message setting changes
	}
	selfID, selfName := b.selfInfo()
	selfStr := chatKey(selfID)
	chatJID := evt.Info.Chat.ToNonAD()
	chat := b.chatIDFor(chatJID)
	fromMe := evt.Info.IsFromMe
	ts := evt.Info.Timestamp.UnixMilli()
	if ts <= 0 {
		ts = time.Now().UnixMilli()
	}
	actor := evt.Info.Sender.ToNonAD().User
	if fromMe {
		actor = selfStr
	}
	senderName, who := selfName, "You"
	if !fromMe {
		senderName = e.displayName(actor, evt.Info.PushName)
		who = senderName
	}

	act := e.classify(evt, chat, who)
	switch {
	case act.reaction != nil:
		e.onReaction(chat, actor, act.reaction)
		return
	case act.edit != nil:
		e.onEdit(chat, act.edit)
		return
	case act.revokeID != "":
		e.onRevoke(chat, act.revokeID)
		return
	case len(act.parts) == 0:
		return // protocol-only: nothing to show
	}
	if act.unsupported != "" {
		// Only the payload type, never its content: this is how new kinds get noticed.
		b.log.Info().Str("type", act.unsupported).Msg("e2ee: payload shown as unsupported")
	}

	// Make sure the chat exists / is flagged E2EE.
	isGroup := chatJID.Server == types.GroupServer
	_, created := b.st.updateThread(chat, func(t *threadRec) {
		t.E2EE = true
		t.JID = chatJID.String()
		t.IsGroup = isGroup
		if !isGroup && !fromMe && t.Name == "" && evt.Info.PushName != "" {
			t.Name = evt.Info.PushName
		}
	})
	if !fromMe && evt.Info.PushName != "" {
		if _, has := b.st.contact(actor); !has {
			b.st.setContact(actor, evt.Info.PushName, "")
		}
	}

	groupID := evt.FBApplication.GetMetadata().GetGroupID()
	if groupID == "" && len(act.parts) > 1 {
		groupID = evt.Info.ID
	}
	var replyTo string
	var reply *Reply
	if qm := evt.FBApplication.GetMetadata().GetQuotedMessage(); qm != nil {
		replyTo = qm.GetStanzaID()
		reply = e.replyView(chat, replyTo)
	}

	type stored struct {
		isNew, changed bool
		snap           Message
	}
	var out []stored
	for i, c := range act.parts {
		id, pts := evt.Info.ID, ts
		if i > 0 {
			id, pts = fmt.Sprintf("%s:%d", evt.Info.ID, i), ts+int64(i)
		}
		sm := &storedMsg{
			Message: Message{
				ID: id, ChatID: chat, SenderID: actor, SenderName: senderName, FromMe: fromMe,
				Timestamp: pts, Kind: c.kind, Text: c.text, Media: c.media, Links: c.links, GroupID: groupID,
			},
			Sort:      pts,
			SenderJID: evt.Info.Sender.ToNonAD().String(),
			WAMedia:   c.ref,
		}
		if fromMe {
			sm.Status = "sent"
		}
		if i == 0 && replyTo != "" {
			sm.WAReplyTo, sm.ReplyTo = replyTo, reply
		}
		isNew, changed, snap := b.st.putMessage(sm)
		if isNew || changed {
			out = append(out, stored{isNew, changed, snap})
		}
	}
	if len(out) == 0 {
		return
	}
	last := out[len(out)-1].snap
	anyNew := false
	for _, o := range out {
		anyNew = anyNew || o.isNew
	}
	if anyNew {
		b.st.updateThread(chat, func(t *threadRec) {
			if ts >= t.LastActivity {
				t.LastActivity = ts
				if txt := previewText(last); txt != "" {
					t.Snippet = txt
				}
			}
			if history {
				return
			}
			if fromMe {
				// Sending (from any device) marks the chat read.
				t.LastRead = max(t.LastRead, ts)
				t.Unread = 0
			} else if ts > t.LastRead {
				t.Unread++ // one per payload, however many bubbles it makes
			}
		})
	}
	if created {
		b.emitChat(chat) // the app learns about the chat before its first message
	}
	// Live = arrived in real time on an established E2EE socket, not part of the offline
	// replay and not old. The chat.upsert right after carries the new unread.
	live := e.ready.Load() && !e.offline.Load() && isFresh(ts)
	for _, o := range out {
		switch {
		case history:
			// Restored pages reach the app through messages.list; the caller emits the chat.
		case o.isNew:
			b.emit("message.new", map[string]any{"message": o.snap, "live": live})
		default:
			b.emit("message.update", map[string]any{"message": o.snap})
		}
	}
	if anyNew && !history {
		b.emitChat(chat)
	}
	if isGroup {
		if t, ok := b.st.thread(chat); ok && t.Name == "" {
			b.go_("e2ee-groupinfo", func() { e.fetchGroupInfo(chat, chatJID) })
		}
	}
}

func (e *e2eeState) fetchGroupInfo(chat string, jid types.JID) {
	cli := e.client()
	if cli == nil {
		return
	}
	ctx, cancel := context.WithTimeout(e.ctx, 20*time.Second)
	defer cancel()
	info, err := cli.GetGroupInfo(ctx, jid)
	if err != nil || info == nil {
		e.b.log.Debug().Err(err).Str("jid", jid.String()).Msg("could not load E2EE group info")
		return
	}
	e.b.st.updateThread(chat, func(t *threadRec) {
		if info.Name != "" {
			t.Name = info.Name
		}
		t.MemberCount = len(info.Participants)
	})
	e.b.emitChat(chat)
}

const (
	memberFetchTTL     = 5 * time.Minute  // a successful E2EE group member lookup is reused this long
	memberFetchFailTTL = 30 * time.Second // and a failed one is not retried before this
)

type cachedMembers struct {
	at  time.Time
	ttl time.Duration
	ids []string
}

// e2eeGroupMembers lists the Facebook user ids of an end-to-end encrypted group
// through whatsmeow, for groups without synced participant rows (chats.members).
// Best effort: it never waits for E2EE start-up and asks the server at most once per
// memberFetchTTL per chat. Only "@msgr" participants are Facebook users.
func (b *bridge) e2eeGroupMembers(ctx context.Context, t threadRec) []string {
	b.memberMu.Lock()
	if c, ok := b.memberCache[t.ID]; ok && time.Since(c.at) < c.ttl {
		ids := c.ids
		b.memberMu.Unlock()
		return ids
	}
	b.memberMu.Unlock()

	b.mu.Lock()
	e := b.e2ee
	b.mu.Unlock()
	if e == nil || !e.ready.Load() {
		return nil
	}
	cli := e.client()
	if cli == nil {
		return nil
	}
	remember := func(ids []string, ttl time.Duration) {
		b.memberMu.Lock()
		if b.memberCache == nil {
			b.memberCache = map[string]cachedMembers{}
		}
		b.memberCache[t.ID] = cachedMembers{at: time.Now(), ttl: ttl, ids: ids}
		b.memberMu.Unlock()
	}
	jid, err := chatJIDOf(t)
	if err != nil {
		return nil
	}
	reqCtx, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	info, err := cli.GetGroupInfo(reqCtx, jid)
	if err != nil || info == nil {
		b.log.Debug().Err(err).Str("jid", jid.String()).Msg("could not load E2EE group members")
		remember(nil, memberFetchFailTTL)
		return nil
	}
	var ids []string
	for _, p := range info.Participants {
		if p.JID.Server == types.MessengerServer && isNumericID(p.JID.User) {
			ids = append(ids, p.JID.User)
		}
	}
	remember(ids, memberFetchTTL)
	return ids
}

func (e *e2eeState) onReaction(chat, actor string, r *waConsumerApplication.ConsumerApplication_ReactionMessage) {
	b := e.b
	id := r.GetKey().GetID()
	if id == "" {
		return
	}
	selfID, _ := b.selfInfo()
	emoji := r.GetText()
	snap, changed, ok := b.st.mutateMessage(id, func(m *storedMsg) {
		if emoji == "" {
			delete(m.Actors, actor)
		} else {
			if m.Actors == nil {
				m.Actors = map[string]string{}
			}
			m.Actors[actor] = emoji
		}
		recomputeReactions(m, chatKey(selfID))
	})
	if ok && changed {
		b.emit("message.update", map[string]any{"message": snap})
	}
}

func (e *e2eeState) onEdit(chat string, ed *waConsumerApplication.ConsumerApplication_EditMessage) {
	b := e.b
	id := ed.GetKey().GetID()
	if id == "" {
		return
	}
	text := e.textOf(chat, ed.GetMessage())
	snap, changed, ok := b.st.mutateMessage(id, func(m *storedMsg) {
		if m.Deleted || m.Text == text {
			return
		}
		m.Text, m.Edited = text, true
		m.Links = nil // their offsets belonged to the old text
	})
	if ok && changed {
		b.emit("message.update", map[string]any{"message": snap})
		if last := b.st.lastMessage(chat); last != nil && last.ID == id {
			b.st.updateThread(chat, func(t *threadRec) { t.Snippet = previewText(snap) })
			b.emitChat(chat)
		}
	}
}

func (e *e2eeState) onRevoke(chat, id string) {
	b := e.b
	if id == "" {
		return
	}
	// A payload that made several bubbles (album, photo + caption) is unsent as a whole:
	// its extra parts are "<id>:<n>".
	changedAny := false
	for n := 0; n < 20; n++ {
		target := id
		if n > 0 {
			target = fmt.Sprintf("%s:%d", id, n)
		}
		snap, changed, ok := b.st.mutateMessage(target, func(m *storedMsg) {
			m.Deleted, m.Text, m.Media, m.WAMedia, m.Actors, m.Reactions, m.Links = true, "", nil, nil, nil, nil, nil
		})
		if !ok {
			break
		}
		if changed {
			changedAny = true
			b.emit("message.update", map[string]any{"message": snap})
		}
	}
	if changedAny {
		b.emitChat(chat)
	}
}

func (e *e2eeState) onReceipt(evt *events.Receipt) {
	b := e.b
	chat := b.chatIDFor(evt.Chat)
	var upTo int64
	for _, id := range evt.MessageIDs {
		if m, ok := b.st.message(chat, id); ok {
			upTo = max(upTo, m.Timestamp)
		}
	}
	switch evt.Type {
	case types.ReceiptTypeRead:
		if evt.IsGroup || upTo == 0 {
			return
		}
		for _, m := range b.st.markOwnRead(chat, upTo) {
			b.emit("message.update", map[string]any{"message": m})
		}
		b.st.updateThread(chat, func(t *threadRec) { t.PeerRead = max(t.PeerRead, upTo) })
	case types.ReceiptTypeReadSelf:
		if upTo == 0 {
			upTo = evt.Timestamp.UnixMilli()
		}
		// Read on another device. The count is worked out before taking the store
		// lock: updateThread holds it while running the callback.
		cur, _ := b.st.thread(chat)
		lastRead := max(cur.LastRead, upTo)
		unread := b.settledUnread(chat, cur, lastRead)
		b.st.updateThread(chat, func(t *threadRec) {
			t.LastRead = max(t.LastRead, lastRead)
			t.Unread = unread
		})
		b.emitChat(chat)
	case types.ReceiptTypeDelivered:
		for _, id := range evt.MessageIDs {
			if snap, changed, ok := b.st.mutateMessage(id, func(m *storedMsg) {
				if m.FromMe && m.Status == "sent" {
					m.Status = "delivered"
				}
			}); ok && changed {
				b.emit("message.update", map[string]any{"message": snap})
			}
		}
	}
}

func (e *e2eeState) onChatPresence(evt *events.ChatPresence) {
	if evt.IsFromMe {
		return
	}
	b := e.b
	user := evt.Sender.ToNonAD().User
	if user == "" {
		user = evt.Chat.User
	}
	data := map[string]any{
		"chatId": b.chatIDFor(evt.Chat),
		"userId": user,
		"typing": evt.State == types.ChatPresenceComposing,
	}
	if name := e.displayName(user, ""); name != "" {
		data["name"] = name
	}
	b.emit("typing", data)
}

// Sending ----------------------------------------------------------------------------

// e2eeClient returns the connected E2EE client, waiting briefly for a socket that
// is still starting up (device registration takes a few seconds).
func (b *bridge) e2eeClient(ctx context.Context) (*e2eeState, *whatsmeow.Client, error) {
	deadline := time.Now().Add(30 * time.Second)
	for {
		b.mu.Lock()
		e := b.e2ee
		b.mu.Unlock()
		if e == nil {
			return nil, nil, errors.New("end-to-end encryption is not available: it failed to start or is still being set up (see the log)")
		}
		if cli := e.client(); cli != nil && e.ready.Load() {
			return e, cli, nil
		}
		if time.Now().After(deadline) {
			return nil, nil, errors.New("end-to-end encryption is not connected yet; try again in a moment")
		}
		select {
		case <-time.After(300 * time.Millisecond):
		case <-ctx.Done():
			return nil, nil, ctx.Err()
		}
	}
}

func chatJIDOf(t threadRec) (types.JID, error) {
	raw := t.JID
	if raw == "" {
		if t.IsGroup {
			raw = peerID(t) + "@" + types.GroupServer
		} else {
			raw = peerID(t) + "@" + types.MessengerServer
		}
	}
	jid, err := types.ParseJID(raw)
	if err != nil || jid.IsEmpty() {
		return types.JID{}, fmt.Errorf("invalid end-to-end encrypted chat address %q", raw)
	}
	return jid, nil
}

func newWAMessageID() string { return strconv.FormatInt(methods.GenerateEpochID(), 10) }

func wrapContent(c *waConsumerApplication.ConsumerApplication_Content) *waConsumerApplication.ConsumerApplication {
	return &waConsumerApplication.ConsumerApplication{
		Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_Content{Content: c},
		},
	}
}

func waKeyFor(chatJID types.JID, m storedMsg) *waCommon.MessageKey {
	key := &waCommon.MessageKey{
		RemoteJID: proto.String(chatJID.String()),
		ID:        proto.String(m.ID),
	}
	if m.FromMe {
		key.FromMe = proto.Bool(true)
	}
	if chatJID.Server != types.MessengerServer && chatJID.Server != types.DefaultUserServer {
		key.Participant = proto.String(m.SenderJID)
	}
	return key
}

func (b *bridge) e2eeSend(ctx context.Context, t threadRec, text, replyTo, filePath string, mentions []mentionSpan) (Message, error) {
	e, cli, err := b.e2eeClient(ctx)
	if err != nil {
		return Message{}, err
	}
	chatJID, err := chatJIDOf(t)
	if err != nil {
		return Message{}, err
	}
	var meta waMsgApplication.MessageApplication_Metadata
	var replyView *Reply
	if replyTo != "" {
		orig, ok := b.st.message(t.ID, replyTo)
		switch {
		case !ok:
			return Message{}, newErr(codeNotFound, "message %s not found in chat %s", replyTo, t.ID)
		case orig.SenderJID == "":
			return Message{}, newErr(codeUnsupported, "cannot reply to a message from before end-to-end encryption was enabled")
		}
		meta.QuotedMessage = &waMsgApplication.MessageApplication_Metadata_QuotedMessage{
			StanzaID:    proto.String(orig.ID),
			Participant: proto.String(orig.SenderJID),
		}
		replyView = &Reply{ID: orig.ID, Text: orig.Text, SenderName: orig.SenderName, Kind: orig.Kind}
	}

	// On the wire each mention is a "@<fbid>@msgr" token plus the JID in MentionedJID;
	// the message we store and return keeps the text the user typed ("@Name").
	kind, sendText := "text", text
	wireText, mentionedJIDs := rewriteE2EEMentions(text, mentions)
	var media *Media
	var content *waConsumerApplication.ConsumerApplication_Content
	extraText := ""
	if filePath == "" {
		content = &waConsumerApplication.ConsumerApplication_Content{
			Content: &waConsumerApplication.ConsumerApplication_Content_MessageText{
				MessageText: &waCommon.MessageText{Text: proto.String(wireText), MentionedJID: mentionedJIDs},
			},
		}
	} else {
		var captioned bool
		content, kind, media, captioned, err = buildMediaContent(ctx, cli, filePath, wireText, mentionedJIDs)
		if err != nil {
			return Message{}, err
		}
		if !captioned {
			sendText, extraText = "", text // audio/documents have no caption field
		}
	}

	id := newWAMessageID()
	resp, err := cli.SendFBMessage(ctx, chatJID, wrapContent(content), &meta, whatsmeow.SendRequestExtra{ID: id})
	if err != nil {
		return Message{}, fmt.Errorf("failed to send encrypted message: %w", err)
	}
	ts := resp.Timestamp.UnixMilli()
	if resp.Timestamp.IsZero() {
		ts = time.Now().UnixMilli()
	}
	selfID, selfName := b.selfInfo()
	sm := &storedMsg{
		Message: Message{
			ID: id, ChatID: t.ID, SenderID: chatKey(selfID), SenderName: selfName, FromMe: true,
			Timestamp: ts, Kind: kind, Text: sendText, Media: media, ReplyTo: replyView, Status: "sent",
		},
		Sort:      ts,
		SenderJID: e.selfJID().String(),
		WAReplyTo: replyTo,
	}
	_, _, snap := b.st.putMessage(sm)
	b.touchAfterSend(t.ID, snap)

	if extraText != "" {
		if _, err := b.e2eeSend(ctx, t, extraText, "", "", mentions); err != nil {
			b.log.Warn().Err(err).Msg("could not send the text that accompanied the file")
		}
	}
	return snap, nil
}

// buildMediaContent uploads a local file to the WhatsApp media servers (encrypted)
// and wraps it in the matching ConsumerApplication message. Same layout as
// MessageConverter.ToWhatsApp in mautrix-meta. captioned reports whether the
// message type carries the caption text.
func buildMediaContent(ctx context.Context, cli *whatsmeow.Client, path, caption string, mentionedJIDs []string) (
	content *waConsumerApplication.ConsumerApplication_Content, kind string, media *Media, captioned bool, err error,
) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, "", nil, false, newErr(codeInvalidParams, "cannot read %q: %v", path, err)
	}
	mimeType := mime.TypeByExtension(strings.ToLower(filepath.Ext(path)))
	if mimeType == "" {
		mimeType = http.DetectContentType(data)
	}
	if mt, _, perr := mime.ParseMediaType(mimeType); perr == nil {
		mimeType = mt
	}
	fileName := filepath.Base(path)

	var mediaType whatsmeow.MediaType
	var typ string
	isGIF := mimeType == "image/gif"
	switch {
	case isGIF:
		// Facebook wants GIFs inside a VideoMessage flagged as gif playback.
		mediaType, typ, kind = whatsmeow.MediaVideo, "video", "gif"
	case strings.HasPrefix(mimeType, "image/"):
		mediaType, typ, kind = whatsmeow.MediaImage, "image", "image"
	case strings.HasPrefix(mimeType, "video/"):
		mediaType, typ, kind = whatsmeow.MediaVideo, "video", "video"
	case strings.HasPrefix(mimeType, "audio/"):
		mediaType, typ, kind = whatsmeow.MediaAudio, "audio", "audio"
	default:
		mediaType, typ, kind = whatsmeow.MediaDocument, "document", "file"
	}

	width, height := 0, 0
	if typ == "image" || isGIF {
		if mimeType == "image/png" {
			data = normalizePNG(data)
		}
		if cfg, _, derr := image.DecodeConfig(bytes.NewReader(data)); derr == nil {
			width, height = cfg.Width, cfg.Height
		}
	}
	uploaded, err := cli.Upload(ctx, data, mediaType)
	if err != nil {
		return nil, "", nil, false, fmt.Errorf("media upload failed: %w", err)
	}
	tw, th := clampTo400(width, height)
	if tw == 0 && typ == "image" {
		tw, th = 400, 400 // iOS refuses to show images without a thumbnail size
	}
	transport := &waMediaTransport.WAMediaTransport{
		Integral: &waMediaTransport.WAMediaTransport_Integral{
			FileSHA256:        uploaded.FileSHA256,
			MediaKey:          uploaded.MediaKey,
			FileEncSHA256:     uploaded.FileEncSHA256,
			DirectPath:        &uploaded.DirectPath,
			MediaKeyTimestamp: proto.Int64(time.Now().Unix()),
		},
		Ancillary: &waMediaTransport.WAMediaTransport_Ancillary{
			FileLength: proto.Uint64(uint64(len(data))),
			Mimetype:   &mimeType,
			Thumbnail: &waMediaTransport.WAMediaTransport_Ancillary_Thumbnail{
				ThumbnailWidth:  proto.Uint32(uint32(tw)),
				ThumbnailHeight: proto.Uint32(uint32(th)),
			},
			ObjectID: &uploaded.ObjectID,
		},
	}
	captionMsg := &waCommon.MessageText{}
	if caption != "" {
		captionMsg = &waCommon.MessageText{Text: proto.String(caption), MentionedJID: mentionedJIDs}
	}

	content = &waConsumerApplication.ConsumerApplication_Content{}
	switch {
	case isGIF:
		msg := &waConsumerApplication.ConsumerApplication_VideoMessage{Caption: captionMsg}
		err = msg.Set(&waMediaTransport.VideoTransport{
			Integral: &waMediaTransport.VideoTransport_Integral{Transport: transport},
			Ancillary: &waMediaTransport.VideoTransport_Ancillary{
				Height: proto.Uint32(uint32(height)), Width: proto.Uint32(uint32(width)), GifPlayback: proto.Bool(true),
			},
		})
		content.Content = &waConsumerApplication.ConsumerApplication_Content_VideoMessage{VideoMessage: msg}
		captioned = true
	case typ == "image":
		msg := &waConsumerApplication.ConsumerApplication_ImageMessage{Caption: captionMsg}
		err = msg.Set(&waMediaTransport.ImageTransport{
			Integral:  &waMediaTransport.ImageTransport_Integral{Transport: transport},
			Ancillary: &waMediaTransport.ImageTransport_Ancillary{Height: proto.Uint32(uint32(height)), Width: proto.Uint32(uint32(width))},
		})
		content.Content = &waConsumerApplication.ConsumerApplication_Content_ImageMessage{ImageMessage: msg}
		captioned = true
	case typ == "video":
		msg := &waConsumerApplication.ConsumerApplication_VideoMessage{Caption: captionMsg}
		err = msg.Set(&waMediaTransport.VideoTransport{
			Integral:  &waMediaTransport.VideoTransport_Integral{Transport: transport},
			Ancillary: &waMediaTransport.VideoTransport_Ancillary{},
		})
		content.Content = &waConsumerApplication.ConsumerApplication_Content_VideoMessage{VideoMessage: msg}
		captioned = true
	case typ == "audio":
		msg := &waConsumerApplication.ConsumerApplication_AudioMessage{PTT: proto.Bool(false)}
		err = msg.Set(&waMediaTransport.AudioTransport{
			Integral:  &waMediaTransport.AudioTransport_Integral{Transport: transport},
			Ancillary: &waMediaTransport.AudioTransport_Ancillary{Seconds: proto.Uint32(0)},
		})
		content.Content = &waConsumerApplication.ConsumerApplication_Content_AudioMessage{AudioMessage: msg}
	default:
		msg := &waConsumerApplication.ConsumerApplication_DocumentMessage{FileName: &fileName}
		err = msg.Set(&waMediaTransport.DocumentTransport{
			Integral:  &waMediaTransport.DocumentTransport_Integral{Transport: transport},
			Ancillary: &waMediaTransport.DocumentTransport_Ancillary{},
		})
		content.Content = &waConsumerApplication.ConsumerApplication_Content_DocumentMessage{DocumentMessage: msg}
	}
	if err != nil {
		return nil, "", nil, false, fmt.Errorf("could not build the media message: %w", err)
	}
	media = &Media{Mime: mimeType, FileName: fileName, Size: int64(len(data)), Width: width, Height: height, Path: path}
	return content, kind, media, captioned, nil
}

func clampTo400(w, h int) (int, int) {
	if w > 400 {
		h = h * 400 / w
		w = 400
	}
	if h > 400 {
		w = w * 400 / h
		h = 400
	}
	return w, h
}

// normalizePNG re-encodes PNGs with exotic colour models as NRGBA: Messenger
// clients reject some of them (see reencodeNRGBA in mautrix-meta).
func normalizePNG(data []byte) []byte {
	cfg, err := png.DecodeConfig(bytes.NewReader(data))
	if err != nil || cfg.ColorModel == color.NRGBAModel {
		return data
	}
	img, err := png.Decode(bytes.NewReader(data))
	if err != nil {
		return data
	}
	bounds := img.Bounds()
	out := image.NewNRGBA(image.Rect(0, 0, bounds.Dx(), bounds.Dy()))
	draw.Draw(out, out.Bounds(), img, bounds.Min, draw.Src)
	var buf bytes.Buffer
	if err := png.Encode(&buf, out); err != nil {
		return data
	}
	return buf.Bytes()
}

func (b *bridge) e2eeSendControl(ctx context.Context, t threadRec, content *waConsumerApplication.ConsumerApplication) error {
	_, cli, err := b.e2eeClient(ctx)
	if err != nil {
		return err
	}
	chatJID, err := chatJIDOf(t)
	if err != nil {
		return err
	}
	_, err = cli.SendFBMessage(ctx, chatJID, content, nil, whatsmeow.SendRequestExtra{ID: newWAMessageID()})
	return err
}

func (b *bridge) e2eeReact(ctx context.Context, t threadRec, m storedMsg, emoji string) error {
	chatJID, err := chatJIDOf(t)
	if err != nil {
		return err
	}
	app := wrapContent(&waConsumerApplication.ConsumerApplication_Content{
		Content: &waConsumerApplication.ConsumerApplication_Content_ReactionMessage{
			ReactionMessage: &waConsumerApplication.ConsumerApplication_ReactionMessage{
				Key:               waKeyFor(chatJID, m),
				Text:              proto.String(emoji), // empty removes the reaction
				SenderTimestampMS: proto.Int64(time.Now().UnixMilli()),
			},
		},
	})
	if err := b.e2eeSendControl(ctx, t, app); err != nil {
		return fmt.Errorf("failed to send reaction: %w", err)
	}
	return nil
}

func (b *bridge) e2eeRevoke(ctx context.Context, t threadRec, m storedMsg) error {
	if !m.FromMe {
		return newErr(codeUnsupported, "only your own end-to-end encrypted messages can be unsent")
	}
	chatJID, err := chatJIDOf(t)
	if err != nil {
		return err
	}
	app := &waConsumerApplication.ConsumerApplication{
		Payload: &waConsumerApplication.ConsumerApplication_Payload{
			Payload: &waConsumerApplication.ConsumerApplication_Payload_ApplicationData{
				ApplicationData: &waConsumerApplication.ConsumerApplication_ApplicationData{
					ApplicationContent: &waConsumerApplication.ConsumerApplication_ApplicationData_Revoke{
						Revoke: &waConsumerApplication.ConsumerApplication_RevokeMessage{Key: waKeyFor(chatJID, m)},
					},
				},
			},
		},
	}
	if err := b.e2eeSendControl(ctx, t, app); err != nil {
		return fmt.Errorf("failed to unsend message: %w", err)
	}
	return nil
}

func (b *bridge) e2eeEdit(ctx context.Context, t threadRec, m storedMsg, text string, mentions []mentionSpan) error {
	chatJID, err := chatJIDOf(t)
	if err != nil {
		return err
	}
	wireText, mentionedJIDs := rewriteE2EEMentions(text, mentions)
	app := wrapContent(&waConsumerApplication.ConsumerApplication_Content{
		Content: &waConsumerApplication.ConsumerApplication_Content_EditMessage{
			EditMessage: &waConsumerApplication.ConsumerApplication_EditMessage{
				Key:         waKeyFor(chatJID, m),
				Message:     &waCommon.MessageText{Text: proto.String(wireText), MentionedJID: mentionedJIDs},
				TimestampMS: proto.Int64(time.Now().UnixMilli()),
			},
		},
	})
	if err := b.e2eeSendControl(ctx, t, app); err != nil {
		return fmt.Errorf("failed to edit encrypted message: %w", err)
	}
	return nil
}

func (b *bridge) e2eeMarkRead(ctx context.Context, t threadRec) error {
	_, cli, err := b.e2eeClient(ctx)
	if err != nil {
		return err
	}
	chatJID, err := chatJIDOf(t)
	if err != nil {
		return err
	}
	bySender := map[types.JID][]types.MessageID{}
	for _, m := range b.st.incomingIDsAfter(t.ID, t.LastRead) {
		var sender types.JID // blank in DMs (no participant field)
		if chatJID.Server == types.GroupServer {
			if j, perr := types.ParseJID(m.SenderJID); perr == nil {
				sender = j
			}
		}
		bySender[sender] = append(bySender[sender], m.ID)
	}
	var errs []error
	for sender, ids := range bySender {
		if err := cli.MarkRead(ctx, ids, time.Now(), chatJID, sender); err != nil {
			errs = append(errs, err)
		}
	}
	return errors.Join(errs...)
}

func (b *bridge) e2eeTyping(ctx context.Context, t threadRec, typing bool) error {
	_, cli, err := b.e2eeClient(ctx)
	if err != nil {
		return err
	}
	chatJID, err := chatJIDOf(t)
	if err != nil {
		return err
	}
	state := types.ChatPresencePaused
	if typing {
		state = types.ChatPresenceComposing
	}
	return cli.SendChatPresence(ctx, chatJID, state, types.ChatPresenceMediaText)
}

// e2eeDownload fetches and decrypts a received E2EE attachment into dest.
func (b *bridge) e2eeDownload(ctx context.Context, ref *waMediaRef, dest string) error {
	_, cli, err := b.e2eeClient(ctx)
	if err != nil {
		return err
	}
	var mt whatsmeow.MediaType
	switch ref.Type {
	case "image":
		mt = whatsmeow.MediaImage
	case "video":
		mt = whatsmeow.MediaVideo
	case "audio":
		mt = whatsmeow.MediaAudio
	default:
		mt = whatsmeow.MediaDocument
	}
	data, err := cli.DownloadFB(ctx, &waMediaTransport.WAMediaTransport_Integral{
		MediaKey:      ref.Key,
		FileSHA256:    ref.SHA256,
		FileEncSHA256: ref.EncSHA256,
		DirectPath:    proto.String(ref.DirectPath),
	}, mt)
	if err != nil {
		return fmt.Errorf("media download failed: %w", err)
	}
	return atomicWrite(dest, data, 0o600)
}
