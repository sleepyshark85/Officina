# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'
require_relative 'scripted_http_server'

# What the client makes of the handshake and tool lists a server answers with, over a scripted HTTP server: the test
# kit's fake server sends only well-formed ones.
class ClientTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Scripted = ScriptedHttpServer
  SCHEMA = { 'type' => 'object' }.freeze

  # As the server wrote it, spaces, key order, escapes and number forms included: what every implementation hashes
  # into the prefix fingerprint.
  WRITTEN_SCHEMA = %({ "type" : "object",\n  "properties": {"q": {"type": "string", "pattern": "[{\\"\\u00e9]",
    "description": "Café", "maxLength": 1E2}},\t"required": [ "q" ] })

  def test_mcp01_agt06_a_listed_tool_keeps_its_input_schema_as_written_and_without_a_description_an_empty_one
    page = %(\n {"jsonrpc": "2.0", "id": 2, "result": {"tools": [ {"inputSchema": #{WRITTEN_SCHEMA}, "name": "echo"},
      {"name":"upper","inputSchema":{}} ] } })
    tools = with_client(Scripted::HANDSHAKE + [Scripted.answer(page)], &:list_tools)

    assert_equal [Mcp::Tool.new(name: 'echo', description: '', input_schema: WRITTEN_SCHEMA),
                  Mcp::Tool.new(name: 'upper', description: '', input_schema: '{}')], tools
    assert_predicate tools, :frozen?
  end

  def test_mcp04_a_tool_list_with_a_tool_that_has_no_name_or_input_schema_is_refused
    pages = [{}, { 'tools' => 'echo' }, { 'tools' => [5] }, { 'tools' => [{ 'inputSchema' => SCHEMA }] },
             { 'tools' => [{ 'name' => 5, 'inputSchema' => SCHEMA }] }, { 'tools' => [{ 'name' => 'echo' }] },
             { 'tools' => [{ 'name' => 'echo', 'inputSchema' => 'object' }] }]
    with_client(Scripted.results(*pages)) do |client|
      messages = pages.map { assert_raises(Mcp::Error) { client.list_tools }.message }

      assert_equal ['MCP server web answered tools/list with an unreadable result'] * pages.size, messages
    end
  end

  def test_mcp01_a_cursor_that_is_empty_or_not_text_ends_the_tool_list
    ['', 5].each do |cursor|
      page = { 'tools' => [], 'nextCursor' => cursor }
      requests = Scripted.serve(Scripted.results(page, { 'tools' => [] })) do |url|
        client = connect(url)

        assert_empty client.list_tools
      ensure
        client&.close
      end

      assert_equal 3, requests.size, 'the handshake and one page'
    end
  end

  def test_mcp04_a_response_with_neither_a_result_nor_an_error_is_unreadable
    error = with_client(Scripted::HANDSHAKE + [Scripted.answer('{"jsonrpc":"2.0","id":2,"error":null}')]) do |client|
      assert_raises(Mcp::Error) { client.list_tools }
    end

    assert_equal 'MCP server web answered tools/list with an unreadable result', error.message
  end

  def test_mcp04_a_server_that_gives_a_cursor_twice_is_refused
    page = { 'tools' => [], 'nextCursor' => 'more' }
    error = with_client(Scripted.results(page, page, page)) { |client| assert_raises(Mcp::Error) { client.list_tools } }

    assert_equal 'MCP server web gave the cursor "more" twice', error.message
  end

  def test_mcp04_a_server_that_names_no_protocol_version_is_refused
    Scripted.serve([Scripted.response(1, Scripted::INITIALIZED.except('protocolVersion'))]) do |url|
      error = assert_raises(Mcp::Error) { connect(url) }

      assert_equal 'MCP server web speaks protocol nil; this client speaks 2025-06-18, 2025-03-26, 2024-11-05',
                   error.message
    end
  end

  def test_mcp04_a_cancelled_ping_is_not_sent
    # Only the handshake is answered: a ping sent would find the connection closed.
    error = with_client(Scripted::HANDSHAKE) do |client|
      assert_raises(Mcp::Error) { client.ping(cancel: CancelledAfter.new(1)) }
    end

    assert_equal 'MCP server web, ping: cancelled', error.message
  end

  private

  def server(url) = Mcp::Server.new(name: 'web', url:)

  def connect(url) = Mcp.connect(server(url))

  def with_client(answers)
    Scripted.serve(answers) do |url|
      client = connect(url)
      return yield client
    ensure
      client&.close
    end
  end
end
