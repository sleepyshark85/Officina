# frozen_string_literal: true

require 'open3'
require 'rbconfig'
require 'test_helper'

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
                 'apps/BookshopAssistant; or set BOOKSHOP_EXPORTS to its endpoint, or to nothing to go without ' \
                 "exports.\n", how
  end

  def test_app12_the_command_refuses_an_export_server_setting_that_is_not_an_http_url_and_fails
    output, status = bookshop(env: { 'BOOKSHOP_EXPORTS' => 'localhost:18800' }, stdin_data: '')

    assert_equal 1, status.exitstatus
    assert_equal %(BOOKSHOP_EXPORTS "localhost:18800" cannot be used: MCP server filesystem: localhost:18800 is not ) +
                 "an http or https URL\n", output
  end

  private

  # Runs exe/bookshop with the arguments, offline unless +env+ says otherwise, and returns its output and status.
  def bookshop(*, stdin_data:, env: {})
    Open3.capture2e(OFFLINE.merge(env), RbConfig.ruby, File.expand_path('../exe/bookshop', __dir__), *, stdin_data:)
  end
end
