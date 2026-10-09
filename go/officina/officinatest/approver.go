package officinatest

import (
	"context"
	"fmt"
	"slices"
	"sync"

	"github.com/sleepyshark85/officina/go/officina"
)

// Approver is the human of a test: it answers approval requests with answers scripted in advance, in order, and
// records each call it is asked about. Many runs may use it at once.
type Approver struct {
	mu      sync.Mutex
	answers []officina.Approval
	asked   []officina.ToolCall
}

// NewApprover returns an approver that gives the answers, one per request.
func NewApprover(answers ...officina.Approval) *Approver {
	return &Approver{answers: answers}
}

// Approve records call and returns the next answer, or an error when none is left.
func (a *Approver) Approve(_ context.Context, _ officina.Tool, call officina.ToolCall) (officina.Approval, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.asked = append(a.asked, call)
	if len(a.answers) == 0 {
		return officina.Approval{}, fmt.Errorf("scripted approver: request %d has no answer left", len(a.asked))
	}
	answer := a.answers[0]
	a.answers = a.answers[1:]
	return answer, nil
}

// Asked returns the calls asked about so far, in order.
func (a *Approver) Asked() []officina.ToolCall {
	a.mu.Lock()
	defer a.mu.Unlock()
	return slices.Clone(a.asked)
}
