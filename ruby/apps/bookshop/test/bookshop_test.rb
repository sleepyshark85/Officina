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
    you> /help
    Commands:
      /help          Show this help.
      /audit [<id>]  Show the audit trail of this session, or of the session with that id.
      /quit          Leave the assistant.
    Anything else is a message to the assistant. Ctrl+C stops a reply in progress.
    you> /quit
  TEXT

  def test_app02_the_command_runs_the_console_without_reaching_the_database_or_the_model_until_a_reply_needs_them
    env = { 'ANTHROPIC_API_KEY' => nil, 'BOOKSHOP_DATABASE' => 'postgres://nobody@127.0.0.1:1/nowhere' }
    output, status = Open3.capture2e(env, RbConfig.ruby, File.expand_path('../exe/bookshop', __dir__),
                                     stdin_data: "Sam\n/help\n/quit\n")

    assert_predicate status, :success?, output
    assert_equal TRANSCRIPT, output
  end
end
