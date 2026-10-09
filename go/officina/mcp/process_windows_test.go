//go:build windows

package mcp_test

import "os"

// exited reports whether the process pid has exited and every handle to it is closed, so it can no longer be
// opened.
func exited(pid int) bool {
	p, err := os.FindProcess(pid)
	if err != nil {
		return true
	}
	_ = p.Release() // Only the handle just opened is released.
	return false
}
