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
	// exitWait is how long a closing server may take to exit, once its input is closed, before it is killed.
	exitWait = 5 * time.Second
	// writeFailedWait is how long a failed write waits for the server's exit, whose reason says more.
	writeFailedWait = time.Second
	// stderrKept is how much of the end of a server's error output is kept, to say why it ended.
	stderrKept = 4096
)

// stdio is the stdio transport: the server is a child process, and each message is one line of its input or
// output. Responses are matched to requests by id, so calls may overlap. When the process ends, every waiting
// request fails, with the last line of its error output, which often says why.
type stdio struct {
	stdin io.WriteCloser
	// stop ends the process's context: its input is closed, and it is killed if it has not exited within exitWait.
	stop context.CancelFunc
	// exited is closed once the process has exited and its output has been read; ended says how, by then.
	exited chan struct{}
	ended  string
	// wg waits for the goroutine that waits for the process.
	wg sync.WaitGroup

	// writing lets one message at a time be written.
	writing sync.Mutex
	mu      sync.Mutex
	waiting map[int64]chan response
	stderr  tail
}

// startStdio starts the server's program.
func startStdio(server *Server) (*stdio, error) {
	life, stop := context.WithCancel(context.Background())
	cmd := exec.CommandContext(life, server.Command, server.Args...)
	cmd.Env = append(os.Environ(), server.Env...)
	s := &stdio{stop: stop, exited: make(chan struct{}), waiting: map[int64]chan response{}}
	cmd.Stdout = &lines{line: s.dispatch}
	cmd.Stderr = &s.stderr
	stdin, err := cmd.StdinPipe()
	if err == nil {
		s.stdin = stdin
		// Closing its input lets the server exit by itself, as a container must to be removed; WaitDelay later,
		// it is killed.
		cmd.Cancel = stdin.Close
		cmd.WaitDelay = exitWait
		err = cmd.Start()
	}
	if err != nil {
		stop()
		return nil, fmt.Errorf("mcp server %q could not be started (%s): %w", server.Name, server.Command, err)
	}
	s.wg.Go(func() {
		// How it exited matters less than what it said: the error output names the cause.
		_ = cmd.Wait()
		s.ended = "it closed its connection"
		if said := s.stderr.lastLine(); said != "" {
			s.ended += ": " + said
		}
		close(s.exited)
	})
	return s, nil
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

func (s *stdio) close() {
	s.stop()
	s.wg.Wait()
}

// lines splits what is written to it into lines, and hands each, without its line ending, to line.
type lines struct {
	buf  []byte
	line func([]byte)
}

func (l *lines) Write(p []byte) (int, error) {
	l.buf = append(l.buf, p...)
	for {
		end := bytes.IndexByte(l.buf, '\n')
		if end < 0 {
			break
		}
		l.line(bytes.Clone(bytes.TrimSuffix(l.buf[:end], []byte("\r"))))
		l.buf = l.buf[end+1:]
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
