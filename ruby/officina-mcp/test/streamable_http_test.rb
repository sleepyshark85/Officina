# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'

# The MCP client over Streamable HTTP, against the test kit's fake server on a local port.
class StreamableHttpTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Tool = Sleepyshark::Officina::Testing::FakeMcpTool
  FakeServer = Sleepyshark::Officina::Testing::FakeMcpServer
  ECHO = Tool.new(name: 'echo', handler: ->(input) { input.fetch('text') })

  def test_mcp01_an_http_server_agrees_on_the_protocol_in_a_session_and_runs_its_tools
    server = FakeServer.new(tools: [ECHO])
    with_client(server) do |client|
      assert_equal Mcp::CallResult.new(text: 'hi', error: false), client.call_tool('echo', { 'text' => 'hi' })
      assert_equal [['echo', { 'text' => 'hi' }]], server.calls
    end
  end

  # A few lengths rather than generated ones: each takes a connection per page, slow on Windows.
  def test_mcp01_every_page_of_a_tool_list_is_read_in_order
    [0, 1, 3].each do |count|
      names = Array.new(count) { "tool#{it}" }
      server = FakeServer.new(tools: names.map { Tool.new(name: it, handler: ->(_) { '' }) })

      with_client(server) { |client| assert_equal names, client.list_tools.map(&:name) }
    end
  end

  def test_mcp04_an_http_server_down_at_connect_raises_clearly
    server = FakeServer.new(tools: [ECHO])
    server.go_down
    server.serve_http do |url|
      error = assert_raises(Mcp::Error) { connect(url) }

      assert_match(/\AMCP server web could not be reached: /, error.message)
    end
  end

  def test_mcp04_an_http_server_that_goes_down_mid_call_raises_and_stays_lost
    server = FakeServer.new(tools: [ECHO, Tool.new(name: 'crash', handler: ->(_) { server.go_down })])
    with_client(server) do |client|
      messages = [['crash', {}], ['echo', { 'text' => 'hi' }]].map do |name, arguments|
        assert_raises(Mcp::Error) { client.call_tool(name, arguments) }.message
      end

      assert_match(/\AMCP server web could not be reached: /, messages.first)
      assert_equal [messages.first] * 2, messages
    end
  end

  def test_mcp04_a_session_the_server_ends_loses_the_connection
    server = FakeServer.new(tools: [ECHO])
    with_client(server) do |client|
      server.end_session
      error = assert_raises(Mcp::Error) { client.call_tool('echo', { 'text' => 'hi' }) }

      assert_equal 'MCP server web could not be reached: the server ended the session', error.message
    end
  end

  def test_mcp04_a_server_that_speaks_another_protocol_version_is_refused
    FakeServer.new(tools: [ECHO], protocol_version: '2099-01-01').serve_http do |url|
      error = assert_raises(Mcp::Error) { connect(url) }

      assert_equal 'MCP server web speaks protocol "2099-01-01"; this client speaks 2025-06-18, 2025-03-26, ' \
                   '2024-11-05', error.message
    end
  end

  def test_mcp04_a_call_cancelled_while_it_waits_raises_and_the_next_call_runs
    gate = Thread::Queue.new
    hang = Tool.new(name: 'hang', handler: ->(_) { gate.pop.to_s })
    with_client(FakeServer.new(tools: [ECHO, hang])) do |client|
      error = assert_raises(Mcp::Error) { client.call_tool('hang', {}, cancel: CancelledAfter.new(2)) }
      gate.close

      assert_equal 'MCP server web, tools/call: cancelled', error.message
      assert_equal 'hi', echo(client)
    ensure
      gate.close
    end
  end

  def test_mcp01_a_url_that_is_not_http_is_refused
    assert_raises(ArgumentError) { connect('ftp://127.0.0.1/mcp') }
  end

  private

  def echo(client) = client.call_tool('echo', { 'text' => 'hi' }).text

  def connect(url) = Mcp.connect(Mcp::Server.new(name: 'web', url:))

  def with_client(server)
    server.serve_http do |url|
      client = connect(url)
      yield client
    ensure
      client&.close
    end
  end
end
