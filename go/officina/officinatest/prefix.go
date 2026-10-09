package officinatest

import (
	"bytes"
	"errors"
	"fmt"
	"slices"

	"github.com/sleepyshark85/officina/go/officina"
)

// CheckPrefix checks that each request's tools, instructions and earlier messages are byte-identical to the
// previous request's. Pass the requests of a scripted run, or of several runs, saves and resumes, in the order they
// were sent. It returns nil when the prefix is stable, or an error with one line per difference.
func CheckPrefix(requests []officina.Request) error {
	var problems []error
	for i := 1; i < len(requests); i++ {
		previous, next := requests[i-1], requests[i]
		say := func(what string) {
			problems = append(problems, fmt.Errorf("request %d: %s from request %d's", i+1, what, i))
		}
		if !slices.EqualFunc(previous.Tools, next.Tools, sameTool) {
			say("the tools differ")
		}
		if previous.Instructions != next.Instructions {
			say("the instructions differ")
		}
		if len(next.Messages) < len(previous.Messages) {
			say(fmt.Sprintf("has %d messages, fewer than the %d", len(next.Messages), len(previous.Messages)))
			continue
		}
		for j, m := range previous.Messages {
			if !sameMessage(m, next.Messages[j]) {
				say(fmt.Sprintf("message %d differs", j+1))
				break
			}
		}
	}
	return errors.Join(problems...)
}

func sameTool(a, b officina.Tool) bool {
	return a.Name == b.Name && a.Description == b.Description && bytes.Equal(a.InputSchema, b.InputSchema)
}

func sameMessage(a, b officina.Message) bool {
	return a.Role == b.Role && slices.EqualFunc(a.Blocks, b.Blocks, func(x, y officina.Block) bool {
		return x.Text == y.Text && bytes.Equal(x.Raw, y.Raw)
	})
}
