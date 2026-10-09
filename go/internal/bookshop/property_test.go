package bookshop_test

import (
	"encoding/json/v2"
	"errors"
	"fmt"
	"strings"
	"testing"

	"pgregory.net/rapid"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// replySpec is one generated reply: its text, and whether it has a reasoning block, calls a tool, sends run context
// and crashes while its tool runs.
type replySpec struct {
	text                                  string
	thinking, callsTool, context, crashes bool
}

// Pieces JSON escapes or a store could normalize: quotes, backslashes, NUL, non-ASCII, a surrogate pair, a line
// separator, HTML.
var pieces = []string{"café", `"quoted"`, `back\slash`, "nul\x00", "😀", "line sep", "<b>&amp;", "+1", //nolint:gochecknoglobals // A constant list.
	"tab\tnew\nline", " "}

// Over generated replies, each saved to the sessions table's text column after every step and resumed with a
// freshly built agent, some crashing while their tools run, the prefix stays byte-identical and every request is
// valid.
func TestSessions_TEST07_ThePrefixStaysByteIdenticalAcrossSavesToTheSessionsTableAndResumes(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	store := bookshop.NewSessions(d.pool)
	cases := 0
	rapid.Check(t, func(rt *rapid.T) {
		cases++
		id := fmt.Sprintf("property-%04d", cases)
		steps := rapid.SliceOfN(rapid.Custom(func(rt *rapid.T) replySpec {
			s := replySpec{
				text:      strings.Join(rapid.SliceOfN(rapid.SampledFrom(pieces), 1, 6).Draw(rt, "pieces"), ""),
				thinking:  rapid.Bool().Draw(rt, "thinking"),
				callsTool: rapid.Bool().Draw(rt, "calls a tool"),
				context:   rapid.Bool().Draw(rt, "context"),
			}
			s.crashes = s.callsTool && rapid.IntRange(0, 3).Draw(rt, "crash") == 0
			return s
		}), 1, 5).Draw(rt, "steps")

		var requests []officina.Request
		for i, step := range steps {
			// A new start of the application: only the stored text is left, and the agent is built afresh.
			c, saved := &officina.Conversation{ID: id}, ""
			s, err := store.Load(t.Context(), id)
			switch {
			case err == nil:
				c, saved = s.Conversation, s.Saved
				if again, err := json.Marshal(c); err != nil || string(again) != saved {
					rt.Fatalf("the stored session reads back as %s (%v), not as stored: %s", again, err, saved)
				}
			case !errors.Is(err, bookshop.ErrNoSession):
				rt.Fatalf("Load() error = %v", err)
			}
			var replies []officinatest.Reply
			if step.callsTool {
				replies = append(replies, sayThenCall(step.text,
					officinatest.ToolUseBlock(fmt.Sprintf("call-%d-%d", cases, i), "get_book", `{"bookId":144}`)))
			}
			last := officinatest.TextReply(step.text)
			if step.thinking {
				thinking := officina.Block{Raw: []byte(`{ "type" : "thinking", "thinking": "", "signature" : "c2ln+/=" }`)}
				last.Events = append([]officina.ModelEvent{officina.BlockReceived{Block: thinking}}, last.Events...)
			}
			model := officinatest.NewModel("scripted", append(replies, last)...)
			agent, err := bookshop.NewAgent(bookshop.Config{Model: model}, d.pool, officinatest.NewApprover(), nil)
			if err != nil {
				rt.Fatalf("NewAgent() error = %v", err)
			}
			opts := officina.RunOptions{}
			if step.context {
				opts.Context = "Context: " + step.text
			}
			events, result := agent.Stream(t.Context(), c, "Say "+step.text, opts)
			for e := range events {
				if _, ok := e.(officina.ConversationAppended); ok {
					if saved, err = store.Save(t.Context(), c, "Sam", officina.Usage{}, 0, saved); err != nil {
						rt.Fatalf("Save() error = %v", err)
					}
				}
				if _, ok := e.(officina.ToolCallStarted); ok && step.crashes {
					break
				}
			}
			if res, err := result(); err != nil || !step.crashes && res.Status != officina.Completed {
				rt.Fatalf("the run ended %+v, %v", res, err)
			}
			requests = append(requests, model.Requests()...)
		}

		for i, req := range requests {
			if err := officinatest.CheckConversation(req.Messages); err != nil {
				rt.Errorf("request %d is invalid: %v", i+1, err)
			}
		}
		if err := officinatest.CheckPrefix(requests); err != nil {
			rt.Errorf("the prefix changed: %v", err)
		}
	})
}
