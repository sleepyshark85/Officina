# frozen_string_literal: true

require_relative 'claude_test_case'

# What a model call streams and returns: text as it arrives, each block kept, the usage, and the stop.
class StreamTest < ClaudeTestCase
  def test_mdl01_mdl05_ctx05_text_streams_and_the_reply_keeps_each_block_with_the_calls_usage
    api = serve(FakeApi.recorded(testdata('claude/thinking-text-tool.sse')))

    events, reply = collect(model(api), hi)

    assert_equal [TextDelta.new(text: 'Looking up '), TextDelta.new(text: '«Café Libro».'),
                  UsageReported.new(usage: Usage.new(input: 12, output: 42, cache_read: 2048, cache_write: 300))],
                 events
    assert_equal [:tool_use, nil], [reply.stop, reply.detail]
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

  def test_mdl06_a_refusal_stops_with_its_category
    api = serve(FakeApi.recorded(testdata('claude/refusal.sse')))

    _, reply = collect(model(api), hi)

    assert_equal [:refusal, 'cyber'], [reply.stop, reply.detail]
  end

  def test_mdl06_a_refusal_stops_the_run_with_its_category
    api = serve(FakeApi.recorded(testdata('claude/refusal.sse')))
    agent = Agent.new(model: model(api), instructions: 'Answer briefly.')

    result = agent.run(Conversation.new, 'Hi')

    assert_equal Stopped.new(reason: :refusal, detail: 'cyber', usage: Usage.new(input: 9, output: 7)), result
  end

  def test_mdl01_stop_reasons_map_by_their_word_and_an_unknown_one_keeps_it
    words = { 'end_turn' => [:end, nil], 'max_tokens' => [:max_tokens, nil],
              'model_context_window_exceeded' => [:context_full, nil], 'pause_turn' => [:unknown, 'pause_turn'] }
    api = serve(*words.keys.map { text_reply(it) })
    claude = model(api)

    stops = words.keys.map do
      _, reply = collect(claude, hi)
      [reply.stop, reply.detail]
    end

    assert_equal words.values, stops
  end

  def test_ctx05_a_message_delta_without_input_counts_keeps_those_the_call_started_with
    api = serve(FakeApi.sse(START.sub('"input_tokens":10', '"input_tokens":10,"cache_read_input_tokens":7'),
                            *text_events('Hi'),
                            '{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}',
                            '{"type":"message_stop"}'))

    events, = collect(model(api), hi)

    assert_equal UsageReported.new(usage: Usage.new(input: 10, output: 3, cache_read: 7)), events.last
  end

  def test_agt05_cancelling_mid_stream_ends_the_call_with_no_reply
    api = serve(FakeApi.sse(START, *text_events('one', 'two', 'three'),
                            '{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}',
                            '{"type":"message_stop"}'))
    cancel = Cancellation.new
    events = []

    reply = model(api).stream(hi, cancel:) do |event|
      events << event
      cancel.cancel
    end

    assert_nil reply
    assert_equal [TextDelta.new(text: 'one')], events
  end

  def test_agt05_a_call_cancelled_before_it_starts_sends_nothing
    api = serve(text_reply)
    cancel = Cancellation.new
    cancel.cancel

    events, reply = collect(model(api), hi, cancel:)

    assert_equal [[], nil, []], [events, reply, api.requests]
  end

  def test_evt01_what_the_consumers_block_raises_passes_through_and_is_not_retried
    host = Class.new(StandardError)
    api = serve(text_reply, text_reply)

    raised = assert_raises(host) { model(api).stream(hi, cancel: Cancellation.new) { raise host, 'the host failed' } }

    assert_equal ['the host failed', 1], [raised.message, api.requests.size]
  end

  def test_evt01_a_consumer_that_leaves_the_block_ends_the_call
    api = serve(FakeApi.sse(START, *text_events('one', 'two'),
                            '{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}',
                            '{"type":"message_stop"}'))
    seen = []

    model(api).stream(hi, cancel: Cancellation.new) do |event|
      seen << event
      break
    end

    assert_equal [TextDelta.new(text: 'one')], seen
  end
end
