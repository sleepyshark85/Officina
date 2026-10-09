package bookshop

import (
	"context"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgtype"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/sleepyshark85/officina/go/officina"
)

// auditTable is the application's audit sink: each entry is a row of the audit table, the same table and columns as
// the .NET implementation's, written before Write returns. It also reads a session's entries back for /audit: a
// session's id is its conversation's.
type auditTable struct {
	db *pgxpool.Pool
}

const (
	insertAudit = `insert into audit (time, sequence, run, conversation, agent, memory_scope, trace_id, span_id, kind,
	tool, call_id, input, outcome, detail, duration, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
	cost)
values ($1, $2, $3, $4, $5, null, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)`

	selectAudit = `select time, sequence, run, agent, coalesce(trace_id, ''), coalesce(span_id, ''), kind,
	coalesce(tool, ''), coalesce(call_id, ''), coalesce(input, ''), coalesce(outcome, ''), coalesce(detail, ''),
	duration, coalesce(input_tokens, 0), coalesce(output_tokens, 0), coalesce(cache_read_tokens, 0),
	coalesce(cache_write_tokens, 0), coalesce(cost, 0)::float8
from audit
where conversation = $1
order by id`
)

// Write inserts e as a row. Text an entry does not have is null; tokens and cost are only a run's end's.
func (a *auditTable) Write(ctx context.Context, e officina.AuditEntry) error {
	var (
		duration pgtype.Interval
		tokens   [4]*int64
		cost     *float64
	)
	if e.Duration > 0 {
		duration = pgtype.Interval{Microseconds: e.Duration.Microseconds(), Valid: true}
	}
	if e.Kind == officina.AuditRunEnded {
		tokens = [4]*int64{&e.Usage.Input, &e.Usage.Output, &e.Usage.CacheRead, &e.Usage.CacheWrite}
		cost = &e.Cost
	}
	_, err := a.db.Exec(ctx, insertAudit, e.Time, e.Sequence, e.Run, e.Conversation, e.Agent, null(e.TraceID),
		null(e.SpanID), string(e.Kind), null(e.Tool), null(e.CallID), null(e.Input), null(e.Outcome), null(e.Detail),
		duration, tokens[0], tokens[1], tokens[2], tokens[3], cost)
	if err != nil {
		return fmt.Errorf("write the audit entry: %w", err)
	}
	return nil
}

// null returns s, or nil for the empty string, which the table keeps as null.
func null(s string) *string {
	if s == "" {
		return nil
	}
	return &s
}

// entries returns the entries of conversation, in the order they were written.
func (a *auditTable) entries(ctx context.Context, conversation string) ([]officina.AuditEntry, error) {
	rows, err := a.db.Query(ctx, selectAudit, conversation)
	if err != nil {
		return nil, fmt.Errorf("read the audit trail: %w", err)
	}
	defer rows.Close()
	var entries []officina.AuditEntry
	for rows.Next() {
		e := officina.AuditEntry{Conversation: conversation}
		var (
			kind     string
			duration pgtype.Interval
		)
		if err := rows.Scan(&e.Time, &e.Sequence, &e.Run, &e.Agent, &e.TraceID, &e.SpanID, &kind, &e.Tool, &e.CallID,
			&e.Input, &e.Outcome, &e.Detail, &duration, &e.Usage.Input, &e.Usage.Output, &e.Usage.CacheRead,
			&e.Usage.CacheWrite, &e.Cost); err != nil {
			return nil, fmt.Errorf("read the audit trail: %w", err)
		}
		e.Kind, e.Duration = officina.AuditKind(kind), time.Duration(duration.Microseconds)*time.Microsecond
		entries = append(entries, e)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("read the audit trail: %w", err)
	}
	return entries, nil
}

// formatAudit returns what /audit shows of session's entries: grouped by run, each run with a link to its trace on
// the dashboard, each entry with its time in loc, kind, tool and outcome, and a run's end with its tokens and cost.
func formatAudit(session string, entries []officina.AuditEntry, dashboard string, loc *time.Location) string {
	if len(entries) == 0 {
		return "No audit entries for session " + session + "."
	}
	var runs []string
	byRun := map[string][]officina.AuditEntry{}
	for _, e := range entries {
		if byRun[e.Run] == nil {
			runs = append(runs, e.Run)
		}
		byRun[e.Run] = append(byRun[e.Run], e)
	}
	var b strings.Builder
	b.WriteString("Audit of session " + session + ":")
	for i, run := range runs {
		link := "none recorded"
		for _, e := range byRun[run] {
			if e.TraceID != "" {
				link = strings.TrimRight(dashboard, "/") + "/traces/detail/" + e.TraceID
				break
			}
		}
		fmt.Fprintf(&b, "\nRun %d, trace: %s", i+1, link)
		for _, e := range byRun[run] {
			line := fmt.Sprintf("  %s  %-16s  %-20s  %s", e.Time.In(loc).Format(time.TimeOnly), e.Kind, e.Tool, outcome(e))
			b.WriteString("\n" + strings.TrimRight(line, " "))
		}
	}
	return b.String()
}

// outcome returns how an entry's step went, as /audit shows it.
func outcome(e officina.AuditEntry) string {
	switch {
	case e.Kind == officina.AuditRunEnded:
		u := e.Usage
		return fmt.Sprintf("%s  tokens: %s in (%s cached), %s out, $%.4f", e.Outcome,
			thousands(u.Input+u.CacheRead+u.CacheWrite), thousands(u.CacheRead), thousands(u.Output), e.Cost)
	case e.Kind == officina.AuditApprovalAnswered && e.Detail != "":
		return e.Outcome + ": " + e.Detail
	case e.Kind == officina.AuditToolEnded && e.Duration > 0:
		return e.Outcome + "  " + thousands(e.Duration.Milliseconds()) + " ms"
	default:
		return e.Outcome
	}
}

// thousands returns n, which is not negative, with its thousands separated by commas.
func thousands(n int64) string {
	s := strconv.FormatInt(n, 10)
	for i := len(s) - 3; i > 0; i -= 3 {
		s = s[:i] + "," + s[i:]
	}
	return s
}
