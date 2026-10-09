package mcp

import (
	"bufio"
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"mime"
	"net/http"
	"strings"
	"sync"
)

// streamable is the Streamable HTTP transport: each message is a POST, answered with a JSON body or an event
// stream that ends with the response. A session id the server gives is sent back with every later message; a 404
// in a session loses the connection, as the protocol asks the client to start a new session.
type streamable struct {
	url    string
	header http.Header
	// client has a transport of its own, so closing frees its connections.
	client *http.Client

	mu      sync.Mutex
	session string
}

func newStreamable(server *Server) *streamable {
	// A run's cancellation ends a call, not a client timeout.
	return &streamable{
		url: server.URL, header: server.Header,
		client: &http.Client{Transport: &http.Transport{Proxy: http.ProxyFromEnvironment, ForceAttemptHTTP2: true}},
	}
}

func (h *streamable) send(ctx context.Context, msg []byte, id int64, version string) (response, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, h.url, bytes.NewReader(msg))
	if err != nil {
		return response{}, fmt.Errorf("make the request: %w", err)
	}
	req.Header = h.header.Clone()
	if req.Header == nil {
		req.Header = http.Header{}
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json, text/event-stream")
	req.Header.Set("MCP-Protocol-Version", version)
	h.mu.Lock()
	session := h.session
	h.mu.Unlock()
	if session != "" {
		req.Header.Set("Mcp-Session-Id", session)
	}
	resp, err := h.client.Do(req)
	if err != nil {
		return response{}, fmt.Errorf("send the request: %w", err)
	}
	// Only a body cut short could fail to close, and its read has failed already.
	defer func() { _ = resp.Body.Close() }()
	switch {
	case resp.StatusCode == http.StatusNotFound && session != "":
		return response{}, errors.New("the server ended the session")
	case resp.StatusCode < 200 || resp.StatusCode > 299:
		return response{}, fmt.Errorf("the server answered HTTP %s", resp.Status)
	}
	if given := resp.Header.Get("Mcp-Session-Id"); given != "" {
		h.mu.Lock()
		h.session = given
		h.mu.Unlock()
	}
	if id == 0 {
		return response{}, nil
	}
	if media, _, _ := mime.ParseMediaType(resp.Header.Get("Content-Type")); media == "text/event-stream" {
		return readEvents(resp.Body, id)
	}
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return response{}, fmt.Errorf("read the response: %w", err)
	}
	if r, got, ok := parseResponse(body); ok && got == id {
		return r, nil
	}
	return response{}, errors.New("the server's answer is not a response to the request")
}

// readEvents reads an event stream until the response to request id. Each event's data lines, joined, are one
// message; the server's own requests and notifications before the response are skipped.
func readEvents(body io.Reader, id int64) (response, error) {
	stream := bufio.NewReader(body)
	var data []byte
	for {
		line, err := stream.ReadString('\n')
		if err != nil && !errors.Is(err, io.EOF) {
			return response{}, fmt.Errorf("read the event stream: %w", err)
		}
		// An event ends at a blank line; one the stream ends without is incomplete, and dropped.
		switch field := strings.TrimRight(line, "\r\n"); {
		case strings.HasPrefix(field, "data:"):
			data = append(data, strings.TrimPrefix(field[len("data:"):], " ")...)
			data = append(data, '\n')
		case field == "" && data != nil && err == nil:
			if r, got, ok := parseResponse(data); ok && got == id {
				return r, nil
			}
			data = nil
		}
		if err != nil {
			return response{}, errors.New("the server ended its event stream without a response")
		}
	}
}

func (h *streamable) close() {
	h.client.CloseIdleConnections()
}
