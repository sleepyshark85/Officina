package dependencies_test

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"
	"go.uber.org/goleak"
)

const anthropicSDK = "github.com/anthropics/anthropic-sdk-go"

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func TestDependencies_TEST05_ModuleKeepsTheRules(t *testing.T) {
	t.Parallel()

	if diff := cmp.Diff([]string(nil), violations(t, ".", nil)); diff != "" {
		t.Errorf("violations mismatch (-want +got):\n%s", diff)
	}
}

func TestDependencies_TEST05_FixturesBreakingTheRulesFail(t *testing.T) {
	t.Parallel()

	const (
		core     = "example.com/fixture/officina"
		onlySDK  = ": only example.com/fixture/officina/claude may import the Anthropic SDK"
		onlyCore = ": the core may import only the standard library and the OpenTelemetry API"
	)
	tests := []struct {
		fixture string
		want    []string
	}{
		{fixture: "allowed"},
		{
			fixture: "sdk_outside_claude",
			want: []string{
				core + "/mcp imports " + anthropicSDK + onlySDK,
				core + "/officinatest imports " + anthropicSDK + "/option" + onlySDK,
			},
		},
		{
			fixture: "core_outside_rule",
			want: []string{
				core + " imports example.com/fixture/officina/mcp" + onlyCore,
				core + " imports example.com/thirdparty" + onlyCore,
				core + " imports " + anthropicSDK + onlySDK,
				core + " imports " + anthropicSDK + onlyCore,
				core + " imports go.opentelemetry.io/otel/sdk/trace" + onlyCore,
				core + " imports go.opentelemetry.io/otel" + onlyCore,
			},
		},
	}
	// The fixtures resolve their imports to stub modules in testdata, so listing them needs no network.
	offline := []string{"GOPROXY=off", "GOFLAGS=-mod=mod"}
	for _, tt := range tests {
		t.Run(tt.fixture, func(t *testing.T) {
			t.Parallel()

			got := violations(t, filepath.Join("testdata", "dependencies", tt.fixture), offline)
			if diff := cmp.Diff(tt.want, got); diff != "" {
				t.Errorf("violations(%s) mismatch (-want +got):\n%s", tt.fixture, diff)
			}
		})
	}
}

// violations returns, sorted, each direct import of a package of the module in dir that breaks a rule: only the
// Claude package imports the Anthropic SDK, and the core imports only the standard library and the OpenTelemetry
// API. Only direct imports count: the application reaches the SDK through the Claude package, and the OpenTelemetry
// API brings modules of its own.
func violations(t *testing.T, dir string, env []string) []string {
	t.Helper()

	module := strings.TrimSpace(goList(t, dir, env, "-m"))
	core, claude := module+"/officina", module+"/officina/claude"

	var found []string
	for line := range strings.Lines(goList(t, dir, env, "-f", "{{.ImportPath}} {{join .Imports \" \"}}", "./...")) {
		fields := strings.Fields(line)
		pkg, imports := fields[0], fields[1:]
		for _, imported := range imports {
			if pkg != claude && within(imported, anthropicSDK) {
				found = append(found, fmt.Sprintf("%s imports %s: only %s may import the Anthropic SDK", pkg, imported, claude))
			}
			if pkg == core && !allowedInCore(imported) {
				found = append(found, fmt.Sprintf("%s imports %s: the core may import only the standard library "+
					"and the OpenTelemetry API", pkg, imported))
			}
		}
	}
	slices.Sort(found)
	return found
}

// allowedInCore reports whether the core may import the package: the standard library, whose import paths alone
// have no dot in their first element, or the OpenTelemetry API's tracing, metrics and attributes. Never the
// OpenTelemetry SDK or an exporter, nor the root otel package, which holds the global providers.
func allowedInCore(path string) bool {
	first, _, _ := strings.Cut(path, "/")
	return !strings.Contains(first, ".") ||
		within(path, "go.opentelemetry.io/otel/trace") ||
		within(path, "go.opentelemetry.io/otel/metric") ||
		within(path, "go.opentelemetry.io/otel/attribute")
}

// within reports whether the import path is the given path or below it.
func within(path, root string) bool {
	return path == root || strings.HasPrefix(path, root+"/")
}

// goList runs 'go list' with the arguments on the module in dir and returns its output.
func goList(t *testing.T, dir string, env []string, args ...string) string {
	t.Helper()

	cmd := exec.CommandContext(t.Context(), "go", append([]string{"list"}, args...)...)
	cmd.Dir = dir
	cmd.Env = append(os.Environ(), append([]string{"GOWORK=off"}, env...)...)
	var stderr bytes.Buffer
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err != nil {
		t.Fatalf("go list %v in %s: %v\n%s", args, dir, err, stderr.String())
	}
	return string(out)
}
