# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# The session summarizer as /sessions lists the sessions left without a summary, end to end: the real console,
# agents, core and database, with the chat and summarizer models and the staff member scripted.
class ConsoleSummaryListingsTest < Minitest::Test
  include ConsoleSession

  # The process ending in the middle of a reply.
  class Crash < StandardError
  end

  def test_app15_a_session_left_without_a_summary_by_a_crash_is_summarized_when_sessions_lists_it
    model = ScriptedModel.new(say_then_call("I'll add two copies.", call('c1', 'restock_book', RESTOCK)))
    assert_raises(Crash) do
      session(model, 'Sam', 'Restock book 320 with 2.', -> { raise Crash }, summarizer: ScriptedModel.new)
    end
    id = select_row('select id from sessions').first
    summaries = ScriptedModel.new(summary_reply('Restock of book 320 not confirmed',
                                                'The session ended before approval.', 'None made'))

    transcript = session(ScriptedModel.new, 'Sam', '/sessions', '/sessions', '/quit', summarizer: summaries)

    assert_in_order transcript, "you> /sessions\nSummarizing 1 session left without a summary…\n",
                    "  #{id}  ", '  Sam  Restock of book 320 not confirmed  $',
                    "    The session ended before approval.\n    Changes: None made\n",
                    "you> /sessions\nSessions, most recent first:\n", '  Sam  Restock of book 320 not confirmed  $'
    summaries.requests => [request]

    assert_includes request.messages.first.text, "Tool call restock_book #{RESTOCK}\n"
  end

  def test_app15_a_listing_never_summarizes_the_session_in_use_which_is_summarized_once_as_it_is_left
    summaries = ScriptedModel.new(summary_reply('Greeting', 'Sam said hello.'))

    transcript = session(ScriptedModel.new(ScriptedModel.text('Hello.')), 'Sam', 'Hi.', '/sessions', '/quit',
                         summarizer: summaries)

    refute_includes transcript, 'Summarizing'
    assert_match(/\* .*  Sam  \(no title yet\)  \$/, transcript)
    assert_match(/^Session \h{12} summarized: Greeting\n\z/, transcript)
    assert_equal 1, summaries.requests.size
  end

  def test_app15_a_listing_summarizes_a_few_sessions_at_a_time_and_a_failed_summary_keeps_no_title
    # Four sessions left without a summary, the latest last.
    %w[One Two Three Four].each_with_index { |id, index| left_without_summary(id, minutes_ago: 4 - index) }
    # The first summary is not the typed output; the next two are.
    summaries = ScriptedModel.new(ScriptedModel.text('{"title":"Four"}'), summary_reply('Three', 'Sam said three.'),
                                  summary_reply('Two', 'Sam said two.'), summary_reply('One', 'Sam said one.'))

    transcript = session(ScriptedModel.new, 'Sam', '/sessions', '/sessions', '/sessions', '/quit',
                         summarizer: summaries)
    _, *listings = transcript.split("you> /sessions\n")

    assert_in_order listings[0], "Summarizing 3 of 4 sessions left without a summary; /sessions again does more…\n",
                    'could not be summarized: ', "/summary: is required; /changes: is required]\n",
                    '  Sam  (no title yet)  $', '  Sam  Three  $', '  Sam  Two  $', '  Sam  (no title yet)  $'
    # The failed session is not tried again in this console.
    assert_in_order listings[1], "Summarizing 1 session left without a summary…\n", '  Sam  One  $'
    refute_includes listings[2], 'Summarizing'
    assert_equal 4, summaries.requests.size
  end

  def test_app15_a_session_the_database_fails_to_load_is_tried_by_the_next_listing_and_an_unreadable_one_is_not
    left_without_summary('hello', minutes_ago: 0)
    left_without_summary('broken', 'not json', minutes_ago: 1)
    # Loading a session reads a column that listing does not: without it, the database fails each load.
    rename_column('input_tokens', 'input_tokens_gone')
    summaries = ScriptedModel.new(summary_reply('Greeting', 'Sam said hello.'))
    restore = -> { rename_column('input_tokens_gone', 'input_tokens') }

    transcript = session(ScriptedModel.new, 'Sam', '/sessions', restore, '/sessions', '/sessions', '/quit',
                         summarizer: summaries)

    assert_in_order transcript, "you> /sessions\nSummarizing 2 sessions left without a summary…\n",
                    '[Session hello could not be read: ERROR:  column "input_tokens" does not exist',
                    '[Session broken could not be read: ERROR:  column "input_tokens" does not exist',
                    '  Sam  (no title yet)  $', "you> /sessions\nSummarizing 2 sessions left without a summary…\n",
                    "[Session broken could not be read: Not a conversation's JSON", '  Sam  Greeting  $',
                    "you> /sessions\nSessions, most recent first:\n"
    # The third listing tries neither again.
    assert_equal 2, transcript.scan('Summarizing').size
    assert_equal 1, summaries.requests.size
  end

  def test_app15_a_session_with_nothing_to_summarize_says_so_and_is_not_tried_again
    left_without_summary('empty', '{"id":"empty","messages":[]}', minutes_ago: 0)
    summaries = ScriptedModel.new
    telemetry = MemoryTelemetry.new
    logged = []

    transcript = session(ScriptedModel.new, 'Sam', '/sessions', -> { logged.concat(telemetry.logs) }, '/sessions',
                         '/quit', summarizer: summaries, telemetry:)

    assert_in_order transcript, "[Session empty could not be summarized: A run needs a message]\n",
                    '  empty  ', '  Sam  (no title yet)  $'
    warnings = logged.map { it.to_h.values_at(:severity_text, :body) }

    assert_equal [['WARN', 'Session empty could not be summarized: A run needs a message']], warnings
    refute_equal OpenTelemetry::Trace::INVALID_SPAN_ID, logged.first.span_id
    assert_equal 1, transcript.scan('Summarizing').size
    assert_empty summaries.requests
  end

  private

  def rename_column(from, to) = execute("alter table sessions rename column #{from} to #{to}")

  # Stores a session left without a summary, changed the minutes ago: of one message, its id, or of the conversation
  # given.
  def left_without_summary(id, conversation = nil, minutes_ago:)
    conversation ||= JSON.generate({ id:, messages: [{ role: 'user', blocks: [{ text: id }] }] })
    execute(<<~SQL, id, conversation, minutes_ago)
      insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens,
                            cache_write_tokens, cost, updated)
      values ($1, 'Sam', $2, 0, 0, 0, 0, 0, now() - make_interval(mins => $3))
    SQL
  end
end
