// chatspace-messenger: the Facebook Messenger sidecar of ChatSpace.
//
// Speaks the newline-delimited JSON protocol of docs/PROTOCOL.md over
// stdin/stdout, mirroring bridges/node/src/main.ts. Logs go to stderr only.
package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/rs/zerolog"
)

const (
	version        = "0.1.0"
	platformName   = "messenger"
	requestTimeout = 3 * time.Minute
)

var capabilities = []string{"reactions", "reply", "delete", "edit", "typing", "read_receipts", "media_send", "password_login", "mentions", "open_history", "history_pin"}

// frameWriter serialises every frame written to stdout.
type frameWriter struct {
	mu sync.Mutex
	w  *bufio.Writer
}

func (f *frameWriter) write(v any) {
	data, err := json.Marshal(v)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to marshal frame: %v\n", err)
		return
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	_, _ = f.w.Write(data)
	_ = f.w.WriteByte('\n')
	_ = f.w.Flush()
}

type handler func(ctx context.Context, p params) (any, error)

type server struct {
	b        *bridge
	out      *frameWriter
	log      zerolog.Logger
	handlers map[string]handler
	wg       sync.WaitGroup
}

func main() {
	dataDir := flag.String("data-dir", "", "directory for session, store and media files")
	flag.Parse()
	if *dataDir == "" {
		fmt.Fprintln(os.Stderr, "usage: chatspace-messenger --data-dir DIR")
		os.Exit(2)
	}
	abs, err := filepath.Abs(*dataDir)
	if err != nil {
		fmt.Fprintf(os.Stderr, "bad --data-dir: %v\n", err)
		os.Exit(2)
	}
	if err := os.MkdirAll(abs, 0o700); err != nil {
		fmt.Fprintf(os.Stderr, "cannot create data dir: %v\n", err)
		os.Exit(2)
	}

	// stdout is reserved for protocol frames. Keep a private handle and point
	// os.Stdout at stderr so a stray fmt.Println in any library cannot corrupt it.
	out := &frameWriter{w: bufio.NewWriterSize(os.Stdout, 64*1024)}
	os.Stdout = os.Stderr

	zerolog.TimeFieldFormat = time.RFC3339
	level := zerolog.InfoLevel
	if lv := os.Getenv("CHATSPACE_LOG_LEVEL"); lv != "" {
		if parsed, err := zerolog.ParseLevel(lv); err == nil {
			level = parsed
		}
	}
	log := zerolog.New(zerolog.ConsoleWriter{Out: os.Stderr, TimeFormat: "15:04:05", NoColor: true}).
		Level(level).With().Timestamp().Str("bridge", platformName).Logger()
	zerolog.DefaultContextLogger = &log

	s := &server{out: out, log: log}
	s.b = newBridge(abs, log, out)
	s.handlers = s.routes()

	// The app owns our lifetime: stdin EOF (or a termination signal) means exit.
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-sig
		s.exit()
	}()

	reader := bufio.NewReaderSize(os.Stdin, 1<<20)
	for {
		line, err := reader.ReadBytes('\n')
		if len(bytes.TrimSpace(line)) > 0 {
			s.dispatch(line)
		}
		if err != nil {
			if !errors.Is(err, io.EOF) {
				log.Warn().Err(err).Msg("stdin read error")
			}
			break
		}
	}
	s.exit()
}

var exitOnce sync.Once

func (s *server) exit() {
	exitOnce.Do(func() {
		done := make(chan struct{})
		go func() {
			s.b.shutdown()
			close(done)
		}()
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			s.log.Warn().Msg("shutdown timed out")
		}
		os.Exit(0)
	})
}

func (s *server) respond(id json.RawMessage, result any, err error) {
	if err == nil {
		if result == nil {
			result = map[string]any{}
		}
		s.out.write(response{ID: id, Result: result})
		return
	}
	code, msg := codeInternal, err.Error()
	var be *bridgeError
	if errors.As(err, &be) {
		code, msg = be.Code, be.Msg
	}
	s.out.write(response{ID: id, Error: &rpcError{Code: code, Message: msg}})
}

func (s *server) dispatch(line []byte) {
	var req request
	if err := json.Unmarshal(line, &req); err != nil {
		s.log.Warn().Str("frame", string(bytes.TrimSpace(line))).Msg("bad frame")
		return
	}
	h, ok := s.handlers[req.Method]
	if !ok {
		s.respond(req.ID, nil, newErr(codeUnsupported, "unknown method %s", req.Method))
		return
	}
	run := func() {
		defer func() {
			if r := recover(); r != nil {
				s.log.Error().Str("method", req.Method).Interface("panic", r).Msg("handler panicked")
				s.respond(req.ID, nil, newErr(codeInternal, "internal error: %v", r))
			}
		}()
		var p params
		if len(req.Params) > 0 && string(bytes.TrimSpace(req.Params)) != "null" {
			if err := json.Unmarshal(req.Params, &p); err != nil {
				s.respond(req.ID, nil, newErr(codeInvalidParams, "params must be an object"))
				return
			}
		}
		if p == nil {
			p = params{}
		}
		ctx, cancel := context.WithTimeout(context.Background(), requestTimeout)
		defer cancel()
		res, err := h(ctx, p)
		var be *bridgeError
		if err != nil && !errors.As(err, &be) {
			s.log.Error().Err(err).Str("method", req.Method).Msg("request failed")
		}
		s.respond(req.ID, res, err)
	}
	// Like the Node bridge, requests run concurrently (messages.list may wait on
	// the network for a while), except the quick session-lifecycle methods: those
	// run in arrival order so e.g. a login sent right after bridge.hello cannot be
	// overtaken by the hello's initial auth.state.
	switch req.Method {
	case "bridge.hello", "auth.status", "auth.login", "auth.submit", "auth.cancelStep":
		run()
	default:
		go run()
	}
}

func (s *server) routes() map[string]handler {
	b := s.b
	return map[string]handler{
		"bridge.hello": func(ctx context.Context, p params) (any, error) {
			b.hello()
			return map[string]any{"platform": platformName, "version": version, "capabilities": capabilities}, nil
		},
		"auth.status": func(ctx context.Context, p params) (any, error) {
			d := b.authData()
			if d["state"] == stateAwaitingInput {
				// A client that (re)connects mid-login can redraw the screen it missed.
				if step := b.currentStep(); step != nil {
					d["step"] = step
				}
			}
			return d, nil
		},
		"auth.login": func(ctx context.Context, p params) (any, error) {
			return b.login(p)
		},
		"auth.submit": func(ctx context.Context, p params) (any, error) {
			return b.submitLogin(p)
		},
		"auth.cancelStep": func(ctx context.Context, p params) (any, error) {
			return b.cancelLoginStep()
		},
		"auth.logout": func(ctx context.Context, p params) (any, error) {
			return nil, b.logout()
		},
		"chats.list": func(ctx context.Context, p params) (any, error) {
			chats, err := b.listChats(ctx, p.intOr("limit", 100))
			if err != nil {
				return nil, err
			}
			return map[string]any{"chats": chats}, nil
		},
		"chats.avatar": func(ctx context.Context, p params) (any, error) {
			id, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			avatar, err := b.chatAvatar(id)
			if err != nil {
				return nil, err
			}
			return map[string]any{"avatar": avatar}, nil
		},
		"chats.members": func(ctx context.Context, p params) (any, error) {
			id, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			members, err := b.chatMembers(ctx, id)
			if err != nil {
				return nil, err
			}
			return map[string]any{"members": members}, nil
		},
		"messages.list": func(ctx context.Context, p params) (any, error) {
			id, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			msgs, err := b.listMessages(ctx, id, p.intOr("limit", 50), p.str("before"))
			if err != nil {
				return nil, err
			}
			return map[string]any{"messages": msgs}, nil
		},
		"messages.send": func(ctx context.Context, p params) (any, error) {
			id, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			text, filePath := p.str("text"), p.str("filePath")
			if text == "" && filePath == "" {
				return nil, newErr(codeInvalidParams, "text or filePath required")
			}
			msg, err := b.sendMessage(ctx, id, text, p.str("replyTo"), filePath, p.mentions(text))
			if err != nil {
				return nil, err
			}
			return map[string]any{"message": msg}, nil
		},
		"messages.markRead": func(ctx context.Context, p params) (any, error) {
			id, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			return nil, b.markRead(ctx, id)
		},
		"messages.react": func(ctx context.Context, p params) (any, error) {
			chatID, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			msgID, err := p.need("messageId")
			if err != nil {
				return nil, err
			}
			return nil, b.react(ctx, chatID, msgID, p.str("emoji"))
		},
		"messages.delete": func(ctx context.Context, p params) (any, error) {
			chatID, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			msgID, err := p.need("messageId")
			if err != nil {
				return nil, err
			}
			return nil, b.deleteMessage(ctx, chatID, msgID, p.boolean("forEveryone"))
		},
		"messages.edit": func(ctx context.Context, p params) (any, error) {
			chatID, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			msgID, err := p.need("messageId")
			if err != nil {
				return nil, err
			}
			text := p.str("text")
			if strings.TrimSpace(text) == "" {
				return nil, newErr(codeInvalidParams, "text is required")
			}
			msg, err := b.editMessage(ctx, chatID, msgID, text, p.mentions(text))
			if err != nil {
				return nil, err
			}
			return map[string]any{"message": msg}, nil
		},
		"media.download": func(ctx context.Context, p params) (any, error) {
			chatID, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			msgID, err := p.need("messageId")
			if err != nil {
				return nil, err
			}
			path, err := b.downloadMedia(ctx, chatID, msgID)
			if err != nil {
				return nil, err
			}
			return map[string]any{"path": path}, nil
		},
		"history.status": func(ctx context.Context, p params) (any, error) {
			return b.historyStatus()
		},
		"history.restore": func(ctx context.Context, p params) (any, error) {
			return b.restoreHistory(ctx, p.str("pin"))
		},
		"history.loadOpen": func(ctx context.Context, p params) (any, error) {
			chatID, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			return b.loadOpenHistory(ctx, chatID)
		},
		"typing.send": func(ctx context.Context, p params) (any, error) {
			chatID, err := p.need("chatId")
			if err != nil {
				return nil, err
			}
			return nil, b.sendTyping(ctx, chatID, p.boolean("typing"))
		},
	}
}
