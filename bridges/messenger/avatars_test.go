package main

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	"github.com/rs/zerolog"
)

// hexExpiry is the `oe=` value of a signed Meta URL that expires `d` from now.
func hexExpiry(d time.Duration) string { return strconv.FormatInt(time.Now().Add(d).Unix(), 16) }

const pngHeader = "\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR"

func cdnURL(name string, oe time.Duration) string {
	return "https://scontent.xx.fbcdn.net/v/t39/" + name + ".jpg?stp=dst&oe=" + hexExpiry(oe) + "&oh=sig"
}

func TestAvatarExpired(t *testing.T) {
	now := time.Now()
	for url, want := range map[string]bool{
		cdnURL("a", -time.Hour):            true,
		cdnURL("a", time.Hour):             false,
		"https://example.com/p.jpg":        false, // unsigned
		"/Users/x/avatars/1-abc.jpg":       false, // local path
		"https://x.fbcdn.net/a.jpg?oe=zzz": false, // unreadable expiry
		"data:image/png;base64,AAAA":       false,
	} {
		if got := avatarExpired(url, now); got != want {
			t.Fatalf("%s: expired = %v, want %v", url, got, want)
		}
	}
}

func TestAvatarPickPrefersLiveURLOverExpiredOne(t *testing.T) {
	c := newAvatarCache(t.TempDir())
	dead, live := cdnURL("old", -time.Hour), cdnURL("new", time.Hour)
	if got := c.pick("1", dead, live); got != live {
		t.Fatalf("pick = %q, want the live URL", got)
	}
	if got := c.pick("1", dead); got != "" {
		t.Fatalf("only an expired URL and nothing cached must give no avatar, got %q", got)
	}
	// Downloads are off until the session is restored: nothing may be queued.
	if len(c.pending) != 0 {
		t.Fatalf("download queued before enable(): %v", c.pending)
	}
	var nilCache *avatarCache
	if got := nilCache.pick("1", dead, live); got != live {
		t.Fatalf("nil cache: %q", got)
	}
}

func TestAvatarCachedFileServedWhenURLsDie(t *testing.T) {
	dir := t.TempDir()
	c := newAvatarCache(dir)
	c.enable()
	c.fetch = func(ctx context.Context, raw string) ([]byte, error) { return []byte(pngHeader), nil }
	live := cdnURL("pic", time.Hour)
	if got := c.pick("42", live); got != live {
		t.Fatalf("first pick must still answer with the URL, got %q", got)
	}
	var path string
	for i := 0; i < 100 && path == ""; i++ {
		time.Sleep(20 * time.Millisecond)
		c.mu.Lock()
		path = c.files["42"].path
		c.mu.Unlock()
	}
	if path == "" {
		t.Fatal("picture was not cached")
	}
	if filepath.Dir(path) != dir || filepath.Ext(path) != ".png" {
		t.Fatalf("cached at %s", path)
	}
	// Same picture (same URL path, new signature): the local file is served.
	renewed := "https://scontent.xx.fbcdn.net/v/t39/pic.jpg?stp=dst&oe=" + hexExpiry(2*time.Hour) + "&oh=other"
	if got := c.pick("42", renewed); got != path {
		t.Fatalf("pick = %q, want the cached file", got)
	}
	// Every URL expired: the cached picture is better than a blank avatar.
	if got := c.pick("42", cdnURL("pic", -time.Hour)); got != path {
		t.Fatalf("pick = %q, want the cached file", got)
	}
	// A restart finds it again.
	if got := newAvatarCache(dir).pick("42", cdnURL("pic", -time.Hour)); got != path {
		t.Fatalf("after restart: %q", got)
	}
	// forget (logout) wipes pictures.
	c.forget()
	if _, err := os.Stat(path); err == nil {
		t.Fatal("picture survived forget()")
	}
}

func TestAvatarFailedDownloadIsNotRetriedImmediately(t *testing.T) {
	c := newAvatarCache(t.TempDir())
	c.enable()
	calls := 0
	c.fetch = func(ctx context.Context, raw string) ([]byte, error) { calls++; return nil, errors.New("403") }
	live := cdnURL("pic", time.Hour)
	c.pick("7", live)
	for i := 0; i < 100; i++ {
		time.Sleep(20 * time.Millisecond)
		c.mu.Lock()
		n := len(c.failed)
		c.mu.Unlock()
		if n > 0 {
			break
		}
	}
	for i := 0; i < 5; i++ {
		c.pick("7", live)
	}
	time.Sleep(100 * time.Millisecond)
	if calls != 1 {
		t.Fatalf("fetch called %d times, want 1", calls)
	}
}

func TestAvatarHostIsRestrictedToMetaCDN(t *testing.T) {
	if _, err := fetchAvatarBytes(context.Background(), "https://evil.example.com/a.jpg"); err == nil {
		t.Fatal("fetched from a foreign host")
	}
	if _, err := fetchAvatarBytes(context.Background(), "http://scontent.xx.fbcdn.net/a.jpg"); err == nil {
		t.Fatal("fetched over plain http")
	}
}

// The root causes: a message carries no avatar of its own when the sender's contact row
// arrived later (or never came with the message, as for encrypted chats), and a stored
// signed URL dies. The avatar is worked out when the message is handed out.
func TestMessageViewFillsAvatarFromContact(t *testing.T) {
	st := newStore(t.TempDir(), zerolog.Nop())
	live := cdnURL("ann", 24*time.Hour)
	stale := cdnURL("ann-old", -time.Hour)
	st.putMessage(&storedMsg{Message: Message{ID: "a", ChatID: "c", SenderID: "10", Timestamp: 1, Kind: "text"}, Sort: 1})
	st.putMessage(&storedMsg{Message: Message{ID: "b", ChatID: "c", SenderID: "10", SenderAvatar: stale, Timestamp: 2, Kind: "text"}, Sort: 2})
	st.putMessage(&storedMsg{Message: Message{ID: "e", ChatID: "c", SenderID: "10", Timestamp: 3, Kind: "text", FromMe: true}, Sort: 3})

	// No contact row yet: nothing to show, and the expired stored URL is not handed out.
	for _, id := range []string{"a", "b"} {
		if m, _ := st.message("c", id); m.SenderAvatar != "" {
			t.Fatalf("%s: avatar %q without any known picture", id, m.SenderAvatar)
		}
	}
	// The contact arrives: every earlier message gets the picture.
	st.setContact("10", "Ann", live)
	for _, id := range []string{"a", "b"} {
		if m, _ := st.message("c", id); m.SenderAvatar != live || m.SenderName != "Ann" {
			t.Fatalf("%s: %+v", id, m)
		}
	}
	if list, _ := st.listMessages("c", "", 10); len(list) != 3 || list[0].SenderAvatar != live {
		t.Fatalf("list: %+v", list)
	}
	// Our own messages carry none.
	if m, _ := st.message("c", "e"); m.SenderAvatar != "" {
		t.Fatalf("own message avatar %q", m.SenderAvatar)
	}
	// The file on disk keeps what was stored, not the decorated view.
	st.mu.RLock()
	raw := snapshot(st.msgs["c"][0]).SenderAvatar
	st.mu.RUnlock()
	if raw != "" {
		t.Fatalf("view leaked into storage: %q", raw)
	}
}

func TestChatViewAvatarSkipsExpiredURL(t *testing.T) {
	h := newHarness(t)
	b := h.b
	live := cdnURL("peer", time.Hour)
	b.st.updateThread("55", func(th *threadRec) {
		th.Key, th.Type, th.Avatar = 55, 1, cdnURL("old", -time.Hour)
	})
	b.st.setContact("55", "Peer", live)
	th, _ := b.st.thread("55")
	if got := b.chatView(th).Avatar; got != live {
		t.Fatalf("chat avatar = %q, want %q", got, live)
	}
}
