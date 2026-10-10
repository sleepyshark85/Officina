# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# The console's conversation end to end: the streamed reply, the commands, Ctrl+C and the run context.
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

  def test_app02_help_and_unknown_commands_are_answered_and_quit_leaves
    model = ScriptedModel.new

    transcript = session(model, '', '  ', 'Sam', '/help', '', '/memo', '/quit', 'Not read.')

    assert_in_order transcript, "Bookshop Assistant. Type /help for commands.\n",
                    "Who is using the assistant? Your name: \n", "Who is using the assistant? Your name:   \n",
                    "Who is using the assistant? Your name: Sam\n",
                    "you> /help\nCommands:\n  /help          Show this help.\n  /quit          Leave the assistant.\n" \
                    "Anything else is a message to the assistant. Ctrl+C stops a reply in progress.\n",
                    "you> \nyou> /memo\nUnknown command /memo. Type /help for commands.\n", "you> /quit\n"
    refute_includes transcript, 'Not read.'
    assert_empty model.requests
  end

  def test_app03_ctrl_c_stops_the_reply_and_the_session_goes_on
    later = ScriptedModel.new(ScriptedModel.text('Hello again.'))
    model = StallingModel.new('Let me think about every book ', later)

    transcript = session(model, 'Sam', 'Tell me everything.', 'Hello?', '/quit', interrupt_on: 'every book ')

    assert_in_order transcript, "assistant> Let me think about every book \n[Cancelled.]\n", "you> Hello?\n",
                    "assistant> Hello again.\n"
    # The cancelled exchange left nothing behind: the next request holds the new message and the run context.
    messages = later.requests.first.messages

    assert_equal %i[user operator], messages.map(&:role)
    assert_equal 'Hello?', messages.first.text
  end

  def test_app03_ctrl_c_at_the_approval_prompt_stops_the_reply_at_once_and_the_change_is_not_made
    copies = stock(320)
    restock = call('c1', 'restock_book', '{"bookId":320,"quantity":2}')
    model = ScriptedModel.new(say_then_call("I'll add two copies.", restock), ScriptedModel.text('Hello again.'))

    transcript = session(model, 'Sam', 'Restock book 320 with 2.', nil, 'Hello?', '/quit',
                         interrupt_on: 'Approve? [y/N] ')

    # The line typed after Ctrl+C is the next message, not an answer to the abandoned prompt.
    assert_in_order transcript, "    Approve? [y/N] \n",
                    "  < restock_book: error: The call was cancelled while waiting for approval.\n", "[Cancelled.]\n",
                    "you> Hello?\n", "assistant> Hello again.\n"
    assert_equal copies, stock(320)
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
end
