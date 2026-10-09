package bookshop

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"math"
	"strconv"
	"strings"
	"time"

	"github.com/sleepyshark85/officina/go/officina"
)

// Budgets are the console's cost limits, in US dollars: per reply, and per session over all its replies.
type Budgets struct {
	Reply, Session float64
}

// The budgets a zero Budgets stands for.
const (
	defaultReplyBudget   = 0.50
	defaultSessionBudget = 5
)

// sessionsListed is how many sessions /sessions lists.
const sessionsListed = 20

// summariesPerListing is how many sessions left without a summary one listing summarizes; later listings do the rest.
const summariesPerListing = 3

// session is the session in use: its conversation, who started it, what its replies used, the run context it last
// received, and the text it was last stored as, empty before its first save.
type session struct {
	conversation *officina.Conversation
	staffMember  string
	usage        officina.Usage
	cost         float64
	context      string
	saved        string
	// cleared is how many tool calls the last clearing line named, so a clearing the provider repeats on every
	// later call is shown once.
	cleared int
	// changed says the conversation grew since this console took it up, so leaving it needs a new summary.
	changed bool
}

// newSession returns a new session of staffMember, with an id short enough to type in /resume: 12 random hex
// digits, as the .NET implementation makes them. One whose id is taken fails its first save.
func newSession(staffMember string) *session {
	id := make([]byte, 6)
	_, _ = rand.Read(id) // It never fails: it crashes the program instead.
	return &session{conversation: &officina.Conversation{ID: hex.EncodeToString(id)}, staffMember: staffMember}
}

// resume returns the session to go on with after /resume id: the stored session id, leaving current; or current
// itself when the stored one is missing, cannot be read, or was started by another agent, which would fail its next
// reply with a prefix mismatch. It tells the staff member which. Resuming the session in use reloads it without
// leaving it, and keeps whether it changed.
func (c *console) resume(ctx context.Context, agent *officina.Agent, id string, current *session) *session {
	if id == "" {
		c.writeLine("Which session? Type /resume <id>; /sessions lists them.")
		return current
	}
	stored, err := c.sessions.Load(ctx, id)
	switch {
	case errors.Is(err, ErrNoSession):
		c.writeLine("There is no session " + id + ". Type /sessions to list them.")
		return current
	case err != nil:
		c.writeLine("The session could not be read: " + err.Error())
		return current
	case !agent.CanContinue(stored.Conversation):
		c.writeLine("Session " + id + " was started with another version of the assistant, so it cannot go on. " +
			"Type /new to start a new session.")
		return current
	}
	messages := stored.Conversation.Messages()
	c.writeLine(fmt.Sprintf("Resumed session %s: %d messages, $%.4f so far.", id, len(messages), stored.Cost))
	s := &session{conversation: stored.Conversation, staffMember: stored.StaffMember, usage: stored.Usage,
		cost: stored.Cost, saved: stored.Saved}
	for _, m := range messages {
		if m.Role == officina.Operator {
			s.context = m.Text()
		}
	}
	if id == current.conversation.ID {
		s.changed = current.changed
	} else {
		c.leave(ctx, current)
	}
	return s
}

// leave summarizes s as it is left, unless nothing was said since this console took it up.
func (c *console) leave(ctx context.Context, s *session) {
	if c.summarizer == nil || !s.changed {
		return
	}
	if sum, ok := c.summarize(ctx, s.conversation); ok {
		c.writeLine("Session " + s.conversation.ID + " summarized: " + sum.Title)
	}
}

// summarize summarizes a session's conversation and stores the summary, adding its cost to the session's. It
// returns the summary, or says why there is none; a summary that could not be stored is still returned.
func (c *console) summarize(ctx context.Context, conversation *officina.Conversation) (summary, bool) {
	// A summary is written as the session is left, also when the console's input has ended.
	ctx = context.WithoutCancel(ctx)
	id := conversation.ID
	budget := summaryBudget
	res, err := c.summarizer.Run(ctx, nil, transcript(conversation), officina.RunOptions{
		Budget: officina.Limits{Cost: &budget},
	})
	sum, ok := res.Output.(summary)
	if !ok {
		var reason string
		switch {
		case err != nil:
			reason = err.Error()
		case res.Status == officina.Failed:
			reason = res.Detail
		default:
			reason = "stopped: " + res.Stop.String()
		}
		c.unsummarized[id] = true
		c.logger.WarnContext(ctx, "session not summarized", "session", id, "error", reason)
		c.writeLine("[Session " + id + " could not be summarized: " + reason + "]")
		return summary{}, false
	}
	if err := c.sessions.saveSummary(ctx, id, sum, res.Usage, res.Cost); err != nil {
		c.logger.WarnContext(ctx, "summary not saved", "session", id, "error", err)
		c.writeLine("[The summary of session " + id + " could not be saved: " + err.Error() + "]")
	}
	return sum, true
}

// listSessions shows the latest sessions, most recent first, marking current, after summarizing a few of those
// left without a summary (never current).
func (c *console) listSessions(ctx context.Context, current *session) {
	listed, err := c.sessions.list(ctx, sessionsListed)
	switch {
	case err != nil:
		c.writeLine("The sessions could not be read: " + err.Error())
		return
	case len(listed) == 0:
		c.writeLine("No sessions yet.")
		return
	}
	c.summarizeLeft(ctx, listed, current)
	c.writeLine("Sessions, most recent first:")
	for _, l := range listed {
		mark, title := " ", l.title
		if l.id == current.conversation.ID {
			mark = "*"
		}
		if title == "" {
			title = "(no title yet)"
		}
		c.writeLine(fmt.Sprintf("%s %s  %s  %s  %s  $%.4f", mark, l.id, l.updated.In(time.Local).Format("Mon 2 Jan 15:04"),
			l.staffMember, title, l.cost))
		if l.summary != "" {
			c.writeLine("    " + l.summary)
		}
		if len(l.changes) > 0 {
			c.writeLine("    Changes: " + strings.Join(l.changes, "; "))
		}
	}
}

// summarizeLeft summarizes up to summariesPerListing of the listed sessions left without a summary, never current
// nor one whose summary failed in this console, and puts each summary in its listing.
func (c *console) summarizeLeft(ctx context.Context, listed []listing, current *session) {
	if c.summarizer == nil {
		return
	}
	var stale []int
	for i, l := range listed {
		if l.stale && l.id != current.conversation.ID && !c.unsummarized[l.id] {
			stale = append(stale, i)
		}
	}
	switch summarizing := min(len(stale), summariesPerListing); {
	case summarizing < len(stale):
		c.writeLine(fmt.Sprintf("Summarizing %d of %d sessions left without a summary; /sessions again does more…",
			summarizing, len(stale)))
	case summarizing == 1:
		c.writeLine("Summarizing 1 session left without a summary…")
	case summarizing > 1:
		c.writeLine(fmt.Sprintf("Summarizing %d sessions left without a summary…", summarizing))
	}
	for _, i := range stale[:min(len(stale), summariesPerListing)] {
		l := &listed[i]
		stored, err := c.sessions.Load(ctx, l.id)
		switch {
		case errors.Is(err, ErrNoSession):
			continue
		case err != nil:
			c.unsummarized[l.id] = true
			c.writeLine("[" + err.Error() + "]")
			continue
		}
		if sum, ok := c.summarize(ctx, stored.Conversation); ok {
			l.title, l.summary, l.changes = sum.Title, sum.Summary, sum.Changes
		}
	}
}

// save saves s with its totals so far and reports whether it was saved. A failure is told once per reply (told)
// and the reply goes on: the next save stores everything. A reply cancelled midway still saves its last steps.
func (c *console) save(ctx context.Context, s *session, usage officina.Usage, cost float64, told bool) bool {
	ctx = context.WithoutCancel(ctx)
	saved, err := c.sessions.Save(ctx, s.conversation, s.staffMember, usage, cost, s.saved)
	if err == nil {
		s.saved = saved
		return true
	}
	c.logger.WarnContext(ctx, "session not saved", "session", s.conversation.ID, "error", err)
	switch {
	case told:
	case errors.Is(err, ErrSessionChanged):
		c.writeLine("[The session could not be saved: " + err.Error() + ". Type /resume " + s.conversation.ID +
			" to go on from what was saved.]")
	default:
		c.writeLine("[The session could not be saved: " + err.Error() + "]")
	}
	return false
}

// tokens returns usage as the status line and /cost show it: input, with the share read from the cache, and output.
func tokens(u officina.Usage) string {
	input := u.Input + u.CacheRead + u.CacheWrite
	share := 0.0
	if input > 0 {
		share = float64(u.CacheRead) / float64(input)
	}
	return fmt.Sprintf("tokens: %s in (%d%% from cache), %s out", thousands(input), int(math.Round(share*100)),
		thousands(u.Output))
}

// plus returns the tokens of u and v together.
func plus(u, v officina.Usage) officina.Usage {
	return officina.Usage{
		Input: u.Input + v.Input, Output: u.Output + v.Output, CacheRead: u.CacheRead + v.CacheRead,
		CacheWrite: u.CacheWrite + v.CacheWrite, CacheWriteHour: u.CacheWriteHour + v.CacheWriteHour,
	}
}

// dollars returns an amount of US dollars with two to four decimals.
func dollars(v float64) string {
	s := strconv.FormatFloat(v, 'f', 4, 64)
	for strings.HasSuffix(s, "0") && len(s)-strings.IndexByte(s, '.') > 3 {
		s = s[:len(s)-1]
	}
	return s
}
