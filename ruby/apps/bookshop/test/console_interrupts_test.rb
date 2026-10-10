# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# Ctrl+C at the console end to end, as a real SIGINT: during a reply, at an approval prompt and between replies.
class ConsoleInterruptsTest < Minitest::Test
  include ConsoleSession

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

  def test_app03_ctrl_c_with_no_reply_in_progress_leaves_as_the_end_of_the_input_does
    model = ScriptedModel.new

    transcript = session(model, 'Sam', nil, 'Not read.', interrupt_on: 'you> ')

    assert_match(/Hello, Sam\.\nyou> \z/, transcript)
    assert_empty model.requests
  end

  def test_app03_ctrl_c_at_the_first_of_two_approvals_asks_no_more
    copies = [stock(320), stock(321)]
    restocks = [call('c1', 'restock_book', '{"bookId":320,"quantity":2}'),
                call('c2', 'restock_book', '{"bookId":321,"quantity":2}')]
    model = ScriptedModel.new(say_then_call("I'll restock both.", *restocks))

    transcript = session(model, 'Sam', 'Restock books 320 and 321 with 2.', nil, '/quit',
                         interrupt_on: 'Approve? [y/N] ')

    assert_equal 1, transcript.scan('Approve? [y/N]').size, transcript
    assert_equal copies, [stock(320), stock(321)]
  end
end
