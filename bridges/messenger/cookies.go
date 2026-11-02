package main

// Cookie ingestion: the only login method for Messenger is a logged-in browser
// session. We accept whatever a user can realistically copy out of a browser:
//
//   - a raw Cookie header:           c_user=1000...; xs=34%3A...; datr=...; fr=...
//   - a JSON object:                 {"c_user":"1000...","xs":"...","datr":"..."}
//   - a JSON array (cookie export):  [{"name":"c_user","value":"...","domain":".facebook.com"}, ...]
//   - a Netscape cookies.txt file.
//
// The required set is whatever messagix says it is (cookies.FBRequiredCookies:
// xs, c_user, datr).

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

const cookieFileName = "cookies.json"

var cookieNameRe = regexp.MustCompile(`^[A-Za-z0-9_.\-]+$`)

// cookieAttrNames are Set-Cookie attributes that show up when someone pastes a
// whole Set-Cookie line instead of a Cookie header.
var cookieAttrNames = map[string]bool{
	"path": true, "domain": true, "expires": true, "max-age": true, "secure": true,
	"httponly": true, "samesite": true, "priority": true, "partitioned": true,
}

type rawCookie struct {
	Name, Value, Domain string
}

// parseCookieInput turns the "cookies" param (a JSON string, or an inline JSON
// object/array) into name→value pairs plus a platform hint derived from cookie
// domains (types.Unset when there is no hint).
func parseCookieInput(raw json.RawMessage) (map[string]string, types.Platform, error) {
	raw = bytes.TrimSpace(raw)
	if len(raw) == 0 || string(raw) == "null" {
		return nil, types.Unset, newErr(codeInvalidParams, "cookies is required")
	}
	var text string
	if raw[0] == '"' {
		if err := json.Unmarshal(raw, &text); err != nil {
			return nil, types.Unset, newErr(codeInvalidParams, "cookies is not a valid string: %v", err)
		}
	} else {
		text = string(raw)
	}
	return parseCookieText(text)
}

func parseCookieText(text string) (map[string]string, types.Platform, error) {
	text = strings.TrimSpace(text)
	if text == "" {
		return nil, types.Unset, newErr(codeInvalidParams, "cookies is empty")
	}
	var list []rawCookie
	switch {
	case text[0] == '{' || text[0] == '[':
		dec := json.NewDecoder(strings.NewReader(text))
		dec.UseNumber()
		var v any
		if err := dec.Decode(&v); err != nil {
			return nil, types.Unset, newErr(codeInvalidParams, "cookies look like JSON but could not be parsed: %v", err)
		}
		list = collectJSONCookies(v)
	case strings.Contains(text, "\t") && strings.Contains(text, "\n"):
		list = parseNetscape(text)
	default:
		list = parseCookieHeader(text)
	}
	if len(list) == 0 {
		return nil, types.Unset, newErr(codeInvalidParams, "no cookies found in the supplied value")
	}
	vals, hint := resolveCookies(list)
	if len(vals) == 0 {
		return nil, types.Unset, newErr(codeInvalidParams, "no usable facebook.com/messenger.com cookies found in the supplied value")
	}
	return vals, hint, nil
}

func parseCookieHeader(text string) []rawCookie {
	// Tolerate "Cookie: " prefixes and multi-line pastes.
	text = strings.TrimSpace(text)
	if len(text) >= 7 && strings.EqualFold(text[:7], "cookie:") {
		text = text[7:]
	}
	var out []rawCookie
	for _, tok := range strings.FieldsFunc(text, func(r rune) bool { return r == ';' || r == '\n' || r == '\r' }) {
		tok = strings.TrimSpace(tok)
		name, value, ok := strings.Cut(tok, "=")
		name = strings.TrimSpace(name)
		if !ok || name == "" || (cookieAttrNames[strings.ToLower(name)]) {
			continue
		}
		out = append(out, rawCookie{Name: name, Value: value})
	}
	return out
}

func parseNetscape(text string) []rawCookie {
	var out []rawCookie
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimRight(line, "\r")
		line = strings.TrimPrefix(line, "#HttpOnly_")
		if strings.TrimSpace(line) == "" || strings.HasPrefix(line, "#") {
			continue
		}
		f := strings.Split(line, "\t")
		if len(f) < 7 {
			continue
		}
		out = append(out, rawCookie{Domain: f[0], Name: f[5], Value: f[6]})
	}
	return out
}

func scalarString(v any) (string, bool) {
	switch t := v.(type) {
	case string:
		return t, true
	case json.Number:
		return t.String(), true
	case bool:
		return fmt.Sprint(t), true
	}
	return "", false
}

func pickString(m map[string]any, keys ...string) string {
	for _, k := range keys {
		if v, ok := m[k]; ok {
			if s, ok := scalarString(v); ok {
				return s
			}
		}
	}
	return ""
}

func collectJSONCookies(v any) []rawCookie {
	var out []rawCookie
	switch t := v.(type) {
	case []any:
		for _, item := range t {
			out = append(out, collectJSONCookies(item)...)
		}
	case map[string]any:
		if inner, ok := t["cookies"]; ok {
			return collectJSONCookies(inner)
		}
		name := pickString(t, "name", "Name", "key")
		if _, hasValue := t["value"]; name != "" && (hasValue || t["Value"] != nil) {
			out = append(out, rawCookie{
				Name:   name,
				Value:  pickString(t, "value", "Value"),
				Domain: pickString(t, "domain", "Domain", "host", "host_key"),
			})
			return out
		}
		// Plain {"c_user": "...", "xs": "..."} map.
		for k, val := range t {
			if s, ok := scalarString(val); ok {
				out = append(out, rawCookie{Name: k, Value: s})
			} else if inner, ok := val.(map[string]any); ok {
				if s := pickString(inner, "value", "Value"); s != "" {
					out = append(out, rawCookie{Name: k, Value: s, Domain: pickString(inner, "domain", "Domain")})
				}
			}
		}
	}
	return out
}

func domainIs(domain, suffix string) bool {
	return strings.Contains(strings.ToLower(domain), suffix)
}

// resolveCookies normalises values, drops cookies of unrelated domains (browser
// exports often contain every cookie of the profile) and works out whether the
// cookies came from messenger.com or facebook.com.
func resolveCookies(list []rawCookie) (map[string]string, types.Platform) {
	hasFB, hasMsgr := false, false
	for _, c := range list {
		if domainIs(c.Domain, "facebook.com") {
			hasFB = true
		}
		if domainIs(c.Domain, "messenger.com") {
			hasMsgr = true
		}
	}
	hint := types.Unset
	switch {
	case hasFB:
		hint = types.Facebook
	case hasMsgr:
		hint = types.Messenger
	}
	vals := make(map[string]string, len(list))
	for _, c := range list {
		name := strings.TrimSpace(c.Name)
		if !cookieNameRe.MatchString(name) {
			continue
		}
		if c.Domain != "" {
			related := domainIs(c.Domain, "facebook.com") || domainIs(c.Domain, "messenger.com") || domainIs(c.Domain, "fb.com")
			if !related {
				continue
			}
			// When both sites are present, prefer the chosen platform's copy.
			if hasFB && hasMsgr {
				want := "facebook.com"
				if hint == types.Messenger {
					want = "messenger.com"
				}
				if !domainIs(c.Domain, want) {
					if _, exists := vals[name]; exists {
						continue
					}
				}
			}
		}
		val := strings.TrimSpace(c.Value)
		val = strings.Trim(val, `"`)
		if val == "" {
			continue
		}
		if name == "xs" && strings.Contains(val, ":") && !strings.Contains(val, "%") {
			// Browsers store xs percent-encoded; some exporters decode it.
			val = url.QueryEscape(val)
		}
		vals[name] = val
	}
	return vals, hint
}

// buildCookies validates the cookie set against messagix's own requirements.
func buildCookies(platform types.Platform, vals map[string]string) (*cookies.Cookies, error) {
	if platform == types.Unset {
		platform = types.Facebook // an unset platform would demand Instagram's cookie set
	}
	c := &cookies.Cookies{Platform: platform}
	m := make(map[cookies.MetaCookieName]string, len(vals))
	for k, v := range vals {
		m[cookies.MetaCookieName(k)] = v
	}
	c.UpdateValues(m)
	if missing := c.GetMissingCookieNames(); len(missing) > 0 {
		names := make([]string, len(missing))
		for i, n := range missing {
			names[i] = string(n)
		}
		return nil, newErr(codeInvalidParams,
			"missing required cookie(s): %s (a logged-in %s session needs xs, c_user and datr)",
			strings.Join(names, ", "), platform)
	}
	if c.GetUserID() == 0 {
		return nil, newErr(codeInvalidParams, "cookie c_user must be the numeric Facebook user ID, got %q", c.Get(cookies.FBCookieCUser))
	}
	return c, nil
}

func parsePlatform(name string) (types.Platform, error) {
	switch strings.ToLower(strings.TrimSpace(name)) {
	case "":
		return types.Unset, nil
	case "facebook", "fb", "facebook.com":
		return types.Facebook, nil
	case "messenger", "messenger.com":
		return types.Messenger, nil
	}
	return types.Unset, newErr(codeInvalidParams, `platform must be "facebook" or "messenger"`)
}

// Persistence ---------------------------------------------------------------

// cookieFile is the on-disk session. Platform is types.Platform.String() (so a
// native password login is "messenger-lite"); NativeSession is only present for
// the Messenger Lite Android flow. Older files without the field still load.
type cookieFile struct {
	Platform      string               `json:"platform"`
	Cookies       map[string]string    `json:"cookies"`
	NativeSession *types.NativeSession `json:"nativeSession,omitempty"`
}

func cookiePath(dataDir string) string { return filepath.Join(dataDir, cookieFileName) }

func saveCookieFile(dataDir string, c *cookies.Cookies, native *types.NativeSession) error {
	all := c.GetAll()
	f := cookieFile{Platform: c.Platform.String(), Cookies: make(map[string]string, len(all)), NativeSession: native}
	for k, v := range all {
		f.Cookies[string(k)] = v
	}
	data, err := json.MarshalIndent(f, "", "  ")
	if err != nil {
		return err
	}
	return atomicWrite(cookiePath(dataDir), data, 0o600)
}

// loadCookieFile returns (nil, nil, nil) when nothing has been saved yet.
func loadCookieFile(dataDir string) (*cookies.Cookies, *types.NativeSession, error) {
	data, err := os.ReadFile(cookiePath(dataDir))
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil, nil
	} else if err != nil {
		return nil, nil, err
	}
	var f cookieFile
	if err := json.Unmarshal(data, &f); err != nil {
		return nil, nil, fmt.Errorf("corrupt %s: %w", cookieFileName, err)
	}
	platform := types.PlatformFromString(f.Platform)
	if platform == types.Unset {
		platform = types.Facebook
	}
	ck, err := buildCookies(platform, f.Cookies)
	if err != nil {
		return nil, nil, err
	}
	native := f.NativeSession
	if validateNativeSession(native) != nil {
		native = nil // the web session still works without it; it is only needed for push
	}
	return ck, native, nil
}

func removeCookieFile(dataDir string) {
	_ = os.Remove(cookiePath(dataDir))
}

func sortedKeys[V any](m map[string]V) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// atomicWrite writes via a temp file + rename so a crash never leaves a torn file.
func atomicWrite(path string, data []byte, perm os.FileMode) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(filepath.Dir(path), filepath.Base(path)+".tmp-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		os.Remove(tmpName)
		return err
	}
	if err := tmp.Chmod(perm); err != nil {
		tmp.Close()
		os.Remove(tmpName)
		return err
	}
	if err := tmp.Close(); err != nil {
		os.Remove(tmpName)
		return err
	}
	return os.Rename(tmpName, path)
}
