# frozen_string_literal: true

require 'open3'
require 'rbconfig'
require 'tmpdir'
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

  def test_a_workspace_file_loaded_after_the_helper_that_warns_fails_the_run
    Dir.mktmpdir(nil, __dir__) do |directory|
      warns = File.join(directory, 'warns.rb')
      File.write(warns, "def probe = (unused = 1; nil)\n")
      # Without COVERAGE, so the child does not write over this run's coverage report.
      _, error, status = Open3.capture3({ 'COVERAGE' => nil }, RbConfig.ruby, '-w', '-I', __dir__, '-r', 'test_helper',
                                        warns)

      refute_predicate status, :success?
      assert_match 'Minitest::UnexpectedWarning', error
    end
  end
end
