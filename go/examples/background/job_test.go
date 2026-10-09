package background_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"github.com/google/go-cmp/cmp"
	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/examples/background"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

// The background agent sample, offline and unattended: the helpdesk is the test kit's fake MCP server over
// Streamable HTTP, the audit trail a JSON-lines file. Only the model, the MCP server and the refund back end are
// replaced.

const token = "hd-token-5e1f"

// priced is a scripted model at Opus 5.5's list price, as the job's cost budget needs a price.
type priced struct {
	*officinatest.Model
}

func (priced) Info() officina.ModelInfo {
	return officina.ModelInfo{Provider: "scripted", Name: "scripted",
		Price: officina.Price{Input: 5, Output: 25, CacheRead: 0.5, CacheWrite: 6.25, CacheWriteHour: 10}}
}

// helpdesk serves a fake helpdesk over HTTP, requiring the token, until the test ends; it returns the server and its
// endpoint.
func helpdesk(t *testing.T) (*officinatest.MCPServer, string) {
	t.Helper()
	id := func(arguments jsontext.Value) string {
		var in struct {
			ID string `json:"id"`
		}
		if err := json.Unmarshal(arguments, &in); err != nil {
			t.Errorf("the helpdesk got arguments %s: %v", arguments, err)
		}
		return in.ID
	}
	server := officinatest.NewMCPServer(
		officinatest.MCPTool{
			Name: "get_ticket", InputSchema: `{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}`,
			Handler: func(arguments jsontext.Value) (string, error) {
				return "Ticket " + id(arguments) + " from Ana: order A-1042 arrived broken, she wants her £25 back.", nil
			},
		},
		officinatest.MCPTool{
			Name:        "add_note",
			InputSchema: `{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id","note"]}`,
			Handler: func(arguments jsontext.Value) (string, error) {
				return "Note added to " + id(arguments) + ".", nil
			},
		})
	server.Token = token
	srv := httptest.NewServer(server)
	t.Cleanup(srv.Close)
	return server, srv.URL + "/mcp"
}

// refunds records the refunds a job issues.
type refunds struct {
	mu     sync.Mutex
	issued []string
}

func (r *refunds) issue(_ context.Context, ticketID string, amount float64) (string, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.issued = append(r.issued, fmt.Sprintf("%s £%g", ticketID, amount))
	return "refunded", nil
}

// start starts a job of model against url, auditing to file, and closes it when the test ends.
func start(t *testing.T, model *officinatest.Model, url string, r *refunds, file string) *background.Job {
	t.Helper()
	job, err := background.Start(t.Context(), priced{model}, url, token, r.issue, file)
	if err != nil {
		t.Fatalf("Start() error = %v", err)
	}
	t.Cleanup(job.Close)
	return job
}

func TestHandle_GEN06_AnUnattendedJobIsDeniedTheRefundEscalatesWithANoteAndRecordsItAllInTheJSONLinesFile(t *testing.T) {
	t.Parallel()
	server, url := helpdesk(t)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "helpdesk__get_ticket", `{"id":"T-7"}`)),
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c2", "issue_refund", `{"ticketId":"T-7","amount":25}`)),
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c3", "helpdesk__add_note",
			`{"id":"T-7","note":"A refund of £25 needs a person's approval; escalated."}`)),
		officinatest.TextReply(`{"resolution":"Escalated","report":"Ana asks for £25 back for a broken order; a `+
			`refund needs a person's approval."}`))
	file := filepath.Join(t.TempDir(), "audit.jsonl")
	var r refunds
	job := start(t, model, url, &r, file)

	res, err := job.Handle(t.Context(), "T-7")

	if err != nil || res.Status != officina.Completed {
		t.Fatalf("Handle() = %v %v %q, %v; want Completed", res.Status, res.Failure, res.Detail, err)
	}
	if outcome, _ := res.Output.(background.Outcome); outcome.Resolution != background.Escalated {
		t.Errorf("the outcome = %+v, want the ticket escalated", res.Output)
	}
	// Nobody could approve the refund, so it never ran, and the model was told why.
	if r.issued != nil {
		t.Errorf("refunds issued: %q, want none", r.issued)
	}
	requests := model.Requests()
	denied := requests[2].Messages[len(requests[2].Messages)-1].Blocks[0].ToolResult
	want := &officina.ToolResult{CallID: "c2", Content: "The call needs approval, and this run is unattended, so it " +
		"was denied.", IsError: true}
	if diff := cmp.Diff(want, denied); diff != "" {
		t.Errorf("the refund's result mismatch (-want +got):\n%s", diff)
	}
	// The helpdesk's tools ran on the server, over HTTP with the token.
	var called []string
	for _, c := range server.Calls() {
		called = append(called, c.Tool)
	}
	if diff := cmp.Diff([]string{"get_ticket", "add_note"}, called); diff != "" {
		t.Errorf("helpdesk calls mismatch (-want +got):\n%s", diff)
	}

	// The JSON-lines file holds the run's record: the server connected, the write audited before it ran, the end.
	data, err := os.ReadFile(file)
	if err != nil {
		t.Fatalf("ReadFile() error = %v", err)
	}
	type step struct {
		Kind         officina.AuditKind `json:"kind"`
		Tool         string             `json:"tool"`
		Outcome      string             `json:"outcome"`
		Conversation string             `json:"conversation"`
	}
	var got []step
	for line := range strings.Lines(string(data)) {
		var s step
		if err := json.Unmarshal([]byte(line), &s); err != nil {
			t.Fatalf("audit line %q: %v", line, err)
		}
		got = append(got, s)
	}
	wantSteps := []step{
		{officina.AuditRunStarted, "", "", "ticket-T-7"},
		{officina.AuditToolSource, "helpdesk", "connected", "ticket-T-7"},
		{officina.AuditToolStarted, "helpdesk__get_ticket", "", "ticket-T-7"},
		{officina.AuditToolEnded, "helpdesk__get_ticket", "ok", "ticket-T-7"},
		{officina.AuditToolEnded, "issue_refund", "error", "ticket-T-7"},
		{officina.AuditToolStarted, "helpdesk__add_note", "", "ticket-T-7"},
		{officina.AuditToolEnded, "helpdesk__add_note", "ok", "ticket-T-7"},
		{officina.AuditRunEnded, "", "Completed", "ticket-T-7"},
	}
	if diff := cmp.Diff(wantSteps, got); diff != "" {
		t.Errorf("audit trail mismatch (-want +got):\n%s", diff)
	}
	if strings.Contains(string(data), token) {
		t.Errorf("the audit trail holds the helpdesk's token:\n%s", data)
	}
	if err := officinatest.CheckPrefix(requests); err != nil {
		t.Errorf("CheckPrefix() = %v", err)
	}
}

func TestHandle_GEN06_BUD01_AJobThatKeepsCallingToolsStopsAtItsBudget(t *testing.T) {
	t.Parallel()
	_, url := helpdesk(t)
	calls := *background.Budget().ModelCalls
	var replies []officinatest.Reply
	for i := range calls {
		replies = append(replies, officinatest.ToolUseReply(
			officinatest.ToolUseBlock(fmt.Sprintf("c%d", i+1), "helpdesk__get_ticket", `{"id":"T-8"}`)))
	}
	model := officinatest.NewModel("scripted", replies...)
	job := start(t, model, url, &refunds{}, filepath.Join(t.TempDir(), "audit.jsonl"))

	res, err := job.Handle(t.Context(), "T-8")

	if err != nil || res.Status != officina.Stopped || res.Stop != officina.Budget ||
		res.Detail != "the model call budget is used up: 6 of 6" {
		t.Errorf("Handle() = %v %v %q, %v; want Stopped by the model call budget", res.Status, res.Stop, res.Detail, err)
	}
	if n := len(model.Requests()); n != calls {
		t.Errorf("%d model calls, want %d", n, calls)
	}
	if err := officinatest.CheckPrefix(model.Requests()); err != nil {
		t.Errorf("CheckPrefix() = %v", err)
	}
}

func TestStart_GEN06_MCP01_AHelpdeskThatRefusesTheTokenFailsTheStart(t *testing.T) {
	t.Parallel()
	server := officinatest.NewMCPServer()
	server.Token = "another"
	srv := httptest.NewServer(server)
	t.Cleanup(srv.Close)
	t.Cleanup(http.DefaultClient.CloseIdleConnections)

	_, err := background.Start(t.Context(), priced{officinatest.NewModel("scripted")}, srv.URL, token,
		(&refunds{}).issue, filepath.Join(t.TempDir(), "audit.jsonl"))

	if err == nil || !strings.Contains(err.Error(), "401") || strings.Contains(err.Error(), token) {
		t.Errorf("Start() error = %v, want the refusal without the token", err)
	}
}
