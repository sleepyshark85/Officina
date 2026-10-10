# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# The console's conversation end to end: the streamed reply and how it ends, the commands, the run context, and the
# database password kept out of it.
class ConsoleTest < Minitest::Test
  include ConsoleSession

  def test_app01_the_reply_streams_with_text_between_tool_calls_and_each_tool_with_its_input_and_outcome
    search = call('c1', 'search_books', '{"title":"Winter Archive"}')
    model = ScriptedModel.new(say_then_call('Let me look that up.', search),
                              [Officina::TextDelta.new(text: 'We have '), *ScriptedModel.text('12 copies.')])

    transcript = session(model, 'Sam', 'Do we have The Winter Archive?', '/quit')

    assert_in_order transcript, "Who is using the assistant? Your name: Sam\nHello, Sam.\n",
                    "you> Do we have The Winter Archive?\n", "assistant> Let me look that up.\n",
                    %(  > search_books {"title":"Winter Archive"}\n), "  < search_books: ok\n",
                    "We have 12 copies.\n", "you> /quit\n"
    refute_predicate results(model, -1).first, :error?
    assert_includes results(model, -1).first.content, '"title":"The Winter Archive"'
  end

  def test_app01_a_reply_that_restarts_stops_or_fails_says_so_and_the_session_goes_on
    model = ScriptedModel.new([Officina::Retried.new, *ScriptedModel.text('Hello.')],
                              ScriptedModel.stop(:max_tokens), [RuntimeError.new('The model is overloaded')])

    transcript = session(model, 'Sam', 'Hi.', 'Tell me a long story.', 'Again?', '/quit')

    assert_in_order transcript, "you> Hi.\n[The reply was interrupted and starts again.]\nassistant> Hello.\n",
                    "you> Tell me a long story.\n[Stopped: output limit.]\n",
                    "you> Again?\n[Failed: The model is overloaded]\n",
                    "you> /quit\n"
  end

  def test_app01_a_reply_without_text_asks_again
    transcript = session(ScriptedModel.new(ScriptedModel.stop(:end, text: nil)), 'Sam', 'Hi.', '/quit')

    assert_in_order transcript, "you> Hi.\n[The reply has no text. Please ask again.]\n", "you> /quit\n"
  end

  def test_app02_help_and_unknown_commands_are_answered_and_quit_leaves
    model = ScriptedModel.new

    transcript = session(model, '', '  ', 'Sam', '/help', '', '/memo', '/quit', 'Not read.')

    assert_in_order transcript, "Bookshop Assistant. Type /help for commands.\n",
                    "Who is using the assistant? Your name: \n", "Who is using the assistant? Your name:   \n",
                    "Who is using the assistant? Your name: Sam\n",
                    "you> /help\nCommands:\n  /help          Show this help.\n  " \
                    "/audit [<id>]  Show the audit trail of this session, or of the session with that id.\n  " \
                    "/quit          Leave the assistant.\n" \
                    "Anything else is a message to the assistant. Ctrl+C stops a reply in progress.\n",
                    "you> \nyou> /memo\nUnknown command /memo. Type /help for commands.\n", "you> /quit\n"
    refute_includes transcript, 'Not read.'
    assert_empty model.requests
  end

  def test_app13_the_run_context_names_the_date_and_staff_member_and_is_sent_again_only_on_a_new_day
    now = [Time.new(2026, 10, 10, 8)]
    model = ScriptedModel.new(ScriptedModel.text('Good morning.'), ScriptedModel.text('Good evening.'),
                              ScriptedModel.text('Good morning again.'))

    session(model, 'Sam', 'Morning!', -> { now[0] += 12 * 3600 }, 'Evening!', -> { now[0] += 12 * 3600 },
            'Next morning!', '/quit', clock: -> { now.first })

    last = model.requests.last

    assert_equal ['Today is Saturday 10 October 2026. The staff member using the assistant is Sam.',
                  'Today is Sunday 11 October 2026. The staff member using the assistant is Sam.'],
                 last.messages.select { it.role == :operator }.map(&:text)
    assert_equal %i[user operator assistant user assistant user operator], last.messages.map(&:role)
    refute_includes last.instructions, 'Sam'
  end

  def test_evt03_the_database_password_never_reaches_the_console
    password = DatabaseServer::PASSWORD
    model = ScriptedModel.new(say_then_call('Looking.', call('c1', 'find_customer', %({"nameOrEmail":"#{password}"}))),
                              ScriptedModel.text('No one by that name.'))

    transcript = session(model, 'Sam', 'Who is our database user?', '/quit')

    assert_includes transcript, %(  > find_customer {"nameOrEmail":"[redacted]"}\n)
    refute_includes transcript, password
  end
end
