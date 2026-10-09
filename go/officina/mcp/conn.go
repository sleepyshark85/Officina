package mcp

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"sync"
	"sync/atomic"
)

// The protocol versions this client speaks: it asks for the newest, and accepts any of them in answer.
const (
	newestVersion  = "2025-06-18"
	versionsSpoken = "2025-06-18, 2025-03-26, 2024-11-05"
)

// spoken reports whether this client speaks protocol version v.
func spoken(v string) bool {
	switch v {
	case "2025-06-18", "2025-03-26", "2024-11-05":
		return true
	}
	return false
}

// transport carries JSON-RPC messages to a server and back: stdio or Streamable HTTP.
type transport interface {
	// send sends msg, a request with that id, and returns the response to it; or a notification, when id is 0,
	// and returns no response. version is the protocol version agreed. Its error is the transport failing, or
	// ctx ending.
	send(ctx context.Context, msg []byte, id int64, version string) (response, error)
	// close ends the connection, and returns once everything the transport started has stopped.
	close()
}

// response is a JSON-RPC response: its result, or its error.
type response struct {
	ID     jsontext.Value `json:"id"`
	Method string         `json:"method"`
	Result jsontext.Value `json:"result"`
	Error  *struct {
		Message string `json:"message"`
	} `json:"error"`
}

// parseResponse returns the response msg holds and its id. It reports false for anything else a server may write:
// a request or notification of its own, or what is not a JSON-RPC response to a request of this client's, whose
// ids are positive integers.
func parseResponse(msg []byte) (r response, id int64, ok bool) {
	if err := json.Unmarshal(msg, &r); err != nil || r.Method != "" || (r.Result == nil && r.Error == nil) {
		return response{}, 0, false
	}
	if err := json.Unmarshal(r.ID, &id); err != nil || id <= 0 {
		return response{}, 0, false
	}
	return r, id, true
}

// conn is a connection to one MCP server: JSON-RPC 2.0 over a transport, with only what a tool source needs:
// initialize, ping, tools/list and tools/call. A transport failure loses the connection for good: every later
// request fails, and the source connects anew at the next run.
type conn struct {
	server *Server
	t      transport
	// version is the protocol version agreed; set while initializing, before the connection is shared.
	version string
	lastID  atomic.Int64
	// onLost is told, once, why the connection was lost.
	onLost func(reason string)

	mu   sync.Mutex
	lost string
}

// rpcError is a server's JSON-RPC error answer: it is reachable, but refused or failed the request.
type rpcError struct {
	server, method, message string
}

func (e *rpcError) Error() string {
	return fmt.Sprintf("mcp server %q answered %s with an error: %s", e.server, e.method, e.message)
}

// initialize agrees on the protocol; the server accepts other requests only after it.
func (c *conn) initialize(ctx context.Context) error {
	type info struct {
		Name    string `json:"name"`
		Version string `json:"version"`
	}
	params := struct {
		ProtocolVersion string   `json:"protocolVersion"`
		Capabilities    struct{} `json:"capabilities"`
		ClientInfo      info     `json:"clientInfo"`
	}{ProtocolVersion: newestVersion, ClientInfo: info{Name: "officina", Version: "1.0.0"}}
	var result struct {
		ProtocolVersion string `json:"protocolVersion"`
	}
	if err := c.request(ctx, "initialize", params, &result); err != nil {
		return err
	}
	if !spoken(result.ProtocolVersion) {
		return fmt.Errorf("mcp server %q speaks protocol %q; this client speaks %s", c.server.Name,
			result.ProtocolVersion, versionsSpoken)
	}
	c.version = result.ProtocolVersion
	_, err := c.exchange(ctx, "notifications/initialized", nil, 0)
	return err
}

// ping checks that the server answers; an error answer counts, as it came from the server.
func (c *conn) ping(ctx context.Context) error {
	err := c.request(ctx, "ping", nil, nil)
	if _, answered := errors.AsType[*rpcError](err); answered {
		return nil
	}
	return err
}

// listedTool is a tool as a server lists it.
type listedTool struct {
	Name        string         `json:"name"`
	Description string         `json:"description"`
	InputSchema jsontext.Value `json:"inputSchema"`
}

// listTools returns the server's tools, from every page of its list.
func (c *conn) listTools(ctx context.Context) ([]listedTool, error) {
	var (
		tools  []listedTool
		cursor string
	)
	for {
		var params any
		if cursor != "" {
			params = map[string]string{"cursor": cursor}
		}
		var page struct {
			Tools      []listedTool `json:"tools"`
			NextCursor string       `json:"nextCursor"`
		}
		if err := c.request(ctx, "tools/list", params, &page); err != nil {
			return nil, err
		}
		tools = append(tools, page.Tools...)
		if page.NextCursor == "" {
			return tools, nil
		}
		cursor = page.NextCursor
	}
}

// callResult is what a tool call returns: its content, and whether it is an error.
type callResult struct {
	Content []struct {
		Type string `json:"type"`
		Text string `json:"text"`
	} `json:"content"`
	IsError bool `json:"isError"`
}

// callTool calls the tool name on the server with arguments, a JSON object.
func (c *conn) callTool(ctx context.Context, name string, arguments jsontext.Value) (callResult, error) {
	params := struct {
		Name      string         `json:"name"`
		Arguments jsontext.Value `json:"arguments"`
	}{name, arguments}
	var result callResult
	err := c.request(ctx, "tools/call", params, &result)
	return result, err
}

// request sends a request and reads its result into result, unless result is nil.
func (c *conn) request(ctx context.Context, method string, params, result any) error {
	id := c.lastID.Add(1)
	r, err := c.exchange(ctx, method, params, id)
	switch {
	case err != nil:
		return err
	case r.Error != nil:
		return &rpcError{server: c.server.Name, method: method, message: c.server.redact(r.Error.Message)}
	case result == nil:
		return nil
	}
	if err := json.Unmarshal(r.Result, result); err != nil {
		return fmt.Errorf("mcp server %q answered %s with an unreadable result: %w", c.server.Name, method, err)
	}
	return nil
}

// exchange sends a message, a request when id is not 0, and returns its response. A transport failure loses the
// connection; ctx ending does not.
func (c *conn) exchange(ctx context.Context, method string, params any, id int64) (response, error) {
	if reason := c.lostReason(); reason != "" {
		return response{}, errors.New(reason)
	}
	msg, err := json.Marshal(struct {
		JSONRPC string `json:"jsonrpc"`
		ID      int64  `json:"id,omitzero"`
		Method  string `json:"method"`
		Params  any    `json:"params,omitzero"`
	}{"2.0", id, method, params})
	if err != nil {
		return response{}, fmt.Errorf("mcp server %q: write %s: %w", c.server.Name, method, err)
	}
	r, err := c.t.send(ctx, msg, id, c.version)
	switch {
	case err != nil && ctx.Err() != nil:
		return response{}, fmt.Errorf("mcp server %q, %s: %w", c.server.Name, method, ctx.Err())
	case err != nil:
		return response{}, c.lose(fmt.Sprintf("mcp server %q could not be reached: %v", c.server.Name, err))
	}
	return r, nil
}

// lose marks the connection lost, once, tells onLost why, and returns the error a request fails with.
func (c *conn) lose(reason string) error {
	reason = c.server.redact(reason)
	c.mu.Lock()
	first := c.lost == ""
	if first {
		c.lost = reason
	}
	reason = c.lost
	c.mu.Unlock()
	if first {
		c.onLost(reason)
	}
	return errors.New(reason)
}

// lostReason returns why the connection was lost; "" while it works.
func (c *conn) lostReason() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.lost
}
