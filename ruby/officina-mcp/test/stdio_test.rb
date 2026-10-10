# frozen_string_literal: true

require 'test_helper'
require_relative 'cancellations'
require_relative 'stdio_connections'

# The MCP client over stdio, against the test kit's fake server run as a child process: its requests and what it makes
# of the answers.
class StdioTest < Minitest::Test
  include StdioConnections

  cover 'Sleepyshark::Officina::Mcp*'

  def test_mcp01_a_stdio_server_agrees_on_the_protocol_lists_every_page_of_its_tools_and_runs_them
    with_client do |client|
      tools = client.list_tools(cancel: NeverCancelled.new)
      echo = '{"type":"object","properties":{"text":{"type":"string"}}}'

      assert_equal %w[echo upper fail crash stray cut hang], tools.map(&:name)
      assert_equal Mcp::Tool.new(name: 'echo', description: 'The echo tool.', input_schema: echo), tools.first
      assert_equal Mcp::CallResult.new(text: 'HI', error: false), client.call_tool('upper', { 'text' => 'hi' })
    end
  end

  def test_mcp01_requests_from_several_threads_each_get_their_own_response
    # The server answers each pair of requests in reverse, so a response handed to any request but its own shows.
    with_client('reverse') do |client|
      texts = Array.new(8) { |n| "call #{n}" }
      results = texts.map { |text| Thread.new { client.call_tool('echo', { 'text' => text }).text } }.map(&:value)

      assert_equal texts, results
    end
  end

  def test_mcp01_a_response_to_no_request_waiting_is_skipped
    with_client do |client|
      assert_equal 'after', client.call_tool('stray', {}).text
    end
  end

  def test_mcp04_a_tool_list_cancelled_before_it_starts_raises
    with_client do |client|
      error = assert_raises(Mcp::Error) { client.list_tools(cancel: CancelledAfter.new(1)) }

      assert_equal 'MCP server fs, tools/list: cancelled', error.message
    end
  end

  def test_mcp04_a_call_cancelled_while_it_waits_raises
    with_client do |client|
      error = assert_raises(Mcp::Error) { client.call_tool('hang', {}, cancel: CancelledAfter.new(2)) }

      assert_equal 'MCP server fs, tools/call: cancelled', error.message
    end
  end

  def test_mcp04_a_tool_that_fails_gives_an_error_result
    with_client do |client|
      assert_equal Mcp::CallResult.new(text: 'it broke', error: true), client.call_tool('fail', {})
    end
  end

  def test_mcp04_a_request_the_server_refuses_raises_its_error
    with_client do |client|
      error = assert_raises(Mcp::Error) { client.call_tool('missing', {}) }

      assert_equal 'MCP server fs answered tools/call with an error: Method or tool not found.', error.message
      assert_equal 'HI', client.call_tool('upper', { 'text' => 'hi' }).text, 'the connection is kept'
    end
  end

  def test_mcp04_a_server_that_exits_mid_call_raises_with_what_it_said_and_stays_lost
    with_client do |client|
      messages = [['crash', {}], ['echo', { 'text' => 'hi' }]].map do |name, arguments|
        assert_raises(Mcp::Error) { client.call_tool(name, arguments) }.message
      end

      assert_equal ['MCP server fs could not be reached: it closed its connection: out of memory'] * 2, messages
    end
  end
end
