# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# A run's trace: its span, a span per model and tool call under it, what each carries, and the audit entries that point
# into it.
class TelemetryTraceTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  def test_evt02_aud03_a_run_is_one_trace_with_a_span_per_model_and_tool_call_and_its_entries_point_into_it
    collector = Collector.new
    sink = Testing::RecordingAuditSink.new
    clock = FakeClock.new

    ordering_run(collector, sink, clock)
    root = collector.span('invoke_agent clerk')
    children = collector.spans.reject { it.equal?(root) }.sort_by { [it.start_timestamp, it.name] }

    assert_equal(['chat acme-large', 'execute_tool order', 'execute_tool search', 'chat acme-large'],
                 children.map(&:name))
    assert_equal [root.hex_trace_id], collector.spans.map(&:hex_trace_id).uniq
    assert_equal [root.span_id] * 4, children.map(&:parent_span_id)
    refute_predicate OpenTelemetry::Trace::SpanContext.new(span_id: root.parent_span_id), :valid?
    assert_equal [0.0, 3.0], [clock.since_start(root.start_timestamp), clock.since_start(root.end_timestamp)]
  end

  def test_evt02_ctx05_a_runs_span_carries_its_identity_result_usage_cost_and_calls
    collector = Collector.new
    sink = Testing::RecordingAuditSink.new

    ordering_run(collector, sink, FakeClock.new)

    assert_equal run_attributes(sink.entries.first.run), collector.span('invoke_agent clerk').attributes
  end

  def test_evt02_a_model_calls_span_is_a_client_span_with_its_usage_cost_finish_reason_and_retries
    collector = Collector.new

    ordering_run(collector, Testing::RecordingAuditSink.new, FakeClock.new)
    call = collector.spans.select { it.name == 'chat acme-large' }.min_by(&:start_timestamp)

    assert_equal :client, call.kind
    assert_equal({ 'gen_ai.operation.name' => 'chat', 'gen_ai.agent.name' => 'clerk', 'gen_ai.provider.name' => 'acme',
                   'gen_ai.request.model' => 'acme-large', 'officina.model.retries' => 0,
                   'officina.model.time_to_first_token' => 0.0, 'officina.usage.cost' => 0.00111,
                   'gen_ai.usage.input_tokens' => 450, 'gen_ai.usage.output_tokens' => 20,
                   'gen_ai.usage.cache_read.input_tokens' => 300, 'gen_ai.usage.cache_creation.input_tokens' => 50,
                   'gen_ai.response.finish_reasons' => ['tool_use'] }, call.attributes)
  end

  def test_evt02_a_tool_calls_span_has_its_kind_approval_wait_outcome_and_time
    collector = Collector.new
    clock = FakeClock.new

    ordering_run(collector, Testing::RecordingAuditSink.new, clock)
    order = collector.span('execute_tool order')

    assert_equal({ 'gen_ai.operation.name' => 'execute_tool', 'gen_ai.tool.name' => 'order',
                   'gen_ai.tool.call.id' => '2', 'gen_ai.tool.type' => 'function',
                   'officina.tool.source' => 'application', 'officina.tool.kind' => 'write',
                   'officina.tool.approval_wait' => 3.0,
                   'officina.tool.approval' => 'approved', 'officina.tool.outcome' => 'ok', 'officina.tool.ran' => 0.0,
                   'officina.tool.truncated' => false, 'officina.tool.result_length' => 8 }, order.attributes)
    assert_equal [:internal, OpenTelemetry::Trace::Status::UNSET], [order.kind, order.status.code]
    assert_equal [0.0, 3.0], [clock.since_start(order.start_timestamp), clock.since_start(order.end_timestamp)]
    refute_includes collector.span('execute_tool search').attributes, 'officina.tool.approval'
  end

  def test_aud03_each_entry_carries_the_runs_trace_and_the_span_of_the_step_it_records
    collector = Collector.new
    sink = Testing::RecordingAuditSink.new

    ordering_run(collector, sink, FakeClock.new)
    root, search, order = ['invoke_agent clerk', 'execute_tool search', 'execute_tool order'].map { collector.span(it) }

    assert_equal([[:run_started, root], [:tool_started, search], [:tool_ended, search], [:approval_asked, order],
                  [:approval_answered, order], [:tool_started, order], [:tool_ended, order], [:run_ended, root]]
                   .map { |kind, span| [kind, span.hex_trace_id, span.hex_span_id] },
                 sink.entries.map { [it.kind, it.trace_id, it.span_id] })
  end

  def test_evt02_aud03_a_runs_span_is_under_the_hosts_span_and_without_telemetry_its_entries_carry_the_hosts_span
    collector = Collector.new
    sink = Testing::RecordingAuditSink.new
    host = collector.tracer_provider.tracer('host').start_span('reply')

    OpenTelemetry::Trace.with_span(host) do
      traced(collector, Model.new(Model.text('Hi.'))).run(Conversation.new, 'Hi')
      Agent.new(model: Model.new(Model.text('Hi.')), instructions: 'You help.', audit_sink: sink)
           .run(Conversation.new, 'Hi')
    end
    host.finish

    assert_equal host.context.span_id, collector.span('invoke_agent').parent_span_id
    assert_equal ['chat scripted', 'invoke_agent', 'reply'], collector.spans.map(&:name)
    assert_equal([[host.context.hex_trace_id, host.context.hex_span_id]] * 2,
                 sink.entries.map { [it.trace_id, it.span_id] })
  end

  def test_aud03_without_telemetry_or_a_hosts_span_entries_carry_no_trace
    sink = Testing::RecordingAuditSink.new

    run_calls([tool('search') { |_, _| 'Found.' }], call('1', 'search'), audit_sink: sink)

    assert_equal([[nil, nil]] * 4, sink.entries.map { [it.trace_id, it.span_id] })
  end

  private

  # A run of an agent named clerk on the priced model that searches and orders, then answers; the order needs an
  # approval, which takes three seconds.
  def ordering_run(collector, sink, clock)
    search = tool('search') { |_, _| 'Found.' }
    order = tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }
    model = Model.new(reply('Looking.', usage(100, 20, 300, 50), call('1', 'search'), call('2', 'order'),
                            stop: :tool_use),
                      reply('Ordered.', usage(10, 5, 450)), info: ACME)
    traced(collector, model, name: 'clerk', tools: [search, order], audit_sink: sink, clock:,
                             approver: Waiting.new(clock:, seconds: 3, approved: true))
      .run(Conversation.new(id: 'session-1'), 'Order Dune.')
  end

  def run_attributes(run)
    { 'gen_ai.operation.name' => 'invoke_agent', 'gen_ai.conversation.id' => 'session-1', 'officina.run.id' => run,
      'gen_ai.agent.name' => 'clerk', 'gen_ai.provider.name' => 'acme', 'gen_ai.request.model' => 'acme-large',
      'officina.run.result' => 'completed', 'officina.run.model_calls' => 2, 'officina.run.tool_calls' => 2,
      'officina.usage.cost' => 0.00134, 'gen_ai.usage.input_tokens' => 910, 'gen_ai.usage.output_tokens' => 25,
      'gen_ai.usage.cache_read.input_tokens' => 750, 'gen_ai.usage.cache_creation.input_tokens' => 50 }
  end
end
