//go:build unix

package mcp_test

import (
	"errors"
	"syscall"
)

// exited reports whether the process pid has exited and been waited for: a zombie still takes signal 0.
func exited(pid int) bool {
	return errors.Is(syscall.Kill(pid, 0), syscall.ESRCH)
}
