//go:build unix

package mcp

import (
	"os/exec"
	"syscall"
)

// ownGroup makes the server the leader of a process group of its own, which its children join, so stopping the
// group stops them too, whether they were started by a launcher such as npx, uvx or a shell script, or by the server.
// It also keeps the terminal's Ctrl+C, which reaches the foreground group, from reaching the server.
func ownGroup(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
}

// killTree kills the server's process group: the server and every process it started that is still in the group.
func killTree(pid int) {
	// It fails only when no process of the group is left.
	_ = syscall.Kill(-pid, syscall.SIGKILL)
}

// endGroup kills what is left of the server's process group once the server has exited, such as a child that ignored
// the end of its input or holds its output. The group's id cannot be reused while a process of it is left. Once the
// server is reaped and none is left, the id is free, and the system could in principle give it to a new group before
// the kill: the window is a few instructions long, after a whole group has gone, so it is accepted.
func endGroup(pid int) {
	killTree(pid)
}
