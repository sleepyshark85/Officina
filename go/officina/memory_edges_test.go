package officina_test

import (
	"fmt"
	"strconv"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
)

// The edges of the memory tool's ranges, limits and snippets.

func TestMemoryTool_MEM01_ViewRangesInsertLinesAndSnippetsHoldAtTheirEdges(t *testing.T) {
	t.Parallel()
	s := &officina.MapMemoryStore{}
	lines := make([]string, 12)
	for i := range lines {
		lines[i] = "line " + strconv.Itoa(i+1)
	}
	write(t, s, "sam", "a.md", "a\nb")
	write(t, s, "sam", "long.md", strings.Join(lines, "\n"))

	got := runMemory(t, s, "sam",
		`{"command":"view","path":"/memories/a.md","view_range":[1,2]}`,
		`{"command":"view","path":"/memories/a.md","view_range":[2,2]}`,
		`{"command":"view","path":"/memories/a.md","view_range":[1,1]}`,
		`{"command":"view","path":"/memories/a.md","view_range":[3,-1]}`,
		`{"command":"view","path":"/memories/a.md","view_range":[2,-2]}`,
		`{"command":"insert","path":"/memories/a.md","insert_line":2,"insert_text":"c"}`,
		`{"command":"str_replace","path":"/memories/long.md","old_str":"line 7","new_str":"seven\nand a half"}`)

	invalid := "Error: Invalid `view_range`: it should be [start, end] with 1 <= start <= end <= 2, or end -1 for " +
		"the end of the file."
	want := []string{
		"Here's the content of /memories/a.md with line numbers:\n     1\ta\n     2\tb",
		"Here's the content of /memories/a.md with line numbers:\n     2\tb",
		"Here's the content of /memories/a.md with line numbers:\n     1\ta",
		invalid, invalid,
		"The file /memories/a.md has been edited.",
		"The memory file has been edited. A snippet of /memories/long.md with line numbers:\n" +
			"     3\tline 3\n     4\tline 4\n     5\tline 5\n     6\tline 6\n     7\tseven\n     8\tand a half\n" +
			"     9\tline 8\n    10\tline 9\n    11\tline 10\n    12\tline 11",
	}
	if diff := cmp.Diff(want, contents(got)); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
	if text, err := s.Read(t.Context(), "sam", "a.md"); err != nil || text != "a\nb\nc" {
		t.Errorf("Read() = %q, %v; want c inserted after the last line", text, err)
	}
}

func TestMemoryTool_MEM01_AViewEndsAtTheLastLineBreakInItsSecondHalfElseAtTheLimit(t *testing.T) {
	t.Parallel()
	const truncated = "\n[Truncated at 16000 characters: view the rest with view_range.]"
	const header = "Here's the content of /memories/a.md with line numbers:\n"
	// Numbered, each line starts with seven characters ("     1\t"), so a first line of n characters ends at n+7.
	var ninetyNines []string
	for i := 1; i <= 149; i++ {
		ninetyNines = append(ninetyNines, fmt.Sprintf("%6d\t%s", i, strings.Repeat("x", 99)))
	}
	tests := []struct {
		name, text, want string
	}{
		{"exactly the limit", strings.Repeat("x", 16_000-7), "     1\t" + strings.Repeat("x", 16_000-7)},
		{"a line break at the half", strings.Repeat("x", 8_000-7) + "\n" + strings.Repeat("y", 20_000),
			"     1\t" + strings.Repeat("x", 8_000-7) + truncated},
		{"a line break before the half", strings.Repeat("x", 7_999-7) + "\n" + strings.Repeat("y", 20_000),
			"     1\t" + strings.Repeat("x", 7_999-7) + "\n     2\t" + strings.Repeat("y", 16_000-7_999-8) + truncated},
		// Each numbered line is 106 characters and a line break: the last break within the limit ends line 149.
		{"lines of 99", strings.TrimSuffix(strings.Repeat(strings.Repeat("x", 99)+"\n", 400), "\n"),
			strings.Join(ninetyNines, "\n") + truncated},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			s := &officina.MapMemoryStore{}
			write(t, s, "sam", "a.md", tt.text)

			got := runMemory(t, s, "sam", `{"command":"view","path":"/memories/a.md"}`)[0].Content

			if got != header+tt.want {
				t.Errorf("view of %d characters ending %q; want %d ending %q", len([]rune(got)), got[len(got)-90:],
					len([]rune(header+tt.want)), tt.want[len(tt.want)-90:])
			}
		})
	}
}

func TestMemoryStore_MEM03_APathHoldsAtMost1024Characters(t *testing.T) {
	t.Parallel()
	part := strings.Repeat("é", 255)
	longest := strings.Join([]string{part, part, part, part[:2*254], "a"}, "/")
	s := &officina.MapMemoryStore{}

	if n := len([]rune(longest)); n != 1024 {
		t.Fatalf("the longest path has %d characters, want 1024", n)
	}
	if err := s.Write(t.Context(), "sam", longest, "x"); err != nil {
		t.Errorf("Write() of a path of 1024 characters error = %v", err)
	}
	if err := s.Write(t.Context(), "sam", longest+"a", "x"); err == nil {
		t.Error("Write() of a path of 1025 characters succeeded, want an error")
	}
}
