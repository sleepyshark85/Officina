package officinatest

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"sync"
)

// MCPServer is a fake MCP server with tools given in advance, over stdio (Serve, run by a test program) or
// Streamable HTTP (as an http.Handler, such as httptest.NewServer serves). It lists one tool per page, sends a
// notification before each call's result (over HTTP, in an event stream), which clients must skip, and records the
// calls. Over HTTP it requires what the protocol does: both content types accepted, the session it gave, and the
// protocol version once initialized; stricter than a real server, which assumes an older version without that
// header. It can require a bearer token, go down, or end its session. Make it with NewMCPServer, and set its fields
// before it serves.
type MCPServer struct {
	// Token, when set, is the bearer token HTTP requests must carry.
	Token string
	// ProtocolVersion is the version the server answers with; empty answers the one the client asks for.
	ProtocolVersion string

	mu      sync.Mutex
	tools   []MCPTool
	calls   []MCPCall
	session string
	down    bool
}

// MCPTool is a tool of an MCPServer.
type MCPTool struct {
	Name string
	// Description is "The <name> tool." when empty.
	Description string
	// InputSchema is {"type":"object","properties":{"text":{"type":"string"}}} when empty.
	InputSchema string
	// ReadOnly is the readOnlyHint annotation; nil lists no annotations.
	ReadOnly *bool
	// Handler answers a call's arguments; its error is the call's error result, with the error's text.
	Handler func(arguments jsontext.Value) (string, error)
}

// MCPCall is a tool call an MCPServer received, with its arguments as JSON text.
type MCPCall struct {
	Tool, Arguments string
}

// mcpNotification is what the server sends before each call's result.
const mcpNotification = `{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info","data":"working"}}`

// NewMCPServer returns a server with tools.
func NewMCPServer(tools ...MCPTool) *MCPServer {
	return &MCPServer{tools: tools, session: rand.Text()}
}

// SetTools replaces the tools the server lists, as a server whose tools change between connections.
func (s *MCPServer) SetTools(tools ...MCPTool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.tools = tools
}

// SetDown makes the HTTP server drop every request while down is true, as if it had stopped; a tool may set it
// to fail mid-call.
func (s *MCPServer) SetDown(down bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.down = down
}

// EndSession forgets the HTTP session, as a restarted server does: a request in it then gets 404.
func (s *MCPServer) EndSession() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.session = rand.Text()
}

// Calls returns the tool calls received so far, in order.
func (s *MCPServer) Calls() []MCPCall {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]MCPCall(nil), s.calls...)
}

// Serve serves over stdio, one JSON-RPC message per line, until in ends or ctx is done. A line that is not a
// message is skipped.
func (s *MCPServer) Serve(ctx context.Context, in io.Reader, out io.Writer) error {
	lines := bufio.NewReader(in)
	for ctx.Err() == nil {
		line, err := lines.ReadBytes('\n')
		if len(line) > 0 {
			var req mcpRequest
			if json.Unmarshal(line, &req) == nil {
				var reply []byte
				if req.Method == "tools/call" {
					reply = append(reply, mcpNotification+"\n"...)
				}
				if answer := s.handle(req); answer != nil {
					reply = append(append(reply, answer...), '\n')
				}
				if _, err := out.Write(reply); err != nil {
					return fmt.Errorf("serve mcp: %w", err)
				}
			}
		}
		if errors.Is(err, io.EOF) {
			return nil
		}
		if err != nil {
			return fmt.Errorf("serve mcp: %w", err)
		}
	}
	return fmt.Errorf("serve mcp: %w", ctx.Err())
}

// ServeHTTP answers one message of the Streamable HTTP transport.
func (s *MCPServer) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if s.isDown() {
		abort(w)
		return
	}
	var req mcpRequest
	if body, err := io.ReadAll(r.Body); err != nil || json.Unmarshal(body, &req) != nil {
		w.WriteHeader(http.StatusBadRequest)
		return
	}
	accept := r.Header.Get("Accept")
	initializing := req.Method == "initialize"
	s.mu.Lock()
	known := initializing || r.Header.Get("Mcp-Session-Id") == s.session
	session := s.session
	s.mu.Unlock()
	var (
		authorized = s.Token == "" || r.Header.Get("Authorization") == "Bearer "+s.Token
		accepts    = strings.Contains(accept, "application/json") && strings.Contains(accept, "text/event-stream")
		versioned  = initializing || r.Header.Get("MCP-Protocol-Version") != ""
		answer     []byte
	)
	// As a real server does, a request in an unknown session is refused before anything runs.
	if authorized && accepts && versioned && known {
		answer = s.handle(req)
	}
	if s.isDown() {
		abort(w)
		return
	}
	switch {
	case !authorized:
		w.WriteHeader(http.StatusUnauthorized)
	case !accepts || !versioned:
		w.WriteHeader(http.StatusBadRequest)
	case !known:
		w.WriteHeader(http.StatusNotFound)
	case answer == nil:
		w.WriteHeader(http.StatusAccepted)
	case req.Method == "tools/call":
		// The notification's field has no space after the colon, which the event stream format allows.
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = fmt.Fprintf(w, "data:%s\n\ndata: %s\n\n", mcpNotification, answer) // The client sees a failure.
	default:
		if initializing {
			w.Header().Set("Mcp-Session-Id", session)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(answer) // As above.
	}
}

// abort drops the request's connection without an answer.
func abort(w http.ResponseWriter) {
	conn, _, err := http.NewResponseController(w).Hijack()
	if err == nil {
		_ = conn.Close() // The connection is dropped either way.
	}
}

func (s *MCPServer) isDown() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.down
}

// mcpRequest is a JSON-RPC request or notification.
type mcpRequest struct {
	ID     jsontext.Value `json:"id"`
	Method string         `json:"method"`
	Params struct {
		ProtocolVersion string         `json:"protocolVersion"`
		Cursor          string         `json:"cursor"`
		Name            string         `json:"name"`
		Arguments       jsontext.Value `json:"arguments"`
	} `json:"params"`
}

// handle returns the response to req, or nil for a notification.
func (s *MCPServer) handle(req mcpRequest) []byte {
	if req.ID == nil {
		return nil
	}
	var result any
	switch req.Method {
	case "initialize":
		version := s.ProtocolVersion
		if version == "" {
			version = req.Params.ProtocolVersion
		}
		result = map[string]any{
			"protocolVersion": version, "capabilities": map[string]any{"tools": map[string]any{}},
			"serverInfo": map[string]any{"name": "fake", "version": "1.0.0"},
		}
	case "ping":
		result = map[string]any{}
	case "tools/list":
		page, _ := strconv.Atoi(req.Params.Cursor) // No cursor is the first page.
		result = s.list(page)
	case "tools/call":
		if r := s.call(req.Params.Name, req.Params.Arguments); r != nil {
			result = r
		}
	}
	response := map[string]any{"jsonrpc": "2.0", "id": req.ID, "result": result}
	if result == nil {
		delete(response, "result")
		response["error"] = map[string]any{"code": -32601, "message": "Method or tool not found."}
	}
	data, err := json.Marshal(response, json.Deterministic(true))
	if err != nil {
		// Only a tool's invalid input schema fails, which is the test's bug.
		data, _ = json.Marshal(map[string]any{"jsonrpc": "2.0", "id": req.ID, "error": map[string]any{
			"code": -32603, "message": err.Error(),
		}})
	}
	return data
}

// list returns the page of the tool list: one tool per page.
func (s *MCPServer) list(page int) map[string]any {
	s.mu.Lock()
	defer s.mu.Unlock()
	listed := []any{}
	if page >= 0 && page < len(s.tools) {
		t := s.tools[page]
		description, schema := t.Description, t.InputSchema
		if description == "" {
			description = "The " + t.Name + " tool."
		}
		if schema == "" {
			schema = `{"type":"object","properties":{"text":{"type":"string"}}}`
		}
		tool := map[string]any{"name": t.Name, "description": description, "inputSchema": jsontext.Value(schema)}
		if t.ReadOnly != nil {
			tool["annotations"] = map[string]any{"readOnlyHint": *t.ReadOnly}
		}
		listed = append(listed, tool)
	}
	result := map[string]any{"tools": listed}
	if page+1 < len(s.tools) {
		result["nextCursor"] = strconv.Itoa(page + 1)
	}
	return result
}

// call runs the tool name; nil when there is no such tool.
func (s *MCPServer) call(name string, arguments jsontext.Value) map[string]any {
	if arguments == nil {
		arguments = jsontext.Value("{}")
	}
	s.mu.Lock()
	s.calls = append(s.calls, MCPCall{Tool: name, Arguments: string(arguments)})
	i := slices.IndexFunc(s.tools, func(t MCPTool) bool { return t.Name == name })
	var handler func(jsontext.Value) (string, error)
	if i >= 0 {
		handler = s.tools[i].Handler
	}
	s.mu.Unlock()
	if i < 0 {
		return nil
	}
	var (
		text string
		err  error
	)
	if handler != nil {
		if text, err = handler(arguments); err != nil {
			text = err.Error()
		}
	}
	return map[string]any{"content": []any{map[string]any{"type": "text", "text": text}}, "isError": err != nil}
}
