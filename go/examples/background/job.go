package background

import (
	"context"
	"fmt"
	"net/http"
	"time"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/mcp"
)

// Job handles support tickets, each in one unattended run: without an approver, calls needing approval are denied
// and the model is told. Each run is stateless, on a conversation named after its ticket; it needs no memory, as each
// job starts afresh. Make it with Start, and Close it when done.
type Job struct {
	agent    *officina.Agent
	helpdesk *mcp.Source
}

// Outcome is what a job reports about its ticket: its typed output.
type Outcome struct {
	Resolution Resolution `json:"resolution" enum:"Answered,Escalated" jsonschema:"Answered when the note answers the customer; escalated when a person must act."`
	Report     string     `json:"report" jsonschema:"One sentence for the support team on what was done and why."`
}

// Resolution is how a job left its ticket.
type Resolution string

// The resolutions of a ticket.
const (
	Answered  Resolution = "Answered"
	Escalated Resolution = "Escalated"
)

// Budget returns what one job may spend.
func Budget() officina.Limits {
	return officina.Limits{Cost: new(0.20), Tokens: new(int64(100_000)), ModelCalls: new(6), Time: new(2 * time.Minute)}
}

// Refund issues a refund of amount pounds for a ticket, and returns a receipt.
type Refund func(ctx context.Context, ticketID string, amount float64) (string, error)

// refundInput is the refund tool's input.
type refundInput struct {
	TicketID string  `json:"ticketId" jsonschema:"The ticket's id."`
	Amount   float64 `json:"amount" jsonschema:"The amount, in pounds."`
}

// Start connects to the helpdesk's MCP server at helpdeskURL with token, and returns the job of model, which must
// have a price for the job's cost budget. refund issues refunds; the audit trail is appended to auditFile.
func Start(ctx context.Context, model officina.Model, helpdeskURL, token string, refund Refund, auditFile string,
) (*Job, error) {
	server := mcp.Server{Name: "helpdesk", URL: helpdeskURL, Header: http.Header{"Authorization": {"Bearer " + token}}}
	// The host decides what each of the server's tools may do, rather than trusting its annotations.
	helpdesk, err := mcp.Connect(ctx, server, []mcp.AllowedTool{
		{Name: "get_ticket", Kind: officina.Read}, {Name: "add_note", Kind: officina.Write},
	})
	if err != nil {
		return nil, fmt.Errorf("start the ticket job: %w", err)
	}
	agent, err := newAgent(model, helpdesk.Tools(), refund, auditFile, server.Secrets())
	if err != nil {
		// Nothing else holds the connection yet, so it is closed here.
		helpdesk.Close()
		return nil, fmt.Errorf("start the ticket job: %w", err)
	}
	return &Job{agent: agent, helpdesk: helpdesk}, nil
}

// newAgent returns the job's agent: model with the helpdesk's tools and the refund tool, the Outcome as its output,
// and the JSON-lines audit sink.
func newAgent(model officina.Model, helpdesk []officina.Tool, refund Refund, auditFile string, secrets []string,
) (*officina.Agent, error) {
	refundTool, err := officina.NewTool("issue_refund", "Refunds a customer for a ticket. A person must approve each "+
		"refund.", officina.Write, func(ctx context.Context, in refundInput) (string, error) {
		return refund(ctx, in.TicketID, in.Amount)
	})
	if err != nil {
		return nil, fmt.Errorf("refund tool: %w", err)
	}
	refundTool.NeedsApproval = true
	output, err := officina.NewOutput[Outcome]()
	if err != nil {
		return nil, fmt.Errorf("outcome: %w", err)
	}
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools: append(helpdesk, refundTool), Output: output, Name: "ticket-job",
		AuditSink: officina.NewJSONLinesSink(auditFile), Secrets: secrets,
	})
	if err != nil {
		return nil, fmt.Errorf("agent: %w", err)
	}
	return agent, nil
}

// Handle handles one ticket. The result says how the run ended and, when it completed, holds the Outcome; the error
// is the API misused, such as a blank ticket id.
func (j *Job) Handle(ctx context.Context, ticketID string) (officina.Result, error) {
	res, err := j.agent.Run(ctx, &officina.Conversation{ID: "ticket-" + ticketID}, "Handle ticket "+ticketID+".",
		officina.RunOptions{Budget: Budget()})
	if err != nil {
		return res, fmt.Errorf("handle ticket %s: %w", ticketID, err)
	}
	return res, nil
}

// Close closes the connection to the helpdesk.
func (j *Job) Close() {
	j.helpdesk.Close()
}

const instructions = `You work through an online shop's support tickets on your own, with nobody to ask. The user message names one
ticket. Read it with the helpdesk tools, do what you can, and leave a note on the ticket for the customer or
the support team. A refund needs a person's approval: if it is denied, escalate the ticket instead, and say so
in the note. Reply with the outcome only.`
