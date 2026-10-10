# frozen_string_literal: true

require 'test_helper'
require_relative 'support/tool_calls'

# The audit trail a run writes to the agent's sink: its entries, and a write recorded before it runs.
class AuditTrailTest < Minitest::Test
  include Sleepyshark::Officina
  include ToolCalls

  cover 'Sleepyshark::Officina*'

  Sink = Testing::RecordingAuditSink
  START = Time.utc(2026, 10, 10, 9)

  def test_aud03_each_entry_carries_its_time_sequence_run_conversation_and_agent
    sink = Sink.new
    conversation = Conversation.new(id: 'session-7')
    ticks = []
    agent = Agent.new(model: Model.new(Model.text('Hello')), instructions: 'You help.', audit_sink: sink,
                      name: 'shop', clock: clock(ticks))

    agent.run(conversation, 'Hi')
    times = sink.entries.map(&:time)

    assert_equal([[1, 'session-7', 'shop'], [2, 'session-7', 'shop']],
                 sink.entries.map { [it.sequence, it.conversation, it.agent] })
    assert_empty times - ticks
    assert_operator times.first, :<, times.last
    assert_equal 1, sink.entries.map(&:run).uniq.size
    assert_match(/\A\h{32}\z/, sink.entries.first.run)
  end

  def test_aud03_without_a_clock_entries_carry_the_real_time
    sink = Sink.new
    before = Time.now

    run_calls([], call('1', 'none'), audit_sink: sink)

    assert(sink.entries.all? { (before..Time.now).cover?(it.time) })
  end

  def test_aud01_a_run_records_its_start_and_end_and_each_call_and_approval
    sink = Sink.new
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    run_calls(tools, call('1', 'order'), approver: Testing::ScriptedApprover.new(true), audit_sink: sink, clock:)

    assert_equal([[:run_started, nil, nil, nil, nil], [:approval_asked, '1', nil, nil, nil],
                  [:approval_answered, '1', 'approved', nil, nil], [:tool_started, '1', nil, nil, nil],
                  [:tool_ended, '1', 'ok', 'Ordered.', 1.0], [:run_ended, nil, 'completed', nil, nil]],
                 sink.entries.map { [it.kind, it.call_id, it.outcome, it.detail, it.duration] })
    assert_equal([['order', '{"title":"Dune"}']] * 4, sink.entries[1..4].map { [it.tool, it.input] })
  end

  def test_aud01_a_read_records_its_attempt_too
    sink = Sink.new

    run_calls([tool('search') { |_, _| 'Found.' }], call('1', 'search'), audit_sink: sink)

    assert_equal([[:tool_started, 'search', '1', '{"title":"Dune"}']],
                 sink.entries.select { it.kind == :tool_started }.map { [it.kind, it.tool, it.call_id, it.input] })
  end

  def test_aud03_the_agent_keeps_a_frozen_copy_of_the_name_its_entries_carry
    name = +'shop'

    agent = Agent.new(model: Model.new, instructions: 'You help.', name:)
    name << '!'

    assert_equal 'shop', agent.name
    assert_predicate agent.name, :frozen?
  end

  def test_aud01_a_denial_and_a_failed_call_are_recorded_with_why
    sink = Sink.new
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' },
             tool('search') { |_, _| raise IOError, 'down' },
             tool('cancel', kind: :write) { |_, _| ToolFailure.new(message: 'Already cancelled.') }]

    run_calls(tools, call('1', 'order'), call('2', 'search'), call('3', 'cancel'),
              approver: Testing::ScriptedApprover.new('No.'), audit_sink: sink, clock:)

    rows = sink.entries.select { %i[approval_answered tool_ended].include?(it.kind) }

    assert_equal([['denied', 'No.', nil], ['error', 'The call was denied: No.', nil],
                  ['error', 'The tool failed: down', 1.0], ['error', 'Already cancelled.', 1.0]],
                 rows.map { [it.outcome, it.detail, it.duration] })
  end

  def test_aud02_a_write_runs_only_once_its_attempt_is_in_the_trail
    sink = Sink.new
    seen = nil
    tools = [tool('order', kind: :write) { |_, _| seen = sink.entries.last.then { [it.kind, it.call_id] } }]

    run_calls(tools, call('1', 'order'), audit_sink: sink)

    assert_equal [:tool_started, '1'], seen
  end

  def test_aud02_a_write_whose_attempt_cannot_be_recorded_never_runs_and_a_read_still_does
    ran = []
    sink = Sink.new(fails: ->(entry) { entry.kind == :tool_started })
    tools = [tool('order', kind: :write) { |_, _| ran << :order }, tool('search') { |_, _| ran << :search }]

    result = run_calls(tools, call('1', 'order'), call('2', 'search'), audit_sink: sink).results.first

    assert_equal [:search], ran
    assert_equal 'The call was not run: its attempt could not be recorded in the audit trail.', result.content
    assert_predicate result, :error?
    assert_equal [1, 3, 5, 6], sink.entries.map(&:sequence)
  end

  def test_gen02_without_a_sink_there_is_no_trail_and_a_write_runs
    ran = []

    run_calls([tool('order', kind: :write) { |_, _| ran << :order }], call('1', 'order'))

    assert_equal [:order], ran
  end

  def test_aud05_audit_text_is_redacted_and_cut_at_4000_characters_with_its_length_noted
    sink = Sink.new
    tools = [tool('search') { |_, _| "s3cret #{'x' * 5_000}" }]

    run_calls(tools, call('1', 'search', '{"title":"s3cret"}'), audit_sink: sink, secrets: ['s3cret'])

    ended = sink.entries.find { it.kind == :tool_ended }

    assert_equal '{"title":"[redacted]"}', ended.input
    assert_equal "[redacted] #{'x' * 3_989}… [truncated: 5011 characters]", ended.detail
  end

  private

  # A clock a second later at each reading, which keeps what it told in +ticks+.
  def clock(ticks = [])
    now = START
    lock = Mutex.new
    -> { lock.synchronize { (ticks << (now += 1)).last } }
  end
end
