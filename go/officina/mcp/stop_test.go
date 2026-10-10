package mcp_test

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/sleepyshark85/officina/go/officina/mcp"
)

// stubbornServer returns the stubborn fake server, serving or silent, which writes its process ids to file and is
// killed 100 ms after its input is closed.
func stubbornServer(t *testing.T, file, mode string) mcp.Server {
	t.Helper()
	return mcp.WithExitWait(stdioServer(t, "", "stubborn", file, mode), 100*time.Millisecond)
}

func TestClose_MCP01_AServerThatIgnoresItsClosedInputIsStoppedWithEveryProcessItStarted(t *testing.T) {
	t.Parallel()
	file := filepath.Join(t.TempDir(), "fake.pid")
	source, err := mcp.Connect(t.Context(), stubbornServer(t, file, "serve"), nil)
	if err != nil {
		t.Fatalf("Connect() error = %v", err)
	}
	procs := waitForProcesses(t, file)

	source.Close()

	for _, p := range procs {
		waitExited(t, p)
	}
}

func TestClose_MCP01_AChildThatHoldsTheOutputOfAServerThatExitedIsStoppedAtOnce(t *testing.T) {
	t.Parallel()
	if runtime.GOOS == "windows" {
		t.Skip("on Windows a process whose parent has exited is not found, and is cut off only after the wait")
	}
	file := filepath.Join(t.TempDir(), "fake.pid")
	// A wait the test would notice: the child must not hold the source up for it.
	source, err := mcp.Connect(t.Context(), mcp.WithExitWait(stdioServer(t, "", "leaver", file), time.Minute), nil)
	if err != nil {
		t.Fatalf("Connect() error = %v", err)
	}
	procs := waitForProcesses(t, file)

	started := time.Now()
	source.Close()

	if took := time.Since(started); took > 10*time.Second {
		t.Errorf("Close() took %v, want it to return once the server has exited", took)
	}
	for _, p := range procs {
		waitExited(t, p)
	}
}

func TestClose_MCP01_AServerThatExitsOnceItsInputIsClosedIsLeftToFinish(t *testing.T) {
	t.Parallel()
	file := filepath.Join(t.TempDir(), "done")
	source, err := mcp.Connect(t.Context(), stdioServer(t, "", "tidy", file), nil)
	if err != nil {
		t.Fatalf("Connect() error = %v", err)
	}

	source.Close()

	if data, err := os.ReadFile(file); err != nil || string(data) != "done" {
		t.Errorf("the server's last work = %q, %v; want it done before Close returned", data, err)
	}
}

func TestConnect_MCP04_ACancelledConnectStopsAStubbornServerWithEveryProcessItStarted(t *testing.T) {
	t.Parallel()
	file := filepath.Join(t.TempDir(), "fake.pid")
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	server := stubbornServer(t, file, "silent")
	var (
		err        error
		connecting sync.WaitGroup
	)
	connecting.Go(func() {
		_, err = mcp.Connect(ctx, server, nil)
	})

	procs := waitForProcesses(t, file)
	cancel()
	connecting.Wait()

	if !errors.Is(err, context.Canceled) {
		t.Errorf("Connect() error = %v, want context.Canceled", err)
	}
	for _, p := range procs {
		waitExited(t, p)
	}
}

func TestConnect_MCP04_AStdioServerThatSendsAMessageTooLongIsStopped(t *testing.T) {
	t.Parallel()

	_, err := mcp.Connect(t.Context(), stdioServer(t, "", "flood"), nil)

	if want := "it sent a message longer than 16 MB"; err == nil || !strings.Contains(err.Error(), want) {
		t.Errorf("Connect() error = %v, want one saying %q", err, want)
	}
}

func TestConnect_MCP04_AnHTTPServerThatSendsAMessageTooLongIsRefused(t *testing.T) {
	t.Parallel()
	for _, media := range []string{"application/json", "text/event-stream"} {
		t.Run(media, func(t *testing.T) {
			t.Parallel()
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.Header().Set("Content-Type", media)
				_, _ = fmt.Fprint(w, "data: "+strings.Repeat("x", 16<<20)) // The client stops reading.
			}))
			t.Cleanup(srv.Close)

			_, err := mcp.Connect(t.Context(), mcp.Server{Name: "fake", URL: srv.URL}, nil)

			if want := "a message is longer than 16 MB"; err == nil || !strings.Contains(err.Error(), want) {
				t.Errorf("Connect() error = %v, want one saying %q", err, want)
			}
		})
	}
}
