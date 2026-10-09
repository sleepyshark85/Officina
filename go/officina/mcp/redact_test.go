package mcp

import (
	"errors"
	"io"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"
)

func TestRedact_EVT03_EveryCredentialIsRedactedWholeWhateverItsOrder(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name, text, want string
		secrets          []string
	}{
		{"one starts with another", "key abcdef end", "key [redacted] end", []string{"abc", "abcdef"}},
		{"the same, in the other order", "key abcdef end", "key [redacted] end", []string{"abcdef", "abc"}},
		{"overlapping", "key abcdef end", "key [redacted] end", []string{"abcd", "cdef"}},
		{"touching", "abcd", "[redacted]", []string{"ab", "cd"}},
		{"apart", "ab and ab", "[redacted] and [redacted]", []string{"ab"}},
		{"none", "nothing here", "nothing here", []string{"secret"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			server := Server{}
			for i, s := range tt.secrets {
				server.Env = append(server.Env, "S"+string(rune('A'+i))+"="+s)
			}
			if got := server.redact(tt.text); got != tt.want {
				t.Errorf("redact(%q) = %q, want %q", tt.text, got, tt.want)
			}
		})
	}
}

func TestLines_MCP04_ALineLongerThanAMessageMayBeStopsTheServer(t *testing.T) {
	t.Parallel()
	var (
		got     []string
		stopped int
	)
	l := &lines{max: 8, line: func(line []byte) { got = append(got, string(line)) }, tooLong: func() { stopped++ }}

	if _, err := l.Write([]byte("12345678\n1234")); err != nil {
		t.Fatalf("Write() of lines within the limit error = %v", err)
	}
	// A line of exactly the limit may still be on its way.
	if _, err := l.Write([]byte("5678")); err != nil {
		t.Fatalf("Write() up to the limit error = %v", err)
	}
	_, err := l.Write([]byte("9"))
	_, again := l.Write([]byte("\n"))

	if diff := cmp.Diff([]string{"12345678"}, got); diff != "" {
		t.Errorf("lines mismatch (-want +got):\n%s", diff)
	}
	if !errors.Is(err, errTooLong) || !errors.Is(again, errTooLong) || stopped != 1 {
		t.Errorf("Write() past the limit = %v, then %v, with %d stops; want errTooLong twice and one stop", err,
			again, stopped)
	}
}

func TestCapped_MCP04_ReadsUpToItsLimitAndFailsBeyondIt(t *testing.T) {
	t.Parallel()
	within, err := io.ReadAll(&capped{r: strings.NewReader("12345678"), left: 8})
	if err != nil || string(within) != "12345678" {
		t.Errorf("ReadAll() of 8 bytes capped at 8 = %q, %v", within, err)
	}
	if _, err := io.ReadAll(&capped{r: strings.NewReader("123456789"), left: 8}); !errors.Is(err, errTooLong) {
		t.Errorf("ReadAll() of 9 bytes capped at 8 error = %v, want errTooLong", err)
	}
}
