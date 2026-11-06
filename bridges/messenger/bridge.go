package main

// bridge owns the auth state machine and the messagix session lifecycle.
//
// Flow (mirrors pkg/connector/client.go in mautrix-meta):
//
//	cookies -> messagix.NewClient -> LoadMessagesPage (validates the session and
//	returns the initial LightSpeed table) -> SetEventHandler -> Connect (realtime
//	socket with its own reconnect loop) -> ConnectedEvent -> E2EE client.

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
	"github.com/rs/zerolog"
	"go.mau.fi/util/exhttp"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/dgw"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/table"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

const maxConnectAttempts = 8

type bridge struct {
	dataDir string
	log     zerolog.Logger
	out     *frameWriter
	st      *store

	lifeMu   sync.Mutex // serialises login / logout / finishing a native login
	stateMu  sync.Mutex // serialises auth state transitions + their events
	mu       sync.Mutex
	started  bool
	state    string
	stateErr string
	self     *SelfInfo
	selfID   int64
	ck       *cookies.Cookies
	native   *types.NativeSession // Messenger Lite Android only, otherwise nil
	flow     *loginFlow           // in-progress native (password) login
	client   *messagix.Client
	e2ee     *e2eeState
	gen      int
	cancel   context.CancelFunc

	// socketUp is true between the realtime socket's Connected/Reconnected event and
	// its next disconnect: only tables pushed in that window can carry live messages.
	socketUp atomic.Bool

	tblMu sync.Mutex // serialises table ingestion

	wmu     sync.Mutex
	waiters map[string]map[chan struct{}]struct{} // backfill waiters per chat id

	threadsExhausted atomic.Bool

	// E2EE group member lookups (chats.members), see e2eeGroupMembers.
	memberMu    sync.Mutex
	memberCache map[string]cachedMembers

	hist historyState // encrypted backup access, see history.go
}

func newBridge(dataDir string, log zerolog.Logger, out *frameWriter) *bridge {
	return &bridge{
		dataDir: dataDir,
		log:     log,
		out:     out,
		st:      newStore(dataDir, log),
		state:   stateLoggedOut,
		waiters: map[string]map[chan struct{}]struct{}{},
	}
}

func (b *bridge) emit(event string, data any) {
	b.out.write(eventFrame{Event: event, Data: data})
}

func (b *bridge) emitConn(state, detail string) {
	d := map[string]any{"state": state}
	if detail != "" {
		d["detail"] = detail
	}
	b.emit("connection", d)
}

// notify logs to stderr and tells the app (PROTOCOL.md "log" event).
func (b *bridge) notify(level, msg string) {
	switch level {
	case "error":
		b.log.Error().Msg(msg)
	case "warn":
		b.log.Warn().Msg(msg)
	default:
		b.log.Info().Msg(msg)
	}
	b.emit("log", map[string]any{"level": level, "message": msg})
}

// safeGo runs fn and turns panics into log lines instead of killing the sidecar.
func (b *bridge) safeGo(name string, fn func()) {
	defer func() {
		if r := recover(); r != nil {
			b.log.Error().Str("goroutine", name).Interface("panic", r).Msg("recovered from panic")
		}
	}()
	fn()
}

func (b *bridge) go_(name string, fn func()) {
	go b.safeGo(name, fn)
}

// State ----------------------------------------------------------------------

func (b *bridge) authData() map[string]any {
	b.mu.Lock()
	defer b.mu.Unlock()
	d := map[string]any{"state": b.state}
	if b.self != nil {
		d["self"] = b.self
	}
	if b.stateErr != "" {
		d["error"] = b.stateErr
	}
	return d
}

func (b *bridge) setState(state, errMsg string) { b.setStateIf(nil, state, errMsg) }

// setStateIf applies a transition only when cond (evaluated under b.mu) holds.
// Setting and emitting happen under stateMu, so the app always sees auth.state
// events in the order the transitions happened and the last one is the truth.
func (b *bridge) setStateIf(cond func() bool, state, errMsg string) bool {
	b.stateMu.Lock()
	defer b.stateMu.Unlock()
	b.mu.Lock()
	if cond != nil && !cond() {
		b.mu.Unlock()
		return false
	}
	b.state, b.stateErr = state, errMsg
	b.mu.Unlock()
	b.emit("auth.state", b.authData())
	return true
}

func (b *bridge) currentState() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.state
}

func (b *bridge) isCurrent(gen int) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return gen == b.gen
}

// requireRead gates methods that can be answered from the local store.
func (b *bridge) requireRead() error {
	switch b.currentState() {
	case stateLoggedIn, stateConnecting:
		return nil
	}
	return newErr(codeNotLoggedIn, "Not logged in")
}

// requireClient gates methods that need the live messagix client.
func (b *bridge) requireClient() (*messagix.Client, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	switch b.state {
	case stateLoggedIn:
		if b.client == nil {
			return nil, newErr(codeInternal, "Messenger is reconnecting, try again in a moment")
		}
		return b.client, nil
	case stateConnecting:
		return nil, newErr(codeInternal, "Messenger is still connecting")
	}
	return nil, newErr(codeNotLoggedIn, "Not logged in")
}

func (b *bridge) selfIDStr() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return strconv.FormatInt(b.selfID, 10)
}

// Lifecycle ------------------------------------------------------------------

// hello is called on bridge.hello: load the local store synchronously (cheap),
// then restore the saved session without blocking the handshake.
func (b *bridge) hello() {
	b.mu.Lock()
	if b.started {
		b.mu.Unlock()
		return
	}
	b.started = true
	b.mu.Unlock()
	if err := b.st.load(); err != nil {
		b.log.Warn().Err(err).Msg("could not load store.json, starting empty")
	}
	// From here on, profile pictures whose CDN URL is already known are copied locally
	// (paced, no cookies, CDN host only; see avatars.go).
	b.st.avatars.enable()
	b.go_("restore", func() {
		// A login request may overtake this restore; only report the initial state
		// when nothing has touched the session yet.
		untouched := func() bool { return b.ck == nil && b.flow == nil && b.state == stateLoggedOut }
		ck, native, err := loadCookieFile(b.dataDir)
		if err != nil {
			b.setStateIf(untouched, stateError, "Saved cookies are unusable: "+err.Error())
			return
		}
		if ck == nil {
			b.setStateIf(untouched, stateLoggedOut, "")
			return
		}
		b.mu.Lock()
		busy := b.ck != nil || b.flow != nil // a login request beat the restore
		b.mu.Unlock()
		if busy {
			return
		}
		b.log.Info().Str("platform", ck.Platform.String()).Int64("user", ck.GetUserID()).Bool("native_session", native != nil).Msg("restoring saved session")
		b.startSession(ck, native)
	})
}

// startSession (re)starts the connect loop with the given cookies, tearing down
// any previous session first.
func (b *bridge) startSession(ck *cookies.Cookies, native *types.NativeSession) {
	oldCancel, oldClient, oldE2EE := b.detach()
	b.mu.Lock()
	b.gen++
	gen := b.gen
	ctx, cancel := context.WithCancel(context.Background())
	b.cancel, b.ck, b.native = cancel, ck, native
	b.mu.Unlock()
	b.teardown(oldCancel, oldClient, oldE2EE)
	b.setState(stateConnecting, "")
	b.go_("session", func() { b.runSession(ctx, gen, ck) })
}

// detach clears the live session handles and returns them for teardown.
func (b *bridge) detach() (context.CancelFunc, *messagix.Client, *e2eeState) {
	b.mu.Lock()
	defer b.mu.Unlock()
	c, cl, e := b.cancel, b.client, b.e2ee
	b.cancel, b.client, b.e2ee = nil, nil, nil
	b.self = nil
	b.selfID = 0
	b.gen++ // invalidate event handlers of the old session
	b.socketUp.Store(false)
	return c, cl, e
}

// credentials returns the current session credentials (for restarting the session).
func (b *bridge) credentials() (*cookies.Cookies, *types.NativeSession) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.ck, b.native
}

func (b *bridge) teardown(cancel context.CancelFunc, client *messagix.Client, e *e2eeState) {
	if cancel != nil {
		cancel()
	}
	if e != nil {
		e.stop()
	}
	if client != nil {
		client.Disconnect()
	}
}

func (b *bridge) stopSession() {
	b.teardown(b.detach())
}

func (b *bridge) shutdown() {
	ck, native := b.credentials()
	b.discardFlow()
	b.stopSession()
	if ck != nil && b.currentState() != stateLoggedOut {
		if ck.IsLoggedIn() {
			_ = saveCookieFile(b.dataDir, ck, native) // keep refreshed cookies
		}
	}
	b.st.stopSaving()
	if err := b.st.flush(); err != nil {
		b.log.Warn().Err(err).Msg("final store save failed")
	}
}

type fatalError struct {
	msg        string
	invalidate bool // the cookies are definitely dead; forget them
	err        error
}

func (e *fatalError) Error() string { return e.msg }
func (e *fatalError) Unwrap() error { return e.err }

func (b *bridge) runSession(ctx context.Context, gen int, ck *cookies.Cookies) {
	var lastErr error
	for attempt := 0; ctx.Err() == nil; attempt++ {
		if attempt > 0 {
			delay := time.Duration(min(1<<attempt, 60)) * time.Second
			b.emitConn("offline", fmt.Sprintf("connect failed (%v); retrying in %s", lastErr, delay))
			select {
			case <-time.After(delay):
			case <-ctx.Done():
				return
			}
		}
		b.emitConn("connecting", "")
		err := b.connectOnce(ctx, gen, ck)
		if err == nil || ctx.Err() != nil {
			return
		}
		var fe *fatalError
		if errors.As(err, &fe) {
			b.failSession(gen, fe)
			return
		}
		lastErr = err
		b.log.Warn().Err(err).Int("attempt", attempt+1).Msg("connect attempt failed")
		if attempt+1 >= maxConnectAttempts {
			b.failSession(gen, &fatalError{
				msg: fmt.Sprintf("Could not reach Messenger after %d attempts: %v", maxConnectAttempts, err),
				err: err,
			})
			return
		}
	}
}

func (b *bridge) connectOnce(ctx context.Context, gen int, ck *cookies.Cookies) error {
	client := messagix.NewClient(ck, b.log.With().Str("component", "messagix").Logger(), &messagix.Config{
		ClientSettings: exhttp.SensibleClientSettings,
	})
	// Nil-safe: MessengerLite exists only for the Messenger Lite platforms and the
	// session only for the Android flow. Platform checks elsewhere use IsMessenger(),
	// which is true for Facebook, Messenger and both Messenger Lite flavours.
	_, native := b.credentials()
	client.MessengerLite.SetNativeSession(native)
	user, initial, err := client.LoadMessagesPage(ctx)
	if err != nil {
		return classifyLoadError(err, ck)
	}
	fbid := user.GetFBID()
	if fbid == 0 || fbid != ck.GetUserID() {
		return &fatalError{
			msg:        fmt.Sprintf("Facebook logged in as user %d but the c_user cookie is %d; the cookies belong to a different or expired session.", fbid, ck.GetUserID()),
			invalidate: true,
		}
	}
	self := &SelfInfo{ID: strconv.FormatInt(fbid, 10), Name: user.GetName(), Avatar: user.GetAvatarURL()}

	b.mu.Lock()
	if gen != b.gen {
		b.mu.Unlock()
		return context.Canceled
	}
	b.client, b.self, b.selfID = client, self, fbid
	b.mu.Unlock()
	b.st.setSelf(self.ID)
	if err := saveCookieFile(b.dataDir, ck, native); err != nil {
		b.log.Warn().Err(err).Msg("could not persist cookies")
	}

	b.setState(stateLoggedIn, "")
	client.SetEventHandler(func(_ context.Context, evt any) {
		b.safeGo("meta-event", func() { b.handleMetaEvent(ctx, gen, client, evt) })
	})
	b.ingestTable(initial, modeInitial)
	if err := client.Connect(ctx); err != nil {
		client.Disconnect()
		b.mu.Lock()
		if b.gen == gen {
			b.client = nil
		}
		b.mu.Unlock()
		return fmt.Errorf("realtime connect failed: %w", err)
	}
	return nil
}

// failSession moves to the "error" state with a user-readable reason.
func (b *bridge) failSession(gen int, fe *fatalError) {
	if !b.isCurrent(gen) {
		return
	}
	b.log.Error().Err(fe.err).Msg(fe.msg)
	cancel, client, e := b.detach()
	b.teardown(cancel, client, e)
	if fe.invalidate {
		// Also drop them from memory, or shutdown() writes them back and the next launch
		// sends the dead session to Facebook again.
		b.mu.Lock()
		b.ck, b.native = nil, nil
		b.mu.Unlock()
		removeCookieFile(b.dataDir)
	}
	b.emitConn("offline", fe.msg)
	b.setState(stateError, fe.msg)
}

// classifyLoadError maps messagix/HTTP errors to a fatal, user-actionable
// message, or returns the error unchanged when a retry makes sense.
func classifyLoadError(err error, ck *cookies.Cookies) error {
	var ls *types.ErrorResponse
	var gql *types.GraphQLError
	withURL := func(msg string) string {
		if u := httpclient.GetErrorRedirectURL(err); u != "" {
			return msg + " (" + u + ")"
		}
		return msg
	}
	lite := ck != nil && ck.Platform.IsMessengerLite()
	// Cookie sessions keep their original wording; native (password) sessions must not
	// tell the user to export cookies.
	pick := func(cookiesText, liteText string) string {
		if lite {
			return liteText
		}
		return cookiesText
	}
	relogin := pick("Export fresh cookies from a logged-in browser and log in again.", "Log in again with your email and password.")
	switch {
	case errors.Is(err, httpclient.ErrTokenInvalidatedRedirect):
		return &fatalError{msg: pick("Facebook redirected to the login page: the cookies are expired or the session was logged out. ", "Facebook redirected to the login page: the session expired or was logged out. ") + relogin, invalidate: true, err: err}
	case errors.Is(err, httpclient.ErrUserIDIsZero):
		return &fatalError{msg: pick("Facebook did not recognise the cookies (no user in the page data): they are expired, logged out or incomplete. Export fresh cookies and log in again.", "Facebook did not recognise the session (no user in the page data): it expired or was logged out. "+relogin), invalidate: true, err: err}
	case errors.Is(err, httpclient.ErrTokenInvalidated):
		return &fatalError{msg: pick("The Facebook session cookies are no longer valid. ", "The Facebook session is no longer valid. ") + relogin, invalidate: true, err: err}
	case errors.Is(err, httpclient.ErrCheckpointRequired):
		return &fatalError{msg: withURL("Facebook requires a security checkpoint. Open facebook.com in a browser, complete the check, then log in again" + pick(" with fresh cookies.", ".")), err: err}
	case errors.Is(err, httpclient.ErrChallengeRequired):
		return &fatalError{msg: withURL("Facebook requires a security challenge. Open facebook.com in a browser, complete it, then log in again."), err: err}
	case errors.Is(err, httpclient.ErrConsentRequired):
		return &fatalError{msg: withURL("Facebook requires you to accept new terms or consent. Open facebook.com in a browser, accept them, then log in again."), err: err}
	case errors.Is(err, httpclient.ErrAccountSuspended):
		return &fatalError{msg: withURL("This Facebook account is suspended or restricted."), err: err}
	case errors.Is(err, httpclient.ErrVersionIDNotFound):
		return &fatalError{msg: "Could not read the Messenger web client version; Facebook probably changed its web app. The messagix library needs an update.", err: err}
	case errors.As(err, &ls) && ls.ErrorCode == 1357053:
		return &fatalError{msg: "Facebook rejected the credentials (error 1357053). " + pick("Log in again with fresh cookies.", relogin), invalidate: true, err: err}
	case errors.As(err, &gql):
		return &fatalError{msg: "Facebook returned an error: " + gql.Message, err: err}
	}
	return fmt.Errorf("failed to load Messenger: %w", err)
}

func (b *bridge) handleMetaEvent(ctx context.Context, gen int, client *messagix.Client, rawEvt any) {
	if !b.isCurrent(gen) {
		return
	}
	switch evt := rawEvt.(type) {
	case *table.LSTable:
		b.ingestTable(evt, modeLive)
		client.PostHandlePublishResponse(evt)
	case *messagix.ConnectedEvent:
		// Everything the socket delivered before this point was the initial sync
		// (history, not live); from now on pushed tables are real time.
		b.socketUp.Store(true)
		b.log.Info().Msg("connected to the Messenger realtime socket")
		b.emitConn("online", "")
		b.go_("e2ee", func() { b.startE2EE(ctx, gen, client) })
		b.go_("prefetch-threads", func() { b.prefetchThreads(ctx, gen, client) })
	case *messagix.ReconnectedEvent:
		b.socketUp.Store(true) // tables replayed while reconnecting arrived before this event
		b.log.Info().Msg("reconnected to the Messenger realtime socket")
		b.emitConn("online", "")
		b.go_("e2ee", func() { b.startE2EE(ctx, gen, client) }) // no-op when already running
	case *messagix.TransientDisconnectEvent:
		b.socketUp.Store(false)
		detail := "disconnected"
		if evt.Err != nil {
			detail = evt.Err.Error()
		}
		b.log.Warn().Err(evt.Err).Int("attempts", evt.ConnectionAttempts).Msg("realtime socket disconnected")
		b.emitConn("offline", detail)
	case *messagix.PermanentErrorEvent:
		b.socketUp.Store(false)
		if websocket.CloseStatus(evt.Err) == dgw.CloseStatusUnauthorized {
			ck, _ := b.credentials()
			msg := "Facebook closed the realtime connection as unauthorized: the session cookies expired or were revoked. Log in again with fresh cookies."
			if ck != nil && ck.Platform.IsMessengerLite() {
				msg = "Facebook closed the realtime connection as unauthorized: the session expired or was revoked. Log in again with your email and password."
			}
			b.failSession(gen, &fatalError{msg: msg, invalidate: true, err: evt.Err})
			return
		}
		b.log.Error().Err(evt.Err).Msg("permanent realtime error, restarting session in 10s")
		b.emitConn("offline", fmt.Sprintf("realtime error: %v", evt.Err))
		time.AfterFunc(10*time.Second, func() {
			b.mu.Lock()
			ck, native, cur := b.ck, b.native, gen == b.gen
			b.mu.Unlock()
			if cur && ck != nil {
				b.startSession(ck, native)
			}
		})
	default:
		b.log.Debug().Type("event", rawEvt).Msg("unhandled messagix event")
	}
}

// Login / logout -------------------------------------------------------------

func (b *bridge) login(p params) (any, error) {
	switch m := p.str("method"); m {
	case "password":
		return b.loginPassword(p)
	case "", "cookies":
	default:
		return nil, newErr(codeUnsupported, "Messenger supports password login (method \"password\") and cookie login (method \"cookies\"), not %q", m)
	}
	fail := func(err error) (any, error) {
		if b.currentState() != stateLoggedIn {
			b.setState(stateError, err.Error())
		}
		return nil, err
	}
	// Malformed requests are plain errors; only unusable cookies move auth to "error".
	rawCookies, ok := p["cookies"]
	if !ok {
		return nil, newErr(codeInvalidParams, "cookies is required")
	}
	platform, err := parsePlatform(p.str("platform"))
	if err != nil {
		return nil, err
	}
	b.lifeMu.Lock()
	defer b.lifeMu.Unlock()
	b.discardFlow() // a cookie login replaces an in-progress password login
	vals, hint, err := parseCookieInput(rawCookies)
	if err != nil {
		return fail(err)
	}
	if platform == types.Unset {
		platform = hint
	}
	if platform == types.Unset {
		platform = types.Facebook
	}
	ck, err := buildCookies(platform, vals)
	if err != nil {
		return fail(err)
	}
	if err := saveCookieFile(b.dataDir, ck, nil); err != nil {
		b.log.Warn().Err(err).Msg("could not persist cookies")
	}
	b.log.Info().Str("platform", platform.String()).Int64("user", ck.GetUserID()).Msg("logging in with cookies")
	b.startSession(ck, nil)
	return map[string]any{"state": b.currentState()}, nil
}

func (b *bridge) logout() error {
	b.lifeMu.Lock()
	defer b.lifeMu.Unlock()
	b.discardFlow()
	b.stopSession()
	b.mu.Lock()
	b.ck, b.native = nil, nil
	b.mu.Unlock()
	b.st.reset()
	b.memberMu.Lock()
	b.memberCache = nil
	b.memberMu.Unlock()
	removeCookieFile(b.dataDir)
	for _, name := range []string{e2eeDBName, e2eeDBName + "-wal", e2eeDBName + "-shm", e2eeStateName} {
		_ = os.Remove(filepath.Join(b.dataDir, name))
	}
	_ = os.RemoveAll(filepath.Join(b.dataDir, "media"))
	b.forgetHistory()
	b.threadsExhausted.Store(false)
	b.emitConn("offline", "logged out")
	b.setState(stateLoggedOut, "")
	return nil
}
