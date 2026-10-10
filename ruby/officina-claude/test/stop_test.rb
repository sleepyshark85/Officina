# frozen_string_literal: true

require_relative 'claude_test_case'

# How a reply stops: by the API's word, or as a refusal with its category or none.
class StopTest < ClaudeTestCase
  def test_mdl01_stop_reasons_map_by_their_word_and_an_unknown_one_keeps_it
    words = { 'end_turn' => [:end, nil], 'max_tokens' => [:max_tokens, nil],
              'model_context_window_exceeded' => [:context_full, nil], 'refusal' => [:refusal, nil],
              'pause_turn' => [:unknown, 'pause_turn'] }
    api = serve(*words.keys.map { text_reply(it) })
    claude = model(api)

    stops = words.keys.map do
      _, reply = collect(claude, hi)
      [reply.stop, reply.detail]
    end

    assert_equal words.values, stops
  end

  def test_mdl06_a_refusal_stops_with_its_category
    api = serve(FakeApi.recorded(testdata('claude/refusal.sse')))

    _, reply = collect(model(api), hi)

    assert_equal :refusal, reply.stop
    assert_equal 'cyber', reply.detail
  end

  def test_mdl06_a_refusal_with_no_category_stops_with_no_detail
    api = serve(FakeApi.sse(START, *text_events('No.'),
                            '{"type":"message_delta","delta":{"stop_reason":"refusal","stop_details":' \
                            '{"type":"refusal","category":null}},"usage":{"output_tokens":2}}',
                            '{"type":"message_stop"}'))

    _, reply = collect(model(api), hi)

    assert_equal :refusal, reply.stop
    assert_nil reply.detail
  end

  def test_mdl06_a_refusal_stops_the_run_with_its_category
    api = serve(FakeApi.recorded(testdata('claude/refusal.sse')))
    agent = Agent.new(model: model(api), instructions: 'Answer briefly.')

    result = agent.run(Conversation.new, 'Hi')

    assert_equal Stopped.new(reason: :refusal, detail: 'cyber', usage: Usage.new(input: 9, output: 7)), result
  end
end
