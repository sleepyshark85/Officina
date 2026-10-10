# frozen_string_literal: true

require 'test_helper'
require_relative '../steep_verdict'

# What the steep Rake task makes of a Steep run, which exits 0 after logging a FATAL for a file it could not check.
class SteepVerdictTest < Minitest::Test
  FATAL = '2026-10-10 22:13:14.501: FATAL: [Steep 2.1.0] [typecheck:typecheck@6] a file was skipped'

  def test_a_fatal_line_in_steeps_log_fails
    assert_match 'Steep logged a FATAL or ERROR line', SteepVerdict.failure("#{FATAL}\n", true)
  end

  def test_an_error_line_in_steeps_log_fails
    error = FATAL.sub('FATAL', 'ERROR')

    assert_match 'Steep logged a FATAL or ERROR line', SteepVerdict.failure("#{error}\n", true)
  end

  def test_a_log_line_after_the_progress_dots_on_the_same_line_fails
    assert_match 'Steep logged a FATAL or ERROR line', SteepVerdict.failure("...#{FATAL}\n", true)
  end

  def test_steeps_own_failure_fails
    assert_equal 'Steep failed.', SteepVerdict.failure("x.rb:1:1: [error] a type error\n", false)
  end

  def test_a_clean_run_passes
    assert_nil SteepVerdict.failure("...\nNo type error detected.\n", true)
  end

  def test_a_type_error_mentioning_error_is_not_a_log_line
    assert_nil SteepVerdict.failure("x.rb:1:1: [error] ERROR: is not a log line\n", true)
  end
end
