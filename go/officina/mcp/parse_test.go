package mcp

import (
	"bytes"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"strconv"
	"testing"

	"github.com/google/go-cmp/cmp"
)

// The parsers of what a server writes, which the API reaches only through a server: fuzzed, as anything may come.

func FuzzParseResponse_MCP01_NeverPanicsAndAcceptsOnlyResponsesToThisClientsRequests(f *testing.F) {
	for _, seed := range []string{
		`{"jsonrpc":"2.0","id":1,"result":{}}`,
		`{"jsonrpc":"2.0","id":7,"error":{"code":-32601,"message":"Method not found."}}`,
		`{"jsonrpc":"2.0","method":"notifications/message","params":{}}`,
		`{"jsonrpc":"2.0","id":3,"method":"roots/list"}`,
		`{"jsonrpc":"2.0","id":"1","result":{}}`,
		`{"jsonrpc":"2.0","id":0,"result":{}}`,
		`{"jsonrpc":"2.0","id":-2,"result":{}}`,
		`{"jsonrpc":"2.0","id":1.5,"result":{}}`,
		`{"jsonrpc":"2.0","id":1}`,
		`{"id":1,"result":null}`,
		`[{"jsonrpc":"2.0","id":1,"result":{}}]`,
		`not json`,
		``,
	} {
		f.Add([]byte(seed))
	}
	f.Fuzz(func(t *testing.T, msg []byte) {
		r, id, ok := parseResponse(msg)
		r2, id2, ok2 := parseResponse(msg)
		if ok != ok2 || id != id2 || !bytes.Equal(r.Result, r2.Result) {
			t.Fatalf("parseResponse(%q) is not deterministic", msg)
		}
		if !ok {
			if id != 0 || r.Result != nil || r.Error != nil {
				t.Errorf("parseResponse(%q) rejected it but returned %v, %d", msg, r, id)
			}
			return
		}
		if id <= 0 || r.Method != "" || (r.Error == nil && !r.Result.IsValid()) {
			t.Errorf("parseResponse(%q) accepted id %d, method %q, result %q", msg, id, r.Method, r.Result)
		}
	})
}

func FuzzParseResponse_MCP01_FindsTheIDAndResultOfAResponse(f *testing.F) {
	f.Add(int64(1), []byte(`{"tools":[]}`))
	f.Add(int64(1<<53+1), []byte(`"<"`))
	f.Fuzz(func(t *testing.T, id int64, result []byte) {
		// Only a value that decodes compares; a number beyond float64 does not.
		var want any
		if id <= 0 || json.Unmarshal(result, &want) != nil {
			return
		}
		msg, err := json.Marshal(struct {
			JSONRPC string         `json:"jsonrpc"`
			ID      int64          `json:"id"`
			Result  jsontext.Value `json:"result"`
		}{"2.0", id, result})
		if err != nil {
			t.Fatalf("Marshal() error = %v", err)
		}

		r, got, ok := parseResponse(msg)

		// Marshal writes the result in its own form, so the values compare.
		var gotResult any
		if !ok || got != id || json.Unmarshal(r.Result, &gotResult) != nil || !cmp.Equal(want, gotResult) {
			t.Errorf("parseResponse(%s) = %q, %d, %v; want %q, %d, true", msg, r.Result, got, ok, result, id)
		}
	})
}

func FuzzReadEvents_MCP01_FindsTheResponseAfterAnythingElseInTheStream(f *testing.F) {
	for _, seed := range []string{
		"",
		"data:{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}\n\n",
		"data: {\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"a\":1}}\n\n",
		": a comment\nevent: message\nid: 4\ndata: {\"jsonrpc\":\r\ndata: \"2.0\"}\r\n\r\n",
		"data: unterminated",
	} {
		f.Add([]byte(seed))
	}
	f.Fuzz(func(t *testing.T, junk []byte) {
		// Junk alone never panics, and yields only a response to the request asked about.
		if r, err := readEvents(bytes.NewReader(junk), 7); err == nil {
			var id int64
			if err := json.Unmarshal(r.ID, &id); err != nil || id != 7 {
				t.Errorf("readEvents(%q) = response %s, want one with id 7", junk, r.ID)
			}
		}

		// After junk and the blank line ending any event it began, the response is found.
		stream := append(bytes.Clone(junk), "\n\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}\n\n"...)
		r, err := readEvents(bytes.NewReader(stream), 7)
		var id int64
		if err != nil || json.Unmarshal(r.ID, &id) != nil || id != 7 {
			t.Errorf("readEvents(%q) = %s, %v; want the response with id 7", stream, r.ID, err)
		}
	})
}

func FuzzLines_MCP01_SplitsTheOutputIntoLinesHoweverItIsWritten(f *testing.F) {
	f.Add([]byte("{\"a\":1}\n{\"b\":2}\r\n"), 3)
	f.Add([]byte("one\n\nthree\npartial"), 1)
	f.Fuzz(func(t *testing.T, output []byte, chunk int) {
		if chunk <= 0 {
			return
		}
		var got []string
		l := &lines{max: maxMessage, line: func(line []byte) { got = append(got, string(line)) }}
		for rest := output; len(rest) > 0; {
			n := min(chunk, len(rest))
			if written, err := l.Write(rest[:n]); written != n || err != nil {
				t.Fatalf("Write() = %d, %v; want %d, nil", written, err, n)
			}
			rest = rest[n:]
		}

		var want []string
		parts := bytes.Split(output, []byte("\n"))
		for _, part := range parts[:len(parts)-1] {
			want = append(want, string(bytes.TrimSuffix(part, []byte("\r"))))
		}
		if diff := cmp.Diff(want, got); diff != "" {
			t.Errorf("lines of %q in chunks of %d mismatch (-want +got):\n%s", output, chunk, diff)
		}
	})
}

func TestTail_MCP04_KeepsTheLastLineThatIsNotBlank(t *testing.T) {
	t.Parallel()
	var tl tail
	for i := range 2000 {
		_, _ = tl.Write([]byte("line " + strconv.Itoa(i) + "\n")) // It never fails.
	}
	_, _ = tl.Write([]byte("configuration file missing\n  \n\n")) // As above.

	if got := tl.lastLine(); got != "configuration file missing" {
		t.Errorf("lastLine() = %q, want configuration file missing", got)
	}
	if len(tl.buf) > 2*stderrKept {
		t.Errorf("tail keeps %d bytes, want at most %d", len(tl.buf), 2*stderrKept)
	}
}
