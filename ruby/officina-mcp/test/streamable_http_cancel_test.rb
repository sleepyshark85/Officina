# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'

# A Streamable HTTP request cancelled before it is sent or while it waits: it raises, and leaves nothing running.
class StreamableHttpCancelTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Tool = Sleepyshark::Officina::Testing::FakeMcpTool
  FakeServer = Sleepyshark::Officina::Testing::FakeMcpServer
  ECHO = Tool.new(name: 'echo', handler: ->(input) { input.fetch('text') })

  # The tool answers only once the test lets it, so the request's thread can end before that only if the client ends
  # it.
  def test_mcp04_a_call_cancelled_while_it_waits_raises_ends_its_thread_and_the_next_call_runs
    gate = Thread::Queue.new
    with_client(FakeServer.new(tools: [ECHO, Tool.new(name: 'hang', handler: ->(_) { gate.pop.to_s })])) do |client|
      threads = Thread.list.size
      error = assert_raises(Mcp::Error) { client.call_tool('hang', {}, cancel: CancelledAfter.new(2)) }

      assert_equal threads, Thread.list.size, 'the request left no thread behind'
      gate.close

      assert_equal 'MCP server web, tools/call: cancelled', error.message
      assert_equal 'hi', echo(client)
    ensure
      gate.close
    end
  end

  def test_mcp04_a_call_cancelled_before_it_starts_is_not_sent
    server = FakeServer.new(tools: [ECHO])
    with_client(server) do |client|
      error = assert_raises(Mcp::Error) { client.call_tool('echo', { 'text' => 'hi' }, cancel: CancelledAfter.new(1)) }

      assert_equal 'MCP server web, tools/call: cancelled', error.message
      assert_empty server.calls
    end
  end

  private

  def echo(client) = client.call_tool('echo', { 'text' => 'hi' }).text

  def with_client(server)
    server.serve_http do |url|
      client = Mcp.connect(Mcp::Server.new(name: 'web', url:))
      yield client
    ensure
      client&.close
    end
  end
end
