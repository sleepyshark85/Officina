# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# The session summarizer as a session is left, end to end: the real console, agents, core and database, with the chat
# and summarizer models and the staff member scripted.
class ConsoleSummariesTest < Minitest::Test
  include ConsoleSession

  def test_app15_leaving_a_session_summarizes_it_and_sessions_shows_its_title_summary_and_changes
    model = ScriptedModel.new(say_then_call("I'll add two copies.", call('c1', 'restock_book', RESTOCK)),
                              ScriptedModel.text('Done: two more copies.'))
    summaries = ScriptedModel.new(summary_reply('Restock of book 320', 'Sam asked for two more copies of book 320.',
                                                'Book 320: 2 copies added'))

    first = session(model, 'Sam', 'Restock book 320 with 2.', 'y', '/quit', summarizer: summaries)
    id = session_id(first)
    listing = session(ScriptedModel.new, 'Sam', '/sessions', '/quit', summarizer: ScriptedModel.new)

    assert_in_order first, 'Done: two more copies.', "you> /quit\n", "Session #{id} summarized: Restock of book 320\n"
    assert_in_order listing, "  #{id}  ", '  Sam  Restock of book 320  $0.0', "\n    Sam asked for two more copies " \
                                                                              "of book 320.\n",
                    "    Changes: Book 320: 2 copies added\n"
    assert_equal ['Restock of book 320', 'Sam asked for two more copies of book 320.', '{"Book 320: 2 copies added"}'],
                 select_row('select title, summary, changes from sessions where id = $1', id)
  end

  def test_app15_the_summarizer_reads_the_transcript_alone_with_the_output_schema_and_no_tools
    model = ScriptedModel.new(say_then_call("I'll add two copies.", call('c1', 'restock_book', RESTOCK)),
                              ScriptedModel.text('Done: two more copies.'))
    summaries = ScriptedModel.new(summary_reply('Restock', 'Restocked.'))

    session(model, 'Sam', 'Restock book 320 with 2.', 'y', '/quit', summarizer: summaries)

    summaries.requests => [request]

    assert_empty request.tools
    assert_equal Bookshop.const_get(:SessionSummary).schema.to_s, request.output_schema
    assert_equal [:user], request.messages.map(&:role)
    assert_equal "Staff: Restock book 320 with 2.\nAssistant: I'll add two copies.\n" \
                 "Tool call restock_book #{RESTOCK}\n" \
                 "Tool result of restock_book: #{results(model, 1).first.content}\n" \
                 "Assistant: Done: two more copies.\n", request.messages.first.text
  end

  def test_app14_a_summarys_cost_is_added_to_its_sessions_and_counts_in_its_budget
    model = ScriptedModel.new(ScriptedModel.text('Hello.', usage: SUMMARY_USAGE))
    id = session_id(session(model, 'Sam', 'Hi.', '/quit',
                            summarizer: ScriptedModel.new(summary_reply('Greeting', 'Hi.'))))
    later = ScriptedModel.new(ScriptedModel.text('Hello again.'))

    transcript = session(later, 'Sam', "/resume #{id}", '/cost', '/quit')

    # The reply's $0.0075 and the summary's.
    assert_equal %w[2000 200 0.0150], select_row('select input_tokens, output_tokens, cost from sessions where id = $1',
                                                 id)
    assert_includes transcript, "Resumed session #{id}: 3 messages, $0.0150 so far.\n"
    assert_includes transcript, "you> /cost\nSession #{id}: tokens: 2,000 in (0% from cache), 200 out; " \
                                "cost $0.0150 of its $5.00 budget.\n"
  end

  def test_app15_new_resume_and_quit_each_summarize_the_session_left_and_an_unchanged_one_is_not_summarized_again
    summaries = ScriptedModel.new(summary_reply('Greeting', 'Sam said hello.'),
                                  summary_reply('Second greeting', 'Sam said hello again.'),
                                  summary_reply('Third greeting', 'Sam said hello a third time.'))

    first = session(ScriptedModel.new(ScriptedModel.text('Hello.'), ScriptedModel.text('Hello again.')), 'Sam', 'Hi.',
                    '/new', 'Hi again.', '/quit', summarizer: summaries)
    one = session_id(first)
    two = first[/^New session (\h{12})\.$/, 1]
    second = session(ScriptedModel.new(ScriptedModel.text('Hello a third time.')), 'Sam', 'Hi a third time.',
                     "/resume #{one}", '/quit', summarizer: summaries)

    assert_in_order first, "you> /new\nSession #{one} summarized: Greeting\nNew session #{two}.\n",
                    "you> /quit\nSession #{two} summarized: Second greeting\n"
    assert_in_order second, "Resumed session #{one}", "Session #{session_id(second)} summarized: Third greeting\n",
                    "you> /quit\n"
    refute_includes second, "Session #{one} summarized"
    assert_equal 3, summaries.requests.size
  end

  def test_app15_resuming_the_session_in_use_does_not_leave_it_and_the_end_of_the_input_leaves_it
    summaries = ScriptedModel.new(summary_reply('Greeting', 'Sam said hello.'),
                                  summary_reply('Two greetings', 'Twice.'))
    first = session(ScriptedModel.new(ScriptedModel.text('Hello.')), 'Sam', 'Hi.', summarizer: summaries)
    id = session_id(first)

    second = session(ScriptedModel.new(ScriptedModel.text('Hello again.')), 'Sam', "/resume #{id}", 'Hi again.',
                     "/resume #{id}", '/quit', summarizer: summaries)

    assert_equal "Session #{id} summarized: Greeting\n", first.lines.last
    assert_in_order second, "you> /resume #{id}\nResumed session #{id}", "you> /quit\n",
                    "Session #{id} summarized: Two greetings\n"
    assert_equal 1, second.scan('summarized:').size
    assert_includes summaries.requests.last.messages.first.text, "Staff: Hi again.\n"
  end

  def test_app15_a_summarizer_that_fails_or_stops_never_breaks_the_chat_and_the_next_console_tries_again
    summaries = ScriptedModel.new([RuntimeError.new('The model is overloaded')],
                                  ScriptedModel.stop(:max_tokens, text: '{"title":'))
    first = session(ScriptedModel.new(ScriptedModel.text('Hello.'), ScriptedModel.text('Hello again.')), 'Sam', 'Hi.',
                    '/new', 'Hi again.', '/quit', summarizer: summaries)
    one = session_id(first)
    two = first[/^New session (\h{12})\.$/, 1]

    # The listing summarizes the latest first.
    later = ScriptedModel.new(summary_reply('Second greeting', 'Again.'), summary_reply('Greeting', 'Sam said hello.'))
    listing = session(ScriptedModel.new, 'Sam', '/sessions', '/quit', summarizer: later)

    assert_in_order first, "you> /new\n[Session #{one} could not be summarized: The model is overloaded]\n",
                    "New session #{two}.\n", "assistant> Hello again.\n",
                    "you> /quit\n[Session #{two} could not be summarized: stopped: output_limit]\n"
    assert_in_order listing, "Summarizing 2 sessions left without a summary…\n", "  #{two}  ",
                    '  Sam  Second greeting  $', "  #{one}  ", '  Sam  Greeting  $'
  end
end
