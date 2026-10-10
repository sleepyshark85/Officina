# frozen_string_literal: true

require 'bookshop'
require 'stringio'
require 'test_helper'

# The console's terminal on its own, over a pipe no one writes to.
class TerminalTest < Minitest::Test
  # Private to the application's console.
  Terminal = Bookshop.const_get(:Terminal)

  def setup
    super
    @input, @keyboard = IO.pipe
    @terminal = Terminal.new(input: @input, output: StringIO.new)
  end

  def teardown
    @keyboard.close
    @input.close unless @input.closed?
    super
  end

  # The thread-leak check after the test proves the reader was joined.
  def test_app03_close_stops_and_joins_a_read_a_cancelled_prompt_left_waiting
    cancel = Sleepyshark::Officina::Cancellation.new
    cancel.cancel

    assert_nil @terminal.read('Approve? ', cancel:)

    @terminal.close

    assert_predicate @input, :closed?
  end

  def test_app03_once_the_input_is_ended_a_read_gets_no_line
    @terminal.end_input

    assert_nil @terminal.read('you> ')
  end
end
