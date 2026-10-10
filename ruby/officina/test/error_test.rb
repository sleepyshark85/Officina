# frozen_string_literal: true

require 'test_helper'

# The core's error base class.
class ErrorTest < Minitest::Test
  cover 'Sleepyshark::Officina*'

  def test_officinas_errors_are_standard_errors_so_a_plain_rescue_catches_them
    assert_operator Sleepyshark::Officina::Error, :<, StandardError
  end
end
