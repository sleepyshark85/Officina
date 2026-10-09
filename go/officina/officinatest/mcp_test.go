package officinatest_test

import (
	"bytes"
	"context"
	"encoding/json/jsontext"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// mcpTools are the fake server's tools in these tests: echo, annotated read-only, and fail.
func mcpTools() []officinatest.MCPTool {
	yes := true
	return []officinatest.MCPTool{
		{Name: "echo", ReadOnly: &yes, Handler: func(in jsontext.Value) (string, error) { return string(in), nil }},
		{Name: "fail", Description: "Fails.", InputSchema: `{"type":"object"}`, Handler: func(jsontext.Value) (string, error) {
			return "", errors.New("it broke")
		}},
	}
}

func TestMCPServer_TEST01_ServesStdioOneMessagePerLine(t *testing.T) {
	t.Parallel()
	server := officinatest.NewMCPServer(mcpTools()...)
	in := strings.Join([]string{
		`{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}`,
		`{"jsonrpc":"2.0","method":"notifications/initialized"}`,
		`not a message`,
		`{"jsonrpc":"2.0","id":2,"method":"tools/list"}`,
		`{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{"cursor":"1"}}`,
		`{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}`,
		`{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"fail"}}`,
		`{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"missing"}}`,
		`{"jsonrpc":"2.0","id":7,"method":"ping"}`,
		`{"jsonrpc":"2.0","id":8,"method":"resources/list"}`,
	}, "\n")
	var out bytes.Buffer

	if err := server.Serve(t.Context(), strings.NewReader(in), &out); err != nil {
		t.Fatalf("Serve() error = %v", err)
	}

	const note = `{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info","data":"working"}}`
	want := []string{
		`{"id":1,"jsonrpc":"2.0","result":{"capabilities":{"tools":{}},"protocolVersion":"2025-06-18","serverInfo":{"name":"fake","version":"1.0.0"}}}`,
		`{"id":2,"jsonrpc":"2.0","result":{"nextCursor":"1","tools":[{"annotations":{"readOnlyHint":true},"description":"The echo tool.","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}},"name":"echo"}]}}`,
		`{"id":3,"jsonrpc":"2.0","result":{"tools":[{"description":"Fails.","inputSchema":{"type":"object"},"name":"fail"}]}}`,
		note,
		`{"id":4,"jsonrpc":"2.0","result":{"content":[{"text":"{\"text\":\"hi\"}","type":"text"}],"isError":false}}`,
		note,
		`{"id":5,"jsonrpc":"2.0","result":{"content":[{"text":"it broke","type":"text"}],"isError":true}}`,
		note,
		`{"error":{"code":-32601,"message":"Method or tool not found."},"id":6,"jsonrpc":"2.0"}`,
		`{"id":7,"jsonrpc":"2.0","result":{}}`,
		`{"error":{"code":-32601,"message":"Method or tool not found."},"id":8,"jsonrpc":"2.0"}`,
	}
	if diff := cmp.Diff(want, strings.Split(strings.TrimSuffix(out.String(), "\n"), "\n")); diff != "" {
		t.Errorf("output mismatch (-want +got):\n%s", diff)
	}
	wantCalls := []officinatest.MCPCall{{"echo", `{"text":"hi"}`}, {"fail", "{}"}, {"missing", "{}"}}
	if diff := cmp.Diff(wantCalls, server.Calls()); diff != "" {
		t.Errorf("Calls() mismatch (-want +got):\n%s", diff)
	}
}

func TestMCPServer_TEST01_StdioStopsWhenTheContextEndsOrTheOutputFails(t *testing.T) {
	t.Parallel()
	server := officinatest.NewMCPServer()
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if err := server.Serve(ctx, strings.NewReader(""), io.Discard); !errors.Is(err, context.Canceled) {
		t.Errorf("Serve() with its context ended = %v, want context.Canceled", err)
	}
	in := strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"ping"}` + "\n")
	if err := server.Serve(t.Context(), in, failingWriter{}); err == nil {
		t.Error("Serve() to a failing output succeeded, want an error")
	}
}

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) {
	return 0, errors.New("closed")
}

// post sends body to the server at url with the headers, and returns the status, the body and the session.
func post(t *testing.T, url, body string, header http.Header) (status int, text, session string) {
	t.Helper()
	req, err := http.NewRequestWithContext(t.Context(), http.MethodPost, url, strings.NewReader(body))
	if err != nil {
		t.Fatalf("NewRequest() error = %v", err)
	}
	req.Header = header
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return 0, err.Error(), ""
	}
	defer func() { _ = resp.Body.Close() }() // The body has been read.
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("ReadAll() error = %v", err)
	}
	return resp.StatusCode, string(data), resp.Header.Get("Mcp-Session-Id")
}

// httpMCP serves an MCP server with the test tools over HTTP, requiring the token tok, until the test ends.
func httpMCP(t *testing.T) (*officinatest.MCPServer, string) {
	t.Helper()
	server := officinatest.NewMCPServer(mcpTools()...)
	server.Token, server.ProtocolVersion = "tok", "2025-03-26"
	srv := httptest.NewServer(server)
	t.Cleanup(srv.Close)
	t.Cleanup(http.DefaultClient.CloseIdleConnections)
	return server, srv.URL
}

// mcpHeaders returns the headers of a request in session, with the name and value pairs set.
func mcpHeaders(session string, pairs ...string) http.Header {
	h := http.Header{"Authorization": {"Bearer tok"}, "Accept": {"application/json, text/event-stream"}}
	if session != "" {
		h.Set("Mcp-Session-Id", session)
	}
	for i := 0; i+1 < len(pairs); i += 2 {
		h.Set(pairs[i], pairs[i+1])
	}
	return h
}

const (
	mcpInitialize = `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}`
	mcpCall       = `{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{}}}`
)

// initialize starts a session with the server at url, and returns the headers of a request in it.
func initialize(t *testing.T, url string) http.Header {
	t.Helper()
	status, body, session := post(t, url, mcpInitialize, mcpHeaders(""))
	if status != http.StatusOK || session == "" || !strings.Contains(body, `"protocolVersion":"2025-03-26"`) {
		t.Fatalf("initialize = %d %q, session %q; want 200 with the server's version and a session", status, body, session)
	}
	return mcpHeaders(session, "MCP-Protocol-Version", "2025-03-26")
}

func TestMCPServer_TEST01_ServesStreamableHTTPAsTheProtocolRequires(t *testing.T) {
	t.Parallel()
	_, url := httpMCP(t)
	versioned := initialize(t, url)
	session := versioned.Get("Mcp-Session-Id")
	const stream = `data:{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info","data":"working"}}` +
		"\n\n" + `data: {"id":2,"jsonrpc":"2.0","result":{"content":[{"text":"{}","type":"text"}],"isError":false}}` + "\n\n"
	tests := []struct {
		name, body string
		header     http.Header
		status     int
		want       string
	}{
		{"a call streams a notification, then its result", mcpCall, versioned, http.StatusOK, stream},
		{"a notification is accepted", `{"jsonrpc":"2.0","method":"notifications/initialized"}`, versioned,
			http.StatusAccepted, ""},
		{"without the token", mcpCall, mcpHeaders(session, "Authorization", "Bearer no", "MCP-Protocol-Version", "x"),
			http.StatusUnauthorized, ""},
		{"accepting one content type", mcpCall,
			mcpHeaders(session, "Accept", "application/json", "MCP-Protocol-Version", "x"), http.StatusBadRequest, ""},
		{"without the version", mcpCall, mcpHeaders(session), http.StatusBadRequest, ""},
		{"in another session", mcpCall, mcpHeaders("other", "MCP-Protocol-Version", "x"), http.StatusNotFound, ""},
		{"not JSON", "{", versioned, http.StatusBadRequest, ""},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			status, body, _ := post(t, url, tt.body, tt.header)
			if status != tt.status || body != tt.want {
				t.Errorf("POST = %d %q, want %d %q", status, body, tt.status, tt.want)
			}
		})
	}
}

func TestMCPServer_TEST01_AnEndedSessionIsRefusedAndAServerDownDropsRequests(t *testing.T) {
	t.Parallel()
	server, url := httpMCP(t)
	versioned := initialize(t, url)

	server.EndSession()
	if status, _, _ := post(t, url, mcpCall, versioned); status != http.StatusNotFound {
		t.Errorf("a call in an ended session = %d, want 404", status)
	}
	server.SetDown(true)
	if status, _, _ := post(t, url, mcpInitialize, mcpHeaders("")); status != 0 {
		t.Errorf("a request to a server down = %d, want the connection dropped", status)
	}
	server.SetDown(false)
	server.SetTools()
	versioned = initialize(t, url)
	if status, body, _ := post(t, url, `{"jsonrpc":"2.0","id":3,"method":"tools/list"}`, versioned); status != 200 ||
		body != `{"id":3,"jsonrpc":"2.0","result":{"tools":[]}}` {
		t.Errorf("tools/list with no tools = %d %q", status, body)
	}
}
