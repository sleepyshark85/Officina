# frozen_string_literal: true

require 'fileutils'
require 'open3'
require 'rbconfig'
require 'test_helper'
require 'tmpdir'

# The Bookshop Assistant command.
class BookshopTest < Minitest::Test
  TRANSCRIPT = <<~TEXT
    Bookshop Assistant. Type /help for commands.
    Who is using the assistant? Your name: Sam
    Hello, Sam.
    Session <id>.
    you> /help
    Commands:
      /help          Show this help.
      /new           Start a new session.
      /sessions      List the latest sessions.
      /resume <id>   Go on with the session with that id.
      /cost          Show this session's tokens and cost.
      /audit [<id>]  Show the audit trail of this session, or of the session with that id.
      /memory        Show what the assistant remembers for you.
      /quit          Leave the assistant.
    Anything else is a message to the assistant. Ctrl+C stops a reply in progress.
    you> /quit
  TEXT

  # No model, no database and no export server.
  OFFLINE = { 'ANTHROPIC_API_KEY' => nil, 'BOOKSHOP_DATABASE' => 'postgres://nobody@127.0.0.1:1/nowhere',
              'BOOKSHOP_EXPORTS' => '' }.freeze
  # An export server's endpoint where nothing listens.
  NO_SERVER = 'http://127.0.0.1:1/mcp'
  COMMAND = File.expand_path('../exe/bookshop', __dir__)

  def test_app02_the_command_runs_the_console_without_reaching_the_database_or_the_model_until_a_reply_needs_them
    output, status = bookshop(stdin_data: "Sam\n/help\n/quit\n")

    assert_predicate status, :success?, output
    assert_equal TRANSCRIPT, output.sub(/^Session \h{12}\.$/, 'Session <id>.')
  end

  def test_app17_the_command_with_demo_says_so_at_the_start
    output, status = bookshop('--demo', stdin_data: "Sam\n/quit\n")

    assert_predicate status, :success?, output
    assert_equal "Bookshop Assistant. Type /help for commands.\n" \
                 "Demo mode: compaction from 50,000 input tokens, and old tool results cleared above 12 tool calls.\n" \
                 "Who is using the assistant? Your name: Sam\n",
                 output.lines.first(3).join
  end

  def test_app17_the_command_refuses_an_argument_it_does_not_know_and_fails
    output, status = bookshop('--demo', '--fast', stdin_data: '')

    assert_equal 1, status.exitstatus
    assert_equal "Usage: bookshop [--demo]\n", output
  end

  def test_app14_the_command_refuses_a_reply_budget_that_is_not_an_amount_in_one_line_and_fails
    output, status = bookshop(env: { 'BOOKSHOP_REPLY_BUDGET' => 'abc' }, stdin_data: '')

    assert_equal 1, status.exitstatus
    assert_equal %(BOOKSHOP_REPLY_BUDGET "abc" is not an amount of US dollars above zero\n), output
  end

  def test_app12_mcp04_the_command_fails_clearly_when_the_export_server_cannot_be_reached
    output, status = bookshop(env: { 'BOOKSHOP_EXPORTS' => NO_SERVER }, stdin_data: '')
    why, how = output.lines

    assert_equal 1, status.exitstatus
    assert_match(/\AThe export server at #{NO_SERVER} cannot be used: MCP server filesystem could not be reached: /o,
                 why)
    assert_equal 'If it is not running, start it with ./start.sh (or pwsh -File start.ps1) in ' \
                 'bookshop/; or set BOOKSHOP_EXPORTS to its endpoint, or to nothing to go without ' \
                 "exports.\n", how
  end

  def test_app12_the_command_refuses_an_export_server_setting_that_is_not_an_http_url_and_fails
    output, status = bookshop(env: { 'BOOKSHOP_EXPORTS' => 'localhost:18800' }, stdin_data: '')

    assert_equal 1, status.exitstatus
    assert_equal %(BOOKSHOP_EXPORTS "localhost:18800" cannot be used: MCP server filesystem: localhost:18800 is not ) +
                 "an http or https URL\n", output
  end

  def test_the_command_reads_the_env_file_beside_it
    output, status = bookshop(env: { 'BOOKSHOP_REPLY_BUDGET' => nil }, dot_env: "BOOKSHOP_REPLY_BUDGET=abc\n",
                              stdin_data: '')

    assert_equal 1, status.exitstatus
    assert_equal %(BOOKSHOP_REPLY_BUDGET "abc" is not an amount of US dollars above zero\n), output
  end

  def test_a_variable_set_in_the_shell_wins_over_the_env_file
    output, status = bookshop(env: { 'BOOKSHOP_REPLY_BUDGET' => '0.01' }, dot_env: "BOOKSHOP_REPLY_BUDGET=abc\n",
                              stdin_data: "Sam\n/quit\n")

    assert_predicate status, :success?, output
    assert_includes output, "Hello, Sam.\n"
  end

  private

  # Runs a copy of exe/bookshop with the arguments, offline unless +env+ says otherwise, and returns its output and
  # status. The copy is in a temporary application folder, with a .env holding +dot_env+ if given, so a developer's
  # own .env never reaches the tests.
  def bookshop(*, stdin_data:, env: {}, dot_env: nil)
    Dir.mktmpdir do |folder|
      command = File.join(folder, 'exe', 'bookshop')
      FileUtils.mkdir(File.dirname(command))
      FileUtils.cp(COMMAND, command)
      File.write(File.join(folder, '.env'), dot_env) if dot_env
      Open3.capture2e(OFFLINE.merge(env), RbConfig.ruby, command, *, stdin_data:)
    end
  end
end
