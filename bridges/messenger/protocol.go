package main

// Wire types mirroring docs/PROTOCOL.md (and bridges/node/src/types.ts).
// All timestamps are milliseconds since epoch; all IDs are strings.

import (
	"bytes"
	"encoding/json"
	"fmt"
	"math"
)

type SelfInfo struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Avatar string `json:"avatar,omitempty"`
	Phone  string `json:"phone,omitempty"`
}

type Media struct {
	Mime      string  `json:"mime,omitempty"`
	URL       string  `json:"url,omitempty"`
	Path      string  `json:"path,omitempty"`
	Thumbnail string  `json:"thumbnail,omitempty"`
	Width     int     `json:"width,omitempty"`
	Height    int     `json:"height,omitempty"`
	Duration  float64 `json:"duration,omitempty"` // seconds
	FileName  string  `json:"fileName,omitempty"`
	Size      int64   `json:"size,omitempty"`
}

type Reply struct {
	ID         string `json:"id"`
	Text       string `json:"text,omitempty"`
	SenderName string `json:"senderName,omitempty"`
	Kind       string `json:"kind,omitempty"`
}

type Reaction struct {
	Emoji  string `json:"emoji"`
	Count  int    `json:"count"`
	FromMe bool   `json:"fromMe"`
}

type Message struct {
	ID           string     `json:"id"`
	ChatID       string     `json:"chatId"`
	SenderID     string     `json:"senderId"`
	SenderName   string     `json:"senderName,omitempty"`
	SenderAvatar string     `json:"senderAvatar,omitempty"`
	FromMe       bool       `json:"fromMe"`
	Timestamp    int64      `json:"timestamp"`
	Kind         string     `json:"kind"`
	Text         string     `json:"text,omitempty"`
	Media        *Media     `json:"media,omitempty"`
	ReplyTo      *Reply     `json:"replyTo,omitempty"`
	Forwarded    bool       `json:"forwarded,omitempty"`
	Edited       bool       `json:"edited,omitempty"`
	Deleted      bool       `json:"deleted,omitempty"`
	Reactions    []Reaction `json:"reactions,omitempty"`
	Status       string     `json:"status,omitempty"`
}

// Chat.MarkedUnread is deliberately absent: Messenger's LightSpeed tables have no
// manual "mark as unread" flag (only Instagram does), so there is nothing to report.
type Chat struct {
	ID          string   `json:"id"`
	Title       string   `json:"title"`
	Avatar      string   `json:"avatar,omitempty"`
	Kind        string   `json:"kind"`
	Unread      int      `json:"unread"`
	Pinned      bool     `json:"pinned,omitempty"`
	Muted       bool     `json:"muted,omitempty"`
	MutedUntil  int64    `json:"mutedUntil,omitempty"` // ms epoch, -1 = forever, absent = not muted
	Timestamp   int64    `json:"timestamp"`
	LastMessage *Message `json:"lastMessage,omitempty"`
	MemberCount int      `json:"memberCount,omitempty"`
	Encrypted   bool     `json:"encrypted,omitempty"`
	// EncryptedAfter is the id of the last message from before an encrypted chat
	// switched to end-to-end encryption; encrypted messages after it may be missing.
	EncryptedAfter string `json:"encryptedAfter,omitempty"`
}

// Member is one entry of chats.members; ID is what messages.send takes as mentions[].userId.
type Member struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Avatar string `json:"avatar,omitempty"`
}

// AuthField / AuthStep are the payload of the "auth.step" event (PROTOCOL.md AuthStep).
type AuthField struct {
	ID      string   `json:"id"`
	Name    string   `json:"name"`
	Type    string   `json:"type"` // username | password | code | captcha | select | text
	Options []string `json:"options,omitempty"`
	Default string   `json:"default,omitempty"`
}

type AuthStep struct {
	StepID       string      `json:"stepId"`
	Kind         string      `json:"kind"` // "input" | "wait"
	Instructions string      `json:"instructions,omitempty"`
	Fields       []AuthField `json:"fields,omitempty"`
	Images       []string    `json:"images,omitempty"` // data: URLs
	CanCancel    bool        `json:"canCancel"`
	Error        string      `json:"error,omitempty"` // why the previous submission was rejected
}

// Auth states (PROTOCOL.md AuthState).
const (
	stateLoggedOut     = "logged_out"
	stateConnecting    = "connecting"
	stateAwaitingInput = "awaiting_input"
	stateLoggedIn      = "logged_in"
	stateError         = "error"
)

// Error codes (PROTOCOL.md).
const (
	codeNotLoggedIn   = "not_logged_in"
	codeNotFound      = "not_found"
	codeInvalidParams = "invalid_params"
	codeUnsupported   = "unsupported"
	codeInternal      = "internal"
)

// bridgeError is the Go equivalent of the Node bridge's BridgeError.
type bridgeError struct {
	Code string
	Msg  string
}

func (e *bridgeError) Error() string { return e.Msg }

func newErr(code, format string, args ...any) error {
	return &bridgeError{Code: code, Msg: fmt.Sprintf(format, args...)}
}

// Frames.

type request struct {
	ID     json.RawMessage `json:"id"`
	Method string          `json:"method"`
	Params json.RawMessage `json:"params"`
}

type rpcError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

type response struct {
	ID     json.RawMessage `json:"id,omitempty"`
	Result any             `json:"result,omitempty"`
	Error  *rpcError       `json:"error,omitempty"`
}

type eventFrame struct {
	Event string `json:"event"`
	Data  any    `json:"data"`
}

// params is the decoded "params" object of a request.
type params map[string]json.RawMessage

func (p params) str(key string) string {
	raw, ok := p[key]
	if !ok {
		return ""
	}
	var s string
	if json.Unmarshal(raw, &s) != nil {
		return ""
	}
	return s
}

// need mirrors need() in the Node bridge: a non-empty string or invalid_params.
func (p params) need(key string) (string, error) {
	v := p.str(key)
	if v == "" {
		return "", newErr(codeInvalidParams, "%s is required", key)
	}
	return v, nil
}

func (p params) intOr(key string, def int) int {
	raw, ok := p[key]
	if !ok {
		return def
	}
	var f float64
	if json.Unmarshal(raw, &f) != nil || f <= 0 {
		return def
	}
	return int(f)
}

// integer reads a whole-number param that may be zero or negative (intOr only takes
// positives); ok is false when it is missing, null, not a number or not a whole number.
func (p params) integer(key string) (n int64, ok bool) {
	raw, has := p[key]
	if !has || string(bytes.TrimSpace(raw)) == "null" { // null would decode as 0
		return 0, false
	}
	var f float64
	if json.Unmarshal(raw, &f) != nil || f != math.Trunc(f) || math.Abs(f) > 1<<53 {
		return 0, false
	}
	return int64(f), true
}

func (p params) boolean(key string) bool {
	raw, ok := p[key]
	if !ok {
		return false
	}
	var b bool
	if json.Unmarshal(raw, &b) == nil {
		return b
	}
	return false
}
