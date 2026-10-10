# frozen_string_literal: true

require 'socket'
require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'
require_relative 'scripted_http_server'

# The MCP client over Streamable HTTP, against the test kit's fake server on a local port, or a scripted one for what
# the fake never sends: how it reaches a server and what it sends.
class StreamableHttpTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Tool = Sleepyshark::Officina::Testing::FakeMcpTool
  FakeServer = Sleepyshark::Officina::Testing::FakeMcpServer
  Scripted = ScriptedHttpServer
  ECHO = Tool.new(name: 'echo', handler: ->(input) { input.fetch('text') })

  def test_mcp01_an_http_server_agrees_on_the_protocol_in_a_session_and_runs_its_tools
    server = FakeServer.new(tools: [ECHO])
    server.serve_http do |url|
      client = Mcp.connect(Mcp::Server.new(name: 'web', url:))

      assert_equal Mcp::CallResult.new(text: 'hi', error: false), client.call_tool('echo', { 'text' => 'hi' })
      assert_equal [['echo', { 'text' => 'hi' }]], server.calls
      assert_equal({ 'name' => 'officina', 'version' => Sleepyshark::Officina::VERSION }, server.client_info)
    ensure
      client&.close
    end
  end

  def test_mcp01_every_request_carries_the_servers_headers_and_once_agreed_the_version_and_session
    initialized = Scripted.answer(Scripted.message(1, Scripted::INITIALIZED), headers: ['Mcp-Session-Id: s1'])
    answers = [initialized, Scripted::ACCEPTED, Scripted.response(2, { 'content' => [] })]
    requests = Scripted.serve(answers) do |url|
      client = Mcp.connect(Mcp::Server.new(name: 'web', url:, headers: { 'Authorization' => 'Bearer t' }))
      echo(client)
    ensure
      client&.close
    end
    always = { 'content-type' => 'application/json', 'accept' => 'application/json, text/event-stream',
               'accept-encoding' => 'identity', 'authorization' => 'Bearer t' }
    agreed = { 'mcp-protocol-version' => '2025-06-18', 'mcp-session-id' => 's1' }

    assert_equal([always, always.merge(agreed), always.merge(agreed)],
                 requests.map { it.slice(*always.keys, *agreed.keys) })
  end

  def test_mcp01_an_answer_is_an_event_stream_only_when_its_content_type_says_so
    text = ->(id, text) { Scripted.message(id, { 'content' => [{ 'type' => 'text', 'text' => text }] }) }
    event = Scripted.answer("data: #{text.call(2, 'event')}\n\n", type: 'Text/Event-Stream ; charset=utf-8')
    plain = Scripted.answer(text.call(3, 'plain'), type: nil)
    Scripted.serve(Scripted::HANDSHAKE + [event, plain]) do |url|
      client = connect(url)

      assert_equal %w[event plain], Array.new(2) { echo(client) }
    ensure
      client&.close
    end
  end

  # A few lengths rather than generated ones: each takes a connection per page, slow on Windows.
  def test_mcp01_every_page_of_a_tool_list_is_read_in_order
    [0, 1, 3].each do |count|
      names = Array.new(count) { "tool#{it}" }
      FakeServer.new(tools: names.map { Tool.new(name: it, handler: ->(_) { '' }) }).serve_http do |url|
        client = connect(url)

        assert_equal names, client.list_tools(cancel: NeverCancelled.new).map(&:name)
      ensure
        client&.close
      end
    end
  end

  def test_mcp01_a_server_is_reached_at_an_ipv6_address
    Scripted.serve(Scripted.results({ 'content' => [] }), host: '::1') do |url|
      client = connect(url)

      assert_equal '', echo(client)
    ensure
      client&.close
    end
  rescue Errno::EADDRNOTAVAIL
    skip 'This machine has no IPv6 loopback'
  end

  def test_mcp01_an_https_url_is_reached_over_tls
    listener = TCPServer.new('127.0.0.1', 0)
    # The first byte a TLS client sends is that of a handshake record.
    first = Thread.new { listener.accept.then { |connection| connection.read(1).tap { connection.close } } }
    error = assert_raises(Mcp::Error) { connect("https://127.0.0.1:#{listener.addr[1]}/mcp") }

    assert_equal "\x16".b, first.value
    assert_match(/\AMCP server web could not be reached: /, error.message)
  ensure
    listener.close
  end

  def test_mcp01_a_url_that_is_not_http_or_has_no_host_is_refused
    urls = ['ftp://127.0.0.1/mcp', 'http://:8080/mcp', 'http:/mcp']
    messages = urls.map { |url| assert_raises(ArgumentError) { connect(url) }.message }

    assert_equal(urls.map { "MCP server web: #{it} is not an http or https URL" }, messages)
  end

  def test_mcp01_a_closed_connection_sends_nothing_more
    server = FakeServer.new(tools: [ECHO])
    server.serve_http do |url|
      client = connect(url)
      client.close
      error = assert_raises(Mcp::Error) { echo(client) }

      assert_equal 'MCP server web could not be reached: the connection was closed', error.message
      assert_empty server.calls
    end
  end

  private

  def echo(client) = client.call_tool('echo', { 'text' => 'hi' }).text

  def connect(url) = Mcp.connect(Mcp::Server.new(name: 'web', url:))
end
