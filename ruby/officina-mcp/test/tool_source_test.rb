# frozen_string_literal: true

require 'test_helper'
require_relative 'mcp_sources'

# An MCP server's tools as the core's: allowed, named and pinned as the host says, and their calls' results.
class ToolSourceTest < Minitest::Test
  include McpSources

  cover 'Sleepyshark::Officina::Mcp*'

  UPPER = FakeTool.new(name: 'upper', handler: ->(input) { input.fetch('text').upcase }, description: 'Shouts.')

  def test_mcp02_mcp03_only_allowed_tools_appear_named_by_server_and_tool_and_are_writes_unless_the_host_marks_them_read
    allowed = [allow('upper'), allow('echo', kind: :read, needs_approval: true)]
    with_source(FakeServer.new(tools: [ECHO, UPPER, FakeTool.new(name: 'other', handler: ->(_) { '' })]),
                *allowed) do |source|
      tools = source.tools

      assert_equal([['fake__upper', 'Shouts.', :write, false], ['fake__echo', 'The echo tool.', :read, true]],
                   tools.map { [it.name, it.description, it.kind, it.needs_approval?] })
      assert_equal [JSON.generate(ECHO.input_schema)] * 2, tools.map(&:input_schema)
      assert(tools.all? { it.source.equal?(source) })
      assert_predicate tools, :frozen?
      assert_equal 'fake', source.name
    end
  end

  def test_mcp03_an_allowed_tool_the_server_lacks_fails_the_connection_clearly
    FakeServer.new(tools: [UPPER, ECHO]).serve_http do |url|
      error = assert_raises(Mcp::Error) { Mcp::ToolSource.new(server(url), allowed: [allow('echo'), allow('nope')]) }

      assert_equal 'MCP server fake has no tool nope; it has: echo, upper', error.message
    end
  end

  def test_mcp03_an_allowed_tool_whose_schema_the_core_cannot_use_fails_the_connect_with_why
    odd = FakeTool.new(name: 'odd', handler: ->(_) { '' }, input_schema: { 'type' => 'object', 'not' => {} })
    FakeServer.new(tools: [odd]).serve_http do |url|
      error = assert_raises(Mcp::Error) { Mcp::ToolSource.new(server(url), allowed: [allow('odd')]) }

      assert_match(/\AMCP server fake's tool odd cannot be used: .*not/, error.message)
    end
  end

  def test_mcp03_ctx04_the_tool_list_is_read_once_and_pinned_for_the_conversation
    fake = FakeServer.new(tools: [ECHO])
    with_source(fake, allow('echo', kind: :read)) do |source|
      tools = source.tools
      agent = agent(source, Model.text('One.'), Model.text('Two.'))
      conversation = Officina::Conversation.new

      agent.run(conversation, 'Hi')
      fake.end_session
      agent.run(conversation, 'Hi again')

      assert_same tools, source.tools
      assert_equal 4, conversation.messages.size, 'the second run reconnected, with the prefix the first bound'
    end
  end

  def test_mcp04_a_tool_error_and_a_cancelled_call_keep_the_connection
    failing = FakeTool.new(name: 'fail', handler: ->(_) { raise 'it broke' })
    with_source(FakeServer.new(tools: [failing]), allow('fail')) do |source|
      tool = source.tools.first
      broke = Officina::ToolFailure.new(message: 'it broke')

      assert_equal broke, tool.invoke('{}', Officina::Cancellation.new)
      assert_equal Officina::ToolFailure.new(message: 'MCP server fake, tools/call: cancelled'),
                   tool.invoke('{}', Officina::Cancellation.new.tap(&:cancel))
      assert_equal broke, tool.invoke('{}', Officina::Cancellation.new)
    end
  end

  def test_mcp01_closing_the_source_stops_the_server_and_its_tools_then_give_error_results
    source = Mcp::ToolSource.new(stdio_server, allowed: [allow('echo')])
    source.close

    result = agent(source).run(Officina::Conversation.new, 'Hi')

    assert_equal 'The tool source fs is not available: MCP server fs: the tool source is closed', result.detail
    assert_equal Officina::ToolFailure.new(message: 'MCP server fs is not connected'),
                 source.tools.first.invoke('{"text":"hi"}', Officina::Cancellation.new)
  end
end
