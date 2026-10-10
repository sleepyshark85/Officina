# frozen_string_literal: true

require 'test_helper'

# What a Cancellation tells those waiting on it.
class CancellationCallbackTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  def test_agt05_cancelling_calls_each_callback_once
    cancel = Cancellation.new
    calls = []
    cancel.on_cancel { calls << :first }
    cancel.on_cancel { calls << :second }

    2.times { cancel.cancel }

    assert_predicate cancel, :cancelled?
    assert_equal %i[first second], calls
  end

  def test_agt05_a_callback_given_after_cancelling_runs_at_once
    cancel = Cancellation.new
    cancel.cancel
    calls = []

    cancel.on_cancel { calls << :late }

    assert_equal [:late], calls
  end

  def test_agt05_no_callback_runs_until_cancelling
    cancel = Cancellation.new
    calls = []

    cancel.on_cancel { calls << :early }

    refute_predicate cancel, :cancelled?
    assert_empty calls
  end
end
