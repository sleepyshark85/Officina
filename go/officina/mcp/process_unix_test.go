//go:build unix

package mcp_test

import (
	"errors"
	"syscall"
	"testing"
)

// process is a process a test watches, by its id: the id is not given to another process until this one has been
// waited for, and then only once the ids have wrapped around.
type process struct {
	pid int
}

// watch watches the process pid, which must be running.
func watch(_ *testing.T, pid int) process {
	return process{pid: pid}
}

// exited reports whether the process has exited and been waited for: a zombie still takes signal 0.
func (p process) exited(_ *testing.T) bool {
	return errors.Is(syscall.Kill(p.pid, 0), syscall.ESRCH)
}
