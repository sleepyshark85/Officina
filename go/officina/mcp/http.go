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
	// A response, with what the server sends before it, is at most maxMessage bytes.
	body := &capped{r: resp.Body, left: maxMessage}
	if media, _, _ := mime.ParseMediaType(resp.Header.Get("Content-Type")); media == "text/event-stream" {
		return readEvents(body, id)
	}
	data, err := io.ReadAll(body)
	if err != nil {
		return response{}, fmt.Errorf("read the response: %w", err)
	}
	if r, got, ok := parseResponse(data); ok && got == id {
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

// capped reads from r until it has read left bytes, then fails with errTooLong if r holds more.
type capped struct {
	r    io.Reader
	left int64
}

func (c *capped) Read(p []byte) (int, error) {
	if c.left == 0 {
		var one [1]byte
		if n, err := c.r.Read(one[:]); n > 0 {
			return 0, errTooLong
		} else if err != nil {
			return 0, err //nolint:wrapcheck // io.EOF must reach the reader as it is.
		}
		return 0, nil
	}
	if int64(len(p)) > c.left {
		p = p[:c.left]
	}
	n, err := c.r.Read(p)
	c.left -= int64(n)
	return n, err //nolint:wrapcheck // As above.
}

func (h *streamable) close() {
	h.client.CloseIdleConnections()
}
