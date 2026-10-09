package mcp

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strings"
	"sync"
	"time"
)

const (
	// exitWait is how long a closing server may take to exit, once its input is closed, before it is killed with
	// every process it started.
	exitWait = 5 * time.Second
	// writeFailedWait is how long a failed write waits for the server's exit, whose reason says more.
	writeFailedWait = time.Second
	// stderrKept is how much of the end of a server's error output is kept, to say why it ended.
	stderrKept = 4096
	// maxMessage is the longest message a server may send, in bytes; a longer one loses the connection.
	maxMessage = 16 << 20
)

// stdio is the stdio transport: the server is a child process, and each message is one line of its input or
// output. Responses are matched to requests by id, so calls may overlap. When the process ends, every waiting
// request fails, with the last line of its error output, which often says why.
type stdio struct {
	cmd   *exec.Cmd
	stdin io.WriteCloser
	// exitWait is how long close waits for the server to exit by itself, and how long its output may stay open once
	// it has.
	exitWait time.Duration
	// exited is closed once the process has exited and its output has been read; ended says how, by then.
	exited chan struct{}
	ended  string
	// wg waits for the goroutines that wait for the process and read its output.
	wg sync.WaitGroup

	// writing lets one message at a time be written.
	writing sync.Mutex
	mu      sync.Mutex
	waiting map[int64]chan response
	// overflow says why the server was stopped for a message too long; "" if it was not.
	overflow string
	stderr   tail
}

// startStdio starts the server's program, in a process group of its own.
func startStdio(server *Server) (*stdio, error) {
	// The process outlives any caller's context: close stops it.
	cmd := exec.Command(server.Command, server.Args...) //nolint:noctx // See above.
	cmd.Env = append(os.Environ(), server.Env...)
	ownGroup(cmd)
	s := &stdio{cmd: cmd, exitWait: server.exitWait, exited: make(chan struct{}), waiting: map[int64]chan response{}}
	if s.exitWait == 0 {
		s.exitWait = exitWait
	}
	// The server writes to pipes read here rather than by exec, so Wait returns as soon as the server exits, and what
	// is left of its group is killed then: a child that holds the output does not hold up the end.
	stdout, stdoutW, err := os.Pipe()
	if err != nil {
		return nil, fmt.Errorf("mcp server %q: make its output pipe: %w", server.Name, err)
	}
	stderr, stderrW, err := os.Pipe()
	if err != nil {
		_, _ = stdout.Close(), stdoutW.Close() // Pipes never used.
		return nil, fmt.Errorf("mcp server %q: make its error output pipe: %w", server.Name, err)
	}
	cmd.Stdout, cmd.Stderr = stdoutW, stderrW
	stdin, err := cmd.StdinPipe()
	if err == nil {
		s.stdin = stdin
		err = cmd.Start()
	}
	// The server holds the write ends now; were they open here too, the reads would never end.
	_, _ = stdoutW.Close(), stderrW.Close() // Closing an unused end cannot lose anything.
	if err != nil {
		_, _ = stdout.Close(), stderr.Close() // As above.
		return nil, fmt.Errorf("mcp server %q could not be started (%s): %w", server.Name, server.Command, err)
	}
	// Each reader says when its pipe has ended; it closes its end, so a server still writing fails.
	read := make(chan struct{}, 2)
	for _, r := range []struct {
		pipe *os.File
		to   io.Writer
	}{{stdout, &lines{max: maxMessage, line: s.dispatch, tooLong: s.tooLong}}, {stderr, &s.stderr}} {
		s.wg.Go(func() {
			// A copy ends when the pipe does, or when what it is written to refuses more (a message too long).
			_, _ = io.Copy(r.to, r.pipe)
			_ = r.pipe.Close() // Read to its end, or given up on.
			read <- struct{}{}
		})
	}
	s.wg.Go(func() {
		// How it exited matters less than what it said: the error output names the cause.
		_ = cmd.Wait()
		endGroup(cmd.Process.Pid)
		// A process that left the group, or one on Windows, may still hold the output: the pipes are cut off after
		// exitWait, as exec's WaitDelay would.
		timer := time.NewTimer(s.exitWait)
		defer timer.Stop()
		for ended := 0; ended < 2; {
			select {
			case <-read:
				ended++
			case <-timer.C:
				_, _ = stdout.Close(), stderr.Close() // Their reads end, and the readers report.
			}
		}
		s.mu.Lock()
		s.ended = "it closed its connection"
		if s.overflow != "" {
			s.ended = s.overflow
		}
		s.mu.Unlock()
		if said := s.stderr.lastLine(); said != "" {
			s.ended += ": " + said
		}
		close(s.exited)
	})
	return s, nil
}

// tooLong stops a server that sent a message longer than maxMessage.
func (s *stdio) tooLong() {
	s.mu.Lock()
	s.overflow = fmt.Sprintf("it sent a message longer than %d MB", maxMessage>>20)
	s.mu.Unlock()
	s.kill()
}

// kill kills the server and every process it started.
func (s *stdio) kill() {
	killTree(s.cmd.Process.Pid)
	// Where the tree could not be killed, the server itself is; it may have exited already.
	_ = s.cmd.Process.Kill()
}

// close closes the server's input, so it can exit by itself, as a container must to be removed. One that has not
// exited within exitWait is killed with every process it started. close returns once the server has exited.
func (s *stdio) close() {
	_ = s.stdin.Close() // A failure means it was closed already.
	timer := time.NewTimer(s.exitWait)
	defer timer.Stop()
	select {
	case <-s.exited:
	case <-timer.C:
		s.kill()
	}
	s.wg.Wait()
}

func (s *stdio) send(ctx context.Context, msg []byte, id int64, _ string) (response, error) {
	var answer chan response
	if id != 0 {
		answer = make(chan response, 1)
		s.mu.Lock()
		s.waiting[id] = answer
		s.mu.Unlock()
		defer func() {
			s.mu.Lock()
			delete(s.waiting, id)
			s.mu.Unlock()
		}()
	}
	select {
	case <-s.exited:
		return response{}, s.endedError()
	default:
	}
	s.writing.Lock()
	_, err := s.stdin.Write(append(msg, '\n'))
	s.writing.Unlock()
	if err != nil {
		select {
		case <-s.exited:
			return response{}, s.endedError()
		case <-time.After(writeFailedWait):
			return response{}, fmt.Errorf("write to the server: %w", err)
		}
	}
	if answer == nil {
		return response{}, nil
	}
	select {
	case r := <-answer:
		return r, nil
	case <-s.exited:
		// Its response may have come just before it exited.
		select {
		case r := <-answer:
			return r, nil
		default:
			return response{}, s.endedError()
		}
	case <-ctx.Done():
		return response{}, fmt.Errorf("wait for the response: %w", ctx.Err())
	}
}

// endedError says how the process ended; it is called once exited is closed.
func (s *stdio) endedError() error {
	return errors.New(s.ended)
}

// dispatch hands a line of the server's output to the request it responds to; it skips the server's own
// requests and notifications, and anything else.
func (s *stdio) dispatch(line []byte) {
	r, id, ok := parseResponse(line)
	if !ok {
		return
	}
	s.mu.Lock()
	answer := s.waiting[id]
	s.mu.Unlock()
	if answer != nil {
		// A second response with the same id finds the channel full, and is dropped.
		select {
		case answer <- r:
		default:
		}
	}
}

// lines splits what is written to it into lines, and hands each, without its line ending, to line. A line longer
// than max bytes is not kept: tooLong is told, once, and every later write fails.
type lines struct {
	buf     []byte
	max     int
	line    func([]byte)
	tooLong func()
	failed  bool
}

// errTooLong is a server's message over the length a transport accepts.
var errTooLong = fmt.Errorf("a message is longer than %d MB", maxMessage>>20)

func (l *lines) Write(p []byte) (int, error) {
	if l.failed {
		return 0, errTooLong
	}
	l.buf = append(l.buf, p...)
	// Once per line, so the loop ends however its body goes wrong.
	for range bytes.Count(l.buf, []byte("\n")) {
		line, rest, _ := bytes.Cut(l.buf, []byte("\n"))
		if len(line) > l.max {
			break
		}
		l.line(bytes.Clone(bytes.TrimSuffix(line, []byte("\r"))))
		l.buf = rest
	}
	if len(l.buf) > l.max {
		l.failed, l.buf = true, nil
		l.tooLong()
		return 0, errTooLong
	}
	if len(l.buf) == 0 {
		l.buf = nil
	}
	return len(p), nil
}

// tail keeps the end of what is written to it.
type tail struct {
	mu  sync.Mutex
	buf []byte
}

func (t *tail) Write(p []byte) (int, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.buf = append(t.buf, p...)
	if len(t.buf) > 2*stderrKept {
		t.buf = bytes.Clone(t.buf[len(t.buf)-stderrKept:])
	}
	return len(p), nil
}

// lastLine returns the last line written that is not blank, trimmed.
func (t *tail) lastLine() string {
	t.mu.Lock()
	defer t.mu.Unlock()
	lines := strings.Split(string(t.buf), "\n")
	for i := len(lines) - 1; i >= 0; i-- {
		if line := strings.TrimSpace(lines[i]); line != "" {
			return line
		}
	}
	return ""
}
