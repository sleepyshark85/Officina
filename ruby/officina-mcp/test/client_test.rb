# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
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

  def test_mcp04_a_tool_result_without_a_list_of_content_items_is_refused
    unreadable = [{}, { 'content' => 'text' }, { 'content' => ['text'] }]
    with_client(results(*unreadable)) do |client|
      messages = unreadable.map { assert_raises(Mcp::Error) { call(client) }.message }

      assert_equal [format(UNREADABLE, 'tools/call')] * 3, messages
    end
  end

  def test_mcp04_an_error_answer_raises_with_its_message_given_as_an_object_or_as_text
    errors = [{ 'code' => -32_602, 'message' => 'no such tool' }, 'no such tool']
    answers = errors.each_with_index.map { |error, index| Scripted.response(index + 2, error:) }
    with_client(Scripted::HANDSHAKE + answers) do |client|
      messages = errors.map { assert_raises(Mcp::Error) { call(client) }.message }

      assert_equal ['MCP server web answered tools/call with an error: no such tool'] * 2, messages
    end
  end

  def test_mcp01_a_listed_tool_without_a_description_has_an_empty_one
    tool = { 'name' => 'echo', 'inputSchema' => SCHEMA }

    assert_equal [Mcp::Tool.new(name: 'echo', description: '', input_schema: SCHEMA)],
                 with_client(results({ 'tools' => [tool] }), &:list_tools)
  end

  def test_mcp04_a_tool_list_with_a_tool_that_has_no_name_or_input_schema_is_refused
    pages = [{ 'tools' => [{ 'inputSchema' => SCHEMA }] }, { 'tools' => [{ 'name' => 'echo' }] },
             { 'tools' => [{ 'name' => 'echo', 'inputSchema' => 'object' }] }, { 'tools' => 'echo' }]
    with_client(results(*pages)) do |client|
      messages = pages.map { assert_raises(Mcp::Error) { client.list_tools }.message }

      assert_equal [format(UNREADABLE, 'tools/list')] * 4, messages
    end
  end

  def test_mcp01_an_empty_cursor_ends_the_tool_list
    answered = Scripted.serve(results({ 'tools' => [], 'nextCursor' => '' }, { 'tools' => [] })) do |url|
      client = connect(url)

      assert_empty client.list_tools
    ensure
      client&.close
    end

    assert_equal 3, answered, 'the handshake and one page'
  end

  def test_mcp04_a_server_that_gives_a_cursor_twice_is_refused
    page = { 'tools' => [], 'nextCursor' => 'more' }
    error = with_client(results(page, page, page)) { |client| assert_raises(Mcp::Error) { client.list_tools } }

    assert_equal 'MCP server web gave the cursor "more" twice', error.message
  end

  private

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
