package dependencies_test

import (
	"go/scanner"
	"go/token"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// coreLineBudget caps the core's code lines: lines of its non-test files that hold code, not only blanks or
// comments. Growing past it needs a reason and a change here and in docs/implementations/go.md (G13).
const coreLineBudget = 3000

func TestCoreSize_StaysWithinItsLineBudget(t *testing.T) {
	t.Parallel()
	files, err := filepath.Glob(filepath.Join("officina", "*.go"))
	if err != nil {
		t.Fatalf("Glob() error = %v", err)
	}

	lines := 0
	for _, file := range files {
		if !strings.HasSuffix(file, "_test.go") {
			lines += codeLines(t, file)
		}
	}

	if lines == 0 || lines > coreLineBudget {
		t.Errorf("the core has %d code lines; the budget is %d", lines, coreLineBudget)
	}
	t.Logf("the core has %d code lines of its budget of %d", lines, coreLineBudget)
}

// codeLines returns how many lines of the Go file hold a token other than a comment.
func codeLines(t *testing.T, file string) int {
	t.Helper()
	src, err := os.ReadFile(file)
	if err != nil {
		t.Fatalf("ReadFile() error = %v", err)
	}
	fset := token.NewFileSet()
	var s scanner.Scanner
	s.Init(fset.AddFile(file, -1, len(src)), src, func(pos token.Position, msg string) {
		t.Errorf("%s: %s", pos, msg)
	}, 0)
	seen := map[int]bool{}
	for {
		pos, tok, lit := s.Scan()
		if tok == token.EOF {
			break
		}
		// A semicolon the scanner inserts at a line's end is not code of its own.
		if tok == token.SEMICOLON && lit == "\n" {
			continue
		}
		seen[fset.Position(pos).Line] = true
	}
	return len(seen)
}
