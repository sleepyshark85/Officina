package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"slices"
	"strings"
	"sync"
	"testing"
	"unicode/utf16"

	"pgregory.net/rapid"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// propertyTools returns the tools of the property test: each takes the call's id as input, and the writes check
// that their attempt is in sink before they run, noting a breach in breaches.
func propertyTools(sink *memorySink, breaches *[]string, mu *sync.Mutex) []officina.Tool {
	schema := jsontext.Value(`{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}`)
	audited := func(_ context.Context, input jsontext.Value) (string, error) {
		var in struct {
			ID string `json:"id"`
		}
		if err := json.Unmarshal(input, &in); err != nil {
			return "", err
		}
		if !slices.ContainsFunc(sink.Entries(), func(e officina.AuditEntry) bool {
			return e.Kind == officina.AuditToolStarted && e.CallID == in.ID
		}) {
			mu.Lock()
			defer mu.Unlock()
			*breaches = append(*breaches, in.ID+" ran before its audit entry")
		}
		return "written", nil
	}
	tools := []officina.Tool{
		{Name: "read", Kind: officina.Read, Handler: ok},
		{Name: "write", Kind: officina.Write, Handler: audited},
		{Name: "approve", Kind: officina.Write, NeedsApproval: true, Handler: audited},
		{Name: "fail", Kind: officina.Read, Handler: func(context.Context, jsontext.Value) (string, error) {
			return "", errors.New("failed")
		}},
		{Name: "panic", Kind: officina.Read, Handler: func(context.Context, jsontext.Value) (string, error) {
			panic("bug")
		}},
	}
	for i := range tools {
		tools[i].InputSchema = schema
	}
	return tools
}

// replyGen generates one model reply of a run; its calls have ids that start with prefix.
func replyGen(prefix string) *rapid.Generator[officinatest.Reply] {
	return rapid.Custom(func(t *rapid.T) officinatest.Reply {
		switch rapid.IntRange(0, 9).Draw(t, "reply") {
		case 0:
			return officinatest.TextReply("Done.")
		case 1:
			return officinatest.Reply{Events: []officina.ModelEvent{officina.TextDelta{Text: "Par"}},
				Err: errors.New("overloaded")}
		case 2:
			return officinatest.Reply{Events: []officina.ModelEvent{
				officina.BlockReceived{Block: officinatest.TextBlock("Cut")}, officina.Finished{Reason: officina.FinishMaxTokens},
			}}
		}
		n := rapid.IntRange(1, 4).Draw(t, "calls")
		blocks := make([]officina.Block, n)
		for i := range blocks {
			id := fmt.Sprintf("%sc%d", prefix, i)
			name := rapid.SampledFrom([]string{"read", "write", "approve", "fail", "panic", "missing"}).Draw(t, "tool")
			input := `{"id":"` + id + `"}`
			if rapid.IntRange(0, 9).Draw(t, "invalid input") == 0 {
				input = `{"id":1}`
			}
			blocks[i] = officinatest.ToolUseBlock(id, name, input)
		}
		reply := officinatest.ToolUseReply(blocks...)
		if rapid.IntRange(0, 9).Draw(t, "ends with calls") == 0 {
			reply.Events[len(reply.Events)-1] = officina.Finished{Reason: officina.FinishEnd}
		}
		return reply
	})
}

// secretForm returns s written as text, or as the inside of a JSON string in one of the ways an encoder may write
// it: with or without HTML escapes, every non-ASCII character escaped, or / escaped.
func secretForm(t *rapid.T, s string) string {
	quoted, err := json.Marshal(s, jsontext.EscapeForHTML(rapid.Bool().Draw(t, "html")))
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	inner := string(quoted[1 : len(quoted)-1])
	switch rapid.IntRange(0, 3).Draw(t, "form") {
	case 0:
		return s
	case 1:
		return inner
	case 2:
		var b strings.Builder
		for _, r := range inner {
			if r < 0x80 {
				b.WriteRune(r)
				continue
			}
			for _, unit := range utf16.AppendRune(nil, r) {
				b.WriteString(u(fmt.Sprintf("%04X", unit)))
			}
		}
		return b.String()
	default:
		return strings.ReplaceAll(inner, "/", `\/`)
	}
}

func TestRun_TEST07_EVT02_GeneratedSecretsNeverReachResultsEventsTelemetryOrTheTrail(t *testing.T) {
	t.Parallel()
	// The secrets' characters are none of "[redacted]"'s, so a replacement never makes a secret, and no letter or
	// digit, which the names, ids and numbers of telemetry hold, except in a fixed core of letters and digits that
	// none of them holds, around which some secrets are made.
	char := rapid.SampledFrom([]rune(`/<"\é😀€`))
	const core = "Zq7Kx9"
	secret := rapid.OneOf(rapid.StringOfN(char, 1, 4, -1), rapid.Custom(func(t *rapid.T) string {
		return rapid.StringOfN(char, 0, 2, -1).Draw(t, "before") + core + rapid.StringOfN(char, 0, 2, -1).Draw(t, "after")
	}))
	rapid.Check(t, func(t *rapid.T) {
		secrets := rapid.SliceOfN(secret, 1, 4).Draw(t, "secrets")
		// Secrets, each in some form, between fillers of characters no form of a secret holds: once each secret is
		// replaced whole, only the fillers are left.
		var text, fillers strings.Builder
		for range rapid.IntRange(1, 6).Draw(t, "pieces") {
			filler := rapid.StringOfN(rapid.SampledFrom([]rune("#!% ")), 1, 3, -1).Draw(t, "filler")
			text.WriteString(filler + secretForm(t, rapid.SampledFrom(secrets).Draw(t, "secret")))
			fillers.WriteString(filler)
		}
		input, err := json.Marshal(map[string]string{"t": text.String()})
		if err != nil {
			t.Fatalf("Marshal() error = %v", err)
		}
		echo := handlerTool("echo", officina.Read, func(context.Context, jsontext.Value) (string, error) {
			return text.String(), nil
		})
		failing := handlerTool("fail", officina.Read, func(context.Context, jsontext.Value) (string, error) {
			return "", errors.New(text.String())
		})
		denied := handlerTool("deny", officina.Write, ok)
		denied.NeedsApproval = true
		last := officinatest.TextReply(text.String())
		modelFails := rapid.Bool().Draw(t, "model fails")
		if modelFails {
			last = officinatest.Reply{Err: errors.New(text.String())}
		}
		model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
			officinatest.ToolUseBlock("c1", "echo", string(input)), officinatest.ToolUseBlock("c2", "fail", string(input)),
			officinatest.ToolUseBlock("c3", "deny", string(input))), last)
		sink := &memorySink{}
		telemetry := collect()
		defer telemetry.shutdown(t)
		agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
			Tools: []officina.Tool{echo, failing, denied}, AuditSink: sink, Secrets: secrets,
			Approver:       officinatest.NewApprover(officina.Approval{Reason: text.String()}),
			TracerProvider: telemetry.traces, MeterProvider: telemetry.meters,
			TelemetryContent: rapid.Bool().Draw(t, "content"),
		})
		if err != nil {
			t.Fatalf("NewAgent() error = %v", err)
		}
		var c officina.Conversation
		events, result := agent.Stream(context.Background(), &c, "Go", officina.RunOptions{})
		var seen []string
		for e := range events {
			switch e.(type) {
			case officina.ConversationAppended, officina.TextStreamed:
				// The two places a secret may appear, by design.
			default:
				seen = append(seen, fmt.Sprint(e))
			}
		}
		res, err := result()
		if err != nil {
			t.Fatalf("result() error = %v", err)
		}

		redacted := []string{results(c.Messages()[2])[0].Content, results(c.Messages()[2])[1].Content}
		if modelFails {
			redacted = append(redacted, res.Detail)
		} else {
			redacted = append(redacted, res.Text)
		}
		for _, e := range sink.Entries() {
			seen = append(seen, e.Input, e.Detail)
			if e.Kind == officina.AuditToolEnded && e.CallID != "c3" {
				redacted = append(redacted, e.Detail)
			}
		}
		for _, out := range redacted {
			if left := strings.ReplaceAll(out, "[redacted]", ""); left != fillers.String() {
				t.Fatalf("redacting %q left %q of %q, want only the fillers %q", text.String(), left, out, fillers.String())
			}
		}
		seen = append(seen, telemetry.dump(t))
		seen = append(seen, redacted...)
		for _, s := range secrets {
			quoted, err := json.Marshal(s)
			if err != nil {
				t.Fatalf("Marshal() error = %v", err)
			}
			for _, form := range []string{s, string(quoted[1 : len(quoted)-1])} {
				for _, out := range seen {
					if strings.Contains(out, form) {
						t.Fatalf("secret %q reached %q", form, out)
					}
				}
			}
		}
	})
}

func TestRun_TEST07_GeneratedRunsKeepTheConversationValidAndNoWriteRunsUnaudited(t *testing.T) {
	t.Parallel()
	rapid.Check(t, func(t *rapid.T) {
		var (
			c        officina.Conversation
			requests []officina.Request
			breaches []string
			mu       sync.Mutex
		)
		failing := rapid.SliceOf(rapid.StringMatching(`r[0-3]m[0-2]c[0-3]`)).Draw(t, "unaudited calls")
		sink := &memorySink{fail: func(e officina.AuditEntry) bool {
			return e.Kind == officina.AuditToolStarted && slices.Contains(failing, e.CallID)
		}}
		tools := propertyTools(sink, &breaches, &mu)

		for r := range rapid.IntRange(1, 4).Draw(t, "runs") {
			replies := make([]officinatest.Reply, rapid.IntRange(1, 3).Draw(t, "replies"))
			for m := range replies {
				replies[m] = replyGen(fmt.Sprintf("r%dm%d", r, m)).Draw(t, "reply")
			}
			model := officinatest.NewModel("scripted", replies...)
			var approver officina.Approver
			if rapid.Bool().Draw(t, "attended") {
				approver = officinatest.NewApprover(rapid.SliceOfN(rapid.Custom(func(t *rapid.T) officina.Approval {
					return officina.Approval{Approved: rapid.Bool().Draw(t, "approved")}
				}), 0, 6).Draw(t, "answers")...)
			}
			agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
				Tools: tools, Approver: approver, AuditSink: sink,
			})
			if err != nil {
				t.Fatalf("NewAgent() error = %v", err)
			}
			// The host cancels, or stops reading, at a generated event; or never (-1).
			stopAt := rapid.IntRange(-1, 12).Draw(t, "stop at event")
			byBreak := rapid.Bool().Draw(t, "stop by break")
			ctx, cancel := context.WithCancel(context.Background())
			events, result := agent.Stream(ctx, &c, "Go", officina.RunOptions{})
			seen := 0
			for range events {
				if seen == stopAt && byBreak {
					break
				}
				if seen == stopAt {
					cancel()
				}
				seen++
			}
			res, err := result()
			cancel()
			requests = append(requests, model.Requests()...)

			if err != nil {
				t.Fatalf("run %d: error = %v", r, err)
			}
			if strings.Contains(res.Detail, "is invalid") {
				t.Fatalf("run %d sent a request the provider rejects: %s", r, res.Detail)
			}
			if messages := c.Messages(); len(messages) > 0 {
				if err := officinatest.CheckConversation(messages); err != nil {
					t.Fatalf("after run %d, CheckConversation() = %v", r, err)
				}
			}
		}

		if breaches != nil {
			t.Fatalf("writes ran unaudited: %q", breaches)
		}
		if err := officinatest.CheckPrefix(requests); err != nil {
			t.Fatalf("CheckPrefix() = %v", err)
		}
	})
}
