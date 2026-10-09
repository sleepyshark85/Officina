package extraction_test

import (
	"bytes"
	"testing"

	"github.com/google/go-cmp/cmp"
	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/examples/extraction"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func TestClassify_GEN06_AMessageIsClassifiedIntoTypedOutputInOneStatelessCallWithoutTools(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		officinatest.TextReply(`{"category":"Delivery","urgency":"High","orderNumber":"A-1042",`+
			`"summary":"The parcel for order A-1042 has not arrived."}`),
		officinatest.TextReply(`{"category":"Account","urgency":"Low","orderNumber":null,`+
			`"summary":"The customer wants to change their email address."}`))
	agent, err := extraction.NewAgent(model)
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	var got []extraction.Triage
	for _, message := range []string{"My order A-1042 still hasn't arrived and I need it tomorrow!",
		"How do I change my email address?"} {
		triage, ok, err := extraction.Classify(t.Context(), agent, message)
		if err != nil || !ok {
			t.Fatalf("Classify(%q) = %v, %v; want a triage", message, ok, err)
		}
		got = append(got, triage)
	}

	want := []extraction.Triage{
		{Category: extraction.Delivery, Urgency: extraction.High, OrderNumber: new("A-1042"),
			Summary: "The parcel for order A-1042 has not arrived."},
		{Category: extraction.Account, Urgency: extraction.Low,
			Summary: "The customer wants to change their email address."},
	}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("triages mismatch (-want +got):\n%s", diff)
	}
	// Each message is a run of its own: one request, with no tools, the output schema and a new conversation.
	requests := model.Requests()
	if len(requests) != 2 {
		t.Fatalf("%d requests, want 2", len(requests))
	}
	for i, r := range requests {
		if len(r.Tools) != 0 || len(r.Messages) != 1 || r.Messages[0].Role != officina.User ||
			!bytes.Contains(r.OutputSchema, []byte(`"enum":["Billing","Delivery","Product","Account","Other"]`)) {
			t.Errorf("request %d = %d tools, messages %v, output schema %s; want no tools, one user message and "+
				"the triage schema", i+1, len(r.Tools), r.Messages, r.OutputSchema)
		}
	}
}

func TestClassify_GEN06_TEST02_EveryClassificationSendsTheSamePrefix(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		officinatest.TextReply(`{"category":"Billing","urgency":"Normal","orderNumber":null,`+
			`"summary":"A question about an invoice."}`),
		officinatest.TextReply(`{"category":"Product","urgency":"Low","orderNumber":null,`+
			`"summary":"A question about a size."}`))

	// An agent built afresh for each message, as in a new process, sends the same tools, instructions and schema.
	for _, message := range []string{"Why was I charged twice?", "Does this shirt run small?"} {
		agent, err := extraction.NewAgent(model)
		if err != nil {
			t.Fatalf("NewAgent() error = %v", err)
		}
		if _, ok, err := extraction.Classify(t.Context(), agent, message); err != nil || !ok {
			t.Fatalf("Classify(%q) = %v, %v; want a triage", message, ok, err)
		}
	}

	// A stateless run's conversation is new each time, so its prefix is everything before the messages.
	requests := model.Requests()
	for i := range requests {
		requests[i].Messages = nil
	}
	if err := officinatest.CheckPrefix(requests); err != nil {
		t.Errorf("CheckPrefix() = %v", err)
	}
}

func TestClassify_GEN06_OUT02_OutputThatIsNotATriageFailsTheRunAndClassifiesNothing(t *testing.T) {
	t.Parallel()
	const shipping = `{"category":"Shipping","urgency":"High","orderNumber":null,"summary":"Late."}`
	model := officinatest.NewModel("scripted", officinatest.TextReply(shipping), officinatest.TextReply(shipping))
	agent, err := extraction.NewAgent(model)
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	if triage, ok, err := extraction.Classify(t.Context(), agent, "Where is my parcel?"); err != nil || ok {
		t.Errorf("Classify() = %+v, %v, %v; want no triage", triage, ok, err)
	}
	res, err := agent.Run(t.Context(), nil, "Where is my parcel?", officina.RunOptions{})
	if err != nil || res.Status != officina.Failed || res.Failure != officina.InvalidOutput {
		t.Errorf("Run() = %v %v, %v; want Failed with InvalidOutput", res.Status, res.Failure, err)
	}
}
