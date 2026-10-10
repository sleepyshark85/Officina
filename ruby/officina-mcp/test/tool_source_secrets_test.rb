# frozen_string_literal: true

require 'test_helper'
require_relative 'mcp_sources'

# An MCP server's credentials never leave its tool source: its tools' results and the reasons it reports have them
# redacted, whether or not the host gave them to the agent as secrets.
class ToolSourceSecretsTest < Minitest::Test
  include McpSources

  cover 'Sleepyshark::Officina::Mcp*'

  def test_mcp02_the_servers_credentials_are_redacted_from_its_tools_results
    leak = FakeTool.new(name: 'leak', handler: ->(_) { 'the key is tok-123' })
    failing = FakeTool.new(name: 'fail', handler: ->(_) { raise 'tok-123 is refused' })
    FakeServer.new(tools: [leak, failing]).serve_http do |url|
      server = Mcp::Server.new(name: 'fake', url:, headers: { 'Authorization' => 'tok-123' })
      source = Mcp::ToolSource.new(server, allowed: [allow('leak'), allow('fail')])

      results = source.tools.map { it.invoke('{}', Officina::Cancellation.new) }

      assert_equal ['the key is [redacted]', Officina::ToolFailure.new(message: '[redacted] is refused')], results
    ensure
      source&.close
    end
  end

  def test_mcp04_the_reason_a_server_was_lost_has_its_credentials_redacted
    server = Mcp::Server.new(name: 'fs', command: STDIO, env: { 'RUBYOPT' => nil, 'FS_KEY' => 'memory' })
    source = Mcp::ToolSource.new(server, allowed: [allow('crash')])
    sink = Sink.new
    conversation = Officina::Conversation.new

    agent(source, Model.tool_use(call('1', 'fs__crash', '{}')), Model.text('Sorry.'), audit_sink: sink)
      .run(conversation, 'Go')
    reason = 'MCP server fs could not be reached: it closed its connection: out of [redacted]'

    assert_equal [reason, ['disconnected', reason]], [results(conversation)[0].content, changes(sink)[1]]
  ensure
    source&.close
  end
end
