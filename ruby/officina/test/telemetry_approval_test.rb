# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# What a tool call's span and metrics say of its approval: a denial, and a run cancelled while the approver decided.
class TelemetryApprovalTest < Minitest::Test
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

  def test_evt02_a_denied_calls_span_is_marked_failed_as_a_tool_error
    collector = Collector.new

    approved_save(collector, Testing::ScriptedApprover.new('No.'))
    save = collector.span('execute_tool save')

    assert_equal ['tool_error', OpenTelemetry::Trace::Status::ERROR, ''],
                 [save.attributes['error.type'], save.status.code, save.status.description]
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

  private

  # A run whose save call waits two seconds for an approver who answers as the host cancels; the collector and clock.
  def approval_cancelled(approved:, audit_sink: nil)
    collector = Collector.new
    clock = FakeClock.new
    approved_save(collector, Cancelled.new(clock:, approved:), clock:, audit_sink:)
    [collector, clock]
  end

  # A run whose save call, a write, needs the approver's answer.
  def approved_save(collector, approver, **parts)
    save = tool('save', kind: :write, needs_approval: true) { |_, _| 'Saved.' }
    model = Model.new(Model.tool_use(call('1', 'save')), Model.text('Not saved.'))
    traced(collector, model, tools: [save], approver:, **parts).run(Conversation.new, 'Save.')
  end
end
