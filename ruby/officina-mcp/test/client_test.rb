# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'cancellations'
require_relative 'scripted_http_server'

# What the client makes of the results a server answers with, over a scripted HTTP server: the test kit's fake
# server sends only well-formed ones.
class ClientTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Scripted = ScriptedHttpServer
  SCHEMA = { 'type' => 'object' }.freeze
  UNREADABLE = 'MCP server web answered %s with an unreadable result'

  def test_mcp01_a_tool_result_is_its_content_one_item_a_line_with_any_item_that_is_not_text_named
    content = [{ 'type' => 'text', 'text' => 'first' }, { 'type' => 'image', 'data' => 'AA==' },
               { 'type' => 'text', 'text' => 'last' }]
    result = with_client(results({ 'content' => content, 'isError' => true })) { call(it) }

    assert_equal Mcp::CallResult.new(text: "first\n[image content]\nlast", error: true), result
    assert_predicate result, :error?
    assert_predicate result.text, :frozen?
  end

  def test_mcp01_a_tool_result_is_an_error_only_when_it_says_true
    errors = [nil, false, 'true'].map do |is_error|
      result = { 'content' => [], 'isError' => is_error }.compact
      with_client(results(result)) { call(it) }
    end

    assert_equal [Mcp::CallResult.new(text: '', error: false)] * 3, errors
    refute_predicate errors.first, :error?
  end

  def test_mcp04_a_tool_result_without_a_list_of_typed_content_items_is_refused
    unreadable = ['text', [], {}, { 'content' => 'text' }, { 'content' => ['text'] }, { 'content' => [5] },
                  { 'content' => [{ 'text' => 'a' }] }, { 'content' => [{ 'type' => 'text', 'text' => 5 }] },
                  { 'content' => [{ 'type' => 5 }] }, { 'content' => [{ 'type' => 'text' }] }]
    with_client(results(*unreadable)) do |client|
      messages = unreadable.map { assert_raises(Mcp::Error) { call(client) }.message }

      assert_equal [format(UNREADABLE, 'tools/call')] * unreadable.size, messages
    end
  end

  def test_mcp04_an_error_answer_raises_with_its_message_given_as_an_object_or_as_text
    errors = [{ 'code' => -32_602, 'message' => 'no such tool' }, 'no such tool', { 'code' => -32_602 }]
    answers = errors.each_with_index.map { |error, index| Scripted.response(index + 2, error:) }
    with_client(Scripted::HANDSHAKE + answers) do |client|
      messages = errors.map { assert_raises(Mcp::Error) { call(client) }.message }
      expected = 'MCP server web answered tools/call with an error: no such tool'

      assert_equal [expected, expected, expected.delete_suffix('no such tool')], messages
    end
  end

  def test_mcp01_a_listed_tool_without_a_description_has_an_empty_one
    tool = { 'name' => 'echo', 'inputSchema' => SCHEMA }
    tools = with_client(results({ 'tools' => [tool] }), &:list_tools)

    assert_equal [Mcp::Tool.new(name: 'echo', description: '', input_schema: SCHEMA)], tools
    assert_predicate tools, :frozen?
  end

  def test_mcp04_a_tool_list_with_a_tool_that_has_no_name_or_input_schema_is_refused
    pages = [{}, { 'tools' => 'echo' }, { 'tools' => [5] }, { 'tools' => [{ 'inputSchema' => SCHEMA }] },
             { 'tools' => [{ 'name' => 5, 'inputSchema' => SCHEMA }] }, { 'tools' => [{ 'name' => 'echo' }] },
             { 'tools' => [{ 'name' => 'echo', 'inputSchema' => 'object' }] }]
    with_client(results(*pages)) do |client|
      messages = pages.map { assert_raises(Mcp::Error) { client.list_tools }.message }

      assert_equal [format(UNREADABLE, 'tools/list')] * pages.size, messages
    end
  end

  def test_mcp01_a_cursor_that_is_empty_or_not_text_ends_the_tool_list
    ['', 5].each do |cursor|
      requests = Scripted.serve(results({ 'tools' => [], 'nextCursor' => cursor }, { 'tools' => [] })) do |url|
        client = connect(url)

        assert_empty client.list_tools
      ensure
        client&.close
      end

      assert_equal 3, requests.size, 'the handshake and one page'
    end
  end

  def test_mcp04_a_server_that_gives_a_cursor_twice_is_refused
    page = { 'tools' => [], 'nextCursor' => 'more' }
    error = with_client(results(page, page, page)) { |client| assert_raises(Mcp::Error) { client.list_tools } }

    assert_equal 'MCP server web gave the cursor "more" twice', error.message
  end

  def test_mcp04_a_server_that_names_no_protocol_version_is_refused
    Scripted.serve([Scripted.response(1, Scripted::INITIALIZED.except('protocolVersion'))]) do |url|
      error = assert_raises(Mcp::Error) { connect(url) }

      assert_equal 'MCP server web speaks protocol nil; this client speaks 2025-06-18, 2025-03-26, 2024-11-05',
                   error.message
    end
  end

  def test_mcp04_a_connect_already_30_seconds_late_sends_nothing
    requests = Scripted.serve(Scripted::HANDSHAKE) do |url|
      error = assert_raises(Mcp::Error) { Mcp.connect(server(url), clock: readings(0, 30)) }

      assert_equal 'MCP server web did not answer within 30 seconds', error.message
    end

    assert_empty requests
  end

  # Cancelled from its second check on, or late from the clock's third reading on: either comes while initialize is
  # answered, or as the notification that follows is about to be sent.
  def test_mcp04_a_connect_cancelled_or_late_once_initialize_is_answered_raises
    [{ cancel: CancelledAfter.new(2) }, { clock: readings(0, 10, 30) }].each do |options|
      Scripted.serve(Scripted::HANDSHAKE) do |url|
        assert_raises(Mcp::Error) { Mcp.connect(server(url), **options) }
      end
    end
  end

  private

  # A clock that reads each of values in turn, then the last one on.
  def readings(*values) = -> { values.size > 1 ? values.shift : values.first }

  def server(url) = Mcp::Server.new(name: 'web', url:)

  def call(client) = client.call_tool('echo', {})

  # The handshake's answers, then each of results as the response to the request after it.
  def results(*results)
    Scripted::HANDSHAKE + results.each_with_index.map { |result, index| Scripted.response(index + 2, result) }
  end

  def connect(url) = Mcp.connect(Mcp::Server.new(name: 'web', url:))

  def with_client(answers)
    Scripted.serve(answers) do |url|
      client = connect(url)
      return yield client
    ensure
      client&.close
    end
  end
end
