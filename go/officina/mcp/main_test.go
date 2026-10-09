package mcp_test

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/mcp"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// fakeArg, as the first argument, makes the test binary the test kit's fake MCP server over stdio, in the mode
// of the second argument, rather than run the tests.
const fakeArg = "officina-fake-mcp"

func TestMain(m *testing.M) {
	if len(os.Args) > 2 && os.Args[1] == fakeArg {
		os.Exit(fake(os.Args[2], os.Args[3:]))
	}
	// Under the race detector, the fake servers, this binary too, would wait a second before exiting.
	if os.Getenv("GORACE") == "" {
		if err := os.Setenv("GORACE", "atexit_sleep_ms=0"); err != nil {
			fmt.Fprintln(os.Stderr, "set GORACE:", err)
			os.Exit(1)
		}
	}
	goleak.VerifyTestMain(m)
}

// fake runs the fake server in mode and returns its exit code:
//   - serve: serves the tools echo (annotated read-only), upper (no annotations), fail (an error result), token (the
//     credential in FAKE_MCP_TOKEN), pid (its process id) and crash (the process exits mid-call);
//   - complain: reads the first request, says why it cannot go on on its error output, and exits;
//   - silent <file>: writes its process id to the file and never answers, until its input ends;
//   - stubborn <file> serve|silent: starts a child that shares its output (sleeper), writes its own and the child's
//     process ids to the file, serves or stays silent, and never exits by itself, even once its input ends;
//   - leaver <file>: as stubborn serve, but exits once its input ends, leaving its child holding its output;
//   - flood: reads the first request and answers with a line longer than a message may be;
//   - tidy <file>: serves no tools until its input ends, then takes a moment to write "done" to the file and exits.
func fake(mode string, args []string) int {
	switch mode {
	case "tidy":
		_ = officinatest.NewMCPServer().Serve(context.Background(), os.Stdin, os.Stdout) // Until the input ends.
		time.Sleep(50 * time.Millisecond)
		if writeFile(args[0], "done") != nil {
			return 1
		}
	case "stubborn":
		return stubborn(args[0], args[1] == "serve", true)
	case "leaver":
		return stubborn(args[0], true, false)
	case "sleeper":
		time.Sleep(time.Hour)
	case "flood":
		_, _ = bufio.NewReader(os.Stdin).ReadString('\n')           // Any line, or none.
		_, _ = os.Stdout.Write(bytes.Repeat([]byte("x"), 16<<20+1)) // The test sees what it got.
		_, _ = io.Copy(io.Discard, os.Stdin)                        // Until the input ends.
	case "serve":
		yes := true
		server := officinatest.NewMCPServer(
			officinatest.MCPTool{Name: "echo", ReadOnly: &yes, Handler: func(in jsontext.Value) (string, error) {
				return text(in), nil
			}},
			officinatest.MCPTool{Name: "upper", Handler: func(in jsontext.Value) (string, error) {
				return strings.ToUpper(text(in)), nil
			}},
			officinatest.MCPTool{Name: "fail", Handler: func(jsontext.Value) (string, error) {
				return "", errors.New("it broke")
			}},
			officinatest.MCPTool{Name: "token", Handler: func(jsontext.Value) (string, error) {
				return "my token is " + os.Getenv("FAKE_MCP_TOKEN"), nil
			}},
			officinatest.MCPTool{Name: "pid", Handler: func(jsontext.Value) (string, error) {
				return strconv.Itoa(os.Getpid()), nil
			}},
			officinatest.MCPTool{Name: "crash", Handler: func(jsontext.Value) (string, error) {
				os.Exit(3)
				return "", nil
			}},
		)
		if err := server.Serve(context.Background(), os.Stdin, os.Stdout); err != nil {
			fmt.Fprintln(os.Stderr, err)
			return 1
		}
	case "complain":
		_, _ = bufio.NewReader(os.Stdin).ReadString('\n') // Any line, or none.
		fmt.Fprintln(os.Stderr, "configuration file missing")
	case "silent":
		if writePIDs(args[0], os.Getpid()) != nil {
			return 1
		}
		_, _ = io.Copy(io.Discard, os.Stdin) // Until the input ends.
	default:
		return 2
	}
	return 0
}

// stubborn runs the stubborn fake server, which writes "<its pid> <its child's pid>" to file; unless it stays, it
// exits once its input ends.
func stubborn(file string, serve, stay bool) int {
	exe, err := os.Executable()
	if err != nil {
		return 1
	}
	child := exec.CommandContext(context.Background(), exe, fakeArg, "sleeper")
	child.Stdout = os.Stdout
	if err := child.Start(); err != nil {
		return 1
	}
	if writePIDs(file, os.Getpid(), child.Process.Pid) != nil {
		return 1
	}
	if serve {
		_ = officinatest.NewMCPServer().Serve(context.Background(), os.Stdin, os.Stdout) // Until the input ends.
	}
	if stay {
		time.Sleep(time.Hour)
	}
	return 0
}

// writePIDs writes the process ids to file aside and then moves it, so the test never reads it half written.
func writePIDs(file string, pids ...int) error {
	text := make([]string, len(pids))
	for i, pid := range pids {
		text[i] = strconv.Itoa(pid)
	}
	return writeFile(file, strings.Join(text, " "))
}

// writeFile writes text to file aside and then moves it, so the test never reads it half written.
func writeFile(file, text string) error {
	if err := os.WriteFile(file+".tmp", []byte(text), 0o600); err != nil {
		return err
	}
	return os.Rename(file+".tmp", file)
}

// text returns the "text" property of a call's arguments.
func text(arguments jsontext.Value) string {
	var in struct {
		Text string `json:"text"`
	}
	_ = json.Unmarshal(arguments, &in) // A call without text echoes "".
	return in.Text
}

// stdioServer returns the fake server over stdio in mode, with token as its credential if it is not empty.
func stdioServer(t *testing.T, token string, mode ...string) mcp.Server {
	t.Helper()
	exe, err := os.Executable()
	if err != nil {
		t.Fatalf("Executable() error = %v", err)
	}
	if mode == nil {
		mode = []string{"serve"}
	}
	server := mcp.Server{Name: "fake", Command: exe, Args: append([]string{fakeArg}, mode...)}
	if token != "" {
		server.Env = []string{"FAKE_MCP_TOKEN=" + token}
	}
	return server
}

const token = "fake-token-73"

// httpFake returns the fake server over HTTP, requiring the token, with echo (annotated read-only), upper and
// extra; the server stops when the test ends.
func httpFake(t *testing.T, extra ...officinatest.MCPTool) (*officinatest.MCPServer, string) {
	t.Helper()
	yes := true
	fake := officinatest.NewMCPServer(append([]officinatest.MCPTool{
		{Name: "echo", ReadOnly: &yes, Handler: func(in jsontext.Value) (string, error) { return text(in), nil }},
		{Name: "upper", Handler: func(in jsontext.Value) (string, error) { return strings.ToUpper(text(in)), nil }},
	}, extra...)...)
	fake.Token = token
	return httpFakeOf(t, fake)
}

// httpFakeOf serves fake over HTTP until the test ends, and returns it with its endpoint.
func httpFakeOf(t *testing.T, fake *officinatest.MCPServer) (*officinatest.MCPServer, string) {
	t.Helper()
	srv := httptest.NewServer(fake)
	t.Cleanup(srv.Close)
	return fake, srv.URL + "/mcp"
}

// httpServer returns the server at url, sending tok as its bearer token.
func httpServer(url, tok string) mcp.Server {
	return mcp.Server{Name: "fake", URL: url, Header: http.Header{"Authorization": {"Bearer " + tok}}}
}

// connect connects to server with the allowed tools, and closes the source when the test ends.
func connect(t *testing.T, server mcp.Server, allowed ...mcp.AllowedTool) *mcp.Source {
	t.Helper()
	source, err := mcp.Connect(t.Context(), server, allowed)
	if err != nil {
		t.Fatalf("Connect() error = %v", err)
	}
	t.Cleanup(source.Close)
	return source
}

// allow returns the allowed tools of names, with the defaults.
func allow(names ...string) []mcp.AllowedTool {
	allowed := make([]mcp.AllowedTool, len(names))
	for i, name := range names {
		allowed[i] = mcp.AllowedTool{Name: name}
	}
	return allowed
}

// newAgent returns an agent of model with the source's tools and the server's secrets, and opts' other parts.
func newAgent(t *testing.T, model officina.Model, source *mcp.Source, server mcp.Server,
	opts officina.AgentOptions,
) *officina.Agent {
	t.Helper()
	opts.Tools, opts.Secrets, opts.Name = source.Tools(), append(opts.Secrets, server.Secrets()...), "mcp-test"
	agent, err := officina.NewAgent(model, "You use tools.", opts)
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return agent
}

// call returns a block that calls tool with input.
func call(id, tool, input string) officina.Block {
	return officinatest.ToolUseBlock(id, tool, input)
}

// results returns the tool results the model received in request i, from the end when i is negative.
func results(t *testing.T, model *officinatest.Model, i int) []officina.ToolResult {
	t.Helper()
	requests := model.Requests()
	if i < 0 {
		i += len(requests)
	}
	if i < 0 || i >= len(requests) {
		t.Fatalf("the model has %d requests, not one at %d", len(requests), i)
	}
	messages := requests[i].Messages
	var got []officina.ToolResult
	for _, b := range messages[len(messages)-1].Blocks {
		if b.ToolResult != nil {
			got = append(got, *b.ToolResult)
		}
	}
	return got
}

// run runs agent on c and fails the test on an error.
func run(t *testing.T, agent *officina.Agent, c *officina.Conversation, message string) officina.Result {
	t.Helper()
	res, err := agent.Run(t.Context(), c, message, officina.RunOptions{})
	if err != nil {
		t.Fatalf("Run(%q) error = %v", message, err)
	}
	return res
}

// sink records audit entries.
type sink struct {
	mu      sync.Mutex
	entries []officina.AuditEntry
}

func (s *sink) Write(_ context.Context, e officina.AuditEntry) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.entries = append(s.entries, e)
	return nil
}

func (s *sink) all() []officina.AuditEntry {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]officina.AuditEntry(nil), s.entries...)
}

// sourceChanges returns the tool source entries of the trail, as "<source> <outcome>".
func (s *sink) sourceChanges() []string {
	var changes []string
	for _, e := range s.all() {
		if e.Kind == officina.AuditToolSource {
			changes = append(changes, e.Tool+" "+e.Outcome)
		}
	}
	return changes
}
