# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# /audit end to end: a session's entries from the audit table, grouped by run, each run linked to its trace, which
# holds the reply, its run, model calls and tool calls, and the reply's log record.
class ConsoleAuditTest < Minitest::Test
  include ConsoleSession

  # Private to the application, which hands it only to the agent and the console.
  AuditTable = Bookshop.const_get(:AuditTable)
  START = Time.new(2026, 10, 10, 8)
  ADD = '{"name":"Jo Bloggs","email":"jo@example.com"}'

  def test_app16_audit_shows_the_sessions_entries_by_run_with_a_link_to_each_runs_trace
    telemetry = MemoryTelemetry.new
    spans = []
    usage = Officina::Usage.new(input: 1200, output: 300, cache_read: 4000)
    model = ScriptedModel.new(say_then_call('Adding Jo.', call('c1', 'add_customer', ADD)), ScriptedModel.text('Done.'),
                              ScriptedModel.text('Hello.', usage:))
    clock = Clock.new(START)

    # The staff member takes three seconds to approve, and a minute before the next message.
    transcript = session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', -> { clock.advance(3) }, 'y',
                         -> { clock.advance(60) }, 'Hi.', -> { spans = telemetry.spans }, '/audit', '/audit nobody',
                         '/quit', clock:, telemetry:)

    assert_in_order transcript, trail(*spans.select { it.name == 'reply' }.map(&:hex_trace_id)),
                    "you> /audit nobody\nNo audit entries for session nobody.\n"
    assert_empty telemetry.spans, 'Leaving sends the telemetry, which clears what the exporters hold'
  end

  def test_app16_audit_shows_why_a_call_was_denied_and_a_run_without_a_trace
    untraced = untraced_conversation
    telemetry = MemoryTelemetry.new
    spans = []
    model = ScriptedModel.new(say_then_call('Adding Jo.', call('c1', 'add_customer', ADD)), ScriptedModel.text('No.'))

    transcript = session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', 'n', -> { spans = telemetry.spans }, '/audit',
                         "/audit #{untraced}", '/quit', telemetry:, env: { 'BOOKSHOP_DASHBOARD' => 'http://dash/' })
    trace = spans.find { it.name == 'reply' }.hex_trace_id

    assert_in_order transcript, "Run 1, trace: http://dash/traces/detail/#{trace}\n",
                    "  ApprovalAnswered  add_customer          denied: the staff member declined\n",
                    "Audit of session #{untraced}:\nRun 1, trace: none recorded\n"
  end

  def test_app16_audit_shows_the_tokens_of_a_run_end_whose_cost_is_not_recorded
    usage = Officina::Usage.new(input: 1200, output: 300, cache_read: 4000)
    model = ScriptedModel.new(ScriptedModel.text('Hello.', usage:))

    transcript = session(model, 'Sam', 'Hi.', -> { execute('update audit set cost = null') }, '/audit', '/quit')

    assert_includes transcript, '  RunEnded                                completed  ' \
                                "tokens: 5,200 in (4,000 cached), 300 out, $0.0000\n"
  end

  def test_app20_a_reply_is_one_trace_of_its_run_model_and_tool_calls_with_its_log_record
    telemetry = MemoryTelemetry.new
    kept = []
    model = ScriptedModel.new(say_then_call('Looking.', call('c1', 'search_books', '{"title":"Winter"}')),
                              ScriptedModel.text('Twelve copies.'))

    session(model, 'Sam', 'Do we have The Winter Archive?', -> { kept.push(telemetry.spans, telemetry.logs) }, '/quit',
            telemetry:)

    spans, logs = kept
    reply, run = ['reply', 'invoke_agent bookshop'].map { |name| spans.find { it.name == name } }

    assert_equal [reply.hex_trace_id], spans.map(&:hex_trace_id).uniq
    assert_equal [reply.span_id, run.span_id, run.span_id, run.span_id],
                 [run, *spans.select { it.name.start_with?('chat', 'execute_tool') }].map(&:parent_span_id)
    assert_equal 'bookshop-assistant', reply.resource.attribute_enumerator.to_h['service.name']
    assert_equal([['INFO', "Reply in conversation #{conversation} completed: 0 input tokens, 0 output tokens",
                   reply.trace_id, reply.span_id]],
                 logs.map { it.to_h.values_at(:severity_text, :body, :trace_id, :span_id) })
  end

  def test_app16_audit_says_so_when_the_audit_trail_cannot_be_read
    transcript = session(ScriptedModel.new, 'Sam', -> { take_database_down }, '/audit', -> { bring_database_back },
                         '/quit')

    assert_match %r{you> /audit\nThe audit trail could not be read: .+\nyou> /quit\n}, transcript
  end

  private

  # What /audit shows of the first test's session, whose replies are the traces given: the clock moved only while the
  # staff member approved and between the replies, and the tool took no time on it.
  def trail(first, second)
    ended = 'completed  tokens: 0 in (0 cached), 0 out, $0.0000'
    <<~TEXT
      you> /audit
      Audit of session #{conversation}:
      Run 1, trace: http://localhost:18888/traces/detail/#{first}
        #{at(0)}  RunStarted
        #{at(0)}  ApprovalAsked     add_customer
        #{at(3)}  ApprovalAnswered  add_customer          approved
        #{at(3)}  ToolStarted       add_customer
        #{at(3)}  ToolEnded         add_customer          ok  0 ms
        #{at(3)}  RunEnded                                #{ended}
      Run 2, trace: http://localhost:18888/traces/detail/#{second}
        #{at(63)}  RunStarted
        #{at(63)}  RunEnded                                completed  tokens: 5,200 in (4,000 cached), 300 out, $0.0155
    TEXT
  end

  # The id of a conversation whose one run an agent without telemetry audited, so that its rows have no trace.
  def untraced_conversation
    database = Bookshop::Database.new(database_url)
    agent = Officina::Agent.new(name: 'bookshop', model: ScriptedModel.new(ScriptedModel.text('Hello.')),
                                instructions: 'Help.', audit_sink: AuditTable.new(database:))
    Officina::Conversation.new.tap { agent.run(it, 'Hi.') { nil } }.id
  ensure
    database&.close
  end

  # The local time shown for the given seconds after the start.
  def at(seconds) = (START + seconds).strftime('%H:%M:%S')

  # The id of the session's conversation, the one the audit table holds.
  def conversation
    connection = PG.connect(database_url)
    connection.exec('select distinct conversation from audit').getvalue(0, 0)
  ensure
    connection&.close
  end
end
