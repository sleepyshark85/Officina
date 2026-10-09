package mcp

import "time"

// WithExitWait returns s with how long a closing stdio server may take to exit before it is killed, for the tests
// of servers that never exit by themselves.
func WithExitWait(s Server, wait time.Duration) Server {
	s.exitWait = wait
	return s
}
