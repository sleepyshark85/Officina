# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'
require_relative 'scripted_http_server'

# A Streamable HTTP server that fails, refuses or keeps a request waiting: what the client says of it.
class StreamableHttpFailureTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Tool = Sleepyshark::Officina::Testing::FakeMcpTool
  FakeServer = Sleepyshark::Officina::Testing::FakeMcpServer
  Scripted = ScriptedHttpServer
  ECHO = Tool.new(name: 'echo', handler: ->(input) { input.fetch('text') })

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
    reports = Thread.report_on_exception
    with_client(server) do |client|
      messages = []
      # The request's own thread ends with the failure, and says nothing of it.
      assert_silent do
        messages = [['crash', {}], ['echo', { 'text' => 'hi' }]].map do |name, arguments|
          assert_raises(Mcp::Error) { client.call_tool(name, arguments) }.message
        end
      end

      assert_match(/\AMCP server web could not be reached: /, messages.first)
      assert_equal [messages.first] * 2, messages
      assert_equal reports, Thread.report_on_exception, 'the setting of every other thread is left alone'
    end
  end

  def test_mcp04_a_session_the_server_ends_loses_the_connection
    server = FakeServer.new(tools: [ECHO])
    with_client(server) do |client|
      server.end_session
      error = assert_raises(Mcp::Error) { echo(client) }

      assert_equal 'MCP server web could not be reached: the server ended the session', error.message
    end
  end

  def test_mcp04_a_server_that_answers_with_what_is_not_http_raises_and_stays_lost
    garbage = ["garbage\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: many\r\n\r\n{}"]
    garbage.each do |answer|
      messages = []
      requests = Scripted.serve(Scripted::HANDSHAKE + [answer, Scripted.response(3, { 'content' => [] })]) do |url|
        client = connect(url)
        messages = Array.new(2) { assert_raises(Mcp::Error) { echo(client) }.message }
      ensure
        client&.close
      end

      assert_match(/\AMCP server web could not be reached: wrong/, messages.first)
      assert_equal [messages.first, 3], [messages.last, requests.size], 'the second call is not sent'
    end
  end

  def test_mcp04_a_404_before_a_session_is_a_wrong_url_not_an_ended_session
    Scripted.serve(["HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"]) do |url|
      error = assert_raises(Mcp::Error) { connect(url) }

      assert_equal 'MCP server web could not be reached: the server answered HTTP 404 Not Found', error.message
    end
  end

  def test_mcp04_a_server_that_speaks_another_protocol_version_is_refused
    FakeServer.new(tools: [ECHO], protocol_version: '2099-01-01').serve_http do |url|
      error = assert_raises(Mcp::Error) { connect(url) }

      assert_equal 'MCP server web speaks protocol "2099-01-01"; this client speaks 2025-06-18, 2025-03-26, ' \
                   '2024-11-05', error.message
    end
  end

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
