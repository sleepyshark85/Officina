# frozen_string_literal: true

require 'test_helper'
require 'tmpdir'

# The audit trail: its entries, a write recorded before it runs, the JSON-lines sink, redaction and cutting.
class AuditTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  Sink = Testing::RecordingAuditSink
  Search = Input.define { string :title }
  START = Time.utc(2026, 10, 10, 9)

  def test_aud03_each_entry_carries_its_time_sequence_run_conversation_and_agent
    sink = Sink.new
    conversation = Conversation.new(id: 'session-7')

    run_calls(sink, [write_tool { 'Ordered.' }], call('1', 'order'), conversation:)

    entries = sink.entries

    assert_equal (1..entries.size).to_a, entries.map(&:sequence)
    assert_equal [START + 1, START + 2, START + 5, START + 6], entries.map(&:time)
    assert_equal [['session-7'], ['shop'], 1], [entries.map(&:conversation).uniq, entries.map(&:agent).uniq,
                                                entries.map(&:run).uniq.size]
    assert_match(/\A\h{32}\z/, entries.first.run)
  end

  def test_aud01_a_run_records_its_start_and_end_and_each_call_and_approval
    sink = Sink.new
    tools = [write_tool(needs_approval: true) { 'Ordered.' }, read_tool { 'Found.' }]

    run_calls(sink, tools, call('1', 'order'), call('2', 'search'), approver: Testing::ScriptedApprover.new(true))

    rows = sink.entries.map { [it.kind, it.tool, it.call_id, it.outcome, it.detail] }

    assert_equal [[:run_started, nil, nil, nil, nil], [:approval_asked, 'order', '1', nil, nil],
                  [:approval_answered, 'order', '1', 'approved', nil], [:tool_started, 'order', '1', nil, nil],
                  [:tool_ended, 'order', '1', 'ok', 'Ordered.'], [:tool_started, 'search', '2', nil, nil],
                  [:tool_ended, 'search', '2', 'ok', 'Found.'], [:run_ended, nil, nil, 'completed', nil]], rows
    assert_equal ['{"title":"Dune"}'], sink.entries.filter_map(&:input).uniq
    assert_equal [1.0, 1.0], sink.entries.filter_map(&:duration)
  end

  def test_aud01_the_run_end_records_how_it_ended_with_its_usage
    usage = Usage.new(input: 4, output: 2)
    {
      [[UsageReported.new(usage:), *Model.stop(:refusal, detail: 'cyber')]] => ['stopped: refusal', 'cyber'],
      [[RuntimeError.new('overloaded')]] => ['failed: model_error', 'overloaded']
    }.each do |replies, ended|
      sink = Sink.new
      Agent.new(model: Model.new(*replies), instructions: 'You help.', audit_sink: sink).run(Conversation.new, 'Hi')

      assert_equal([:run_ended, *ended], sink.entries.last.then { [it.kind, it.outcome, it.detail] })
    end
    sink = Sink.new
    Agent.new(model: Model.new(Model.text('Hi')), instructions: 'You help.', audit_sink: sink)
         .run(Conversation.new, 'Hi') { break }

    assert_equal 'abandoned', sink.entries.last.outcome
  end

  def test_aud02_a_write_runs_only_once_its_attempt_is_in_the_trail
    sink = Sink.new
    seen = nil

    run_calls(sink, [write_tool { seen = sink.entries.last.then { [it.kind, it.call_id] } }], call('1', 'order'))

    assert_equal [:tool_started, '1'], seen
  end

  def test_aud02_a_write_whose_attempt_cannot_be_recorded_never_runs_and_a_read_still_does
    ran = []
    sink = Sink.new(fails: ->(entry) { entry.kind == :tool_started })
    tools = [write_tool { ran << :order }, read_tool { ran << :search }]

    run_calls(sink, tools, call('1', 'order'), call('2', 'search'))

    assert_equal [:search], ran
    assert_equal ['The call was not run: its attempt could not be recorded in the audit trail.', true],
                 [results[0].content, results[0].error?]
    assert_equal [1, 3, 5, 6], sink.entries.map(&:sequence)
  end

  def test_gen02_without_a_sink_there_is_no_trail_and_a_write_runs
    ran = []

    run_calls(nil, [write_tool { ran << :order }], call('1', 'order'))

    assert_equal [:order], ran
  end

  def test_aud05_audit_text_is_redacted_and_cut_at_4000_characters_with_its_length_noted
    sink = Sink.new

    run_calls(sink, [read_tool { "s3cret #{'x' * 5_000}" }], call('1', 'search', '{"title":"s3cret"}'),
              secrets: ['s3cret'])

    ended = sink.entries.find { it.kind == :tool_ended }

    assert_equal '{"title":"[redacted]"}', ended.input
    assert_equal "[redacted] #{'x' * 3_989}… [truncated: 5011 characters]", ended.detail
  end

  def test_aud04_the_json_lines_sink_appends_each_entry_as_one_object_per_line
    Dir.mktmpdir do |dir|
      path = File.join(dir, 'audit.jsonl')
      sink = JsonLinesAuditSink.new(path)

      run_calls(sink, [write_tool { 'Ordered.' }], call('1', 'order'))
      threads = Array.new(20) { |at| Thread.new { sink.write(entry(at)) } }
      threads.each(&:join)

      lines = File.readlines(path).map { JSON.parse(it) }

      assert_equal 24, lines.size
      assert_equal %w[time sequence run conversation agent kind tool callId input outcome detail duration],
                   lines[2].keys
      assert_equal ['2026-10-10T09:00:05.000000Z', 'tool_ended', 1.0], lines[2].values_at('time', 'kind', 'duration')
      assert_equal({ 'input' => 0, 'output' => 0, 'cache_read' => 0, 'cache_write' => 0 }, lines[3]['usage'])
    end
  end

  def test_aud04_a_sink_that_cannot_write_raises
    sink = JsonLinesAuditSink.new(File.join(Dir.tmpdir, 'no-such-dir-r05b', 'audit.jsonl'))

    assert_raises(SystemCallError) { sink.write(entry(1)) }
  end

  private

  def read_tool(&)
    Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read, &)
  end

  def write_tool(needs_approval: false, &handler)
    Tool.new(name: 'order', description: 'Orders.', input: Search, kind: :write, needs_approval:, &handler)
  end

  def call(id, name, input = '{"title":"Dune"}') = Model.tool_use_block(id, name, input)

  def entry(sequence)
    AuditEntry.new(time: START, sequence:, run: 'r', conversation: 'c', agent: nil, kind: :run_started)
  end

  # A clock that moves one second on each reading.
  def clock
    now = START
    lock = Mutex.new
    -> { lock.synchronize { now += 1 } }
  end

  # Runs an agent named shop on the fake clock; +parts+ are the agent's other optional parts.
  def run_calls(sink, tools, *calls, conversation: Conversation.new, **parts)
    @conversation = conversation
    model = Model.new(Model.tool_use(*calls), Model.text('Done.'))
    Agent.new(model:, instructions: 'You help.', tools:, audit_sink: sink, name: 'shop', clock:, **parts)
         .run(conversation, 'Go')
  end

  def results = @conversation.messages.fetch(2).blocks.map(&:tool_result)
end
