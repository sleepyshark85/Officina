package background_test

import (
	"context"
	"encoding/json/jsontext"
	"fmt"
	"net/http/httptest"
	"os"
	"path/filepath"

	"github.com/sleepyshark85/officina/go/examples/background"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// One ticket handled offline: the test kit's fake MCP server is the helpdesk, over Streamable HTTP, and the scripted
// model, priced for the job's budget, is Claude. The job answers the ticket with a note, and its audit trail is a
// JSON-lines file.
func ExampleJob_Handle() {
	ctx := context.Background()
	helpdesk := officinatest.NewMCPServer(
		officinatest.MCPTool{Name: "get_ticket", Handler: func(jsontext.Value) (string, error) {
			return "Ticket T-9 from Ben: how do I reset my password?", nil
		}},
		officinatest.MCPTool{Name: "add_note", Handler: func(jsontext.Value) (string, error) { return "Noted.", nil }},
	)
	helpdesk.Token = "hd-token"
	srv := httptest.NewServer(helpdesk)
	defer srv.Close()
	dir, err := os.MkdirTemp("", "ticket-job")
	if err != nil {
		fmt.Println(err)
		return
	}
	defer func() { _ = os.RemoveAll(dir) }() // A temporary folder; what is left of it does no harm.

	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "helpdesk__get_ticket", `{"text":"T-9"}`)),
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c2", "helpdesk__add_note",
			`{"text":"Use 'Forgot password' on the sign-in page."}`)),
		officinatest.TextReply(`{"resolution":"Answered","report":"Told Ben how to reset his password."}`))
	refund := func(context.Context, string, float64) (string, error) { return "refunded", nil }
	job, err := background.Start(ctx, priced{model}, srv.URL+"/mcp", "hd-token", refund,
		filepath.Join(dir, "audit.jsonl"))
	if err != nil {
		fmt.Println(err)
		return
	}
	defer job.Close()

	res, err := job.Handle(ctx, "T-9")
	if err != nil {
		fmt.Println(err)
		return
	}
	fmt.Println(res.Status, res.Output.(background.Outcome).Resolution)
	fmt.Println(helpdesk.Calls())
	// Output:
	// Completed Answered
	// [{get_ticket {"text":"T-9"}} {add_note {"text":"Use 'Forgot password' on the sign-in page."}}]
}
