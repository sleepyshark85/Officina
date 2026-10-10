# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# /audit end to end: a session's entries from the audit table, grouped by run, each run linked to its trace, which
# holds the reply, its run, model calls and tool calls, and the reply's log record.
class ConsoleAuditTest < Minitest::Test
  include ConsoleSession

  START = Time.new(2026, 10, 10, 8)
  ADD = '{"name":"Jo Bloggs","email":"jo@example.com"}'
  # A model priced as Claude Opus 5.5 is, in dollars per million tokens.
  PRICED = Officina::ModelInfo.new(provider: 'scripted', name: 'scripted', price: Officina::Price.new(
    input: BigDecimal('5'), output: BigDecimal('25'), cache_read: BigDecimal('0.5'), cache_write: BigDecimal('6.25'),
    cache_write_hour: BigDecimal('10')
  ))

  def test_app16_audit_shows_the_sessions_entries_by_run_with_a_link_to_each_runs_trace
    telemetry = MemoryTelemetry.new
    spans = []
    usage = Officina::Usage.new(input: 1200, output: 300, cache_read: 4000)
    model = ScriptedModel.new(say_then_call('Adding Jo.', call('c1', 'add_customer', ADD)), ScriptedModel.text('Done.'),
                              ScriptedModel.text('Hello.', usage:), info: PRICED)

    transcript = session(model, 'Sam', 'Add Jo Bloggs, jo@example.com.', 'y', 'Hi.', -> { spans = telemetry.spans },
                         '/audit', '/audit nobody', '/quit', clock: ticking, telemetry:)

    assert_in_order transcript, trail(*spans.select { it.name == 'reply' }.map(&:hex_trace_id)),
                    "you> /audit nobody\nNo audit entries for session nobody.\n"
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

  # What /audit shows of the first test's session, whose replies are the traces given.
  def trail(first, second)
    ended = 'completed  tokens: 0 in (0 cached), 0 out, $0.0000'
    <<~TEXT
      you> /audit
      Audit of session #{conversation}:
      Run 1, trace: http://localhost:18888/traces/detail/#{first}
        #{at(3)}  RunStarted
        #{at(11)}  ApprovalAsked     add_customer
        #{at(14)}  ApprovalAnswered  add_customer          approved
        #{at(15)}  ToolStarted       add_customer
        #{at(18)}  ToolEnded         add_customer          ok  1,000 ms
        #{at(26)}  RunEnded                                #{ended}
      Run 2, trace: http://localhost:18888/traces/detail/#{second}
        #{at(30)}  RunStarted
        #{at(36)}  RunEnded                                completed  tokens: 5,200 in (4,000 cached), 300 out, $0.0155
    TEXT
  end

  # A clock that ticks a second each time it is read.
  def ticking
    now = START
    -> { now += 1 }
  end

  # The local time shown for the clock's nth reading.
  def at(ticks) = (START + ticks).strftime('%H:%M:%S')

  # The id of the session's conversation, the one the audit table holds.
  def conversation
    connection = PG.connect(database_url)
    connection.exec('select distinct conversation from audit').getvalue(0, 0)
  ensure
    connection&.close
  end
end
