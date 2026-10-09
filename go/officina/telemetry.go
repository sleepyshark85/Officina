package officina

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/metric"
	metricnoop "go.opentelemetry.io/otel/metric/noop"
	"go.opentelemetry.io/otel/trace"
	tracenoop "go.opentelemetry.io/otel/trace/noop"
)

// scope names the core's tracer and meter, as the .NET implementation names its activity source and meter, so one
// dashboard reads both.
const scope = "Sleepyshark.Officina"

// telemetry is an agent's traces and metrics: a span per run, with a span per model call and per tool call under
// it. Names follow the OpenTelemetry generative-AI conventions where they exist, else officina.*, the same as the
// .NET implementation's. Text appears only when the host opts in, and never a secret.
type telemetry struct {
	tracer  trace.Tracer
	agent   string
	model   ModelInfo
	content bool
	redact  func(string) string
	// dims are the attributes of every measurement: the agent, provider and model.
	dims []attribute.KeyValue

	tokens, cacheTokens                                metric.Int64Histogram
	modelDuration, cacheHitRatio, cost, toolDuration   metric.Float64Histogram
	retries, toolCalls, approvals, runs, auditFailures metric.Int64Counter
	compactions, clearings                             metric.Int64Counter
}

// newTelemetry returns the telemetry of an agent named agent, of model, through the host's providers; a nil one
// emits nothing. With content, spans carry message text and tool inputs and results, redacted.
func newTelemetry(tp trace.TracerProvider, mp metric.MeterProvider, agent string, model ModelInfo, content bool,
	redact func(string) string,
) (*telemetry, error) {
	if tp == nil {
		tp = tracenoop.NewTracerProvider()
	}
	if mp == nil {
		mp = metricnoop.NewMeterProvider()
	}
	t := &telemetry{
		tracer: tp.Tracer(scope), agent: agent, model: model, content: content, redact: redact,
		dims: []attribute.KeyValue{
			attribute.String("gen_ai.agent.name", agent), attribute.String("gen_ai.provider.name", model.Provider),
			attribute.String("gen_ai.request.model", model.Name),
		},
	}
	m := mp.Meter(scope)
	var errs [13]error
	t.tokens, errs[0] = m.Int64Histogram("gen_ai.client.token.usage", metric.WithUnit("{token}"),
		metric.WithDescription("Tokens per model call, by type: input (all of it, cached or not) and output."))
	t.cacheTokens, errs[1] = m.Int64Histogram("officina.model.cache_tokens", metric.WithUnit("{token}"),
		metric.WithDescription("Input tokens per model call read from or written to the cache, by officina.cache.type: "+
			"read or write. Part of the input tokens."))
	t.modelDuration, errs[2] = m.Float64Histogram("gen_ai.client.operation.duration", metric.WithUnit("s"),
		metric.WithDescription("Duration of a model call, retries included."))
	t.cacheHitRatio, errs[3] = m.Float64Histogram("officina.model.cache_hit_ratio", metric.WithUnit("1"),
		metric.WithDescription("The share of a model call's input tokens read from the cache."))
	t.cost, errs[4] = m.Float64Histogram("officina.model.cost", metric.WithUnit("{USD}"),
		metric.WithDescription("What a model call cost, in US dollars, at the model's price."))
	t.retries, errs[5] = m.Int64Counter("officina.model.retries", metric.WithUnit("{retry}"),
		metric.WithDescription("Model call retries."))
	t.toolDuration, errs[6] = m.Float64Histogram("officina.tool.duration", metric.WithUnit("s"),
		metric.WithDescription("Duration of a tool call, from its start to its result, approval included."))
	t.toolCalls, errs[7] = m.Int64Counter("officina.tool.calls", metric.WithUnit("{call}"),
		metric.WithDescription("Tool calls, by outcome."))
	t.approvals, errs[8] = m.Int64Counter("officina.tool.approvals", metric.WithUnit("{approval}"),
		metric.WithDescription("Approvals, by answer."))
	t.runs, errs[9] = m.Int64Counter("officina.runs", metric.WithUnit("{run}"),
		metric.WithDescription("Runs, by result."))
	t.auditFailures, errs[10] = m.Int64Counter("officina.audit.failures", metric.WithUnit("{entry}"),
		metric.WithDescription("Audit entries the sink failed to write."))
	t.compactions, errs[11] = m.Int64Counter("officina.model.compactions", metric.WithUnit("{compaction}"),
		metric.WithDescription("Compactions of the conversation by the provider."))
	t.clearings, errs[12] = m.Int64Counter("officina.model.clearings", metric.WithUnit("{clearing}"),
		metric.WithDescription("Model calls for which the provider cleared old tool results."))
	if err := errors.Join(errs[:]...); err != nil {
		return nil, fmt.Errorf("create the metrics: %w", err)
	}
	return t, nil
}

// startRun starts a run's span, under the host's span in ctx if there is one, and returns ctx with it.
func (t *telemetry) startRun(
	ctx context.Context, conversation, message, memoryScope string,
) (context.Context, trace.Span) {
	name := "invoke_agent"
	if t.agent != "" {
		name += " " + t.agent
	}
	attrs := []attribute.KeyValue{
		attribute.String("gen_ai.operation.name", "invoke_agent"), t.dims[0],
		attribute.String("gen_ai.conversation.id", conversation), t.dims[1], t.dims[2],
	}
	if memoryScope != "" {
		attrs = append(attrs, attribute.String("officina.memory.scope", memoryScope))
	}
	return t.tracer.Start(ctx, name, trace.WithAttributes(t.withContent(attrs, "gen_ai.input.messages",
		func() string { return messages("user", message) })...))
}

// endRun counts the run and ends its span with its result, whose text is already redacted.
func (t *telemetry) endRun(ctx context.Context, span trace.Span, res Result) {
	reason := ""
	switch res.Status {
	case Stopped:
		reason = res.Stop.String()
	case Failed:
		reason = res.Failure.String()
	}
	attrs := []attribute.KeyValue{attribute.String("officina.run.result", strings.ToLower(res.Status.String()))}
	if reason != "" {
		attrs = append(attrs, attribute.String("officina.run.reason", reason))
	}
	t.runs.Add(ctx, 1, metric.WithAttributes(slices.Concat(t.dims, attrs)...))
	span.SetAttributes(attrs...)
	span.SetAttributes(t.usage(res.Usage)...)
	span.SetAttributes(attribute.Int("officina.run.model_calls", res.ModelCalls),
		attribute.Int("officina.run.tool_calls", res.ToolCalls))
	if res.Status == Completed {
		span.SetAttributes(t.withContent(nil, "gen_ai.output.messages", func() string {
			return messages("assistant", res.Text)
		})...)
	}
	if res.Status == Failed {
		span.SetAttributes(attribute.String("error.type", reason))
		span.SetStatus(codes.Error, res.Detail)
	}
	span.End()
}

// startModelCall starts a model call's span under the run's in ctx, and returns ctx with it.
func (t *telemetry) startModelCall(ctx context.Context) (context.Context, trace.Span) {
	return t.tracer.Start(ctx, "chat "+t.model.Name, trace.WithSpanKind(trace.SpanKindClient), trace.WithAttributes(
		attribute.String("gen_ai.operation.name", "chat"), t.dims[1], t.dims[2], t.dims[0]))
}

// compacted records a compaction on the model call's span in ctx, and counts it.
func (t *telemetry) compacted(ctx context.Context, e CompactionReported) {
	t.compactions.Add(ctx, 1, t.with())
	trace.SpanFromContext(ctx).SetAttributes(attribute.Int64("officina.compaction.tokens", e.Tokens),
		attribute.Int64("officina.compaction.summary_tokens", e.SummaryTokens))
}

// cleared records a clearing of old tool results on the model call's span in ctx, and counts it.
func (t *telemetry) cleared(ctx context.Context, e ClearingReported) {
	t.clearings.Add(ctx, 1, t.with())
	trace.SpanFromContext(ctx).SetAttributes(attribute.Int64("officina.clearing.tokens", e.Tokens),
		attribute.Int("officina.clearing.tool_calls", e.ToolCalls))
}

// retried counts a retry of a model call.
func (t *telemetry) retried(ctx context.Context) {
	t.retries.Add(ctx, 1, metric.WithAttributes(t.dims...))
}

// endModelCall records a model call's metrics and ends its span; the call started at started.
func (t *telemetry) endModelCall(ctx context.Context, span trace.Span, started time.Time, r reply) {
	u := r.usage
	input, cost := u.Input+u.CacheRead+u.CacheWrite, t.model.Price.cost(u)
	chat := attribute.String("gen_ai.operation.name", "chat")
	// A call that reported no tokens, such as one that failed before its reply started, records none.
	if u != (Usage{}) {
		t.tokens.Record(ctx, input, t.with(chat, attribute.String("gen_ai.token.type", "input")))
		t.tokens.Record(ctx, u.Output, t.with(chat, attribute.String("gen_ai.token.type", "output")))
		t.cacheTokens.Record(ctx, u.CacheRead, t.with(attribute.String("officina.cache.type", "read")))
		t.cacheTokens.Record(ctx, u.CacheWrite, t.with(attribute.String("officina.cache.type", "write")))
		t.cost.Record(ctx, cost, t.with())
	}
	if input > 0 {
		t.cacheHitRatio.Record(ctx, float64(u.CacheRead)/float64(input), t.with())
	}
	failure := attribute.String("error.type", "model_error")
	if r.failure == nil {
		t.modelDuration.Record(ctx, time.Since(started).Seconds(), t.with(chat))
	} else {
		t.modelDuration.Record(ctx, time.Since(started).Seconds(), t.with(chat, failure))
	}

	span.SetAttributes(t.usage(u)...)
	span.SetAttributes(attribute.Int("officina.model.retries", r.retries))
	if r.finished != nil {
		span.SetAttributes(attribute.StringSlice("gen_ai.response.finish_reasons", []string{finishWord(*r.finished)}))
	}
	if r.firstText >= 0 {
		span.SetAttributes(attribute.Float64("officina.model.time_to_first_token", r.firstText.Seconds()))
	}
	span.SetAttributes(t.withContent(nil, "gen_ai.output.messages", func() string {
		var text strings.Builder
		for _, b := range r.blocks {
			text.WriteString(b.Text)
		}
		return messages("assistant", text.String())
	})...)
	if r.failure != nil {
		span.SetAttributes(failure)
		span.SetStatus(codes.Error, t.redact(r.failure.Error()))
	}
	span.End()
}

// finishWord returns a finish reason as the span names it: the reason in snake case, or an unknown one's own word.
func finishWord(f Finished) string {
	switch f.Reason {
	case FinishEnd:
		return "end"
	case FinishToolUse:
		return "tool_use"
	case FinishMaxTokens:
		return "max_tokens"
	case FinishRefusal:
		return "refusal"
	case FinishContextFull:
		return "context_full"
	case FinishUnknown:
		if f.Detail != "" {
			return f.Detail
		}
	}
	return "unknown"
}

// startToolCall starts a tool call's span under the run's in ctx, and returns ctx with it; found says whether the
// agent has the tool.
func (t *telemetry) startToolCall(ctx context.Context, tl tool, found bool, call ToolCall) (context.Context, trace.Span) {
	source := "application"
	switch {
	case found && tl.Source != nil:
		source = tl.Source.Name()
	case found && tl.memory:
		source = "memory"
	}
	attrs := []attribute.KeyValue{
		attribute.String("gen_ai.operation.name", "execute_tool"), attribute.String("gen_ai.tool.name", call.Name),
		attribute.String("gen_ai.tool.call.id", call.ID), attribute.String("gen_ai.tool.type", "function"),
		attribute.String("officina.tool.source", source),
	}
	if found {
		attrs = append(attrs, attribute.String("officina.tool.kind", strings.ToLower(tl.Kind.String())))
	}
	attrs = t.withContent(attrs, "gen_ai.tool.call.arguments", func() string { return string(call.Input) })
	return t.tracer.Start(ctx, "execute_tool "+call.Name, trace.WithAttributes(attrs...))
}

// approved records an approval's answer and how long it took, on the call's span in ctx and in the metrics.
func (t *telemetry) approved(ctx context.Context, tool string, approved bool, wait time.Duration) {
	answer := "denied"
	if approved {
		answer = "approved"
	}
	t.approvals.Add(ctx, 1, t.with(attribute.String("gen_ai.tool.name", tool),
		attribute.String("officina.tool.approval", answer)))
	trace.SpanFromContext(ctx).SetAttributes(attribute.String("officina.tool.approval", answer))
	t.waited(ctx, wait)
}

// waited records how long the call in ctx waited for its approval.
func (t *telemetry) waited(ctx context.Context, wait time.Duration) {
	trace.SpanFromContext(ctx).SetAttributes(attribute.Float64("officina.tool.approval_wait", wait.Seconds()))
}

// toolOutcome is how a tool call ended, as telemetry names it.
type toolOutcome string

const (
	toolOK    toolOutcome = "ok"
	toolError toolOutcome = "error"
	// toolBlocked is a write that did not run, as its attempt could not be audited.
	toolBlocked toolOutcome = "blocked"
)

// endToolCall records a tool call's metrics and ends its span. The call started at started, its tool ran for ran
// (negative if it did not run), and its result had length bytes before it was cut to content.
func (t *telemetry) endToolCall(ctx context.Context, span trace.Span, started time.Time, call ToolCall,
	outcome toolOutcome, ran time.Duration, length int, content string,
) {
	dims := t.with(attribute.String("gen_ai.tool.name", call.Name), attribute.String("officina.tool.outcome", string(outcome)))
	t.toolCalls.Add(ctx, 1, dims)
	t.toolDuration.Record(ctx, time.Since(started).Seconds(), dims)
	span.SetAttributes(attribute.String("officina.tool.outcome", string(outcome)),
		attribute.Bool("officina.tool.truncated", length > maxResult), attribute.Int("officina.tool.result_length", length))
	if ran >= 0 {
		span.SetAttributes(attribute.Float64("officina.tool.ran", ran.Seconds()))
	}
	span.SetAttributes(t.withContent(nil, "gen_ai.tool.call.result", func() string { return content })...)
	switch outcome {
	case toolBlocked:
		span.SetAttributes(attribute.String("error.type", "audit_unavailable"))
		span.SetStatus(codes.Error, "")
	case toolError:
		span.SetAttributes(attribute.String("error.type", "tool_error"))
		span.SetStatus(codes.Error, "")
	}
	span.End()
}

// auditFailed counts an audit entry of kind the sink failed to write, and marks the span in ctx: the step's it
// recorded.
func (t *telemetry) auditFailed(ctx context.Context, kind AuditKind) {
	k := attribute.String("officina.audit.kind", string(kind))
	t.auditFailures.Add(ctx, 1, t.with(k))
	trace.SpanFromContext(ctx).AddEvent("officina.audit.failed", trace.WithAttributes(k))
}

// with returns the measurement option of the agent's attributes and attrs.
func (t *telemetry) with(attrs ...attribute.KeyValue) metric.MeasurementOption {
	return metric.WithAttributes(slices.Concat(t.dims, attrs)...)
}

// usage returns the span attributes of u and its cost.
func (t *telemetry) usage(u Usage) []attribute.KeyValue {
	return []attribute.KeyValue{
		attribute.Float64("officina.usage.cost", t.model.Price.cost(u)),
		attribute.Int64("gen_ai.usage.input_tokens", u.Input+u.CacheRead+u.CacheWrite),
		attribute.Int64("gen_ai.usage.output_tokens", u.Output),
		attribute.Int64("gen_ai.usage.cache_read.input_tokens", u.CacheRead),
		attribute.Int64("gen_ai.usage.cache_creation.input_tokens", u.CacheWrite),
	}
}

// withContent returns attrs with the content attribute key of value, redacted, when the host opted in.
func (t *telemetry) withContent(attrs []attribute.KeyValue, key string, value func() string) []attribute.KeyValue {
	if !t.content {
		return attrs
	}
	return append(attrs, attribute.String(key, t.redact(value())))
}

// messages returns one text message in the semantic conventions' message format.
func messages(role, text string) string {
	type part struct {
		Type    string `json:"type"`
		Content string `json:"content"`
	}
	type message struct {
		Role  string `json:"role"`
		Parts []part `json:"parts"`
	}
	// Only invalid UTF-8 could fail, and it is allowed, as replacement characters: nothing fails.
	data, _ := json.Marshal([]message{{Role: role, Parts: []part{{Type: "text", Content: text}}}},
		jsontext.AllowInvalidUTF8(true))
	return string(data)
}
