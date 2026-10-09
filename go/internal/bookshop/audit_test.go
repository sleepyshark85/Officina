package bookshop_test

import (
	"context"
	"log/slog"
	"regexp"
	"slices"
	"sync"
	"testing"

	"github.com/google/go-cmp/cmp"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
	"go.opentelemetry.io/otel/trace"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// logs is a log handler that keeps each record's message and the trace of its context.
type logs struct {
	mu      sync.Mutex
	records []string
}

func (*logs) Enabled(context.Context, slog.Level) bool { return true }

func (l *logs) Handle(ctx context.Context, r slog.Record) error {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.records = append(l.records, r.Message+" "+trace.SpanContextFromContext(ctx).TraceID().String())
	return nil
}

func (l *logs) WithAttrs([]slog.Attr) slog.Handler { return l }

func (l *logs) WithGroup(string) slog.Handler { return l }

func TestConsole_APP16_APP20_AuditShowsTheSessionsEntriesByRunEachLinkedToItsTraceWhichTheLogsJoin(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	spans := tracetest.NewInMemoryExporter()
	traces := sdktrace.NewTracerProvider(sdktrace.WithSyncer(spans))
	t.Cleanup(func() {
		if err := traces.Shutdown(context.Background()); err != nil {
			t.Errorf("Shutdown() error = %v", err)
		}
	})
	logged := &logs{}
	tools := sayThenCall("Let me check, then restock.",
		officinatest.ToolUseBlock("c1", "get_book", `{"bookId":320}`),
		officinatest.ToolUseBlock("c2", "restock_book", `{"bookId":320,"quantity":2}`))
	tools.Events = slices.Insert(tools.Events, len(tools.Events)-1, officina.ModelEvent(officina.UsageReceived{
		Usage: officina.Usage{Input: 1000, Output: 20, CacheRead: 500},
	}))
	model := officinatest.NewModel("scripted", tools, officinatest.TextReply("Done."), officinatest.TextReply("Hello."))

	transcript := sessionOf(t, bookshop.Config{
		TracerProvider: traces, Logger: slog.New(logged), Dashboard: "http://dashboard.test/",
	}, d, model, "", "Sam", "Restock book 320 with 2.", "y", "Hi", "/audit", "/audit no-such-session", "/quit")

	var replies, replySpans, runParents []string
	for _, s := range spans.GetSpans() {
		switch s.Name {
		case "reply":
			replies = append(replies, s.SpanContext.TraceID().String())
			replySpans = append(replySpans, s.SpanContext.SpanID().String())
			if s.Parent.IsValid() {
				t.Errorf("a reply span has parent %s, want none", s.Parent.SpanID())
			}
		case "invoke_agent bookshop":
			runParents = append(runParents, s.Parent.SpanID().String())
		}
	}
	if len(replies) != 2 {
		t.Fatalf("%d reply spans, want 2", len(replies))
	}
	// A run's span ends before its reply's, so the runs come first, in order.
	if diff := cmp.Diff(replySpans, runParents); diff != "" {
		t.Errorf("the runs' parents mismatch the replies (-want +got):\n%s", diff)
	}
	session := regexp.MustCompile(`Audit of session ([0-9a-f]{12}):`).FindStringSubmatch(transcript)
	if session == nil {
		t.Fatalf("transcript lacks the audit of the session:\n%s", transcript)
	}
	at := `\n  \d\d:\d\d:\d\d  `
	want := regexp.MustCompile(`you> /audit\nAudit of session ` + session[1] + `:\n` +
		`Run 1, trace: http://dashboard\.test/traces/detail/` + replies[0] +
		at + `RunStarted` +
		at + `ToolStarted       get_book` +
		at + `ToolEnded         get_book              ok  \d+ ms` +
		at + `ApprovalAsked     restock_book` +
		at + `ApprovalAnswered  restock_book          approved` +
		at + `ToolStarted       restock_book` +
		at + `ToolEnded         restock_book          ok  \d+ ms` +
		at + `RunEnded                                Completed  tokens: 1,500 in \(500 cached\), 20 out, \$0\.0045` +
		`\nRun 2, trace: http://dashboard\.test/traces/detail/` + replies[1] +
		at + `RunStarted` +
		at + `RunEnded                                Completed  tokens: 0 in \(0 cached\), 0 out, \$0\.0000` +
		`\nyou> /audit no-such-session\nNo audit entries for session no-such-session\.\n`)
	if !want.MatchString(transcript) {
		t.Errorf("transcript lacks the audit trail %s\ntranscript:\n%s", want, transcript)
	}

	if rows := scalar[int64](t, d, "select count(*) from audit where conversation = $1 and trace_id is not null "+
		"and span_id is not null", session[1]); rows != 10 {
		t.Errorf("%d rows of the session with a trace and span, want 10", rows)
	}
	if diff := cmp.Diff([]string{"reply ended " + replies[0], "reply ended " + replies[1]}, logged.records); diff != "" {
		t.Errorf("logs mismatch (-want +got):\n%s", diff)
	}
}

func TestConsole_APP16_AnAuditTrailThatCannotBeReadIsSaidSoAndTheSessionGoesOn(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)

	transcript := session(t, d, officinatest.NewModel("scripted"), "", "Sam", func() { d.takeDown(t) }, "/audit",
		func() { d.bringBack(t) }, "/audit", "/quit")

	inOrder(t, transcript, "you> /audit\nThe audit trail could not be read: ", "you> /audit\nNo audit entries for session ",
		"you> /quit\n")
}
