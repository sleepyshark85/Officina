# frozen_string_literal: true

require 'open3'
require 'rbconfig'
require 'tmpdir'
require 'test_helper'

# The Rake task that runs Steep, which exits 0 after logging a FATAL for a file it could not check.
class SteepTaskTest < Minitest::Test
  RAKEFILE = File.expand_path('../Rakefile', __dir__)

  def test_a_fatal_line_in_steeps_log_fails_the_task
    error, status = rake_steep("puts '2026-10-10 22:13:14.501: FATAL: [Steep 2.1.0] a file was skipped'")

    refute_predicate status, :success?
    assert_match 'Steep logged a FATAL or ERROR line', error
  end

  def test_an_error_line_in_steeps_log_fails_the_task
    error, status = rake_steep("puts '2026-10-10 22:13:14.501: ERROR: [Steep 2.1.0] a file was skipped'")

    refute_predicate status, :success?
    assert_match 'Steep logged a FATAL or ERROR line', error
  end

  def test_steeps_own_failure_fails_the_task
    error, status = rake_steep('puts "officina/lib/x.rb:1:1: [error] a type error"; exit 1')

    refute_predicate status, :success?
    assert_match 'Steep failed.', error
  end

  def test_a_clean_run_passes
    _, status = rake_steep("puts 'No type error detected.'")

    assert_predicate status, :success?
  end

  def test_a_type_error_mentioning_error_is_not_a_log_line
    _, status = rake_steep("puts 'x.rb:1:1: [error] ERROR: is not a log line'")

    assert_predicate status, :success?
  end

  private

  # Runs `rake steep` with a fake `steep` first on PATH that runs the given Ruby; returns the stderr and the status.
  def rake_steep(steep_ruby)
    Dir.mktmpdir do |directory|
      steep = File.join(directory, 'steep')
      File.write(steep, "#!#{RbConfig.ruby}\n#{steep_ruby}\n")
      File.chmod(0o755, steep)
      env = { 'PATH' => "#{directory}#{File::PATH_SEPARATOR}#{ENV.fetch('PATH')}", 'COVERAGE' => nil }
      _, error, status = Open3.capture3(env, RbConfig.ruby, '-S', 'rake', '-f', RAKEFILE, 'steep')
      [error, status]
    end
  end
end
