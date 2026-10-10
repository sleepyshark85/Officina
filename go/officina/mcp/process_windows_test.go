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
	t.Cleanup(func() { _ = syscall.CloseHandle(h) }) // A handle opened here closes; nothing is lost if not.
	return process{pid: pid, h: h}
}

// exited reports whether the process has exited. Unlike a zombie on Unix, a process left unwaited for cannot be
// told apart from one held by another process; the client's Wait closes its handle itself.
func (p process) exited() bool {
	s, _ := syscall.WaitForSingleObject(p.h, 0) // A failed wait returns WAIT_FAILED, which is not an exit.
	return s == syscall.WAIT_OBJECT_0
}
