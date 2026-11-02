package main

// Native email/password login (no cookies, no WebView).
//
// This drives messagix's Messenger Lite "Bloks" login the same way mautrix-meta's
// connector does (pkg/connector/login.go MetaNativeLogin): a messagix.Client with
// fake cookies of the Messenger Lite iOS platform, then MessengerLite.DoLoginSteps
// is called until it either asks the user for something (a bridgev2.LoginStep),
// fails, or returns real session cookies. Steps are translated to the "auth.step"
// event of docs/PROTOCOL.md; answers come back through auth.submit.
//
// Lifecycle: at most one loginFlow exists (bridge.flow). auth.login (any method),
// auth.logout and a finished flow discard it. A flow is driven by one goroutine at a
// time (loginFlow.busy); wait steps (approve on another device) keep that goroutine
// polling until the library returns the next step.

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/rs/zerolog"
	"go.mau.fi/util/exhttp"
	"maunium.net/go/mautrix/bridgev2"

	"go.mau.fi/mautrix-meta/pkg/messagix"
	"go.mau.fi/mautrix-meta/pkg/messagix/bloks"
	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/httpclient"
	"go.mau.fi/mautrix-meta/pkg/messagix/loginerrors"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
	"go.mau.fi/mautrix-meta/pkg/messagix/useragent"
)

const (
	// loginPlatform is the Messenger Lite flavour used for password login ("messenger-lite").
	// iOS is the connector's default; Android additionally yields a NativeSession that is only
	// needed for push registration, which ChatSpace does not do.
	loginPlatform = types.MessengerLiteIOS

	loginCallTimeout = 2 * time.Minute  // one DoLoginSteps call that is not a wait
	loginMaxWait     = 10 * time.Minute // "approve on another device" polling
	loginMaxRetries  = 3                // re-shows of the credentials step after a local rejection

	cookieHint = " If this keeps happening, sign in at facebook.com in your browser and use the Cookies login instead."
)

// validateNativeSession mirrors the connector's check.
func validateNativeSession(s *types.NativeSession) error {
	if s != nil && (s.AccessToken == "" || s.AppID != useragent.MessengerLiteAndroidAppID || s.DeviceID == uuid.Nil || s.FamilyDeviceID == uuid.Nil) {
		return errors.New("invalid native Messenger session")
	}
	return nil
}

type loginFlow struct {
	b      *bridge
	client *messagix.Client
	log    zerolog.Logger
	ctx    context.Context
	cancel context.CancelFunc

	mu         sync.Mutex
	busy       bool      // a drive goroutine owns the library right now
	cur        *AuthStep // step on screen (nil while the library is working)
	curFields  []bridgev2.LoginInputDataField
	waitCancel context.CancelCauseFunc // set while a wait step is being polled
	stepCancel bool                    // user pressed back on the wait step in flight
	lastStepID string                  // last step shown, to recognise a re-shown (rejected) step
	pendingErr string                  // rejection to attach to the next step
	prefUser   string                  // credentials from auth.login, used once
	prefPass   string
	lastUser   string // username to pre-fill when the credentials step comes back
}

// Entry points ----------------------------------------------------------------

// loginPassword handles auth.login {method:"password"}. It returns immediately;
// everything else is reported through auth.state / auth.step events.
func (b *bridge) loginPassword(p params) (any, error) {
	username := strings.TrimSpace(p.str("username"))
	password := p.str("password")

	b.lifeMu.Lock()
	defer b.lifeMu.Unlock()
	b.discardFlow()
	b.stopSession()
	b.mu.Lock()
	b.ck, b.native = nil, nil
	b.mu.Unlock()

	log := b.log.With().Str("component", "messagix").Logger()
	client := messagix.NewClient(&cookies.Cookies{Platform: loginPlatform}, log, &messagix.Config{
		ClientSettings: exhttp.SensibleClientSettings,
	})
	ctx, cancel := context.WithCancel(context.Background())
	f := &loginFlow{
		b: b, client: client, log: log, ctx: ctx, cancel: cancel,
		prefUser: username, prefPass: password, lastUser: username,
		busy: true,
	}
	b.mu.Lock()
	b.flow = f
	b.mu.Unlock()
	b.setState(stateConnecting, "")
	b.log.Info().Str("platform", loginPlatform.String()).Msg("starting native password login")
	b.go_("login-flow", func() { f.drive(nil, false) })
	return map[string]any{"state": stateConnecting}, nil
}

// discardFlow throws away an in-progress login. Safe when there is none.
func (b *bridge) discardFlow() {
	b.mu.Lock()
	f := b.flow
	b.flow = nil
	b.mu.Unlock()
	if f != nil {
		f.cancel()
	}
}

func (b *bridge) currentFlow() *loginFlow {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.flow
}

// currentStep is the step to show for auth.status (nil when none is on screen).
func (b *bridge) currentStep() *AuthStep {
	f := b.currentFlow()
	if f == nil {
		return nil
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.cur
}

// submitLogin handles auth.submit.
func (b *bridge) submitLogin(p params) (any, error) {
	f := b.currentFlow()
	if f == nil {
		return nil, newErr(codeInvalidParams, "There is no login waiting for input")
	}
	inputs, aliases := parseSubmitInputs(p)
	if err := f.submit(inputs, aliases); err != nil {
		return nil, err
	}
	return map[string]any{"state": b.currentState()}, nil
}

// cancelLoginStep handles auth.cancelStep.
func (b *bridge) cancelLoginStep() (any, error) {
	f := b.currentFlow()
	if f == nil {
		return nil, newErr(codeInvalidParams, "There is no login step to go back from")
	}
	if err := f.cancelStep(); err != nil {
		return nil, err
	}
	return map[string]any{"state": b.currentState()}, nil
}

// parseSubmitInputs reads params.inputs (string or numeric values). Telegram-style
// top-level username/password/code are accepted as a convenience (aliases) and are
// resolved against the field types of the step on screen in submit().
func parseSubmitInputs(p params) (inputs, aliases map[string]string) {
	inputs, aliases = map[string]string{}, map[string]string{}
	if raw, ok := p["inputs"]; ok {
		dec := json.NewDecoder(bytes.NewReader(raw))
		dec.UseNumber()
		var generic map[string]any
		if err := dec.Decode(&generic); err == nil {
			for k, v := range generic {
				if s, ok := scalarString(v); ok {
					inputs[k] = s
				}
			}
		}
	}
	for _, k := range []string{"username", "password", "code"} {
		if v := p.str(k); v != "" {
			aliases[k] = v
		}
	}
	return inputs, aliases
}

// submit validates the answers against the step on screen and resumes the flow.
func (f *loginFlow) submit(inputs, aliases map[string]string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.busy || f.cur == nil || f.cur.Kind != "input" {
		return newErr(codeInvalidParams, "No login step is waiting for input right now")
	}
	clean := map[string]string{}
	for _, fld := range f.curFields {
		v, ok := inputs[fld.ID]
		if !ok {
			v = aliases[aliasForFieldType(fld.Type)]
		}
		switch fld.Type {
		case bridgev2.LoginInputFieldTypePassword, bridgev2.LoginInputFieldTypeSelect:
		default:
			v = strings.TrimSpace(v)
		}
		if v == "" {
			if fld.Optional {
				continue
			}
			return newErr(codeInvalidParams, "%s is required", fld.Name)
		}
		if fld.Type == bridgev2.LoginInputFieldTypeSelect && len(fld.Options) > 0 && !containsString(fld.Options, v) {
			return newErr(codeInvalidParams, "%q is not one of the available choices for %s", v, fld.Name)
		}
		clean[fld.ID] = v
		if fld.Type == bridgev2.LoginInputFieldTypeUsername {
			f.lastUser = v
		}
	}
	f.busy = true
	f.cur = nil
	f.b.go_("login-flow", func() { f.drive(clean, false) })
	return nil
}

func aliasForFieldType(t bridgev2.LoginInputFieldType) string {
	switch t {
	case bridgev2.LoginInputFieldTypeUsername:
		return "username"
	case bridgev2.LoginInputFieldTypePassword:
		return "password"
	case bridgev2.LoginInputFieldType2FACode, bridgev2.LoginInputFieldTypeCaptchaCode:
		return "code"
	}
	return ""
}

func containsString(list []string, s string) bool {
	for _, v := range list {
		if v == s {
			return true
		}
	}
	return false
}

// cancelStep goes back from the step on screen (only when the library allows it:
// MFA code pages and the approval wait have a "Try another way").
func (f *loginFlow) cancelStep() error {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.cur == nil || !f.cur.CanCancel {
		return newErr(codeInvalidParams, "This login step cannot be cancelled")
	}
	if f.cur.Kind == "wait" {
		if f.stepCancel {
			return newErr(codeInvalidParams, "Already going back")
		}
		// The polling goroutine notices the flag, stops, taps "Try another way" and goes on.
		f.stepCancel = true
		if f.waitCancel != nil {
			f.waitCancel(bridgev2.ErrLoginStepCancelled)
		}
		return nil
	}
	if f.busy {
		return newErr(codeInvalidParams, "The login is busy, try again in a moment")
	}
	f.busy = true
	f.cur = nil
	f.b.go_("login-flow", func() { f.drive(nil, true) })
	return nil
}

// Driver -----------------------------------------------------------------------

// drive runs library calls until the user has to act, the flow succeeds or fails,
// or it gets discarded. inputs are the answers for the first call.
func (f *loginFlow) drive(inputs map[string]string, cancelFirst bool) {
	defer func() {
		if r := recover(); r != nil {
			f.b.log.Error().Interface("panic", r).Msg("panic in native login")
			f.fail(fmt.Errorf("internal error in the login flow: %v", r))
		}
	}()
	if cancelFirst {
		if !f.goBack() {
			return
		}
	}
	retries := 0
	for {
		if f.ctx.Err() != nil {
			return
		}
		step, ck, err, cancelled := f.call(inputs)
		inputs = nil
		if f.ctx.Err() != nil {
			return
		}
		switch {
		case cancelled:
			if !f.goBack() {
				return
			}
		case err != nil:
			if msg, ok := recoverableInputError(err); ok && retries < loginMaxRetries {
				// Rejected before anything was sent (phone number, bad username):
				// the library still sits on the credentials page, ask again.
				retries++
				f.mu.Lock()
				f.pendingErr = msg
				f.mu.Unlock()
				continue
			}
			f.fail(err)
			return
		case ck != nil:
			f.succeed(ck)
			return
		case step != nil:
			next, again := f.handleStep(step)
			if !again {
				return
			}
			inputs = next
		default:
			f.fail(errors.New("the login flow ended without a result"))
			return
		}
	}
}

// goBack taps "Try another way" after the user cancelled a step. false = stop driving.
func (f *loginFlow) goBack() bool {
	ctx, cancel := context.WithTimeout(f.ctx, loginCallTimeout)
	defer cancel()
	if err := f.client.MessengerLite.CancelLoginStep(f.log.WithContext(ctx)); err != nil {
		if f.ctx.Err() == nil {
			f.fail(err)
		}
		return false
	}
	return true
}

// call performs one DoLoginSteps call. When the step on screen is a wait step the
// call polls without timeout (bounded by loginMaxWait) and can be cancelled with
// bridgev2.ErrLoginStepCancelled, which is what the library expects.
func (f *loginFlow) call(inputs map[string]string) (step *bridgev2.LoginStep, ck *cookies.Cookies, err error, cancelled bool) {
	f.mu.Lock()
	waiting := f.cur != nil && f.cur.Kind == "wait"
	var (
		ctx    context.Context
		cancel context.CancelFunc
	)
	if waiting {
		cctx, ccancel := context.WithCancelCause(f.ctx)
		tctx, tcancel := context.WithTimeout(cctx, loginMaxWait)
		ctx, cancel = tctx, func() { tcancel(); ccancel(nil) }
		f.waitCancel = ccancel
		if f.stepCancel {
			ccancel(bridgev2.ErrLoginStepCancelled)
		}
	} else {
		ctx, cancel = context.WithTimeout(f.ctx, loginCallTimeout)
	}
	f.mu.Unlock()
	defer cancel()

	// The library deletes the keys it consumes; give it its own copy.
	var in map[string]string
	if len(inputs) > 0 {
		in = make(map[string]string, len(inputs))
		for k, v := range inputs {
			in[k] = v
		}
	} else {
		in = map[string]string{}
	}
	step, ck, err = f.client.MessengerLite.DoLoginSteps(f.log.WithContext(ctx), in)

	f.mu.Lock()
	f.waitCancel = nil
	cancelled = f.stepCancel && (err != nil || (step == nil && ck == nil))
	f.stepCancel = false
	if cancelled {
		f.cur = nil // the wait step is gone; the next call is a normal one
	}
	f.mu.Unlock()
	return
}

// handleStep reacts to a step from the library. again=true means keep driving
// (with the returned answers for the next call).
func (f *loginFlow) handleStep(step *bridgev2.LoginStep) (next map[string]string, again bool) {
	switch step.Type {
	case bridgev2.LoginStepTypeUserInput:
		if creds, ok := f.autoCredentials(step); ok {
			return creds, true
		}
		f.showStep(step, "input")
		return nil, false
	case bridgev2.LoginStepTypeDisplayAndWait:
		f.showStep(step, "wait")
		return nil, true // keep polling for the next step in this goroutine
	case bridgev2.LoginStepTypeCookies:
		f.fail(unsupportedCookieStep(step))
		return nil, false
	default:
		f.fail(fmt.Errorf("Facebook asked for a login step ChatSpace does not support (%s).%s", step.Type, cookieHint))
		return nil, false
	}
}

// autoCredentials answers the username/password page with the credentials given to
// auth.login, once, so the UI does not have to show its form twice.
func (f *loginFlow) autoCredentials(step *bridgev2.LoginStep) (map[string]string, bool) {
	f.mu.Lock()
	defer f.mu.Unlock()
	user, pass := f.prefUser, f.prefPass
	f.prefUser, f.prefPass = "", ""
	if user == "" || pass == "" || step.UserInputParams == nil {
		return nil, false
	}
	fields := step.UserInputParams.Fields
	if len(fields) != 2 {
		return nil, false
	}
	var hasUser, hasPass bool
	for _, fld := range fields {
		switch {
		case fld.ID == "username" && fld.Type == bridgev2.LoginInputFieldTypeUsername:
			hasUser = true
		case fld.ID == "password" && fld.Type == bridgev2.LoginInputFieldTypePassword:
			hasPass = true
		}
	}
	if !hasUser || !hasPass {
		return nil, false
	}
	f.lastUser = user
	f.lastStepID = step.StepID
	return map[string]string{"username": user, "password": pass}, true
}

// showStep publishes a step to the UI. Input steps hand control to the user
// (the driver goroutine ends); wait steps keep the driver polling.
func (f *loginFlow) showStep(step *bridgev2.LoginStep, kind string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	reshown := f.lastStepID == step.StepID
	s := toAuthStep(step, kind, reshown, f.pendingErr, f.lastUser)
	f.pendingErr = ""
	f.lastStepID = step.StepID
	f.cur = s
	f.curFields = nil
	if kind == "input" {
		if step.UserInputParams != nil {
			f.curFields = step.UserInputParams.Fields
		}
		f.busy = false
	}
	f.b.publishStep(f, s)
}

// publishStep emits auth.step then auth.state awaiting_input, atomically with
// respect to other state transitions, and only while f is still the active flow.
func (b *bridge) publishStep(f *loginFlow, s *AuthStep) {
	b.stateMu.Lock()
	defer b.stateMu.Unlock()
	b.mu.Lock()
	if b.flow != f {
		b.mu.Unlock()
		return
	}
	b.state, b.stateErr = stateAwaitingInput, ""
	b.mu.Unlock()
	b.emit("auth.step", s)
	b.emit("auth.state", b.authData())
}

// Success / failure -------------------------------------------------------------

func (f *loginFlow) succeed(ck *cookies.Cookies) {
	b := f.b
	native := f.client.MessengerLite.GetNativeSession()
	if missing := ck.GetMissingCookieNames(); len(missing) > 0 {
		f.fail(loginerrors.MissingCookies.AppendMessage(": %v", missing))
		return
	}
	if ck.GetUserID() <= 0 {
		f.fail(loginerrors.TokenExchange)
		return
	}
	if err := validateNativeSession(native); err != nil {
		f.fail(err)
		return
	}
	b.lifeMu.Lock()
	defer b.lifeMu.Unlock()
	b.mu.Lock()
	if b.flow != f { // discarded meanwhile (logout / new login)
		b.mu.Unlock()
		return
	}
	b.flow = nil
	b.mu.Unlock()
	f.cancel()
	if err := saveCookieFile(b.dataDir, ck, native); err != nil {
		b.log.Warn().Err(err).Msg("could not persist the session")
	}
	b.log.Info().Str("platform", ck.Platform.String()).Int64("user", ck.GetUserID()).Msg("native login succeeded, starting session")
	b.startSession(ck, native)
}

// fail ends the flow with auth.state "error" and a human-readable message.
func (f *loginFlow) fail(err error) {
	b := f.b
	msg := humanLoginError(err)
	b.log.Warn().Err(err).Msg("native login failed")
	if b.setStateIf(func() bool {
		if b.flow != f {
			return false
		}
		b.flow = nil
		return true
	}, stateError, msg) {
		f.cancel()
	}
}

// Error translation --------------------------------------------------------------

// recoverableInputError reports errors the library raises before submitting
// anything (so the credentials page can simply be asked again).
func recoverableInputError(err error) (string, bool) {
	var re bridgev2.RespError
	if errors.As(err, &re) {
		switch re.ErrCode {
		case loginerrors.PhoneNumber.ErrCode, loginerrors.InvalidUsername.ErrCode:
			return re.Err, true
		}
	}
	return "", false
}

func unsupportedCookieStep(step *bridgev2.LoginStep) error {
	what := "a step that cannot be done inside ChatSpace"
	switch {
	case strings.HasSuffix(step.StepID, ".recaptcha"):
		what = "a Google reCAPTCHA"
	case strings.HasSuffix(step.StepID, ".google_oauth"):
		what = "sign-in with Google"
	}
	return fmt.Errorf("Facebook is asking for %s, which ChatSpace cannot show. Please use the Cookies login instead: sign in at facebook.com in your browser, then paste your cookies into ChatSpace.", what)
}

func humanLoginError(err error) string {
	var cp bloks.CheckpointError
	switch {
	case errors.As(err, &cp):
		return loginerrors.Checkpoint.Err + "." + cookieHint
	case errors.Is(err, context.DeadlineExceeded):
		return "Facebook did not answer in time. Check your internet connection and try again."
	case errors.Is(err, httpclient.ErrRateLimited):
		return loginerrors.RateLimited.Err
	case errors.Is(err, httpclient.ErrRequestFailed):
		return "Could not reach Facebook. Check your internet connection and try again."
	}
	var re bridgev2.RespError
	if errors.As(err, &re) {
		msg := strings.TrimRight(re.Err, ". ")
		switch re.ErrCode {
		case loginerrors.Checkpoint.ErrCode, loginerrors.Challenge.ErrCode, loginerrors.Consent.ErrCode,
			loginerrors.ReCaptcha.ErrCode, loginerrors.MandatoryOAuth.ErrCode, loginerrors.NoSupportedMFA.ErrCode,
			loginerrors.MandatoryPasskey.ErrCode, loginerrors.TokenExchange.ErrCode, loginerrors.NoSMSAvailable.ErrCode,
			loginerrors.AFADStopped.ErrCode, "FI.MAU.META_UNINFORMATIVE_ERROR":
			return msg + "." + cookieHint
		}
		return msg + "."
	}
	msg := err.Error()
	if msg == "" {
		return "Facebook login failed."
	}
	if strings.HasPrefix(msg, "Facebook is asking") || strings.HasPrefix(msg, "Facebook asked") {
		return msg // already written for the user
	}
	return "Facebook login failed: " + msg
}

// Step translation ---------------------------------------------------------------

func toAuthStep(step *bridgev2.LoginStep, kind string, reshown bool, pendingErr, userDefault string) *AuthStep {
	s := &AuthStep{StepID: step.StepID, Kind: kind, Instructions: strings.TrimSpace(step.Instructions), Error: pendingErr}
	if kind == "wait" {
		if step.DisplayAndWaitParams != nil {
			s.CanCancel = step.DisplayAndWaitParams.CanCancel
		}
		if s.Instructions == "" {
			s.Instructions = "Approve the login on another device where you are signed in to Facebook."
		}
		return s
	}
	p := step.UserInputParams
	if p == nil {
		return s
	}
	s.CanCancel = p.CanCancel
	for _, fld := range p.Fields {
		af := AuthField{ID: fld.ID, Name: fld.Name, Type: fieldType(fld.Type), Options: fld.Options, Default: fld.DefaultValue}
		if fld.Type == bridgev2.LoginInputFieldTypeUsername && af.Default == "" {
			af.Default = userDefault
		}
		s.Fields = append(s.Fields, af)
	}
	for _, a := range p.Attachments {
		if a == nil || len(a.Content) == 0 {
			continue
		}
		mime := a.Info.MimeType
		if mime == "" {
			mime = http.DetectContentType(a.Content)
		}
		if !strings.HasPrefix(mime, "image/") { // skips the audio captcha
			continue
		}
		s.Images = append(s.Images, "data:"+mime+";base64,"+base64.StdEncoding.EncodeToString(a.Content))
	}
	if reshown && s.Error == "" {
		s.Error, s.Instructions = splitRejection(step.StepID, s.Instructions)
	}
	return s
}

func fieldType(t bridgev2.LoginInputFieldType) string {
	switch t {
	case bridgev2.LoginInputFieldTypeUsername:
		return "username"
	case bridgev2.LoginInputFieldTypePassword:
		return "password"
	case bridgev2.LoginInputFieldType2FACode:
		return "code"
	case bridgev2.LoginInputFieldTypeCaptchaCode:
		return "captcha"
	case bridgev2.LoginInputFieldTypeSelect:
		return "select"
	}
	return "text"
}

// When a step is shown again after a rejected answer the library bakes the reason
// into the instructions ("<reason>. <normal instructions>", or just the reason for
// the captcha page). Split it back out so the UI can style it as an error.
var rejectionWords = []string{"rejected", "invalid", "incorrect", "not connected", "not sent", "wrong", "try again"}

func splitRejection(stepID, instructions string) (errMsg, rest string) {
	if strings.HasSuffix(stepID, ".captcha") {
		if strings.Contains(strings.ToLower(instructions), "rejected") {
			return instructions, "Type the characters shown in the image."
		}
		return "", instructions
	}
	head, tail, ok := strings.Cut(instructions, ". ")
	if !ok || len(head) > 90 {
		return "", instructions
	}
	low := strings.ToLower(head)
	for _, w := range rejectionWords {
		if strings.Contains(low, w) {
			return head, strings.TrimSpace(tail)
		}
	}
	return "", instructions
}
