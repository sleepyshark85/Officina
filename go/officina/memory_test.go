package officina_test

import (
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"
	"go.opentelemetry.io/otel/attribute"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// The memory tool and stores through real runs: only the model and the approver are scripted.

// store is a built-in memory store, made fresh for each test.
type store struct {
	name string
	new  func(t *testing.T) officina.MemoryStore
}

// stores returns the built-in stores.
func stores() []store {
	return []store{
		{"in memory", func(*testing.T) officina.MemoryStore { return &officina.MapMemoryStore{} }},
		{"files", func(t *testing.T) officina.MemoryStore {
			t.Helper()
			return officina.NewFileMemoryStore(t.TempDir())
		}},
	}
}

// memoryCalls returns a reply calling the memory tool once per input, with ids m0, m1….
func memoryCalls(inputs ...string) officinatest.Reply {
	blocks := make([]officina.Block, len(inputs))
	for i, input := range inputs {
		blocks[i] = officinatest.ToolUseBlock("m"+strconv.Itoa(i), "memory", input)
	}
	return officinatest.ToolUseReply(blocks...)
}

// runMemory runs one reply's memory calls in scope and returns their results.
func runMemory(t *testing.T, s officina.MemoryStore, scope string, inputs ...string) []officina.ToolResult {
	t.Helper()
	model := officinatest.NewModel("scripted", memoryCalls(inputs...), officinatest.TextReply("Done."))
	if res := run(t, newAgent(t, model, officina.NewMemoryTool(s)), nil, "Go.", officina.RunOptions{MemoryScope: scope}); res.Status != officina.Completed {
		t.Fatalf("run = %v, want it completed", res)
	}
	requests := model.Requests()
	last := requests[len(requests)-1].Messages
	return results(last[len(last)-1])
}

// contents returns the results' contents.
func contents(results []officina.ToolResult) []string {
	var got []string
	for _, r := range results {
		got = append(got, r.Content)
	}
	return got
}

// paths returns the paths of a scope's files, sorted.
func paths(t *testing.T, s officina.MemoryStore, scope string) []string {
	t.Helper()
	files, err := s.List(t.Context(), scope)
	if err != nil {
		t.Fatalf("List() error = %v", err)
	}
	var got []string
	for _, f := range files {
		got = append(got, f.Path)
	}
	slices.Sort(got)
	return got
}

// write writes a file to s, failing the test on an error.
func write(t *testing.T, s officina.MemoryStore, scope, path, text string) {
	t.Helper()
	if err := s.Write(t.Context(), scope, path, text); err != nil {
		t.Fatalf("Write(%q, %q) error = %v", scope, path, err)
	}
}

func TestMemoryTool_MEM01_MEM02_TheModelViewsCreatesEditsRenamesAndDeletesFilesInEitherStore(t *testing.T) {
	t.Parallel()
	for _, st := range stores() {
		t.Run(st.name, func(t *testing.T) {
			t.Parallel()
			s := st.new(t)

			got := runMemory(t, s, "sam",
				`{"command":"view","path":"/memories"}`,
				`{"command":"create","path":"/memories/prefs.md","file_text":"Prices: without tax.\nTone: brief.\n"}`,
				`{"command":"str_replace","path":"/memories/prefs.md","old_str":"without","new_str":"with"}`,
				`{"command":"insert","path":"/memories/prefs.md","insert_line":0,"insert_text":"# Sam\n"}`,
				`{"command":"view","path":"/memories/prefs.md"}`,
				`{"command":"view","path":"/memories/prefs.md","view_range":[2,-1]}`,
				`{"command":"create","path":"/memories/customers/ana/notes.md","file_text":"Likes crime."}`,
				`{"command":"rename","old_path":"/memories/customers","new_path":"/memories/people"}`,
				`{"command":"view","path":"/memories/"}`,
				`{"command":"delete","path":"/memories/people"}`,
				`{"command":"view","path":"/memories/people/ana/notes.md"}`)

			want := []string{
				"Here're the files and directories up to 2 levels deep in /memories, excluding hidden items:\n0B\t/memories",
				"File created successfully at: /memories/prefs.md",
				"The memory file has been edited. A snippet of /memories/prefs.md with line numbers:\n" +
					"     1\tPrices: with tax.\n     2\tTone: brief.\n     3\t",
				"The file /memories/prefs.md has been edited.",
				"Here's the content of /memories/prefs.md with line numbers:\n     1\t# Sam\n     2\tPrices: with tax.\n" +
					"     3\tTone: brief.",
				"Here's the content of /memories/prefs.md with line numbers:\n     2\tPrices: with tax.\n     3\tTone: brief.",
				"File created successfully at: /memories/customers/ana/notes.md",
				"Successfully renamed /memories/customers to /memories/people",
				"Here're the files and directories up to 2 levels deep in /memories, excluding hidden items:\n" +
					"49B\t/memories\n12B\t/memories/people\n12B\t/memories/people/ana\n37B\t/memories/prefs.md",
				"Successfully deleted /memories/people",
				"The path /memories/people/ana/notes.md does not exist. Please provide a valid path.",
			}
			if diff := cmp.Diff(want, contents(got)); diff != "" {
				t.Errorf("results mismatch (-want +got):\n%s", diff)
			}
			for i, r := range got {
				if r.IsError != (i == len(got)-1) {
					t.Errorf("result %d IsError = %t, want only the last to be an error", i, r.IsError)
				}
			}
			files, err := s.List(t.Context(), "sam")
			if diff := cmp.Diff([]officina.MemoryFile{{Path: "prefs.md", Size: 37}}, files); err != nil || diff != "" {
				t.Errorf("List() error = %v, files mismatch (-want +got):\n%s", err, diff)
			}
		})
	}
}

func TestMemoryTool_MEM01_MistakesAreErrorResultsThatChangeNothing(t *testing.T) {
	t.Parallel()
	for _, st := range stores() {
		t.Run(st.name, func(t *testing.T) {
			t.Parallel()
			s := st.new(t)
			write(t, s, "sam", "a.md", "x\nx\n")
			write(t, s, "sam", "b.md", "b")

			got := runMemory(t, s, "sam",
				`{"command":"str_replace","path":"/memories/a.md","old_str":"x","new_str":"y"}`,
				`{"command":"str_replace","path":"/memories/a.md","old_str":"z","new_str":"y"}`,
				`{"command":"str_replace","path":"/memories/a.md"}`,
				`{"command":"str_replace","path":"/memories/c.md","old_str":"x"}`,
				`{"command":"insert","path":"/memories/a.md","insert_line":9,"insert_text":"y"}`,
				`{"command":"insert","path":"/memories/a.md","insert_line":-1,"insert_text":"y"}`,
				`{"command":"insert","path":"/memories/a.md","insert_text":"y"}`,
				`{"command":"insert","path":"/memories/c.md","insert_line":0,"insert_text":"y"}`,
				`{"command":"create","path":"/memories/a.md/c.md","file_text":"y"}`,
				`{"command":"create","path":"/memories/a.md"}`,
				`{"command":"create","path":"/memories","file_text":"y"}`,
				`{"command":"rename","old_path":"/memories/a.md","new_path":"/memories/b.md"}`,
				`{"command":"rename","old_path":"/memories/c.md","new_path":"/memories/d.md"}`,
				`{"command":"rename","old_path":"/memories","new_path":"/memories/d"}`,
				`{"command":"rename","old_path":"/memories/b.md","new_path":"/memories/a.md/b.md"}`,
				`{"command":"rename","old_path":"/memories/a.md","new_path":"/memories/a.md/c.md"}`,
				`{"command":"rename","old_path":"/memories/b.md"}`,
				`{"command":"delete","path":"/memories"}`,
				`{"command":"delete","path":"/memories/c.md"}`,
				`{"command":"view","path":"/memories/a.md","view_range":[3,1]}`,
				`{"command":"view","path":"/memories/a.md","view_range":[1]}`,
				`{"command":"view","path":"/memories/a.md","view_range":[0,1]}`,
				`{"command":"view","path":"/memories/a.md","view_range":[1,3]}`,
				`{"command":"view"}`,
				`{"command":"undo","path":"/memories/a.md"}`)

			want := []string{
				"No replacement was performed. Multiple occurrences of old_str `x` in lines: 1, 2. Please ensure it is unique",
				"No replacement was performed, old_str `z` did not appear verbatim in /memories/a.md.",
				"Error: Parameter `old_str` is required for command: str_replace",
				"Error: The path /memories/c.md does not exist. Please provide a valid path.",
				"Error: Invalid `insert_line` parameter: 9. It should be within the range of lines of the file: [0, 3]",
				"Error: Invalid `insert_line` parameter: -1. It should be within the range of lines of the file: [0, 3]",
				"Error: Parameters `insert_line` and `insert_text` are required for command: insert",
				"Error: The path /memories/c.md does not exist",
				"Error: Cannot create /memories/a.md/c.md: /memories/a.md is a file.",
				"Error: Parameter `file_text` is required for command: create",
				"Error: Cannot create /memories: it is a directory.",
				"Error: The destination /memories/b.md already exists",
				"Error: The path /memories/c.md does not exist",
				"Error: The memory directory /memories itself cannot be renamed.",
				"Error: Cannot move to /memories/a.md/b.md: /memories/a.md is a file.",
				"Error: Cannot move /memories/a.md into itself.",
				"Error: The path (none) is not a valid path under /memories.",
				"Error: The memory directory /memories itself cannot be deleted.",
				"Error: The path /memories/c.md does not exist",
			}
			invalidRange := "Error: Invalid `view_range`: it should be [start, end] with 1 <= start <= end <= 2, or end -1 " +
				"for the end of the file."
			want = append(want, invalidRange, invalidRange, invalidRange, invalidRange,
				"Error: The path (none) is not a valid path under /memories.",
				"The input does not match the tool's schema:\n"+
					`/command: must be one of ["view","create","str_replace","insert","delete","rename"]`)
			if diff := cmp.Diff(want, contents(got)); diff != "" {
				t.Errorf("results mismatch (-want +got):\n%s", diff)
			}
			for i, r := range got {
				if !r.IsError {
					t.Errorf("result %d is not an error: %s", i, r.Content)
				}
			}
			if text, err := s.Read(t.Context(), "sam", "a.md"); err != nil || text != "x\nx\n" {
				t.Errorf("Read() = %q, %v; want a.md unchanged", text, err)
			}
			if diff := cmp.Diff([]string{"a.md", "b.md"}, paths(t, s, "sam")); diff != "" {
				t.Errorf("files mismatch (-want +got):\n%s", diff)
			}
		})
	}
}

func TestMemoryTool_MEM03_APathOutsideTheScopeIsRefusedWithAnErrorResult(t *testing.T) {
	t.Parallel()
	for _, path := range []string{
		"/etc/passwd", "/memories/../secrets.env", "/memories/notes/../../x", "/memories/%2e%2e/x", `/memories/..\x`,
		"/memories//x", "/memoriesx/a", "memories/a", "/memories/C:/a", "/memories/./a", "/memories/ ",
		"/memories/NUL", "/memories/a\x00b",
	} {
		t.Run(path, func(t *testing.T) {
			t.Parallel()
			s := &officina.MapMemoryStore{}
			input, err := json.Marshal(path)
			if err != nil {
				t.Fatal(err)
			}

			got := runMemory(t, s, "sam", `{"command":"create","path":`+string(input)+`,"file_text":"x"}`,
				`{"command":"rename","old_path":"/memories/a","new_path":`+string(input)+`}`)

			want := "Error: The path " + path + " is not a valid path under /memories."
			for _, r := range got {
				if r.Content != want || !r.IsError {
					t.Errorf("result = %q (error %t), want the error %q", r.Content, r.IsError, want)
				}
			}
			if len(paths(t, s, "sam")) > 0 {
				t.Errorf("files = %q, want none", paths(t, s, "sam"))
			}
		})
	}
}

func TestMemoryStore_MEM02_MEM03_ScopesThatDifferOnlyInCaseStayApart(t *testing.T) {
	t.Parallel()
	for _, st := range stores() {
		t.Run(st.name, func(t *testing.T) {
			t.Parallel()
			s := st.new(t)

			write(t, s, "Sam", "a.md", "upper")
			write(t, s, "sam", "a.md", "lower")

			for scope, want := range map[string]string{"Sam": "upper", "sam": "lower"} {
				if text, err := s.Read(t.Context(), scope, "a.md"); err != nil || text != want {
					t.Errorf("Read(%q) = %q, %v; want %q", scope, text, err, want)
				}
			}
			if got := paths(t, s, "Sam"); len(got) != 1 {
				t.Errorf("Sam's files = %q, want one", got)
			}
		})
	}
}

func TestMemoryStore_MEM02_AMissingFileIsNotExistAndDeletingItIsNoError(t *testing.T) {
	t.Parallel()
	for _, st := range stores() {
		t.Run(st.name, func(t *testing.T) {
			t.Parallel()
			s := st.new(t)

			if _, err := s.Read(t.Context(), "sam", "a.md"); !errors.Is(err, fs.ErrNotExist) {
				t.Errorf("Read() of a scope never written to error = %v, want fs.ErrNotExist", err)
			}
			write(t, s, "sam", "a/b/c.md", "c")
			write(t, s, "sam", "a/d.md", "d")
			if _, err := s.Read(t.Context(), "sam", "a/x.md"); !errors.Is(err, fs.ErrNotExist) {
				t.Errorf("Read() of a missing file error = %v, want fs.ErrNotExist", err)
			}
			for _, p := range []string{"a/b/c.md", "a/b/c.md", "x/y.md"} {
				if err := s.Delete(t.Context(), "sam", p); err != nil {
					t.Errorf("Delete(%q) error = %v", p, err)
				}
			}
			if diff := cmp.Diff([]string{"a/d.md"}, paths(t, s, "sam")); diff != "" {
				t.Errorf("files mismatch (-want +got):\n%s", diff)
			}
			if err := s.Delete(t.Context(), "other", "a.md"); err != nil {
				t.Errorf("Delete() in a scope never written to error = %v", err)
			}
			if got := paths(t, s, "other"); got != nil {
				t.Errorf("files of a scope never written to = %q, want none", got)
			}
		})
	}
}

func TestFileMemoryStore_MEM03_EmptyDirectoriesGoAndAScopeWhoseDirectoryIsALinkIsRefused(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	s := officina.NewFileMemoryStore(dir)
	write(t, s, "sam", "a/b/c.md", "c")
	if err := s.Delete(t.Context(), "sam", "a/b/c.md"); err != nil {
		t.Fatalf("Delete() error = %v", err)
	}
	if entries, err := os.ReadDir(filepath.Join(dir, "73616d")); err != nil || len(entries) != 0 {
		t.Errorf("the scope's directory holds %v (%v), want nothing", entries, err)
	}
	outside := t.TempDir()
	if err := os.WriteFile(filepath.Join(outside, "a.md"), []byte("outside"), 0o600); err != nil {
		t.Fatal(err)
	}
	// The directory of scope "ana".
	if err := os.Symlink(outside, filepath.Join(dir, "616e61")); err != nil {
		t.Skipf("links cannot be made here: %v", err)
	}

	if text, err := s.Read(t.Context(), "ana", "a.md"); err == nil {
		t.Errorf("Read() through a linked scope = %q, want an error", text)
	}
	if err := s.Write(t.Context(), "ana", "b.md", "x"); err == nil {
		t.Error("Write() through a linked scope succeeded, want an error")
	}
	if _, err := s.List(t.Context(), "ana"); err == nil {
		t.Error("List() of a linked scope succeeded, want an error")
	}
	if _, err := os.Stat(filepath.Join(outside, "b.md")); !errors.Is(err, fs.ErrNotExist) {
		t.Errorf("a file was written outside the store: %v", err)
	}
}

func TestMemoryTool_MEM01_ALongFilesViewIsCutAt16000CharactersAndAFileHoldsAtMostItsLimit(t *testing.T) {
	t.Parallel()
	s := &officina.MapMemoryStore{}
	write(t, s, "sam", "long.md", strings.TrimSuffix(strings.Repeat(strings.Repeat("x", 99)+"\n", 400), "\n"))
	write(t, s, "sam", "one-line.md", strings.Repeat("é", 20_000))

	got := runMemory(t, s, "sam",
		`{"command":"view","path":"/memories/long.md"}`,
		`{"command":"view","path":"/memories/long.md","view_range":[2,-1]}`,
		`{"command":"create","path":"/memories/big.md","file_text":"`+strings.Repeat("é", 50_001)+`"}`,
		`{"command":"create","path":"/memories/fits.md","file_text":"`+strings.Repeat("é", 50_000)+`"}`,
		`{"command":"view","path":"/memories/one-line.md"}`)

	const truncated = "\n[Truncated at 16000 characters: view the rest with view_range.]"
	if n := len([]rune(got[0].Content)); !strings.HasSuffix(got[0].Content, truncated) || n < 15_000 || n > 16_200 {
		t.Errorf("long view of %d characters ends %q, want it cut at a line break near 16000", n,
			got[0].Content[len(got[0].Content)-80:])
	}
	if got[1].IsError || !got[2].IsError || got[3].IsError {
		t.Errorf("results' errors = %t, %t, %t; want only the oversized create refused", got[1].IsError, got[2].IsError,
			got[3].IsError)
	}
	if want := "Error: /memories/big.md would hold 50001 characters; a memory file holds at most 50000. Keep it shorter, " +
		"or split it."; got[2].Content != want {
		t.Errorf("oversized create = %q, want %q", got[2].Content, want)
	}
	// A view with no line break to end at is cut at the limit, never inside a character.
	if want := "\t" + strings.Repeat("é", 16_000-len("     1\t")) + truncated; !strings.HasSuffix(got[4].Content, want) {
		t.Errorf("one-line view ends %q, want it cut at 16000 characters", got[4].Content[len(got[4].Content)-80:])
	}
	if _, err := s.Read(t.Context(), "sam", "big.md"); !errors.Is(err, fs.ErrNotExist) {
		t.Errorf("Read(big.md) error = %v, want no such file", err)
	}
}

func TestMemoryTool_MEM01_AListingShowsTwoLevelsWithoutHiddenItemsAndSizesInKAndM(t *testing.T) {
	t.Parallel()
	s := &officina.MapMemoryStore{}
	write(t, s, "sam", "a/b/c/d.md", strings.Repeat("x", 2048))
	write(t, s, "sam", ".hidden/e.md", "e")
	write(t, s, "sam", "a/.f.md", "f")
	write(t, s, "sam", "big.md", strings.Repeat("y", 3*1024*1024/2))

	got := runMemory(t, s, "sam", `{"command":"view","path":"/memories"}`, `{"command":"view","path":"/memories/a"}`)

	want := []string{
		"Here're the files and directories up to 2 levels deep in /memories, excluding hidden items:\n" +
			"1.5M\t/memories\n2.0K\t/memories/a\n2.0K\t/memories/a/b\n1.5M\t/memories/big.md",
		"Here're the files and directories up to 2 levels deep in /memories/a, excluding hidden items:\n" +
			"2.0K\t/memories/a\n2.0K\t/memories/a/b\n2.0K\t/memories/a/b/c",
	}
	if diff := cmp.Diff(want, contents(got)); diff != "" {
		t.Errorf("listings mismatch (-want +got):\n%s", diff)
	}
}

func TestValidMemoryScope_MEM03_RefusesWhatCouldLeaveTheScopeOrNameAnotherFileOnSomeSystem(t *testing.T) {
	t.Parallel()
	for _, scope := range []string{
		"", ".", "..", "a/b", `a\b`, "a:b", "a*", "a?", `a"`, "a<", "a>", "a|", "%2e", "a.", "a ", "a\x00", "a\x1f",
		"a\u0085", "CON", "con.txt", "com¹.md", "LPT³", "CONIN$", "conout$.txt", "nul.tar.gz", "Aux ", "COM0",
		strings.Repeat("a", 256), "\xff",
	} {
		if officina.ValidMemoryScope(scope) {
			t.Errorf("ValidMemoryScope(%q) = true, want false", scope)
		}
	}
	for _, scope := range []string{"sam", "Sam", "a.md", ".hidden", "...a", "a b", "conor", "COM10", "LPT", "comx",
		strings.Repeat("é", 255), "ünïcödé"} {
		if !officina.ValidMemoryScope(scope) {
			t.Errorf("ValidMemoryScope(%q) = false, want true", scope)
		}
	}
}

func TestRun_MEM03_ARunSeesOnlyItsScopesFiles(t *testing.T) {
	t.Parallel()
	s := &officina.MapMemoryStore{}
	runMemory(t, s, "ana", `{"command":"create","path":"/memories/a.md","file_text":"Ana's"}`)

	got := runMemory(t, s, "ben", `{"command":"view","path":"/memories"}`, `{"command":"view","path":"/memories/a.md"}`)

	if !strings.HasSuffix(got[0].Content, "\n0B\t/memories") || !got[1].IsError {
		t.Errorf("Ben's results = %q, want an empty memory and no a.md", contents(got))
	}
	if text, err := s.Read(t.Context(), "ana", "a.md"); err != nil || text != "Ana's" {
		t.Errorf("Read() = %q, %v; want Ana's file", text, err)
	}
}

func TestRun_MEM03_ARunOfAnAgentWithMemoryNeedsAValidScope(t *testing.T) {
	t.Parallel()
	withMemory := newAgent(t, officinatest.NewModel("scripted"), officina.NewMemoryTool(&officina.MapMemoryStore{}))
	without := newAgent(t, officinatest.NewModel("scripted"))
	tests := []struct {
		name  string
		agent *officina.Agent
		scope string
		want  string
	}{
		{"no scope", withMemory, "", "run: the agent has memory, so the run needs a memory scope"},
		{"climbing", withMemory, "../ana", `run: "../ana" is not a valid memory scope`},
		{"invalid without memory", without, "a/b", `run: "a/b" is not a valid memory scope`},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			_, err := tt.agent.Run(t.Context(), nil, "Hi", officina.RunOptions{MemoryScope: tt.scope})
			if err == nil || err.Error() != tt.want {
				t.Errorf("Run() error = %v, want %q", err, tt.want)
			}
		})
	}
}

// auditedStore checks, at each write, that the call's attempt is already in the audit trail.
type auditedStore struct {
	officina.MapMemoryStore
	sink   *memorySink
	writes int
}

func (s *auditedStore) Write(ctx context.Context, scope, path, text string) error {
	if !slices.ContainsFunc(s.sink.Entries(), func(e officina.AuditEntry) bool {
		return e.Kind == officina.AuditToolStarted && strings.Contains(e.Input, path)
	}) {
		return fmt.Errorf("the write of %s ran before its attempt was audited", path)
	}
	s.writes++
	return s.MapMemoryStore.Write(ctx, scope, path, text)
}

func (*auditedStore) Delete(context.Context, string, string) error {
	return errors.New("not approved")
}

func TestRun_MEM04_MemoryWritesAreAuditedBeforeTheyRunAndAskApprovalWhileViewsDoNot(t *testing.T) {
	t.Parallel()
	sink := &memorySink{}
	s := &auditedStore{sink: sink}
	approver := officinatest.NewApprover(officina.Approval{Approved: true}, officina.Approval{Reason: "not now"})
	model := officinatest.NewModel("scripted", memoryCalls(`{"command":"view","path":"/memories"}`,
		`{"command":"create","path":"/memories/a.md","file_text":"x"}`, `{"command":"delete","path":"/memories/a.md"}`),
		officinatest.TextReply("Done."))
	memory := officina.NewMemoryTool(s)
	memory.NeedsApproval = true
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools: []officina.Tool{memory}, AuditSink: sink, Approver: approver,
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	run(t, agent, nil, "Go.", officina.RunOptions{MemoryScope: "sam"})

	var asked []string
	for _, call := range approver.Asked() {
		asked = append(asked, call.ID)
	}
	if diff := cmp.Diff([]string{"m1", "m2"}, asked); diff != "" {
		t.Errorf("calls asked about mismatch (-want +got):\n%s", diff)
	}
	if text, err := s.Read(t.Context(), "sam", "a.md"); s.writes != 1 || err != nil || text != "x" {
		t.Errorf("%d writes, Read() = %q, %v; want the one approved write", s.writes, text, err)
	}
	var ended []string
	for _, e := range sink.Entries() {
		if e.Kind == officina.AuditToolEnded {
			ended = append(ended, e.Outcome+" "+e.Detail)
		}
	}
	if len(ended) != 3 || !strings.HasPrefix(ended[0], "ok ") || !strings.HasPrefix(ended[1], "ok ") ||
		ended[2] != "error The call was denied: not now" {
		t.Errorf("tool outcomes = %q, want ok, ok, then the denial", ended)
	}
}

func TestRun_AUD03_EVT02_EveryAuditEntryAndTheRunSpanNameTheMemoryScopeAndTheMemoryToolItsSource(t *testing.T) {
	t.Parallel()
	sink := &memorySink{}
	telemetry := newCollector(t)
	model := officinatest.NewModel("scripted", memoryCalls(`{"command":"create","path":"/memories/a.md","file_text":"x"}`),
		officinatest.TextReply("Done."))
	agent := telemetry.agent(t, model, officina.AgentOptions{
		Tools: []officina.Tool{officina.NewMemoryTool(&officina.MapMemoryStore{})}, AuditSink: sink,
	})

	run(t, agent, nil, "Go.", officina.RunOptions{MemoryScope: "sam"})

	var kinds []officina.AuditKind
	for _, e := range sink.Entries() {
		kinds = append(kinds, e.Kind)
		if e.MemoryScope != "sam" {
			t.Errorf("%s entry's memory scope = %q, want sam", e.Kind, e.MemoryScope)
		}
	}
	want := []officina.AuditKind{officina.AuditRunStarted, officina.AuditToolStarted, officina.AuditToolEnded,
		officina.AuditRunEnded}
	if diff := cmp.Diff(want, kinds); diff != "" {
		t.Errorf("entries mismatch (-want +got):\n%s", diff)
	}
	attrs := map[attribute.Key]attribute.Value{}
	for _, span := range []string{"invoke_agent", "execute_tool memory"} {
		for _, kv := range telemetry.span(t, span).Attributes {
			attrs[kv.Key] = kv.Value
		}
	}
	if scope, source := attrs["officina.memory.scope"].AsString(), attrs["officina.tool.source"].AsString(); scope != "sam" ||
		source != "memory" {
		t.Errorf("memory scope %q, tool source %q; want sam and memory", scope, source)
	}
}

func TestRun_MEM05_MemoryNeverReachesTheInstructionsAndThePrefixStaysStableAsItChanges(t *testing.T) {
	t.Parallel()
	s := &officina.MapMemoryStore{}
	write(t, s, "sam", "prefs.md", "Prices with tax.")
	model := officinatest.NewModel("scripted",
		memoryCalls(`{"command":"view","path":"/memories/prefs.md"}`),
		memoryCalls(`{"command":"create","path":"/memories/prefs.md","file_text":"Prices without tax."}`),
		officinatest.TextReply("Noted."), officinatest.TextReply("Hello again."))
	agent := newAgent(t, model, officina.NewMemoryTool(s))
	var c officina.Conversation

	run(t, agent, &c, "What do I prefer?", officina.RunOptions{MemoryScope: "sam"})
	run(t, agent, &c, "Hi.", officina.RunOptions{MemoryScope: "sam"})

	requests := model.Requests()
	for i, r := range requests {
		if r.Instructions != instructions {
			t.Errorf("request %d's instructions = %q, want them unchanged", i+1, r.Instructions)
		}
	}
	if err := officinatest.CheckPrefix(requests); err != nil {
		t.Errorf("CheckPrefix() = %v", err)
	}
	if viewed := results(requests[1].Messages[len(requests[1].Messages)-1]); !strings.Contains(viewed[0].Content, "Prices with tax.") {
		t.Errorf("view result = %q, want the file's text", viewed[0].Content)
	}
	if !agent.CanContinue(&c) {
		t.Error("CanContinue() = false after memory changed, want true")
	}
}

func TestAgent_CTX04_AnAgentWithMemoryIsFingerprintedAsDotNetsIs(t *testing.T) {
	t.Parallel()
	// What the .NET implementation computes for RequestPrefix("scripted", [MemoryTool.Create(…)], "Answer
	// briefly."): its memory tool's description and schema, as written there, are part of the prefix.
	const dotnet = "4d845546ce9d9d98fbaa9895456764834a5f2c3eb4c60051547da1162319120f"
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	agent, err := officina.NewAgent(model, "Answer briefly.", officina.AgentOptions{
		Tools: []officina.Tool{officina.NewMemoryTool(&officina.MapMemoryStore{})},
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	var c officina.Conversation

	run(t, agent, &c, "Hi", officina.RunOptions{MemoryScope: "sam"})

	if got := fingerprint(t, &c); got != dotnet {
		t.Errorf("fingerprint = %s, want .NET's %s", got, dotnet)
	}
}

// brokenStore is a store whose disk fails at the operation fail names.
type brokenStore struct {
	officina.MapMemoryStore
	fail string
}

var errDiskGone = errors.New("the disk is gone")

func (s *brokenStore) List(ctx context.Context, scope string) ([]officina.MemoryFile, error) {
	if s.fail == "list" {
		return nil, errDiskGone
	}
	return s.MapMemoryStore.List(ctx, scope)
}

func (s *brokenStore) Read(ctx context.Context, scope, path string) (string, error) {
	if s.fail == "read" {
		return "", errDiskGone
	}
	return s.MapMemoryStore.Read(ctx, scope, path)
}

func (s *brokenStore) Write(ctx context.Context, scope, path, text string) error {
	if s.fail == "write" {
		return errDiskGone
	}
	return s.MapMemoryStore.Write(ctx, scope, path, text)
}

func (s *brokenStore) Delete(ctx context.Context, scope, path string) error {
	if s.fail == "delete" {
		return errDiskGone
	}
	return s.MapMemoryStore.Delete(ctx, scope, path)
}

func TestMemoryTool_MEM01_AStoreThatFailsGivesAnErrorResultThatSaysWhere(t *testing.T) {
	t.Parallel()
	tests := []struct {
		fail, input, want string
	}{
		{"list", `{"command":"view","path":"/memories"}`, "list the memory: the disk is gone"},
		{"read", `{"command":"view","path":"/memories/a.md"}`, "read the memory: the disk is gone"},
		{"read", `{"command":"str_replace","path":"/memories/a.md","old_str":"a"}`, "read the memory: the disk is gone"},
		{"read", `{"command":"insert","path":"/memories/a.md","insert_line":0,"insert_text":"b"}`,
			"read the memory: the disk is gone"},
		{"read", `{"command":"rename","old_path":"/memories/a.md","new_path":"/memories/b.md"}`,
			"rename in the memory: the disk is gone"},
		{"write", `{"command":"create","path":"/memories/b.md","file_text":"b"}`, "write to the memory: the disk is gone"},
		{"write", `{"command":"str_replace","path":"/memories/a.md","old_str":"a"}`, "write to the memory: the disk is gone"},
		{"write", `{"command":"insert","path":"/memories/a.md","insert_line":0,"insert_text":"b"}`,
			"write to the memory: the disk is gone"},
		{"delete", `{"command":"delete","path":"/memories/a.md"}`, "delete from the memory: the disk is gone"},
	}
	for _, tt := range tests {
		t.Run(tt.fail+" "+tt.input, func(t *testing.T) {
			t.Parallel()
			s := &brokenStore{}
			write(t, &s.MapMemoryStore, "sam", "a.md", "a")
			s.fail = tt.fail

			got := runMemory(t, s, "sam", tt.input)

			if got[0].Content != tt.want || !got[0].IsError {
				t.Errorf("result = %q (error %t), want the error %q", got[0].Content, got[0].IsError, tt.want)
			}
		})
	}
}
