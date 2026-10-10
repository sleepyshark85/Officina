# frozen_string_literal: true

require 'test_helper'
require_relative 'memory_telemetry'

# Closing the application: the export server's connection is closed, and the telemetry sent even when the database
# fails to close.
class ApplicationTest < Minitest::Test
  Officina = Sleepyshark::Officina
  ScriptedModel = Officina::Testing::ScriptedModel

  # A database whose connections cannot be closed, as when the server has gone.
  class LostDatabase
    def close = raise PG::ConnectionBad, 'the server closed the connection'
  end

  def test_app20_closing_sends_the_telemetry_even_when_the_database_fails_to_close
    memory = MemoryTelemetry.new
    agent = Officina::Agent.new(name: 'bookshop', model: ScriptedModel.new(ScriptedModel.text('Hello.')),
                                instructions: 'Help.', telemetry: memory.telemetry.officina)
    memory.telemetry.reply('c1') { agent.run(Officina::Conversation.new, 'Hi.') { nil } }
    application = Bookshop::Application.new(database: LostDatabase.new, exports: nil, telemetry: memory.telemetry,
                                            console: nil)

    refute_empty memory.spans
    assert_raises(PG::ConnectionBad) { application.close }
    assert_empty memory.spans, 'Closing sends the telemetry, which clears what the exporters hold'
  end

  def test_app12_closing_closes_the_export_servers_connection
    fake = Officina::Testing::FakeMcpServer.new(tools: [Officina::Testing::FakeMcpTool.new(name: 'list_directory',
                                                                                           handler: ->(_) { '' })])
    fake.serve_http do |url|
      exports = Officina::Mcp::ToolSource.new(Officina::Mcp::Server.new(name: 'filesystem', url:),
                                              allowed: [Officina::Mcp::AllowedTool.new(name: 'list_directory')])
      Bookshop::Application.new(database: Bookshop::Database.new('postgres://nobody@127.0.0.1:1/nowhere'), exports:,
                                telemetry: MemoryTelemetry.new.telemetry, console: nil).close

      error = assert_raises(Officina::Mcp::Error) { exports.connect(cancel: Officina::Cancellation.new) }
      assert_equal 'MCP server filesystem: the tool source is closed', error.message
    end
  end
end
