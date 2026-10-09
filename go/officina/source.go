package officina

import (
	"context"
	"fmt"
	"slices"
)

// ToolSource is where tools that share a connection come from, such as an MCP server; its tools name it as their
// Source. Each run connects its agent's sources before its first model call, and fails if one cannot connect; a
// source lost later gives error results for its calls. Connection changes go to the audit trail. A source is told
// apart from another by ==, so it must be comparable, such as a pointer.
type ToolSource interface {
	// Name names the source in the audit trail and telemetry.
	Name() string
	// Connect makes sure the source is connected, reconnecting it if it was lost. Its error says why it cannot,
	// and holds no secret. Runs may call it concurrently.
	Connect(ctx context.Context) error
	// Changes returns the connection changes since it was last called, oldest first; each is returned once. Runs
	// take them when they connect and after each reply's tool calls, so a change of a source that several runs
	// share is recorded by whichever run takes it first.
	Changes() []SourceChange
}

// SourceChange is a change to a tool source's connection; Detail says why it failed or was lost.
type SourceChange struct {
	State  SourceState
	Detail string
}

// SourceState is a tool source's connection state, as the audit trail records it.
type SourceState int

// The states a tool source's connection changes to.
const (
	// SourceConnected means the source connected.
	SourceConnected SourceState = iota + 1
	// SourceFailed means an attempt to connect failed.
	SourceFailed
	// SourceLost means a connection was lost.
	SourceLost
)

// String returns the state's name.
func (s SourceState) String() string {
	return name(int(s), "SourceState", "SourceConnected", "SourceFailed", "SourceLost")
}

// sourcesOf returns the distinct sources of tools, in the order of the tools. It fails for a source that cannot be
// compared, which == panics on.
func sourcesOf(tools []Tool) (sources []ToolSource, err error) {
	defer func() {
		if r := recover(); r != nil {
			sources, err = nil, fmt.Errorf("a tool source cannot be compared, as it must be: %v", r)
		}
	}()
	for _, t := range tools {
		if t.Source != nil && !slices.Contains(sources, t.Source) {
			sources = append(sources, t.Source)
		}
	}
	return sources, nil
}

// connectSources connects each of the agent's tool sources and records their changes; it returns why the run
// cannot start, or "" when all connected or ctx ended first.
func (a *Agent) connectSources(ctx context.Context, audit *recorder) string {
	unavailable := ""
	for _, s := range a.sources {
		if err := s.Connect(ctx); err != nil {
			if ctx.Err() == nil {
				unavailable = fmt.Sprintf("the tool source %q is not available: %v", s.Name(), err)
			}
			break
		}
	}
	a.recordSourceChanges(ctx, audit)
	return unavailable
}

// recordSourceChanges writes the connection changes of the agent's tool sources to the audit trail.
func (a *Agent) recordSourceChanges(ctx context.Context, audit *recorder) {
	for _, s := range a.sources {
		for _, c := range s.Changes() {
			outcome := "disconnected"
			switch c.State {
			case SourceConnected:
				outcome = "connected"
			case SourceFailed:
				outcome = "failed"
			}
			// A missing entry blocks nothing: the change has happened.
			_ = audit.record(ctx, AuditEntry{Kind: AuditToolSource, Tool: s.Name(), Outcome: outcome, Detail: c.Detail})
		}
	}
}
