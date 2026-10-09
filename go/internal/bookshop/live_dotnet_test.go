//go:build live

package bookshop_test

import (
	"bytes"
	"encoding/json/v2"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
)

// dotnetApp is the .NET application's project.
const dotnetApp = "../../../apps/BookshopAssistant"

// A session the .NET application saved resumes in this one, with the same prefix and cache reads: the .NET
// application, built here and run headless with its settings pointing at the test's database and export server,
// answers one message and quits; this application resumes the session and answers a follow-up. It needs the .NET SDK,
// and costs a few cents.
func TestLive_APP10_ASessionTheDotNetApplicationSavedResumesHereWithTheSamePrefixAndCacheReads(t *testing.T) {
	t.Parallel()
	if _, err := exec.LookPath("dotnet"); err != nil {
		t.Skip("the .NET application needs the .NET SDK, which was not found")
	}
	d := newDatabase(t)
	exports := exportServer(t)

	id := dotnetSession(t, d, exports, "How many orders has Alice Martin placed?")

	model := liveModel(t, false)
	out := &transcript{}
	console, err := bookshop.Build(t.Context(), bookshop.Config{
		Database: d.url, Model: model, Memory: &officina.MapMemoryStore{}, Exports: exports, Out: out, Echo: true,
		In:      &input{t: t, script: []any{"Sam", "/resume " + id, "And when was the latest?", "/quit"}},
		Budgets: bookshop.Budgets{Reply: 0.20, Session: 0.50},
	})
	if err != nil {
		t.Fatalf("Build() error = %v", err)
	}
	defer console.Close()
	if err := console.Run(t.Context()); err != nil {
		t.Fatalf("Run() error = %v\ntranscript:\n%s", err, out)
	}

	t.Log(out.String())
	calls := model.usage()
	for i, u := range calls {
		t.Logf("call %d: %d input tokens, %d read from the cache, %d written to it, %d output", i+1,
			u.Input+u.CacheRead+u.CacheWrite, u.CacheRead, u.CacheWrite, u.Output)
	}
	inOrder(t, out.String(), "Resumed session "+id+": ", "you> And when was the latest?", "[tokens: ")
	if strings.Contains(out.String(), "[Failed") || strings.Contains(out.String(), "another version") {
		t.Errorf("the resumed session did not go on:\n%s", out)
	}
	// The first call's prefix and the .NET session's messages were cached by the .NET application's calls.
	if len(calls) == 0 || calls[0].CacheRead <= calls[0].Input+calls[0].CacheWrite {
		t.Errorf("the first call read too little from the cache, want most of its input: %+v", calls)
	}
}

// dotnetSession builds the .NET application, runs it against d and the export server at exports with the staff
// member Sam asking question, and returns the id of the session it saved.
func dotnetSession(t *testing.T, d *database, exports, question string) string {
	t.Helper()
	bin := t.TempDir()
	build := exec.CommandContext(t.Context(), "dotnet", "build", dotnetApp, "-c", "Release", "-o", bin, "--nologo",
		"-v", "quiet")
	if output, err := build.CombinedOutput(); err != nil {
		t.Fatalf("build the .NET application: %v\n%s", err, output)
	}
	settings, err := json.Marshal(map[string]any{
		"Database": "Host=" + server.host + ";Port=" + server.port + ";Username=" + user + ";Password=" + password +
			";Database=" + d.name,
		"ExportsUrl": exports, "DataFolder": filepath.Join(bin, "data"),
	})
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	if err := os.WriteFile(filepath.Join(bin, "appsettings.Local.json"), settings, 0o600); err != nil {
		t.Fatalf("write the .NET settings: %v", err)
	}
	run := exec.CommandContext(t.Context(), "dotnet", filepath.Join(bin, "BookshopAssistant.dll"))
	run.Dir = bin
	run.Stdin = strings.NewReader("Sam\n" + question + "\n/quit\n")
	var output bytes.Buffer
	run.Stdout, run.Stderr = &output, &output
	if err := run.Run(); err != nil {
		t.Fatalf("run the .NET application: %v\n%s", err, output.String())
	}
	t.Logf(".NET application:\n%s", output.String())
	session := regexp.MustCompile(`Session ([0-9a-f]{12})\.`).FindStringSubmatch(output.String())
	if session == nil || !strings.Contains(output.String(), "[tokens: ") {
		t.Fatalf("the .NET application saved no answered session:\n%s", output.String())
	}
	return session[1]
}
