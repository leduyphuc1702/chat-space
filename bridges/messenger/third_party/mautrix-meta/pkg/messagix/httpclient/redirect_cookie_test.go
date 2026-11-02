package httpclient

import (
	"io"
	"net/http"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/util/exhttp"

	"go.mau.fi/mautrix-meta/pkg/messagix/cookies"
	"go.mau.fi/mautrix-meta/pkg/messagix/types"
)

type fakeParent struct {
	log zerolog.Logger
	jar *cookies.Cookies
}

func (f *fakeParent) GetPlatform() types.Platform    { return types.Facebook }
func (f *fakeParent) GetLogger() *zerolog.Logger     { return &f.log }
func (f *fakeParent) GetCookies() *cookies.Cookies   { return f.jar }
func (f *fakeParent) GetEndpoint(name string) string { return "" }
func (f *fakeParent) IsAuthenticated() bool          { return true }

type regionalRedirect struct{}

func (regionalRedirect) RoundTrip(r *http.Request) (*http.Response, error) {
	resp := &http.Response{Request: r, Header: http.Header{}, Body: io.NopCloser(strings.NewReader(""))}
	switch {
	case r.URL.Host == "www.facebook.com":
		resp.StatusCode = http.StatusFound
		resp.Header.Set("Location", "https://web.facebook.com/messages?_rdc=1&_rdr")
	case strings.Contains(r.Header.Get("cookie"), "xs=secret"):
		resp.StatusCode = http.StatusOK
	default:
		resp.StatusCode = http.StatusFound
		resp.Header.Set("Location", "https://web.facebook.com/login.php?next=x")
	}
	return resp, nil
}

func TestRegionalRedirectKeepsCookies(t *testing.T) {
	jar := &cookies.Cookies{}
	jar.UpdateValues(map[cookies.MetaCookieName]string{"c_user": "1", "xs": "secret"})
	c := NewHTTPClient(&fakeParent{log: zerolog.Nop(), jar: jar}, nil, exhttp.ClientSettings{})
	c.SetTransportOverride(regionalRedirect{})
	req, _ := http.NewRequest(http.MethodGet, "https://www.facebook.com/messages", nil)
	req.Header.Set("cookie", jar.String())
	resp, err := c.HTTP.Do(req)
	if err != nil {
		t.Fatalf("redirect lost the session: %v", err)
	}
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status %d", resp.StatusCode)
	}
}
