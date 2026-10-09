//go:build windows

package mcp

import (
	"context"
	"os/exec"
	"strconv"
)

// ownGroup does nothing on Windows: a process's tree is found from its parent, and killTree stops it.
func ownGroup(*exec.Cmd) {}

// killTree kills the server and every process it started, through taskkill, as the standard library has no job
// objects. A process whose parent has already exited is no longer in the tree, and is not stopped.
func killTree(pid int) {
	ctx, cancel := context.WithTimeout(context.Background(), exitWait)
	defer cancel()
	// When taskkill fails, the server has exited already; Wait's own kill follows in any case.
	_ = exec.CommandContext(ctx, "taskkill", "/T", "/F", "/PID", strconv.Itoa(pid)).Run()
}

// endGroup does nothing on Windows: once the server has exited, what it started is no longer found from it.
func endGroup(int) {}
