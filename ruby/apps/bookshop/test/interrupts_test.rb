# frozen_string_literal: true

require 'bookshop'
require 'test_helper'

# Ctrl+C's handling on its own.
class InterruptsTest < Minitest::Test
  # Private to the application's console.
  Interrupts = Bookshop.const_get(:Interrupts)

  def test_app03_watching_puts_back_what_ctrl_c_did_before
    before = proc {}
    outer = Signal.trap('INT', before)
    begin
      Interrupts.new(idle: -> {}).watch { :done }
    ensure
      after = Signal.trap('INT', outer)
    end

    assert_same before, after
  end
end
