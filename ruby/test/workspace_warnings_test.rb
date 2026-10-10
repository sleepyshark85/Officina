# frozen_string_literal: true

require 'test_helper'

# Which warnings fail the tests.
class WorkspaceWarningsTest < Minitest::Test
  def test_a_warning_about_a_file_of_the_workspace_fails
    assert_raises(Minitest::UnexpectedWarning) { Warning.warn("#{__FILE__}:1: warning: assigned but unused\n") }
  end

  def test_a_warning_about_a_gem_is_only_printed
    warning = "#{WorkspaceWarnings::VENDOR}bundle/gems/x/lib/x.rb:1: warning: assigned but unused\n"
    _, printed = capture_io { Warning.warn(warning) }

    assert_equal warning, printed
  end
end
