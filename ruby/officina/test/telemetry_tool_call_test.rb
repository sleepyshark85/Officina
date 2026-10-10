# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# What a tool call's span says of an audit gap, a cancelled approval, a cut result and a tool the agent lacks.
class TelemetryToolCallTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  # A human who is still deciding when the host cancels the run, two seconds later, and then gives the answer.
  Cancelled = Data.define(:clock, :approved) do
    def approve(_tool, _call, cancel:)
      clock.advance(2)
      cancel.cancel
      Officina::Approval.new(approved:)
    end
  end

  def test_aud06_an_audit_sink_failure_and_a_write_it_blocks_show_on_their_spans
    collector = Collector.new

    blocked_save(collector)
    run, span = ['invoke_agent', 'execute_tool save'].map { collector.span(it) }

    assert_equal ['blocked', 'audit_unavailable', nil, OpenTelemetry::Trace::Status::ERROR],
                 [*span.attributes.values_at('officina.tool.outcome', 'error.type', 'officina.tool.ran'),
                  span.status.code]
    assert_equal [['officina.audit.failed', { 'officina.audit.kind' => 'ToolStarted' }, span.start_timestamp]],
                 events(span)
    assert_equal [['officina.audit.failed', { 'officina.audit.kind' => 'RunStarted' }, run.start_timestamp]],
                 events(run)
  end

  def test_aud06_audit_failures_are_counted_by_kind_and_a_blocked_write_by_its_outcome
    collector = Collector.new

    blocked_save(collector)

    assert_equal([[{ 'officina.audit.kind' => 'RunStarted' }, 1], [{ 'officina.audit.kind' => 'ToolStarted' }, 1]],
                 collector.points_of('officina.audit.failures', SCRIPTED).map { [it.attributes, it.sum] })
    assert_equal([[{ 'gen_ai.tool.name' => 'save', 'officina.tool.outcome' => 'blocked' }, 1]],
                 collector.points_of('officina.tool.calls', SCRIPTED).map { [it.attributes, it.sum] })
  end

  def test_evt02_a_call_cancelled_while_waiting_for_approval_records_the_wait_only
    collector, clock = approval_cancelled(approved: false)
    span = collector.span('execute_tool save')

    assert_in_delta 2.0, span.attributes['officina.tool.approval_wait']
    refute_includes span.attributes, 'officina.tool.approval'
    assert_empty collector.points_of('officina.tool.approvals')
    assert_in_delta 2.0, clock.since_start(span.end_timestamp)
  end

  def test_evt02_an_approval_given_as_the_run_was_cancelled_is_counted_and_the_call_ends_unrun
    sink = Testing::RecordingAuditSink.new
    collector, = approval_cancelled(approved: true, audit_sink: sink)

    assert_equal ['approved', 'error', nil],
                 collector.attributes('execute_tool save', 'officina.tool.approval', 'officina.tool.outcome',
                                      'officina.tool.ran')
    assert_equal [1], collector.points_of('officina.tool.approvals').map(&:sum)
    assert_equal(1, sink.entries.count { it.kind == :tool_ended })
  end

  def test_evt02_a_tool_that_raises_ends_its_span_as_a_tool_error_after_running
    collector = Collector.new
    clock = FakeClock.new
    lookup = tool('lookup') do |_, _|
      clock.advance(2)
      raise 'lookup is down'
    end

    traced(collector, Model.new(Model.tool_use(call('1', 'lookup')), Model.text('Sorry.')), tools: [lookup], clock:)
      .run(Conversation.new, 'Look.')
    span = collector.span('execute_tool lookup')

    assert_equal ['error', 'tool_error', 2.0],
                 span.attributes.values_at('officina.tool.outcome', 'error.type', 'officina.tool.ran')
    assert_equal OpenTelemetry::Trace::Status::ERROR, span.status.code
  end

  def test_tool06_evt02_a_result_of_exactly_the_limit_is_kept_whole_and_one_longer_is_marked_truncated
    collector = Collector.new
    tools = [tool('exact') { |_, _| 'x' * 64_000 }, tool('over') { |_, _| 'x' * 64_001 }]

    traced(collector, Model.new(Model.tool_use(call('1', 'exact'), call('2', 'over')), Model.text('Done.')), tools:)
      .run(Conversation.new, 'Go')
    keys = %w[officina.tool.truncated officina.tool.result_length]

    assert_equal([[false, 64_000], [true, 64_001]],
                 %w[exact over].map { collector.attributes("execute_tool #{it}", *keys) })
  end

  def test_evt02_a_call_of_a_tool_the_agent_lacks_has_no_kind_and_a_reads_kind_is_read
    collector = Collector.new

    traced(collector, Model.new(Model.tool_use(call('1', 'gone'), call('2', 'search')), Model.text('Done.')),
           tools: [tool('search') { |_, _| 'Found.' }]).run(Conversation.new, 'Go')
    keys = %w[officina.tool.kind officina.tool.outcome error.type]

    assert_equal([[nil, 'error', 'tool_error'], ['read', 'ok', nil]],
                 %w[gone search].map { collector.attributes("execute_tool #{it}", *keys) })
  end

  private

  # A run whose save, a write, cannot run, as the sink fails its attempt; it fails the run's start too.
  def blocked_save(collector)
    sink = Testing::RecordingAuditSink.new(fails: ->(entry) { %i[run_started tool_started].include?(entry.kind) })
    save = tool('save', kind: :write) { |_, _| raise 'must not run' }
    traced(collector, Model.new(Model.tool_use(call('1', 'save')), Model.text('Not saved.')),
           tools: [save], audit_sink: sink, clock: FakeClock.new).run(Conversation.new, 'Save.')
  end

  # A run whose save call waits two seconds for an approver who answers as the host cancels; the collector and clock.
  def approval_cancelled(approved:, audit_sink: nil)
    collector = Collector.new
    clock = FakeClock.new
    save = tool('save', kind: :write, needs_approval: true) { |_, _| 'Saved.' }
    traced(collector, Model.new(Model.tool_use(call('1', 'save'))),
           clock:, tools: [save], approver: Cancelled.new(clock:, approved:), audit_sink:)
      .run(Conversation.new, 'Save.')
    [collector, clock]
  end

  def events(span) = span.events.to_a.map { [it.name, it.attributes, it.timestamp] }
end
