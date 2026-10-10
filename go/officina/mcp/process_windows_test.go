//go:build windows

package mcp_test

import (
	"syscall"
	"testing"
)

// process is a process a test watches, through a handle opened while it is known to be running. Its id alone says
// nothing once it has exited: another process, such as a virus scanner, may still hold a handle to it, so it can
// still be opened, and once none does, the id may be given to a new process.
type process struct {
	pid int
	h   syscall.Handle
}

// watch opens the process pid, which must be running, until the test ends.
func watch(t *testing.T, pid int) process {
	t.Helper()
	h, err := syscall.OpenProcess(syscall.SYNCHRONIZE, false, uint32(pid))
	if err != nil {
		t.Fatalf("open process %d: %v", pid, err)
	}
	t.Cleanup(func() { _ = syscall.CloseHandle(h) }) // The close error is dropped: the test has ended and nothing could act on it.
	return process{pid: pid, h: h}
}

// exited reports whether the process has exited. Unlike a zombie on Unix, a process left unwaited for cannot be
// told apart from one held by another process; the client's Wait closes its handle itself.
func (p process) exited(t *testing.T) bool {
	t.Helper()
	s, err := syscall.WaitForSingleObject(p.h, 0)
	if s == syscall.WAIT_FAILED {
		t.Fatalf("wait for process %d: %v", p.pid, err)
	}
	return s == syscall.WAIT_OBJECT_0
}
