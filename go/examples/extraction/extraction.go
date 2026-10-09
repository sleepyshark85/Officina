package extraction

import (
	"context"
	"fmt"

	"github.com/sleepyshark85/officina/go/officina"
)

// Triage is what the triage agent extracts from a customer message: its typed output.
type Triage struct {
	Category Category `json:"category" enum:"Billing,Delivery,Product,Account,Other" jsonschema:"What the message is about."`
	Urgency  Urgency  `json:"urgency" enum:"Low,Normal,High" jsonschema:"High when the customer is blocked or has been charged wrongly; low for a question that can wait."`
	// OrderNumber is nil when the message gives none.
	OrderNumber *string `json:"orderNumber" jsonschema:"The order number the message gives, such as A-1042, or null when it gives none."`
	Summary     string  `json:"summary" jsonschema:"One sentence on what the customer wants."`
}

// Category is what a customer message is about.
type Category string

// The categories of a customer message.
const (
	Billing  Category = "Billing"
	Delivery Category = "Delivery"
	Product  Category = "Product"
	Account  Category = "Account"
	Other    Category = "Other"
)

// Urgency is how soon a customer message needs an answer.
type Urgency string

// The urgencies of a customer message.
const (
	Low    Urgency = "Low"
	Normal Urgency = "Normal"
	High   Urgency = "High"
)

// NewAgent returns the triage agent of model: no tools, no approver, no memory, and Triage as its output.
func NewAgent(model officina.Model) (*officina.Agent, error) {
	output, err := officina.NewOutput[Triage]()
	if err != nil {
		return nil, fmt.Errorf("triage agent: %w", err)
	}
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{Name: "triage", Output: output})
	if err != nil {
		return nil, fmt.Errorf("triage agent: %w", err)
	}
	return agent, nil
}

// Classify triages message with agent, made by NewAgent, on a conversation of its own. ok is false when the run did
// not complete, as on a refusal or output that is not a Triage; the error is the run's API misused, such as a blank
// message.
func Classify(ctx context.Context, agent *officina.Agent, message string) (t Triage, ok bool, err error) {
	res, err := agent.Run(ctx, nil, message, officina.RunOptions{})
	if err != nil {
		return Triage{}, false, fmt.Errorf("classify: %w", err)
	}
	t, ok = res.Output.(Triage)
	return t, ok, nil
}

const instructions = `You triage messages that customers send to an online shop's support team. The user message is one customer
message. Classify what it is about and how urgent it is, copy the order number it gives (if any) exactly as
written, and summarize in one sentence what the customer wants. Do not answer the customer.`
