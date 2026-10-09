package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"io/fs"
	"maps"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"pgregory.net/rapid"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// Generated paths (climbing, absolute, encoded, through a link, either separator) never reach a file outside the
// run's scope, in either store, through the memory tool or the store directly. Each case starts with a file in scope
// "sam", one in scope "other", and a secret outside the stores; in the file store, sam's directory holds a link to
// the secret's directory and one to other's directory, where links can be made.

const secret = "TOP-SECRET"

// The file store's directories of scopes sam and other: the hex of their UTF-8 bytes.
const (
	samDir   = "73616d"
	otherDir = "6f74686572"
)

// memoryCommands are the commands tried; "rename back" renames the generated path to a file in scope.
func memoryCommands() []string {
	return []string{"view", "create", "str_replace", "insert", "delete", "rename", "rename back"}
}

// scopeAttempt is one case: a command, its path, and a second path for renames.
type scopeAttempt struct {
	Command, Path, Other string
}

// pathGen generates mostly a route out of the scope to a target; otherwise any pieces, joined by either separator or
// none. Either may start with /memories/.
func pathGen() *rapid.Generator[string] {
	pieces := []string{
		"..", ".", "", "a", "b.md", "link", "peer", "memories", "/memories", "other", "outside", "secret.txt", "%2e%2e",
		"%2f", "..%2f", "%252e%252e", "C:", `c:\`, `\`, "/", `..\..`, `\\server\share`, "~", "...", ".. ", "a.", "NUL",
		"con.txt", "x\x00", "\u2215", "\uff0e\uff0e", "/etc/passwd",
	}
	routes := []string{
		"link", "./link", "a/../link", "peer", "..", "../..", "a/../..", "../../outside", "%2e%2e", "..%2f..",
		"\uff0e\uff0e", "", "C:", `\\server\share`, "x\x00/..", "... ", `..\..`,
	}
	targets := []string{"secret.txt", "outside/secret.txt", "other/o.md", "o.md", "memory/other/o.md",
		otherDir + "/o.md", "memory/" + otherDir + "/o.md", "etc/passwd"}
	separator := rapid.SampledFrom([]string{"/", "/", "/", `\`})
	return rapid.Custom(func(t *rapid.T) string {
		prefix := ""
		if rapid.Bool().Draw(t, "prefixed") {
			prefix = "/memories/"
		}
		if rapid.IntRange(0, 4).Draw(t, "kind") > 0 {
			return prefix + rapid.SampledFrom(routes).Draw(t, "route") + separator.Draw(t, "separator") +
				rapid.SampledFrom(targets).Draw(t, "target")
		}
		parts := rapid.SliceOfN(rapid.SampledFrom(pieces), 1, 4).Draw(t, "pieces")
		path := parts[0]
		for _, p := range parts[1:] {
			path += rapid.SampledFrom([]string{"/", `\`, ""}).Draw(t, "join") + p
		}
		return prefix + path
	})
}

func TestMemory_TEST07_MEM03_GeneratedPathsNeverLeaveTheirScope(t *testing.T) {
	t.Parallel()
	paths := pathGen()
	rapid.Check(t, func(rt *rapid.T) {
		a := scopeAttempt{
			Command: rapid.SampledFrom(memoryCommands()).Draw(rt, "command"),
			Path:    paths.Draw(rt, "path"), Other: paths.Draw(rt, "other"),
		}
		for _, files := range []bool{false, true} {
			checkScope(rt, t.TempDir(), files, a)
		}
	})
}

func FuzzMemory_MEM03_PathsNeverLeaveTheirScope(f *testing.F) {
	f.Add(uint8(0), "/memories/link/secret.txt", "")
	f.Add(uint8(1), "/memories/peer/o.md", "")
	f.Add(uint8(2), "/memories/../outside/secret.txt", "")
	f.Add(uint8(5), "/memories/a/b.md", "/memories/link/x.md")
	f.Add(uint8(6), "/memories/peer/o.md", "")
	f.Add(uint8(4), `/memories/..\..\outside`, "")
	f.Add(uint8(3), "/memories/%2e%2e/"+otherDir+"/o.md", "")
	f.Fuzz(func(t *testing.T, command uint8, path, other string) {
		commands := memoryCommands()
		a := scopeAttempt{Command: commands[int(command)%len(commands)], Path: path, Other: other}
		for _, files := range []bool{false, true} {
			checkScope(t, t.TempDir(), files, a)
		}
	})
}

// checkScope tries a in a store under dir, the file store if files is set, and checks that nothing outside sam's
// scope was read or changed.
func checkScope(t tb, dir string, files bool, a scopeAttempt) {
	t.Helper()
	ctx := context.Background()
	outside := filepath.Join(dir, "outside", "secret.txt")
	memoryDir := filepath.Join(dir, "memory")
	var s officina.MemoryStore = &officina.MapMemoryStore{}
	if files {
		s = officina.NewFileMemoryStore(memoryDir)
	}
	if err := os.MkdirAll(filepath.Dir(outside), 0o700); err != nil {
		t.Fatalf("arrange: %v", err)
	}
	if err := os.WriteFile(outside, []byte(secret), 0o600); err != nil {
		t.Fatalf("arrange: %v", err)
	}
	if err := s.Write(ctx, "sam", "a/b.md", "inside"); err != nil {
		t.Fatalf("arrange: %v", err)
	}
	if err := s.Write(ctx, "other", "o.md", "other scope"); err != nil {
		t.Fatalf("arrange: %v", err)
	}
	if files {
		// Where links cannot be made, as on Windows without the privilege, there is none to follow.
		_ = os.Symlink(filepath.Dir(outside), filepath.Join(memoryDir, samDir, "link"))
		_ = os.Symlink(filepath.Join("..", otherDir), filepath.Join(memoryDir, samDir, "peer"))
	}
	before := snapshot(t, dir)

	path, other := a.Path, a.Other
	if a.Command == "rename back" {
		path, other = a.Other, "/memories/a/b.md"
	}
	input, err := json.Marshal(map[string]any{
		"command": strings.TrimSuffix(a.Command, " back"), "path": path, "old_path": path, "new_path": other,
		// Replacing "TOP" with itself shows the secret, were the file reached, without saying it in the input.
		"file_text": "x", "old_str": "TOP", "new_str": "TOP", "insert_line": 0, "insert_text": "x",
	}, jsontext.AllowInvalidUTF8(true))
	if err != nil {
		t.Fatalf("marshal the input: %v", err)
	}
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("m", "memory", string(input))), officinatest.TextReply("Done."))
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{Tools: []officina.Tool{officina.NewMemoryTool(s)}})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	var outputs []string
	events, result := agent.Stream(ctx, nil, "Go.", officina.RunOptions{MemoryScope: "sam"})
	for event := range events {
		if finished, ok := event.(officina.ToolCallFinished); ok {
			outputs = append(outputs, finished.Result.Content)
		}
	}
	if _, err := result(); err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	// The store directly: refusing is fine; reaching outside the scope is what the checks catch.
	relative := strings.TrimPrefix(path, "/memories/")
	if text, err := s.Read(ctx, "sam", relative); err == nil {
		outputs = append(outputs, text)
	}
	_ = s.Write(ctx, "sam", relative, "x")
	_ = s.Delete(ctx, "sam", relative)
	listed, _ := s.List(ctx, "sam")
	for _, f := range listed {
		outputs = append(outputs, f.Path)
		if slices.ContainsFunc(strings.Split(f.Path, "/"), func(part string) bool { return !officina.ValidMemoryScope(part) }) {
			t.Errorf("sam's listing holds %q, not a valid memory path", f.Path)
		}
	}

	for _, o := range outputs {
		if strings.Contains(o, secret) || strings.Contains(o, "other scope") {
			t.Errorf("%+v in a %s store: an output holds what is outside the scope: %q", a, kind(files), o)
		}
	}
	if text, err := s.Read(ctx, "other", "o.md"); err != nil || text != "other scope" {
		t.Errorf("%+v in a %s store: other's file is %q, %v; want it unchanged", a, kind(files), text, err)
	}
	if listed, err := s.List(ctx, "other"); err != nil || len(listed) != 1 {
		t.Errorf("%+v in a %s store: other's files are %v, %v; want one", a, kind(files), listed, err)
	}
	// On disk, nothing changed outside sam's directory.
	scope := filepath.Join(memoryDir, samDir) + string(filepath.Separator)
	after := snapshot(t, dir)
	maps.DeleteFunc(before, func(p, _ string) bool { return strings.HasPrefix(p, scope) })
	maps.DeleteFunc(after, func(p, _ string) bool { return strings.HasPrefix(p, scope) })
	if !maps.Equal(before, after) {
		t.Errorf("%+v in a %s store: outside the scope, the files went from %q to %q", a, kind(files), before, after)
	}
}

func kind(files bool) string {
	if files {
		return "file"
	}
	return "map"
}

// snapshot returns every file under dir with its text, without following links.
func snapshot(t tb, dir string) map[string]string {
	t.Helper()
	files := map[string]string{}
	err := filepath.WalkDir(dir, func(p string, d fs.DirEntry, err error) error {
		if err != nil || !d.Type().IsRegular() {
			return err
		}
		data, err := os.ReadFile(p)
		files[p] = string(data)
		return err
	})
	if err != nil {
		t.Fatalf("snapshot: %v", err)
	}
	return files
}
