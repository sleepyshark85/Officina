# frozen_string_literal: true

require_relative 'claude_test_case'

# What a model call streams and returns: text as it arrives, each block kept, and the usage.
class StreamTest < ClaudeTestCase
  def test_mdl01_mdl05_ctx05_text_streams_and_the_reply_keeps_each_block_with_the_calls_usage
    api = serve(FakeApi.recorded(testdata('claude/thinking-text-tool.sse')))

    events, reply = collect(model(api), hi)

    assert_equal [TextDelta.new(text: 'Looking up '), TextDelta.new(text: '«Café Libro».'),
                  UsageReported.new(usage: Usage.new(input: 12, output: 42, cache_read: 2048, cache_write: 300))],
                 events
    assert_equal :tool_use, reply.stop
    assert_nil reply.detail
  end

  def test_mdl05_each_block_is_kept_as_the_gem_writes_it_in_the_canonical_form_with_its_text_or_call
    api = serve(FakeApi.recorded(testdata('claude/thinking-text-tool.sse')))

    _, reply = collect(model(api), hi)
    thinking, text, use = reply.blocks

    assert_equal '{"signature":"EqQBCkYIBRgCKkB+sig/a==","thinking":"","type":"thinking"}', thinking.raw
    assert_equal 'Looking up «Café Libro».', text.text
    assert_equal '{"text":"Looking up «Café Libro».","type":"text"}', text.raw
    assert_equal ToolCall.new(id: 'toolu_01', name: 'search', input: '{"query":"Gaudy Night"}'), use.tool_call
    assert_equal '{"id":"toolu_01","input":{"query":"Gaudy Night"},"name":"search","type":"tool_use"}', use.raw
  end

  def test_ctx05_a_message_delta_without_input_counts_keeps_those_the_call_started_with
    api = serve(FakeApi.sse(START.sub('"input_tokens":10',
                                      '"input_tokens":10,"cache_read_input_tokens":7,"cache_creation_input_tokens":4'),
                            *text_events('Hi'),
                            '{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}',
                            '{"type":"message_stop"}'))

    events, = collect(model(api), hi)

    assert_equal UsageReported.new(usage: Usage.new(input: 10, output: 3, cache_read: 7, cache_write: 4)), events.last
  end

  def test_ctx05_a_message_deltas_counts_replace_those_the_call_started_with
    api = serve(FakeApi.sse(START, *text_events('Hi'),
                            '{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":4,' \
                            '"output_tokens":2,"cache_read_input_tokens":3,"cache_creation_input_tokens":1}}',
                            '{"type":"message_stop"}'))

    events, = collect(model(api), hi)

    assert_equal UsageReported.new(usage: Usage.new(input: 4, output: 2, cache_read: 3, cache_write: 1)), events.last
  end

  def test_mdl01_a_reply_returns_once_it_stops_and_closes_the_connection
    api = serve(text_reply(ending: :hold))

    _, reply = collect(model(api), hi)

    assert_equal :end, reply.stop
    assert_predicate api, :closed_by_client?
  end
end
