package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"slices"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// sharedPrefix is the prefix of the sessions the implementations share in the repository's testdata/session, with
// the fingerprint the .NET implementation computed for it. Its strings hold every kind of character JSON escapes.
type sharedPrefix struct {
	Settings     string `json:"settings"`
	Instructions string `json:"instructions"`
	Tools        []struct {
		Name        string `json:"name"`
		Description string `json:"description"`
		InputSchema string `json:"inputSchema"`
	} `json:"tools"`
	Fingerprint string `json:"fingerprint"`
	// ContextManagement holds context management settings, each with the fingerprint .NET computed for the prefix
	// with them.
	ContextManagement []struct {
		CompactAt          int64  `json:"compactAt"`
		ClearAfter         int    `json:"clearAfter"`
		ClearKeep          int    `json:"clearKeep"`
		ClearAtLeastTokens int64  `json:"clearAtLeastTokens"`
		Fingerprint        string `json:"fingerprint"`
	} `json:"contextManagement"`
	// Output holds output schemas, each with a compaction threshold or none, and the fingerprint .NET computed for
	// the prefix with them. The schema is sharedOutput's.
	Output []struct {
		Schema      string `json:"schema"`
		CompactAt   int64  `json:"compactAt"`
		Fingerprint string `json:"fingerprint"`
	} `json:"output"`
}

// sharedOutput is the type whose schema the shared prefix's output entries hold.
type sharedOutput struct {
	Title   string   `json:"title" jsonschema:"A title «short», with <b>&amp; 'quotes' + more."`
	Copies  uint     `json:"copies"`
	Changes []string `json:"changes,omitempty"`
	Note    *string  `json:"note"`
}

// readSharedPrefix returns the shared prefix.
func readSharedPrefix(t *testing.T) sharedPrefix {
	t.Helper()
	var p sharedPrefix
	if err := json.Unmarshal([]byte(sharedFile(t, "session", "prefix.json")), &p); err != nil {
		t.Fatalf("unmarshal the shared prefix: %v", err)
	}
	return p
}

// agent returns the agent of the shared prefix on model: its search tool reads, and place_order writes.
func (p sharedPrefix) agent(t *testing.T, model officina.Model) *officina.Agent {
	t.Helper()
	return p.managing(t, model, officina.ContextManagement{})
}

// managing returns the agent of the shared prefix on model, with context management cm.
func (p sharedPrefix) managing(t *testing.T, model officina.Model, cm officina.ContextManagement) *officina.Agent {
	t.Helper()
	return p.with(t, model, officina.AgentOptions{ContextManagement: cm})
}

// with returns the agent of the shared prefix on model, with the options of opts besides the tools.
func (p sharedPrefix) with(t *testing.T, model officina.Model, opts officina.AgentOptions) *officina.Agent {
	t.Helper()
	var tools []officina.Tool
	for _, st := range p.Tools {
		kind := officina.Read
		if st.Name == "place_order" {
			kind = officina.Write
		}
		tools = append(tools, officina.Tool{
			Name: st.Name, Description: st.Description, InputSchema: jsontext.Value(st.InputSchema), Kind: kind,
			Handler: func(context.Context, jsontext.Value) (string, error) { return "ok", nil },
		})
	}
	opts.Tools = tools
	agent, err := officina.NewAgent(model, p.Instructions, opts)
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return agent
}

// fingerprint returns the fingerprint a conversation's JSON carries.
func fingerprint(t *testing.T, c *officina.Conversation) string {
	t.Helper()
	data, err := json.Marshal(c)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	var wire struct {
		Fingerprint string `json:"fingerprint"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	return wire.Fingerprint
}

func TestAgent_CTX04_TheFingerprintIsDotNetsByteForByte(t *testing.T) {
	t.Parallel()
	p := readSharedPrefix(t)
	model := officinatest.NewModel(p.Settings, officinatest.TextReply("Hello."))
	var c officina.Conversation

	run(t, p.agent(t, model), &c, "Hi", officina.RunOptions{})

	if got := fingerprint(t, &c); got != p.Fingerprint {
		t.Errorf("fingerprint = %s, want .NET's %s", got, p.Fingerprint)
	}
}

func TestAgent_CTX04_HIST01_TheFingerprintWithContextManagementIsDotNetsByteForByte(t *testing.T) {
	t.Parallel()
	p := readSharedPrefix(t)
	if len(p.ContextManagement) == 0 {
		t.Fatal("the shared prefix has no context management settings")
	}
	for _, s := range p.ContextManagement {
		cm := officina.ContextManagement{CompactAt: s.CompactAt, ClearToolResults: officina.ToolResultClearing{
			After: s.ClearAfter, Keep: s.ClearKeep, AtLeastTokens: s.ClearAtLeastTokens,
		}}
		t.Run(fmt.Sprintf("%+v", cm), func(t *testing.T) {
			t.Parallel()
			model := compacting(officinatest.NewModel(p.Settings, officinatest.TextReply("Hello.")))
			var c officina.Conversation

			run(t, p.managing(t, model, cm), &c, "Hi", officina.RunOptions{})

			if got := fingerprint(t, &c); got != s.Fingerprint {
				t.Errorf("fingerprint = %s, want .NET's %s", got, s.Fingerprint)
			}
		})
	}
}

func TestAgent_CTX04_OUT01_TheFingerprintWithAnOutputSchemaIsDotNetsByteForByte(t *testing.T) {
	t.Parallel()
	p := readSharedPrefix(t)
	if len(p.Output) == 0 {
		t.Fatal("the shared prefix has no output schemas")
	}
	output, err := officina.NewOutput[sharedOutput]()
	if err != nil {
		t.Fatalf("NewOutput() error = %v", err)
	}
	for _, s := range p.Output {
		t.Run(fmt.Sprintf("compactAt=%d", s.CompactAt), func(t *testing.T) {
			t.Parallel()
			model := compacting(officinatest.NewModel(p.Settings, officinatest.TextReply(`{"title":"Hi","copies":1,`+
				`"note":null}`)))
			var c officina.Conversation

			run(t, p.with(t, model, officina.AgentOptions{Output: output, ContextManagement: officina.ContextManagement{
				CompactAt: s.CompactAt,
			}}), &c, "Hi", officina.RunOptions{})

			if got := string(model.Requests()[0].OutputSchema); got != s.Schema {
				t.Errorf("output schema = %s, want the shared %s", got, s.Schema)
			}
			if got := fingerprint(t, &c); got != s.Fingerprint {
				t.Errorf("fingerprint = %s, want .NET's %s", got, s.Fingerprint)
			}
		})
	}
}

func TestAgent_CTX04_ASchemaIsFingerprintedAsGivenNotReEncoded(t *testing.T) {
	t.Parallel()
	schemas := []string{`{"type":"object"}`, `{ "type": "object" }`, `{"type":"obj\` + `u0065ct"}`}
	var fingerprints []string
	for _, s := range schemas {
		search := tool("search", "Searches.")
		search.InputSchema = jsontext.Value(s)
		var c officina.Conversation
		run(t, newAgent(t, officinatest.NewModel("scripted", officinatest.TextReply("Hi.")), search), &c, "Hi",
			officina.RunOptions{})
		fingerprints = append(fingerprints, fingerprint(t, &c))
	}

	if slices.Sort(fingerprints); len(slices.Compact(fingerprints)) != len(schemas) {
		t.Errorf("fingerprints = %q, want one per way of writing the schema", fingerprints)
	}
}

func TestRun_APP10_ASessionDotNetSavedMidReplyResumesWithItsPrefixAndInterruptedCallsAnswered(t *testing.T) {
	t.Parallel()
	p := readSharedPrefix(t)
	var c officina.Conversation
	if err := json.Unmarshal([]byte(sharedFile(t, "session", "dotnet-session.json")), &c); err != nil {
		t.Fatalf("unmarshal .NET's session: %v", err)
	}
	stored := c.Messages()
	model := officinatest.NewModel(p.Settings, officinatest.TextReply("The order may not have gone through."))
	agent := p.agent(t, model)

	if !agent.CanContinue(&c) {
		t.Fatal("CanContinue() = false for the session .NET saved with the same prefix")
	}
	result := run(t, agent, &c, "Did the order go through?", officina.RunOptions{})

	if result.Status != officina.Completed {
		t.Fatalf("result = %+v, want Completed", result)
	}
	req := model.Requests()[0]
	// The stored messages, raw blocks included, go out byte for byte: the prefix .NET's last request cached.
	if err := officinatest.CheckPrefix([]officina.Request{{Tools: req.Tools, Instructions: req.Instructions,
		Messages: stored}, req}); err != nil {
		t.Errorf("the resumed request does not continue the stored one: %v", err)
	}
	want := []officina.ToolResult{{CallID: "toolu_02", Content: "The call was interrupted: the application stopped " +
		"before its result was recorded, so it may or may not have taken effect.", IsError: true}}
	if diff := cmp.Diff(want, results(req.Messages[len(stored)])); diff != "" {
		t.Errorf("interrupted results mismatch (-want +got):\n%s", diff)
	}
	if err := officinatest.CheckConversation(req.Messages); err != nil {
		t.Errorf("the request is invalid: %v", err)
	}
}

func TestRun_APP10_ASessionSavedMidReplyIsWrittenAsTheSharedFixtureHoldsIt(t *testing.T) {
	t.Parallel()
	p := readSharedPrefix(t)
	thinking := `{ "type":"thinking",  "thinking":"caf\` + `u00e9 \"quoted\" \/ é", "signature":"c2ln+/=" }`
	model := officinatest.NewModel(p.Settings,
		officinatest.Reply{Events: []officina.ModelEvent{
			officina.BlockReceived{Block: officina.Block{Raw: jsontext.Value(thinking)}},
			officina.BlockReceived{Block: officinatest.TextBlock("Looking up «Café Libro» & <friends>.")},
			officina.BlockReceived{Block: officinatest.ToolUseBlock("go_01", "search", `{"query":"café"}`)},
			officina.Finished{Reason: officina.FinishToolUse},
		}},
		officinatest.TextReply("It is in stock: 3 copies."),
		officinatest.ToolUseReply(officinatest.ToolUseBlock("go_02", "place_order", `{"bookId":7,"quantity":1}`)))
	agent := p.agent(t, model)
	c := officina.Conversation{ID: "go-0001"}
	run(t, agent, &c, "Is Café Libro in stock?",
		officina.RunOptions{Context: "Today is Friday 9 October 2026. The staff member is Zoë."})

	// The application stops while the order's tool runs: what it saved after the reply that asked for it is all
	// there is.
	var saved []byte
	events, _ := agent.Stream(t.Context(), &c, "Order one copy for <Ann & Bob>.", officina.RunOptions{})
	for e := range events {
		if appended, ok := e.(officina.ConversationAppended); ok && appended.Message.Role == officina.Assistant {
			var err error
			if saved, err = json.Marshal(&c); err != nil {
				t.Fatalf("Marshal() error = %v", err)
			}
			break
		}
	}

	if want := strings.TrimRight(sharedFile(t, "session", "go-session.json"), "\r\n"); string(saved) != want {
		t.Errorf("saved session = %s\nwant the shared fixture %s", saved, want)
	}
}

func TestRun_AUD01_InterruptedCallsAreAuditedAndReportedBeforeTheRunGoesOn(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "search", `{}`), officinatest.ToolUseBlock("c2", "search", `{}`)),
		officinatest.TextReply("Sorry, where were we?"))
	sink := &memorySink{}
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools: []officina.Tool{tool("search", "Searches.")}, AuditSink: sink, Name: "bookshop",
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	c := officina.Conversation{ID: "s1"}
	var saved []byte
	events, _ := agent.Stream(t.Context(), &c, "Search twice.", officina.RunOptions{})
	for e := range events {
		if appended, ok := e.(officina.ConversationAppended); ok && appended.Message.Role == officina.Assistant {
			saved, _ = json.Marshal(&c) // A conversation always marshals.
			break
		}
	}
	var resumed officina.Conversation
	if err := json.Unmarshal(saved, &resumed); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}

	got, _ := stream(t.Context(), t, agent, &resumed, "Hello?", officina.RunOptions{},
		func(officina.RunEvent) bool { return true })

	first, ok := got[0].(officina.ConversationAppended)
	if !ok || first.Message.Role != officina.User || len(first.Message.Blocks) != 2 ||
		!first.Message.Blocks[1].ToolResult.IsError || first.Message.Blocks[1].ToolResult.CallID != "c2" {
		t.Errorf("first event = %+v, want the interrupted results of c1 and c2", got[0])
	}
	var interrupted []string
	for _, e := range sink.Entries() {
		if e.Conversation == "s1" && e.Kind == officina.AuditToolEnded && e.Outcome == "interrupted" {
			interrupted = append(interrupted, e.CallID+" "+e.Tool+" "+e.Input)
		}
	}
	if diff := cmp.Diff([]string{"c1 search {}", "c2 search {}"}, interrupted); diff != "" {
		t.Errorf("interrupted audit entries mismatch (-want +got):\n%s", diff)
	}
	if err := officinatest.CheckConversation(resumed.Messages()); err != nil {
		t.Errorf("the resumed conversation is invalid: %v", err)
	}
}

func TestRun_APP10_ACompleteConversationGetsNoInterruptedResults(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("One."), officinatest.TextReply("Two."))
	agent := newAgent(t, model)
	var c officina.Conversation
	run(t, agent, &c, "Hi", officina.RunOptions{})

	got, _ := stream(t.Context(), t, agent, &c, "Again", officina.RunOptions{},
		func(officina.RunEvent) bool { return true })

	if appended, ok := got[0].(officina.ConversationAppended); ok {
		t.Errorf("first event = %+v, want nothing appended before the reply", appended)
	}
	if n := len(c.Messages()); n != 4 {
		t.Errorf("the conversation has %d messages, want 4", n)
	}
}
