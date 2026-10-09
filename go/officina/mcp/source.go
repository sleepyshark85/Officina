package mcp

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"fmt"
	"maps"
	"net/http"
	"regexp"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/sleepyshark85/officina/go/officina"
)

// connectTimeout is how long connecting, or checking a connection, may take.
const connectTimeout = 30 * time.Second

// Server says how to reach an MCP server: a program to start, over stdio, or a URL, over Streamable HTTP. Its
// environment variable and header values count as credentials (see Secrets).
type Server struct {
	// Name names the server and prefixes its tools' names, so it holds only ASCII letters, digits, '_' and '-'.
	Name string
	// Command, with Args, starts the server as a child process that speaks over its standard input and output.
	// Env adds environment variables, each "key=value", to those of this process.
	Command string
	Args    []string
	Env     []string
	// URL is the server's Streamable HTTP endpoint; Header is sent with every request, such as a credential.
	URL    string
	Header http.Header

	// exitWait, when set, replaces exitWait for this server, so tests of a stubborn one stay fast.
	exitWait time.Duration
}

// Secrets returns the values that may hold credentials: those of Env and Header. Give them to the agent as
// secrets, so they never reach events, telemetry or the audit trail; the source itself redacts them from its tools'
// results and its errors.
func (s Server) Secrets() []string {
	var secrets []string
	for _, v := range s.Env {
		if _, value, _ := strings.Cut(v, "="); value != "" {
			secrets = append(secrets, value)
		}
	}
	for _, name := range slices.Sorted(maps.Keys(s.Header)) {
		for _, v := range s.Header[name] {
			if v != "" {
				secrets = append(secrets, v)
			}
		}
	}
	return secrets
}

// redact returns text with every stretch that holds one of the server's credentials replaced by "[redacted]", as
// the core redacts an agent's secrets: it finds every occurrence of every credential in the original text and
// merges those that overlap or touch, so credentials that share characters are redacted whole whatever their order.
func (s Server) redact(text string) string {
	secrets := s.Secrets()
	var b strings.Builder
	done, start, end := 0, -1, -1
	for at := range len(text) {
		for _, secret := range secrets {
			if !strings.HasPrefix(text[at:], secret) {
				continue
			}
			if at > end {
				if start >= 0 {
					b.WriteString(text[done:start] + "[redacted]")
					done = end
				}
				start = at
			}
			end = max(end, at+len(secret))
		}
	}
	if start < 0 {
		return text
	}
	b.WriteString(text[done:start] + "[redacted]" + text[end:])
	return b.String()
}

// check reports what makes the server unusable.
func (s Server) check() error {
	// The pattern is a constant that compiles.
	named, _ := regexp.MatchString(`^[A-Za-z0-9_-]+$`, s.Name)
	switch {
	case !named:
		return errors.New("the name must be ASCII letters, digits, '_' and '-', as it prefixes tool names")
	case (s.Command == "") == (s.URL == ""):
		return errors.New("give either a command or a URL")
	}
	return nil
}

// AllowedTool is a tool of the server that the host lets the agent use, and how it runs.
type AllowedTool struct {
	// Name is the tool's name on the server.
	Name string
	// Kind is Write when it is zero. A server's read-only annotation is not trusted: a write wrongly marked read
	// would run alongside other calls, and even when its attempt could not be audited.
	Kind          officina.ToolKind
	NeedsApproval bool
}

// Source is an MCP server's allowed tools, as tools of the core. It reads the server's tool list once, keeps the
// allowed tools, names each "<server>__<tool>" and pins them, so Tools stays the same for every conversation. It is
// the tools' officina.ToolSource: each run reconnects a server that was lost, and fails if it cannot; a server lost
// during a run gives error results for its calls. Close it when done.
type Source struct {
	server Server
	tools  []officina.Tool

	// connecting lets one Connect or Close at a time check or replace the connection.
	connecting sync.Mutex
	mu         sync.Mutex
	conn       *conn
	closed     bool
	changes    []officina.SourceChange
}

// Connect connects to server and pins its allowed tools, in the order of allowed. It fails if the server cannot
// be started or reached, speaks another protocol version, or lacks an allowed tool. Once it has returned, ctx
// ending no longer affects the connection: Close ends it. If ctx ends while connecting, a server it started is
// stopped, and has exited when Connect returns.
func Connect(ctx context.Context, server Server, allowed []AllowedTool) (*Source, error) {
	if err := server.check(); err != nil {
		return nil, fmt.Errorf("connect to mcp server %q: %w", server.Name, err)
	}
	server.Args, server.Env, server.Header = slices.Clone(server.Args), slices.Clone(server.Env), server.Header.Clone()
	s := &Source{server: server}
	if err := s.Connect(ctx); err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, connectTimeout)
	defer cancel()
	listed, err := s.current().listTools(ctx)
	if err == nil {
		s.tools, err = s.pin(allowed, listed)
	}
	if err != nil {
		s.Close()
		return nil, err
	}
	return s, nil
}

// Name returns the server's name.
func (s *Source) Name() string {
	return s.server.Name
}

// Tools returns the allowed tools, in the order they were allowed.
func (s *Source) Tools() []officina.Tool {
	return slices.Clone(s.tools)
}

// Connect checks the connection, and reconnects if it was lost; the tool list stays pinned. Runs call it before
// their first model call.
func (s *Source) Connect(ctx context.Context) error {
	s.connecting.Lock()
	defer s.connecting.Unlock()
	if s.isClosed() {
		return fmt.Errorf("mcp server %q: the source is closed", s.server.Name)
	}
	timed, cancel := context.WithTimeout(ctx, connectTimeout)
	defer cancel()
	if c := s.current(); c != nil {
		if c.lostReason() == "" {
			err := c.ping(timed)
			switch {
			case err == nil:
				return nil
			case ctx.Err() != nil:
				return err
			case timed.Err() != nil:
				s.change(officina.SourceLost, s.silent())
			}
		}
		s.mu.Lock()
		s.conn = nil
		s.mu.Unlock()
		c.t.close()
	}
	c, err := s.open(timed)
	switch {
	case err != nil && ctx.Err() != nil:
		return err
	case err != nil:
		if timed.Err() != nil {
			err = errors.New(s.silent())
		}
		s.change(officina.SourceFailed, err.Error())
		return err
	}
	s.mu.Lock()
	s.conn = c
	s.changes = append(s.changes, officina.SourceChange{State: officina.SourceConnected})
	s.mu.Unlock()
	return nil
}

// silent says the server did not answer in time.
func (s *Source) silent() string {
	return fmt.Sprintf("mcp server %q did not answer within %v", s.server.Name, connectTimeout)
}

// open starts or reaches the server and agrees on the protocol. On any failure, ctx ending included, it closes
// the connection, so no server process outlives it.
func (s *Source) open(ctx context.Context) (*conn, error) {
	c := &conn{server: &s.server, version: newestVersion}
	if s.server.URL != "" {
		c.t = newStreamable(&s.server)
	} else {
		// The process outlives ctx, which bounds only the connecting: Close stops it.
		t, err := startStdio(&s.server)
		if err != nil {
			return nil, err
		}
		c.t = t
	}
	// A connection that fails while connecting is reported as failed, not also as lost.
	c.onLost = func(reason string) {
		s.mu.Lock()
		defer s.mu.Unlock()
		if s.conn == c {
			s.changes = append(s.changes, officina.SourceChange{State: officina.SourceLost, Detail: reason})
		}
	}
	if err := c.initialize(ctx); err != nil {
		c.t.close()
		return nil, err
	}
	return c, nil
}

// Changes returns the connection changes since it was last called, oldest first.
func (s *Source) Changes() []officina.SourceChange {
	s.mu.Lock()
	defer s.mu.Unlock()
	changes := s.changes
	s.changes = nil
	return changes
}

// Close closes the connection. A stdio server's input is closed, so it can exit by itself; one that has not
// within 5 seconds is killed with every process it started. On Unix the server runs in a process group of its own,
// and what is left of the group is killed as soon as it has exited too; on Windows a process whose parent has exited
// is no longer found, and is not stopped, nor on Unix one that left the group (with setsid). Close returns once the
// server has exited. The tools then give error results, and runs fail to connect the source.
func (s *Source) Close() {
	s.connecting.Lock()
	defer s.connecting.Unlock()
	s.mu.Lock()
	c := s.conn
	s.conn, s.closed = nil, true
	s.mu.Unlock()
	if c != nil {
		c.t.close()
	}
}

func (s *Source) current() *conn {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.conn
}

func (s *Source) isClosed() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.closed
}

func (s *Source) change(state officina.SourceState, detail string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.changes = append(s.changes, officina.SourceChange{State: state, Detail: detail})
}

// pin returns the core's tool for each allowed tool of the server's list.
func (s *Source) pin(allowed []AllowedTool, listed []listedTool) ([]officina.Tool, error) {
	byName := map[string]listedTool{}
	for _, l := range listed {
		byName[l.Name] = l
	}
	tools := make([]officina.Tool, 0, len(allowed))
	for _, a := range allowed {
		l, ok := byName[a.Name]
		if !ok {
			return nil, fmt.Errorf("mcp server %q has no tool %q; it has: %s", s.server.Name, a.Name,
				strings.Join(slices.Sorted(maps.Keys(byName)), ", "))
		}
		kind := a.Kind
		if kind == 0 {
			kind = officina.Write
		}
		tools = append(tools, officina.Tool{
			Name: s.server.Name + "__" + a.Name, Description: l.Description, InputSchema: l.InputSchema, Kind: kind,
			NeedsApproval: a.NeedsApproval, Source: s,
			Handler: func(ctx context.Context, input jsontext.Value) (string, error) {
				return s.call(ctx, a.Name, input)
			},
		})
	}
	return tools, nil
}

// call calls the tool name on the server. Its error, which the model gets as an error result, says when the
// server is not connected or failed, or the tool returned an error.
func (s *Source) call(ctx context.Context, name string, input jsontext.Value) (string, error) {
	c := s.current()
	if c == nil {
		return "", fmt.Errorf("mcp server %q is not connected", s.server.Name)
	}
	if reason := c.lostReason(); reason != "" {
		return "", fmt.Errorf("mcp server %q is not connected: %s", s.server.Name, reason)
	}
	result, err := c.callTool(ctx, name, input)
	if err != nil {
		return "", err
	}
	parts := make([]string, len(result.Content))
	for i, item := range result.Content {
		parts[i] = item.Text
		if item.Type != "text" {
			parts[i] = "[" + item.Type + " content]"
		}
	}
	// The server's own credentials are redacted, whether or not the host gave them to the agent as secrets.
	content := s.server.redact(strings.Join(parts, "\n"))
	if result.IsError {
		return "", &toolError{content}
	}
	return content, nil
}

// toolError is a tool's error result, its content as the server sent it.
type toolError struct {
	content string
}

func (e *toolError) Error() string {
	return e.content
}
