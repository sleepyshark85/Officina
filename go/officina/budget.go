package officina

import (
	"fmt"
	"math"
	"strconv"
	"time"
)

// Limits are a run's budget. Each limit is optional, nil for none, and checked before every model call; a limit
// already used up, zero included, stops the run before its next call. Each call's output limit is lowered to what
// the remaining cost and tokens allow, so a call overshoots by at most its input (about twice that when it compacts,
// as compaction reads the prompt again). A call in flight is never stopped.
type Limits struct {
	// Cost is the most the run may spend, in US dollars at the model's price; it needs a model with a price.
	Cost *float64
	// Tokens is the most tokens the run may use: input, output, cache reads and writes.
	Tokens     *int64
	ModelCalls *int
	// Time is the longest the run may take; a call that starts in time may end after it.
	Time *time.Duration
}

// spending is what a run has used so far, against its budget.
type spending struct {
	budget                Limits
	price                 Price
	started               time.Time
	usage                 Usage
	cost                  float64
	modelCalls, toolCalls int
}

// add counts a model call that used u.
func (s *spending) add(u Usage) {
	s.usage = s.usage.plus(u)
	s.cost += s.price.cost(u)
	s.modelCalls++
}

// report returns res with what the run used.
func (s *spending) report(res Result) Result {
	res.Usage, res.Cost, res.ModelCalls, res.ToolCalls = s.usage, s.cost, s.modelCalls, s.toolCalls
	res.Duration = time.Since(s.started)
	return res
}

// outputLimit returns the most output tokens the next call may use, and false when the budget does not limit them.
func (s *spending) outputLimit() (int64, bool) {
	limit, limited := int64(math.MaxInt64), false
	if s.budget.Tokens != nil {
		limit, limited = *s.budget.Tokens-s.usage.total(), true
	}
	if s.budget.Cost != nil && s.price.Output > 0 {
		limited = true
		if affordable := math.Floor((*s.budget.Cost - s.cost) * 1e6 / s.price.Output); affordable < float64(limit) {
			limit = int64(affordable)
		}
	}
	return max(limit, 0), limited
}

// reached says which limit is used up, so the run may not call the model again; empty when none is.
func (s *spending) reached() string {
	b := s.budget
	switch elapsed := time.Since(s.started); {
	case b.ModelCalls != nil && s.modelCalls >= *b.ModelCalls:
		return fmt.Sprintf("the model call budget is used up: %d of %d", s.modelCalls, *b.ModelCalls)
	case b.Time != nil && elapsed >= *b.Time:
		return fmt.Sprintf("the time budget is used up: %s s of %s s", decimals(elapsed.Seconds(), 1),
			decimals(b.Time.Seconds(), 1))
	case b.Tokens != nil && s.usage.total() >= *b.Tokens:
		return fmt.Sprintf("the token budget is used up: %s of %s tokens", thousands(s.usage.total()),
			thousands(*b.Tokens))
	}
	if limit, _ := s.outputLimit(); b.Cost != nil && (s.cost >= *b.Cost || limit < 1) {
		return fmt.Sprintf("the cost budget is used up: $%s of $%s", decimals(s.cost, 6), decimals(*b.Cost, 6))
	}
	return ""
}

// decimals returns v with at most n decimals, n at least 1, without trailing zeros.
func decimals(v float64, n int) string {
	s := strconv.FormatFloat(v, 'f', n, 64)
	for s[len(s)-1] == '0' {
		s = s[:len(s)-1]
	}
	if s[len(s)-1] == '.' {
		s = s[:len(s)-1]
	}
	return s
}

// thousands returns n, which is not negative, with its thousands separated by commas.
func thousands(n int64) string {
	s := strconv.FormatInt(n, 10)
	for i := len(s) - 3; i > 0; i -= 3 {
		s = s[:i] + "," + s[i:]
	}
	return s
}
