package officina_test

import (
	"cmp"
	"context"
	"encoding/json/jsontext"
	"errors"
	"fmt"
	"iter"
	"slices"
	"strings"
	"testing"
	"testing/synctest"
	"time"

	gocmp "github.com/google/go-cmp/cmp"
	"github.com/google/go-cmp/cmp/cmpopts"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"
	"go.opentelemetry.io/otel/trace"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// collector is the OpenTelemetry SDK with an in-memory exporter and reader: it collects the spans and metrics of
// the agents given its providers.
type collector struct {
	spans  *tracetest.InMemoryExporter
	reader *sdkmetric.ManualReader
	traces *sdktrace.TracerProvider
	meters *sdkmetric.MeterProvider
}

// tb is what the collector needs of a test: a testing.T's or a rapid.T's.
type tb interface {
	Helper()
	Errorf(format string, args ...any)
	Fatalf(format string, args ...any)
}

// collect returns a collector, which shutdown stops.
func collect() *collector {
	c := &collector{spans: tracetest.NewInMemoryExporter(), reader: sdkmetric.NewManualReader()}
	c.traces = sdktrace.NewTracerProvider(sdktrace.WithSyncer(c.spans))
	c.meters = sdkmetric.NewMeterProvider(sdkmetric.WithReader(c.reader))
	return c
}

func (c *collector) shutdown(t tb) {
	t.Helper()
	if err := errors.Join(c.traces.Shutdown(context.Background()), c.meters.Shutdown(context.Background())); err != nil {
		t.Errorf("Shutdown() error = %v", err)
	}
}

// newCollector returns a collector that stops when t ends.
func newCollector(t *testing.T) *collector {
	t.Helper()
	c := collect()
	t.Cleanup(func() { c.shutdown(t) })
	return c
}

// agent returns an agent of model with opts, whose telemetry goes to c.
func (c *collector) agent(t *testing.T, model officina.Model, opts officina.AgentOptions) *officina.Agent {
	t.Helper()
	opts.TracerProvider, opts.MeterProvider = c.traces, c.meters
	agent, err := officina.NewAgent(model, instructions, opts)
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return agent
}

// span returns the one span named name.
func (c *collector) span(t *testing.T, name string) tracetest.SpanStub {
	t.Helper()
	var found []tracetest.SpanStub
	for _, s := range c.spans.GetSpans() {
		if s.Name == name {
			found = append(found, s)
		}
	}
	if len(found) != 1 {
		t.Fatalf("%d spans named %q, want 1", len(found), name)
	}
	return found[0]
}

// point is one data point of a metric: its attributes besides the agent, provider and model, and its sum and count
// (a counter has no count).
type point struct {
	Metric string
	Attrs  map[string]any
	Sum    float64
	Count  uint64
}

// points returns the data points of the metrics, sorted, and checks that each has the agent's attributes, dims.
func (c *collector) points(t tb, dims map[string]any) []point {
	t.Helper()
	var rm metricdata.ResourceMetrics
	if err := c.reader.Collect(context.Background(), &rm); err != nil {
		t.Fatalf("Collect() error = %v", err)
	}
	var got []point
	add := func(name string, set attribute.Set, sum float64, count uint64) {
		attrs := attrMap(set.ToSlice())
		for k, v := range dims {
			if attrs[k] != v {
				t.Errorf("%s %v: %s = %v, want %v", name, attrs, k, attrs[k], v)
			}
			delete(attrs, k)
		}
		got = append(got, point{name, attrs, sum, count})
	}
	for _, sm := range rm.ScopeMetrics {
		for _, m := range sm.Metrics {
			switch d := m.Data.(type) {
			case metricdata.Histogram[int64]:
				for _, p := range d.DataPoints {
					add(m.Name, p.Attributes, float64(p.Sum), p.Count)
				}
			case metricdata.Histogram[float64]:
				for _, p := range d.DataPoints {
					add(m.Name, p.Attributes, p.Sum, p.Count)
				}
			case metricdata.Sum[int64]:
				for _, p := range d.DataPoints {
					add(m.Name, p.Attributes, float64(p.Value), 0)
				}
			default:
				t.Errorf("metric %s has data of type %T", m.Name, d)
			}
		}
	}
	slices.SortFunc(got, func(a, b point) int {
		return cmp.Or(cmp.Compare(a.Metric, b.Metric), cmp.Compare(fmt.Sprint(a.Attrs), fmt.Sprint(b.Attrs)))
	})
	return got
}

// dump returns everything the spans and metrics carry, as text: names, attributes, events and statuses.
func (c *collector) dump(t tb) string {
	t.Helper()
	var b strings.Builder
	for _, s := range c.spans.GetSpans() {
		fmt.Fprintln(&b, s.Name, s.Status.Description, attrMap(s.Attributes))
		for _, e := range s.Events {
			fmt.Fprintln(&b, e.Name, attrMap(e.Attributes))
		}
	}
	fmt.Fprintln(&b, c.points(t, nil))
	return b.String()
}

func attrMap(kvs []attribute.KeyValue) map[string]any {
	m := map[string]any{}
	for _, kv := range kvs {
		m[string(kv.Key)] = kv.Value.AsInterface()
	}
	return m
}

// pricedModel is a scripted model of another provider, with a price.
type pricedModel struct {
	*officinatest.Model
}

func (pricedModel) Info() officina.ModelInfo {
	return officina.ModelInfo{Provider: "acme", Name: "acme-large", Price: officina.Price{
		Input: 4, Output: 20, CacheRead: 0.2, CacheWrite: 5, CacheWriteHour: 8,
	}}
}

// waitingApprover is a human who takes a while to approve.
type waitingApprover time.Duration

func (w waitingApprover) Approve(context.Context, officina.Tool, officina.ToolCall) (officina.Approval, error) {
	time.Sleep(time.Duration(w))
	return officina.Approval{Approved: true}, nil
}

// approx compares the floats of attributes and points to the precision of their sums.
func approx() gocmp.Option {
	return cmpopts.EquateApprox(0, 1e-12)
}

func TestRun_EVT02_AUD03_ARunIsOneTraceWithASpanPerModelAndToolCallAndItsAuditEntriesPointIntoIt(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		telemetry := newCollector(t)
		sink := &memorySink{}
		order := handlerTool("order", officina.Write, func(context.Context, jsontext.Value) (string, error) { return "ok", nil })
		order.NeedsApproval = true
		model := pricedModel{officinatest.NewModel("scripted",
			officinatest.Reply{Events: []officina.ModelEvent{
				officina.TextDelta{Text: "Looking."}, officina.BlockReceived{Block: officinatest.TextBlock("Looking.")},
				officina.BlockReceived{Block: officinatest.ToolUseBlock("c1", "search", `{"query":"x"}`)},
				officina.BlockReceived{Block: officinatest.ToolUseBlock("c2", "order", `{}`)},
				officina.UsageReceived{Usage: officina.Usage{Input: 100, Output: 20, CacheRead: 300, CacheWrite: 50}},
				officina.Finished{Reason: officina.FinishToolUse},
			}},
			officinatest.Reply{Events: []officina.ModelEvent{
				officina.TextDelta{Text: "Ordered."}, officina.BlockReceived{Block: officinatest.TextBlock("Ordered.")},
				officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 5, CacheRead: 450}},
				officina.Finished{Reason: officina.FinishEnd},
			}})}
		agent := telemetry.agent(t, model, officina.AgentOptions{
			Name: "clerk", AuditSink: sink, Approver: waitingApprover(3 * time.Second),
			Tools: []officina.Tool{tool("search", "Searches."), order},
		})
		c := officina.Conversation{ID: "session-1"}
		started := time.Now()

		run(t, agent, &c, "Order x.", officina.RunOptions{})

		spans := telemetry.spans.GetSpans()
		root := telemetry.span(t, "invoke_agent clerk")
		var children []string
		slices.SortFunc(spans, func(a, b tracetest.SpanStub) int {
			return cmp.Or(a.StartTime.Compare(b.StartTime), cmp.Compare(a.Name, b.Name))
		})
		for _, s := range spans {
			if s.SpanContext.TraceID() != root.SpanContext.TraceID() {
				t.Errorf("span %s is in trace %s, want the run's %s", s.Name, s.SpanContext.TraceID(), root.SpanContext.TraceID())
			}
			if s.Name != root.Name {
				children = append(children, s.Name)
				if s.Parent.SpanID() != root.SpanContext.SpanID() {
					t.Errorf("span %s has parent %s, want the run's span", s.Name, s.Parent.SpanID())
				}
			}
		}
		if root.Parent.IsValid() {
			t.Errorf("the run's span has parent %s, want none", root.Parent.SpanID())
		}
		wantChildren := []string{"chat acme-large", "execute_tool order", "execute_tool search", "chat acme-large"}
		if diff := gocmp.Diff(wantChildren, children); diff != "" {
			t.Errorf("child spans mismatch (-want +got):\n%s", diff)
		}
		if !root.StartTime.Equal(started) || root.EndTime.Sub(root.StartTime) != 3*time.Second {
			t.Errorf("run span from %v to %v, want 3s from %v", root.StartTime, root.EndTime, started)
		}

		wantRun := map[string]any{
			"gen_ai.operation.name": "invoke_agent", "gen_ai.agent.name": "clerk", "gen_ai.conversation.id": "session-1",
			"gen_ai.provider.name": "acme", "gen_ai.request.model": "acme-large", "officina.run.id": sink.Entries()[0].Run,
			"officina.run.result": "completed", "gen_ai.usage.input_tokens": int64(910), "gen_ai.usage.output_tokens": int64(25),
			"gen_ai.usage.cache_read.input_tokens": int64(750), "gen_ai.usage.cache_creation.input_tokens": int64(50),
			"officina.usage.cost": 0.00134, "officina.run.model_calls": int64(2), "officina.run.tool_calls": int64(2),
		}
		if diff := gocmp.Diff(wantRun, attrMap(root.Attributes), approx()); diff != "" {
			t.Errorf("run span attributes mismatch (-want +got):\n%s", diff)
		}

		var firstCall tracetest.SpanStub
		for _, s := range spans {
			if s.Name == "chat acme-large" {
				firstCall = s
				break
			}
		}
		wantCall := map[string]any{
			"gen_ai.operation.name": "chat", "gen_ai.provider.name": "acme", "gen_ai.request.model": "acme-large",
			"gen_ai.agent.name": "clerk", "gen_ai.usage.input_tokens": int64(450), "gen_ai.usage.output_tokens": int64(20),
			"gen_ai.usage.cache_read.input_tokens": int64(300), "gen_ai.usage.cache_creation.input_tokens": int64(50),
			"officina.usage.cost": 0.00111, "officina.model.retries": int64(0),
			"gen_ai.response.finish_reasons": []string{"tool_use"}, "officina.model.time_to_first_token": 0.0,
		}
		if diff := gocmp.Diff(wantCall, attrMap(firstCall.Attributes), approx()); diff != "" {
			t.Errorf("model call span attributes mismatch (-want +got):\n%s", diff)
		}
		if firstCall.SpanKind != trace.SpanKindClient {
			t.Errorf("model call span kind = %v, want client", firstCall.SpanKind)
		}

		orderSpan := telemetry.span(t, "execute_tool order")
		wantOrder := map[string]any{
			"gen_ai.operation.name": "execute_tool", "gen_ai.tool.name": "order", "gen_ai.tool.call.id": "c2",
			"gen_ai.tool.type": "function", "officina.tool.source": "application", "officina.tool.kind": "write",
			"officina.tool.approval": "approved", "officina.tool.approval_wait": 3.0, "officina.tool.outcome": "ok",
			"officina.tool.ran": 0.0, "officina.tool.truncated": false, "officina.tool.result_length": int64(2),
		}
		if diff := gocmp.Diff(wantOrder, attrMap(orderSpan.Attributes)); diff != "" {
			t.Errorf("tool call span attributes mismatch (-want +got):\n%s", diff)
		}
		if orderSpan.EndTime.Sub(orderSpan.StartTime) != 3*time.Second || orderSpan.Status.Code != codes.Unset {
			t.Errorf("order span took %v with status %v, want 3s and unset", orderSpan.EndTime.Sub(orderSpan.StartTime),
				orderSpan.Status.Code)
		}
		if _, asked := attrMap(telemetry.span(t, "execute_tool search").Attributes)["officina.tool.approval"]; asked {
			t.Error("the search span has an approval, want none: it needs none")
		}

		// Each entry carries the trace, and the span of the step it records.
		search := telemetry.span(t, "execute_tool search").SpanContext.SpanID()
		order2 := orderSpan.SpanContext.SpanID()
		type step struct {
			Kind officina.AuditKind
			Span trace.SpanID
		}
		want := []step{
			{officina.AuditRunStarted, root.SpanContext.SpanID()}, {officina.AuditToolStarted, search},
			{officina.AuditToolEnded, search}, {officina.AuditApprovalAsked, order2},
			{officina.AuditApprovalAnswered, order2}, {officina.AuditToolStarted, order2}, {officina.AuditToolEnded, order2},
			{officina.AuditRunEnded, root.SpanContext.SpanID()},
		}
		var got []step
		for _, e := range sink.Entries() {
			if e.TraceID != root.SpanContext.TraceID().String() {
				t.Errorf("%s entry has trace %q, want the run's %s", e.Kind, e.TraceID, root.SpanContext.TraceID())
			}
			id, err := trace.SpanIDFromHex(e.SpanID)
			if err != nil {
				t.Errorf("%s entry has span %q: %v", e.Kind, e.SpanID, err)
			}
			got = append(got, step{e.Kind, id})
		}
		if diff := gocmp.Diff(want, got); diff != "" {
			t.Errorf("entries' spans mismatch (-want +got):\n%s", diff)
		}
		if end := sink.Entries()[len(sink.Entries())-1]; end.Cost < 0.00134-1e-12 || end.Cost > 0.00134+1e-12 {
			t.Errorf("the end entry's cost = %v, want 0.00134", end.Cost)
		}
	})
}

func TestRun_EVT02_CTX05_MetricsCountTokensCostCacheHitRatioToolOutcomesApprovalsAndResults(t *testing.T) {
	t.Parallel()
	telemetry := newCollector(t)
	save := handlerTool("save", officina.Write, func(context.Context, jsontext.Value) (string, error) { return "saved", nil })
	save.NeedsApproval = true
	model := pricedModel{officinatest.NewModel("scripted",
		officinatest.Reply{Events: []officina.ModelEvent{
			officina.BlockReceived{Block: officinatest.ToolUseBlock("c1", "save", `{}`)},
			officina.UsageReceived{Usage: officina.Usage{Input: 100, Output: 20, CacheRead: 300}},
			officina.Finished{Reason: officina.FinishToolUse},
		}},
		officinatest.Reply{Events: []officina.ModelEvent{
			officina.BlockReceived{Block: officinatest.TextBlock("Not saved.")},
			officina.UsageReceived{Usage: officina.Usage{Output: 5, CacheRead: 400, CacheWrite: 10, CacheWriteHour: 10}},
			officina.Finished{Reason: officina.FinishEnd},
		}})}
	agent := telemetry.agent(t, model, officina.AgentOptions{
		Name: "clerk", Tools: []officina.Tool{save},
		Approver: officinatest.NewApprover(officina.Approval{Reason: "no"}),
	})

	run(t, agent, nil, "Save.", officina.RunOptions{})

	got := telemetry.points(t, map[string]any{
		"gen_ai.agent.name": "clerk", "gen_ai.provider.name": "acme", "gen_ai.request.model": "acme-large",
	})
	// Durations are left out: the run takes no time to speak of.
	durations := map[string]bool{"gen_ai.client.operation.duration": true, "officina.tool.duration": true}
	for i := range got {
		if durations[got[i].Metric] {
			got[i].Sum = 0
		}
	}
	want := []point{
		{"gen_ai.client.operation.duration", map[string]any{"gen_ai.operation.name": "chat"}, 0, 2},
		{"gen_ai.client.token.usage", map[string]any{"gen_ai.operation.name": "chat", "gen_ai.token.type": "input"}, 810, 2},
		{"gen_ai.client.token.usage", map[string]any{"gen_ai.operation.name": "chat", "gen_ai.token.type": "output"}, 25, 2},
		{"officina.model.cache_hit_ratio", map[string]any{}, 0.75 + 400.0/410, 2},
		{"officina.model.cache_tokens", map[string]any{"officina.cache.type": "read"}, 700, 2},
		{"officina.model.cache_tokens", map[string]any{"officina.cache.type": "write"}, 10, 2},
		{"officina.model.cost", map[string]any{}, 0.00086 + 0.00026, 2},
		{"officina.runs", map[string]any{"officina.run.result": "completed"}, 1, 0},
		{"officina.tool.approvals", map[string]any{"gen_ai.tool.name": "save", "officina.tool.approval": "denied"}, 1, 0},
		{"officina.tool.calls", map[string]any{"gen_ai.tool.name": "save", "officina.tool.outcome": "error"}, 1, 0},
		{"officina.tool.duration", map[string]any{"gen_ai.tool.name": "save", "officina.tool.outcome": "error"}, 0, 1},
	}
	if diff := gocmp.Diff(want, got, approx()); diff != "" {
		t.Errorf("metrics mismatch (-want +got):\n%s", diff)
	}
	save2 := telemetry.span(t, "execute_tool save")
	if typ := attrMap(save2.Attributes)["error.type"]; typ != "tool_error" || save2.Status.Code != codes.Error {
		t.Errorf("denied call's span: error.type %v, status %v; want tool_error, error", typ, save2.Status.Code)
	}
}

func TestTelemetry_EVT02_InstrumentsHaveTheNamesAndUnitsOfDotNets(t *testing.T) {
	t.Parallel()
	telemetry := newCollector(t)
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
		officinatest.ToolUseBlock("c1", "search", `{}`)), officinatest.Reply{Events: []officina.ModelEvent{
		officina.UsageReceived{Usage: officina.Usage{Input: 1}}, officina.Retried{}, officina.TextDelta{Text: "Hi"},
		officina.BlockReceived{Block: officinatest.TextBlock("Hi")}, officina.UsageReceived{Usage: officina.Usage{Input: 1}},
		officina.Finished{Reason: officina.FinishEnd},
	}})
	agent := telemetry.agent(t, model, officina.AgentOptions{
		Tools: []officina.Tool{tool("search", "")}, AuditSink: &memorySink{fail: func(officina.AuditEntry) bool { return true }},
	})
	approve := handlerTool("approve", officina.Read, ok)
	approve.NeedsApproval = true
	approving := telemetry.agent(t, officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "approve", `{}`)), officinatest.TextReply("Done.")),
		officina.AgentOptions{Tools: []officina.Tool{approve}, Approver: officinatest.NewApprover(officina.Approval{Approved: true})})

	run(t, agent, nil, "Hi", officina.RunOptions{})
	run(t, approving, nil, "Hi", officina.RunOptions{})

	var rm metricdata.ResourceMetrics
	if err := telemetry.reader.Collect(context.Background(), &rm); err != nil {
		t.Fatalf("Collect() error = %v", err)
	}
	got := map[string]string{}
	for _, sm := range rm.ScopeMetrics {
		if sm.Scope.Name != "Sleepyshark.Officina" {
			t.Errorf("scope %q, want .NET's Sleepyshark.Officina", sm.Scope.Name)
		}
		for _, m := range sm.Metrics {
			got[m.Name] = m.Unit
		}
	}
	want := map[string]string{
		"gen_ai.client.token.usage": "{token}", "officina.model.cache_tokens": "{token}",
		"gen_ai.client.operation.duration": "s", "officina.model.cache_hit_ratio": "1", "officina.model.cost": "{USD}",
		"officina.model.retries": "{retry}", "officina.tool.duration": "s", "officina.tool.calls": "{call}",
		"officina.tool.approvals": "{approval}", "officina.runs": "{run}", "officina.audit.failures": "{entry}",
	}
	if diff := gocmp.Diff(want, got); diff != "" {
		t.Errorf("instruments mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_EVT02_ARetryIsCountedAndTheFailedAttemptsTokensAreKept(t *testing.T) {
	t.Parallel()
	telemetry := newCollector(t)
	reply := officinatest.TextReply("Hi.")
	reply.Events = slices.Concat([]officina.ModelEvent{
		officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 1}}, officina.Retried{},
		officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 3}},
	}, reply.Events)
	agent := telemetry.agent(t, officinatest.NewModel("scripted", reply), officina.AgentOptions{})

	result := run(t, agent, nil, "Hi", officina.RunOptions{})

	if want := (officina.Usage{Input: 20, Output: 4}); result.Usage != want {
		t.Errorf("usage = %+v, want %+v", result.Usage, want)
	}
	attrs := attrMap(telemetry.span(t, "chat scripted").Attributes)
	if got := []any{attrs["officina.model.retries"], attrs["gen_ai.usage.input_tokens"], attrs["gen_ai.usage.output_tokens"]}; !slices.Equal(got, []any{int64(1), int64(20), int64(4)}) {
		t.Errorf("retries, input and output tokens = %v, want [1 20 4]", got)
	}
	for _, p := range telemetry.points(t, nil) {
		if p.Metric == "officina.model.retries" && p.Sum != 1 {
			t.Errorf("retries counted %v, want 1", p.Sum)
		}
	}
}

func TestRun_EVT02_TimeToFirstTokenIsTheWaitForTheFirstTextOfTheAttemptThatSucceeded(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		telemetry := newCollector(t)
		model := funcModel(func(context.Context, officina.Request) iter.Seq2[officina.ModelEvent, error] {
			return func(yield func(officina.ModelEvent, error) bool) {
				time.Sleep(time.Second)
				for _, e := range []officina.ModelEvent{officina.TextDelta{Text: "Hel"}, officina.Retried{}} {
					if !yield(e, nil) {
						return
					}
				}
				time.Sleep(2 * time.Second)
				for _, e := range []officina.ModelEvent{officina.TextDelta{Text: "Hel"}, nil, officina.TextDelta{Text: "lo."},
					officina.BlockReceived{Block: officinatest.TextBlock("Hello.")}, officina.Finished{Reason: officina.FinishEnd}} {
					if e == nil {
						time.Sleep(time.Second)
						continue
					}
					if !yield(e, nil) {
						return
					}
				}
			}
		})

		run(t, telemetry.agent(t, model, officina.AgentOptions{}), nil, "Hi", officina.RunOptions{})

		if got := attrMap(telemetry.span(t, "chat func").Attributes)["officina.model.time_to_first_token"]; got != 3.0 {
			t.Errorf("time to first token = %v, want 3s: from the call's start to the retry's first text", got)
		}
	})
}

func TestRun_EVT02_EVT03_AFailedModelCallMarksItsSpanAndTheRunsWithoutTheSecret(t *testing.T) {
	t.Parallel()
	telemetry := newCollector(t)
	model := officinatest.NewModel("scripted", officinatest.Reply{
		Events: []officina.ModelEvent{officina.TextDelta{Text: "Hel"}},
		Err:    errors.New("upstream refused key s3cret"),
	})
	agent := telemetry.agent(t, model, officina.AgentOptions{Secrets: []string{"s3cret"}})

	result := run(t, agent, nil, "Hi", officina.RunOptions{})

	if result.Detail != "upstream refused key [redacted]" {
		t.Errorf("detail = %q, want the error redacted", result.Detail)
	}
	for name, errorType := range map[string]string{"invoke_agent": "ModelError", "chat scripted": "model_error"} {
		s := telemetry.span(t, name)
		got := []any{s.Status.Code, s.Status.Description, attrMap(s.Attributes)["error.type"]}
		if want := []any{codes.Error, "upstream refused key [redacted]", errorType}; !slices.Equal(got, want) {
			t.Errorf("span %s: status, description and error type = %v, want %v", name, got, want)
		}
	}
	for _, p := range telemetry.points(t, nil) {
		if p.Metric == "gen_ai.client.operation.duration" && p.Attrs["error.type"] != "model_error" {
			t.Errorf("the failed call's duration has error type %v, want model_error", p.Attrs["error.type"])
		}
	}
	runs := telemetry.span(t, "invoke_agent")
	if got := attrMap(runs.Attributes); got["officina.run.result"] != "failed" || got["officina.run.reason"] != "ModelError" {
		t.Errorf("run result and reason = %v, %v; want failed, ModelError", got["officina.run.result"], got["officina.run.reason"])
	}
}

func TestRun_AUD06_AnAuditSinkFailureAndAWriteItBlocksShowInTelemetry(t *testing.T) {
	t.Parallel()
	telemetry := newCollector(t)
	save := handlerTool("save", officina.Write, func(context.Context, jsontext.Value) (string, error) {
		return "", errors.New("must not run")
	})
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "save", `{}`)),
		officinatest.TextReply("Not saved."))
	agent := telemetry.agent(t, model, officina.AgentOptions{Tools: []officina.Tool{save}, AuditSink: &memorySink{
		fail: func(e officina.AuditEntry) bool {
			return e.Kind == officina.AuditToolStarted || e.Kind == officina.AuditRunStarted
		},
	}})

	run(t, agent, nil, "Save.", officina.RunOptions{})

	events := func(s tracetest.SpanStub) []string {
		var got []string
		for _, e := range s.Events {
			got = append(got, e.Name+" "+fmt.Sprint(attrMap(e.Attributes)))
		}
		return got
	}
	saveSpan := telemetry.span(t, "execute_tool save")
	attrs := attrMap(saveSpan.Attributes)
	if got := []any{attrs["officina.tool.outcome"], attrs["error.type"], saveSpan.Status.Code}; !slices.Equal(got,
		[]any{"blocked", "audit_unavailable", codes.Error}) {
		t.Errorf("save span outcome, error type and status = %v, want blocked, audit_unavailable, error", got)
	}
	if _, ran := attrs["officina.tool.ran"]; ran {
		t.Error("the blocked call's span says it ran")
	}
	if diff := gocmp.Diff([]string{"officina.audit.failed map[officina.audit.kind:ToolStarted]"}, events(saveSpan)); diff != "" {
		t.Errorf("save span events mismatch (-want +got):\n%s", diff)
	}
	if diff := gocmp.Diff([]string{"officina.audit.failed map[officina.audit.kind:RunStarted]"},
		events(telemetry.span(t, "invoke_agent"))); diff != "" {
		t.Errorf("run span events mismatch (-want +got):\n%s", diff)
	}
	var failures, outcomes []string
	for _, p := range telemetry.points(t, nil) {
		switch p.Metric {
		case "officina.audit.failures":
			failures = append(failures, fmt.Sprint(p.Attrs["officina.audit.kind"], " ", p.Sum))
		case "officina.tool.calls":
			outcomes = append(outcomes, fmt.Sprint(p.Attrs["officina.tool.outcome"]))
		}
	}
	if diff := gocmp.Diff([]string{"RunStarted 1", "ToolStarted 1"}, failures); diff != "" {
		t.Errorf("audit failures mismatch (-want +got):\n%s", diff)
	}
	if diff := gocmp.Diff([]string{"blocked"}, outcomes); diff != "" {
		t.Errorf("tool outcomes mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_EVT03_EVT04_TelemetryCarriesNoTextUnlessTheHostOptsInAndNeverASecret(t *testing.T) {
	t.Parallel()
	for _, optIn := range []bool{false, true} {
		t.Run(fmt.Sprint("opt in ", optIn), func(t *testing.T) {
			t.Parallel()
			telemetry := newCollector(t)
			model := officinatest.NewModel("scripted",
				officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "search", `{"query":"input-text hunter2"}`)),
				officinatest.TextReply("answer-text hunter2"))
			search := handlerTool("search", officina.Read, func(context.Context, jsontext.Value) (string, error) {
				return "result-text hunter2", nil
			})
			agent := telemetry.agent(t, model, officina.AgentOptions{
				Tools: []officina.Tool{search}, Secrets: []string{"hunter2"}, TelemetryContent: optIn,
			})

			run(t, agent, nil, "question-text hunter2", officina.RunOptions{})

			dump := telemetry.dump(t)
			if strings.Contains(dump, "hunter2") {
				t.Errorf("telemetry holds the secret:\n%s", dump)
			}
			for _, text := range []string{"question-text", "input-text", "result-text", "answer-text"} {
				if strings.Contains(dump, text) != optIn {
					t.Errorf("telemetry holds %q: %v, want %v\n%s", text, !optIn, optIn, dump)
				}
			}
			if optIn {
				got := attrMap(telemetry.span(t, "invoke_agent").Attributes)["gen_ai.input.messages"]
				if want := `[{"role":"user","parts":[{"type":"text","content":"question-text [redacted]"}]}]`; got != want {
					t.Errorf("input messages = %v, want %s", got, want)
				}
			}
		})
	}
}

func TestRun_EVT02_ARunsSpanIsUnderTheHostsSpanAndWithoutProvidersEntriesCarryTheHostsTrace(t *testing.T) {
	t.Parallel()
	telemetry := newCollector(t)
	ctx, host := telemetry.traces.Tracer("host").Start(t.Context(), "reply")
	sink := &memorySink{}
	traced := telemetry.agent(t, officinatest.NewModel("scripted", officinatest.TextReply("Hi.")), officina.AgentOptions{})
	untraced, err := officina.NewAgent(officinatest.NewModel("scripted", officinatest.TextReply("Hi.")), instructions,
		officina.AgentOptions{AuditSink: sink})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	if _, err := traced.Run(ctx, nil, "Hi", officina.RunOptions{}); err != nil {
		t.Fatalf("Run() error = %v", err)
	}
	if _, err := untraced.Run(ctx, nil, "Hi", officina.RunOptions{}); err != nil {
		t.Fatalf("Run() error = %v", err)
	}
	host.End()

	if got := telemetry.span(t, "invoke_agent").Parent.SpanID(); got != host.SpanContext().SpanID() {
		t.Errorf("run span's parent = %s, want the host's span %s", got, host.SpanContext().SpanID())
	}
	if len(telemetry.spans.GetSpans()) != 3 {
		t.Errorf("%d spans, want 3: the host's and the traced run's, and none of the untraced run", len(telemetry.spans.GetSpans()))
	}
	for _, e := range sink.Entries() {
		if e.TraceID != host.SpanContext().TraceID().String() {
			t.Errorf("%s entry has trace %q, want the host's", e.Kind, e.TraceID)
		}
	}
}

func TestRun_EVT02_ACallCancelledWhileWaitingForApprovalRecordsTheWaitOnly(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		telemetry := newCollector(t)
		save := handlerTool("save", officina.Write, ok)
		save.NeedsApproval = true
		model := officinatest.NewModel("scripted", officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "save", `{}`)))
		agent := telemetry.agent(t, model, officina.AgentOptions{Tools: []officina.Tool{save}, Approver: blockingApprover{}})
		ctx, cancel := context.WithCancel(t.Context())
		defer cancel()

		events, result := agent.Stream(ctx, nil, "Save.", officina.RunOptions{})
		for e := range events {
			if _, asked := e.(officina.ApprovalAsked); asked {
				time.AfterFunc(2*time.Second, cancel)
			}
		}
		if _, err := result(); err != nil {
			t.Fatalf("result() error = %v", err)
		}

		attrs := attrMap(telemetry.span(t, "execute_tool save").Attributes)
		if attrs["officina.tool.approval_wait"] != 2.0 {
			t.Errorf("approval wait = %v, want 2s", attrs["officina.tool.approval_wait"])
		}
		if answer, found := attrs["officina.tool.approval"]; found {
			t.Errorf("approval = %v, want none: the approver never answered", answer)
		}
		for _, p := range telemetry.points(t, nil) {
			if p.Metric == "officina.tool.approvals" {
				t.Errorf("an approval was counted: %v", p)
			}
		}
	})
}

// blockingApprover answers only when the run is cancelled.
type blockingApprover struct{}

func (blockingApprover) Approve(ctx context.Context, _ officina.Tool, _ officina.ToolCall) (officina.Approval, error) {
	<-ctx.Done()
	return officina.Approval{}, ctx.Err()
}
