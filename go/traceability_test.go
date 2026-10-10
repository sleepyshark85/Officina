package dependencies_test

import (
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"testing"
)

func TestTraceability_EveryTestThePageNamesExists(t *testing.T) {
	t.Parallel()
	page, err := os.ReadFile(filepath.Join("docs", "traceability.md"))
	if err != nil {
		t.Fatalf("ReadFile() error = %v", err)
	}
	defined := map[string]bool{}
	declared := regexp.MustCompile(`(?m)^func ((?:Test|Fuzz|Example)\w*)\(`)
	err = filepath.WalkDir(".", func(path string, d fs.DirEntry, err error) error {
		switch {
		case err != nil:
			return err
		case d.IsDir() && d.Name() == "testdata":
			return filepath.SkipDir
		case d.IsDir() || !strings.HasSuffix(path, "_test.go"):
			return nil
		}
		src, err := os.ReadFile(path)
		for _, m := range declared.FindAllSubmatch(src, -1) {
			defined[string(m[1])] = true
		}
		return err
	})
	if err != nil {
		t.Fatalf("WalkDir() error = %v", err)
	}

	var missing []string
	for _, m := range regexp.MustCompile("`((?:Test|Fuzz|Example)\\w*)`").FindAllStringSubmatch(string(page), -1) {
		if !defined[m[1]] && !slices.Contains(missing, m[1]) {
			missing = append(missing, m[1])
		}
	}
	if missing != nil {
		t.Errorf("docs/traceability.md names tests that do not exist: %s", strings.Join(missing, ", "))
	}
}
