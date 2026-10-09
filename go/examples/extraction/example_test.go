package extraction_test

import (
	"context"
	"fmt"

	"github.com/sleepyshark85/officina/go/examples/extraction"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// A customer message triaged offline, the scripted model in place of Claude (claude.New in a real host).
func ExampleClassify() {
	model := officinatest.NewModel("scripted", officinatest.TextReply(`{"category":"Delivery","urgency":"High",`+
		`"orderNumber":"A-1042","summary":"The parcel for order A-1042 has not arrived."}`))
	agent, err := extraction.NewAgent(model)
	if err != nil {
		fmt.Println(err)
		return
	}

	triage, ok, err := extraction.Classify(context.Background(), agent,
		"My order A-1042 still hasn't arrived and I need it tomorrow!")
	if err != nil || !ok {
		fmt.Println("not classified", err)
		return
	}
	fmt.Println(triage.Category, triage.Urgency, *triage.OrderNumber)
	fmt.Println(triage.Summary)
	// Output:
	// Delivery High A-1042
	// The parcel for order A-1042 has not arrived.
}
