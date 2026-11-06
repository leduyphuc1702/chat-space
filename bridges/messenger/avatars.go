package main

// Profile pictures.
//
// Meta serves avatars from signed CDN URLs whose `oe=` parameter is the expiry (hex unix
// seconds, about a month out). A URL stored on a message or contact therefore dies by
// itself, and the app (which has no cookies and no way to refresh it) shows a blank
// avatar for it. Two things keep avatars working:
//
//   - senderAvatar / chat avatars are worked out when a message or chat is handed to the
//     app (store.view, chatView), from the freshest contact row, never frozen at the moment
//     the message first arrived;
//   - a picture whose URL we already know is copied once under ${dataDir}/avatars from the
//     CDN host (plain GET of that URL, no cookies, no Meta API) and then served as an
//     absolute local path, which cannot expire.
//
// The fetches are paced and only start after the session was restored (hello), so tests
// and a logged-out bridge never touch the network.

import (
	"context"
	"crypto/sha1"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	avatarFetchPause   = 300 * time.Millisecond // between two downloads
	avatarRetryAfter   = 6 * time.Hour          // a picture that failed is not asked for again before this
	avatarMaxBytes     = 2 << 20
	avatarQueueSize    = 512
	avatarFetchTimeout = 20 * time.Second
)

type avatarJob struct {
	key, url, hash string
}

type avatarCache struct {
	dir string
	// fetch downloads one picture; replaced in tests. It returns the bytes.
	fetch func(ctx context.Context, rawURL string) ([]byte, error)

	mu      sync.Mutex
	enabled bool
	started bool
	files   map[string]avatarFile // key -> newest cached picture
	pending map[string]bool       // key|hash being fetched or queued
	failed  map[string]time.Time  // key|hash -> last failed attempt
	queue   chan avatarJob
	now     func() time.Time
}

type avatarFile struct {
	path, hash string
}

func newAvatarCache(dir string) *avatarCache {
	c := &avatarCache{
		dir:     dir,
		fetch:   fetchAvatarBytes,
		files:   map[string]avatarFile{},
		pending: map[string]bool{},
		failed:  map[string]time.Time{},
		queue:   make(chan avatarJob, avatarQueueSize),
		now:     time.Now,
	}
	c.scan()
	return c
}

// avatarFileName is "<key>-<hash>.<ext>": the hash is of the picture's URL path (stable
// while the signature in the query changes), so a new profile picture gets a new file.
func avatarFileName(key, hash, ext string) string {
	return safeFileName(key) + "-" + hash + ext
}

// scan loads the pictures cached by earlier runs.
func (c *avatarCache) scan() {
	entries, err := os.ReadDir(c.dir)
	if err != nil {
		return
	}
	newest := map[string]time.Time{}
	for _, e := range entries {
		name := e.Name()
		ext := filepath.Ext(name)
		stem := strings.TrimSuffix(name, ext)
		i := strings.LastIndexByte(stem, '-')
		if e.IsDir() || i <= 0 || strings.HasPrefix(name, ".") {
			continue
		}
		info, err := e.Info()
		if err != nil || info.Size() == 0 {
			continue
		}
		key, hash := stem[:i], stem[i+1:]
		if prev, ok := newest[key]; ok && !info.ModTime().After(prev) {
			continue
		}
		newest[key] = info.ModTime()
		c.files[key] = avatarFile{path: filepath.Join(c.dir, name), hash: hash}
	}
}

// enable lets pick() start downloads. Called once the session is restored.
func (c *avatarCache) enable() {
	c.mu.Lock()
	c.enabled = true
	c.mu.Unlock()
}

// avatarExpired reports whether a signed Meta URL is past its `oe=` expiry. URLs without
// one (local paths, data: URLs, unsigned URLs) are never considered expired.
func avatarExpired(raw string, now time.Time) bool {
	if !strings.HasPrefix(raw, "http") {
		return false
	}
	u, err := url.Parse(raw)
	if err != nil {
		return false
	}
	oe := u.Query().Get("oe")
	if oe == "" {
		return false
	}
	n, err := strconv.ParseInt(oe, 16, 64)
	if err != nil || n <= 0 {
		return false
	}
	return now.Unix() >= n
}

func avatarHash(raw string) string {
	u, err := url.Parse(raw)
	p := raw
	if err == nil {
		p = u.Path
	}
	sum := sha1.Sum([]byte(p))
	return hex.EncodeToString(sum[:5])
}

// pick chooses what to hand to the app as the avatar of `key` (a contact id, or "chat-<id>"
// for a group picture) among candidate URLs, best first:
//
//   - the cached local file, when it is the picture the first live candidate points to;
//   - else the first candidate that has not expired (a download is queued);
//   - else the cached file, however old (the URLs are all dead);
//   - else "" so the app falls back to its placeholder instead of a broken image.
func (c *avatarCache) pick(key string, candidates ...string) string {
	if c == nil {
		for _, u := range candidates {
			if u != "" && !avatarExpired(u, time.Now()) {
				return u
			}
		}
		return ""
	}
	c.mu.Lock()
	now := c.now()
	file, haveFile := c.files[key]
	live := ""
	for _, u := range candidates {
		if u == "" || avatarExpired(u, now) {
			continue
		}
		live = u
		break
	}
	if live == "" {
		c.mu.Unlock()
		if haveFile {
			return file.path
		}
		return ""
	}
	if !strings.HasPrefix(live, "http") {
		c.mu.Unlock()
		return live // a local path or data: URL needs no caching
	}
	hash := avatarHash(live)
	if haveFile && file.hash == hash {
		c.mu.Unlock()
		return file.path
	}
	var job *avatarJob
	if c.enabled && c.dir != "" {
		pk := key + "|" + hash
		if !c.pending[pk] && now.Sub(c.failed[pk]) >= avatarRetryAfter {
			c.pending[pk] = true
			job = &avatarJob{key: key, url: live, hash: hash}
		}
	}
	startWorker := job != nil && !c.started
	if startWorker {
		c.started = true
	}
	c.mu.Unlock()
	if job != nil {
		select {
		case c.queue <- *job:
		default: // queue full: try again on a later view
			c.mu.Lock()
			delete(c.pending, key+"|"+hash)
			c.mu.Unlock()
		}
	}
	if startWorker {
		go c.worker()
	}
	return live
}

func (c *avatarCache) worker() {
	for job := range c.queue {
		c.download(job)
		time.Sleep(avatarFetchPause)
	}
}

func (c *avatarCache) download(job avatarJob) {
	pk := job.key + "|" + job.hash
	done := func(ok bool) {
		c.mu.Lock()
		delete(c.pending, pk)
		if !ok {
			c.failed[pk] = c.now()
		}
		c.mu.Unlock()
	}
	ctx, cancel := context.WithTimeout(context.Background(), avatarFetchTimeout)
	defer cancel()
	data, err := c.fetch(ctx, job.url)
	if err != nil || len(data) == 0 {
		done(false)
		return
	}
	ext := avatarExt(data)
	if ext == "" {
		done(false)
		return
	}
	path := filepath.Join(c.dir, avatarFileName(job.key, job.hash, ext))
	if err := atomicWrite(path, data, 0o600); err != nil {
		done(false)
		return
	}
	c.mu.Lock()
	old, had := c.files[job.key]
	c.files[job.key] = avatarFile{path: path, hash: job.hash}
	c.mu.Unlock()
	if had && old.path != path {
		_ = os.Remove(old.path)
	}
	done(true)
}

func avatarExt(data []byte) string {
	switch http.DetectContentType(data) {
	case "image/jpeg":
		return ".jpg"
	case "image/png":
		return ".png"
	case "image/gif":
		return ".gif"
	case "image/webp":
		return ".webp"
	}
	return ""
}

// avatarHostOK limits downloads to Meta's media CDNs.
func avatarHostOK(u *url.URL) bool {
	if u.Scheme != "https" {
		return false
	}
	h := strings.ToLower(u.Hostname())
	for _, suffix := range []string{".fbcdn.net", ".fbsbx.com", ".cdninstagram.com"} {
		if strings.HasSuffix(h, suffix) {
			return true
		}
	}
	return false
}

// fetchAvatarBytes GETs a picture from the CDN host named in its (already known) URL. It
// sends no cookies and talks to nothing but that host.
func fetchAvatarBytes(ctx context.Context, rawURL string) ([]byte, error) {
	u, err := url.Parse(rawURL)
	if err != nil || !avatarHostOK(u) {
		return nil, fmt.Errorf("avatar URL is not on a Meta CDN host")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)")
	req.Header.Set("Accept", "image/*")
	resp, err := (&http.Client{Timeout: avatarFetchTimeout}).Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("avatar HTTP %d", resp.StatusCode)
	}
	return io.ReadAll(io.LimitReader(resp.Body, avatarMaxBytes))
}

// forget removes every cached picture (logout).
func (c *avatarCache) forget() {
	c.mu.Lock()
	c.files = map[string]avatarFile{}
	c.pending = map[string]bool{}
	c.failed = map[string]time.Time{}
	c.mu.Unlock()
	if c.dir != "" {
		_ = os.RemoveAll(c.dir)
	}
}
