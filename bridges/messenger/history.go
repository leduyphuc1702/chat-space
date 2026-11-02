package main

// Encrypted chat history (Messenger "secure storage").
//
// E2EE messages sent before ChatSpace's device was registered are not delivered
// to it: they live only in the account's encrypted backup. Opening that backup
// needs the 6-digit PIN the user chose in Messenger. The PIN unlocks the backup's
// recovery code (an OPAQUE login against Meta's HSM, which allows only a few wrong
// attempts), the code derives the backup's "virtual device", and from it we enrol
// a history device of our own. Only that device (never the PIN) is kept, in
// ${dataDir}/e2ee-history.json, so later pages are fetched without asking again.
// Mirrors pkg/connector/{login_history,history,backfill_e2ee}.go of mautrix-meta.

import (
	"cmp"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"sync"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waArmadilloBackupMessage"
	"go.mau.fi/whatsmeow/proto/waConsumerApplication"
	"go.mau.fi/whatsmeow/proto/waMsgApplication"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	"go.mau.fi/mautrix-meta/pkg/messagix/securehistory"
)

// secureHistoryEnabled gates every call to Meta's backup/HSM APIs. Meta once locked
// the account right after a restore that also pulled the latest page of 80 chats in
// a burst, so pages are only fetched for the chat the user is reading, the way
// Messenger restores a thread when it is opened or scrolled.
const secureHistoryEnabled = true

const (
	historyName     = "e2ee-history.json"
	archivePageSize = 30
)

var historyPINPattern = regexp.MustCompile(`^[0-9]{6}$`)

type historyFile struct {
	FBID   int64                 `json:"fbid"`
	Device *securehistory.Device `json:"device"`
}

type historyState struct {
	mu      sync.Mutex // serialises the restore, page fetches and epoch updates
	loaded  bool
	fbid    int64
	device  *securehistory.Device
	cursors map[string]int64 // chat -> archive timestamp to continue before
}

func (b *bridge) historyPath() string { return filepath.Join(b.dataDir, historyName) }

func (b *bridge) loadHistoryLocked() *securehistory.Device {
	if !secureHistoryEnabled {
		return nil
	}
	selfID, _ := b.selfInfo()
	if !b.hist.loaded {
		b.hist.loaded = true
		var f historyFile
		if data, err := os.ReadFile(b.historyPath()); err == nil && json.Unmarshal(data, &f) == nil &&
			f.Device != nil && f.Device.ID != "" {
			b.hist.fbid, b.hist.device = f.FBID, f.Device
		}
	}
	if b.hist.device == nil || b.hist.fbid != selfID {
		return nil
	}
	return b.hist.device
}

func (b *bridge) saveHistoryLocked(fbid int64, d *securehistory.Device) error {
	data, err := json.Marshal(historyFile{FBID: fbid, Device: d})
	if err != nil {
		return err
	}
	if err := atomicWrite(b.historyPath(), data, 0o600); err != nil {
		return err
	}
	b.hist.loaded, b.hist.fbid, b.hist.device = true, fbid, d
	return nil
}

func (b *bridge) hasHistoryDevice() bool {
	b.hist.mu.Lock()
	defer b.hist.mu.Unlock()
	return b.loadHistoryLocked() != nil
}

func (b *bridge) forgetHistory() {
	b.hist.mu.Lock()
	b.hist.loaded, b.hist.fbid, b.hist.device, b.hist.cursors = true, 0, nil, nil
	_ = os.Remove(b.historyPath())
	b.hist.mu.Unlock()
}

// history.status ------------------------------------------------------------------

// historyStatus tells the app whether to offer the PIN: available = there are
// encrypted chats, restored = we already hold a history device. It answers from
// local state only: Meta is contacted solely when the user submits a PIN.
func (b *bridge) historyStatus() (map[string]any, error) {
	if err := b.requireRead(); err != nil {
		return nil, err
	}
	if !secureHistoryEnabled {
		return map[string]any{"available": false, "restored": false}, nil
	}
	if b.hasHistoryDevice() {
		return map[string]any{"available": true, "restored": true}, nil
	}
	encrypted := slices.ContainsFunc(b.st.threadList(), func(t threadRec) bool { return t.E2EE && !hiddenThread(t) })
	return map[string]any{"available": encrypted, "restored": false}, nil
}

// history.restore -----------------------------------------------------------------

// restoreHistory unlocks the backup with the PIN and enrols our history device.
// A wrong PIN is a normal answer (restored=false, attemptsRemaining), not an error.
func (b *bridge) restoreHistory(ctx context.Context, pin string) (map[string]any, error) {
	if !secureHistoryEnabled {
		return nil, newErr(codeInternal, "Restoring encrypted history is turned off")
	}
	if !historyPINPattern.MatchString(pin) {
		return nil, newErr(codeInvalidParams, "The PIN is 6 digits")
	}
	client, err := b.requireClient()
	if err != nil {
		return nil, err
	}
	e, _, err := b.e2eeClient(ctx)
	if err != nil {
		return nil, newErr(codeInternal, "%v", err)
	}
	e.mu.Lock()
	dev := e.dev
	e.mu.Unlock()
	if dev == nil || dev.IdentityKey == nil {
		return nil, newErr(codeInternal, "End-to-end encryption is not set up yet; try again in a moment")
	}
	selfID, _ := b.selfInfo()

	b.hist.mu.Lock()
	defer b.hist.mu.Unlock()
	if b.loadHistoryLocked() != nil {
		return map[string]any{"restored": true}, nil
	}
	h := client.GetSecureHistoryClient()
	if h.RegistrationID == "" {
		return nil, newErr(codeUnsupported, "This Messenger session cannot restore encrypted history")
	}
	backup, err := h.GetBackup(ctx)
	if errors.Is(err, securehistory.ErrNoPINBackup) {
		return map[string]any{"restored": false, "noBackup": true}, nil
	} else if err != nil {
		return nil, newErr(codeInternal, "Could not read the encrypted chat backup: %v", err)
	}
	recovery, err := h.RecoverPIN(ctx, backup, []byte(pin))
	var wrong *securehistory.RecoveryError
	if errors.As(err, &wrong) {
		b.log.Warn().Str("code", wrong.Code).Msg("Messenger rejected the secure storage PIN")
		res := map[string]any{"restored": false, "wrongPin": true}
		if wrong.AttemptsRemaining != nil {
			res["attemptsRemaining"] = *wrong.AttemptsRemaining
		}
		return res, nil
	} else if errors.Is(err, securehistory.ErrNoPINBackup) {
		return map[string]any{"restored": false, "noBackup": true}, nil
	} else if err != nil {
		return nil, newErr(codeInternal, "Could not unlock the encrypted chat backup: %v", err)
	}
	virtual, err := h.GetVirtualDevice(ctx, backup, selfID, recovery.Code)
	if err != nil {
		return nil, newErr(codeInternal, "Could not open the encrypted chat backup: %v", err)
	}
	epochs, err := virtual.DeriveEpochs()
	if err != nil {
		return nil, newErr(codeInternal, "Could not open the encrypted chat backup: %v", err)
	}
	enrollment, err := virtual.PrepareDevice(backup.ID, h.RegistrationID, dev.IdentityKey, epochs)
	if err != nil {
		return nil, newErr(codeInternal, "Could not prepare the history device: %v", err)
	}
	device, err := h.AddDevice(ctx, enrollment)
	if err != nil {
		return nil, newErr(codeInternal, "Messenger did not accept the history device: %v", err)
	}
	if err := b.saveHistoryLocked(selfID, device); err != nil {
		return nil, newErr(codeInternal, "Could not save the history device: %v", err)
	}
	b.log.Info().Msg("restored access to the encrypted chat backup")
	return map[string]any{"restored": true}, nil
}

// Paging --------------------------------------------------------------------------

// archiveFrontier is the timestamp the next backup page of chat ends at: our
// oldest E2EE message, or now when there is none. Messages from the chat's
// pre-encryption thread are older than everything in the backup, so they do not
// count.
func (b *bridge) archiveFrontier(chat string) int64 {
	ts := time.Now().Add(time.Minute).UnixMilli()
	if oldest, ok := b.st.oldestEncrypted(chat); ok {
		ts = oldest
	}
	b.hist.mu.Lock()
	if c, ok := b.hist.cursors[chat]; ok && c < ts {
		ts = c
	}
	b.hist.mu.Unlock()
	return ts
}

// archiveBackfillOnce fetches the page of the encrypted backup preceding what we
// have of an E2EE chat. Returns whether the store grew.
func (b *bridge) archiveBackfillOnce(ctx context.Context, chat string, t threadRec) bool {
	b.mu.Lock()
	e := b.e2ee
	b.mu.Unlock()
	client := b.liveClient()
	if e == nil || client == nil {
		return false
	}
	jid, err := chatJIDOf(t)
	if err != nil {
		return false
	}
	selfID, _ := b.selfInfo()
	ts := b.archiveFrontier(chat)

	b.hist.mu.Lock()
	defer b.hist.mu.Unlock()
	dev := b.loadHistoryLocked()
	if dev == nil {
		return false
	}
	page, err := client.GetSecureHistoryClient().FetchMessages(ctx, dev, jid.User, true, ts, archivePageSize)
	if errors.Is(err, securehistory.ErrArchiveDeleted) {
		b.log.Warn().Str("chat", chat).Msg("the encrypted chat backup is gone; the PIN is needed again")
		b.hist.device, b.hist.cursors = nil, nil
		_ = os.Remove(b.historyPath())
		return false
	} else if err != nil {
		b.log.Warn().Err(err).Str("chat", chat).Msg("could not fetch encrypted history")
		return false
	}
	if page.Epochs != nil && len(page.Epochs.Edges) > 0 {
		next, err := dev.WithEpochs(page.Epochs)
		if err != nil {
			b.log.Warn().Err(err).Str("chat", chat).Msg("could not apply encrypted history epochs")
			return false
		}
		if err := b.saveHistoryLocked(selfID, next); err != nil {
			b.log.Warn().Err(err).Msg("could not save encrypted history epochs")
			return false
		}
		dev = next
	}
	msgs, err := page.Decrypt(dev, jid.User, selfID, jid.Server == types.GroupServer)
	if err != nil {
		b.log.Warn().Err(err).Str("chat", chat).Msg("could not decrypt encrypted history")
		return false
	}

	before := b.st.messageCount(chat)
	for _, m := range msgs {
		e.ingestArchive(jid, m)
	}
	b.log.Info().Str("chat", chat).Int("messages", len(msgs)).Msg("fetched a page of the encrypted chat backup")
	more := page.Range != nil && page.Range.HasMoreBefore != nil && *page.Range.HasMoreBefore
	if more && page.Range.Before > 0 && page.Range.Before < ts {
		if b.hist.cursors == nil {
			b.hist.cursors = map[string]int64{}
		}
		b.hist.cursors[chat] = page.Range.Before
	} else {
		b.st.updateThread(chat, func(t *threadRec) { t.ArchiveNoMoreBefore = true })
	}
	if b.st.messageCount(chat) > before {
		b.emitChat(chat)
		return true
	}
	return false
}

// Archive messages ----------------------------------------------------------------

// ingestArchive stores a restored message, then its reactions and edits.
func (e *e2eeState) ingestArchive(chat types.JID, m *securehistory.DecryptedMessage) {
	if m == nil || m.Message == nil {
		return
	}
	parent := e.archiveEvent(chat, m.Message.Message, m.Message.TimestampMS)
	if parent == nil {
		return
	}
	e.ingest(parent, true)
	sups := make([]*securehistory.DecryptedProtobuf, 0, len(m.Supplementals))
	for _, s := range m.Supplementals {
		if s != nil && s.Message != nil {
			sups = append(sups, s)
		}
	}
	slices.SortFunc(sups, func(a, c *securehistory.DecryptedProtobuf) int { return cmp.Compare(a.TimestampMS, c.TimestampMS) })
	for _, s := range sups {
		evt := e.archiveEvent(chat, s.Message, s.TimestampMS)
		if evt == nil {
			continue
		}
		app, _ := evt.Message.(*waConsumerApplication.ConsumerApplication)
		content := app.GetPayload().GetContent()
		if content.GetReactionMessage() != nil || content.GetEditMessage() != nil {
			e.ingest(evt, true)
		}
	}
}

// archiveEvent turns a backup entry into the event live delivery would have
// produced (pkg/connector/backfill_e2ee.go decodeArchiveMessage).
func (e *e2eeState) archiveEvent(chat types.JID, msg *waArmadilloBackupMessage.BackupMessage, tsMS int64) *events.FBMessage {
	payload, ok := msg.GetPayload().(*waArmadilloBackupMessage.BackupMessage_EncryptedTransportMessage)
	if !ok {
		return nil // admin notices and transport events
	}
	var application waMsgApplication.MessageApplication
	if err := proto.Unmarshal(payload.EncryptedTransportMessage, &application); err != nil {
		return nil
	}
	decoded, err := whatsmeow.DecodeFBApplication(&application)
	if err != nil {
		return nil
	}
	md := msg.GetMetadata()
	if md.GetMessageID() == "" || md.GetSenderID() == "" || tsMS <= 0 {
		return nil
	}
	selfID, _ := e.b.selfInfo()
	decoded.Info = types.MessageInfo{
		MessageSource: types.MessageSource{
			Chat:     chat,
			Sender:   types.NewJID(md.GetSenderID(), types.MessengerServer),
			IsFromMe: md.GetSenderID() == chatKey(selfID),
			IsGroup:  chat.Server == types.GroupServer,
		},
		ID:        md.GetMessageID(),
		Timestamp: time.UnixMilli(tsMS),
	}
	return &decoded
}
