# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'
require_relative 'scripted_http_server'

# What the client makes of the answers to tool calls, over a scripted HTTP server: the test kit's fake server sends
# only well-formed ones.
class ClientResultTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  Scripted = ScriptedHttpServer

  def test_mcp01_a_tool_result_is_its_content_one_item_a_line_with_any_item_that_is_not_text_named
    content = [{ 'type' => 'text', 'text' => 'first' }, { 'type' => 'image', 'data' => 'AA==' },
               { 'type' => 'text', 'text' => 'last' }]
    result = with_client(Scripted.results({ 'content' => content, 'isError' => true })) { call(it) }

    assert_equal Mcp::CallResult.new(text: "first\n[image content]\nlast", error: true), result
    assert_predicate result, :error?
    assert_predicate result.text, :frozen?
  end

  def test_mcp01_a_tool_result_is_an_error_only_when_it_says_true
    errors = [nil, false, 'true'].map do |is_error|
      result = { 'content' => [], 'isError' => is_error }.compact
      with_client(Scripted.results(result)) { call(it) }
    end

    assert_equal [Mcp::CallResult.new(text: '', error: false)] * 3, errors
    refute_predicate errors.first, :error?
  end

  def test_mcp04_a_tool_result_without_a_list_of_typed_content_items_is_refused
    unreadable = ['text', [], {}, { 'content' => 'text' }, { 'content' => ['text'] }, { 'content' => [5] },
                  { 'content' => [{ 'text' => 'a' }] }, { 'content' => [{ 'type' => 'text', 'text' => 5 }] },
                  { 'content' => [{ 'type' => 5 }] }, { 'content' => [{ 'type' => 'text' }] }]
    with_client(Scripted.results(*unreadable)) do |client|
      messages = unreadable.map { assert_raises(Mcp::Error) { call(client) }.message }

      assert_equal ['MCP server web answered tools/call with an unreadable result'] * unreadable.size, messages
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

  private

  def call(client) = client.call_tool('echo', {})

  def with_client(answers)
    Scripted.serve(answers) do |url|
      client = Mcp.connect(Mcp::Server.new(name: 'web', url:))
      return yield client
    ensure
      client&.close
    end
  end
end
