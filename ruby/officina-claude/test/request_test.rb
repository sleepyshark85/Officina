# frozen_string_literal: true

require_relative 'claude_test_case'

# How a request reaches the API: the shared golden layout, stored blocks byte for byte, and the other kinds of
# message.
class RequestTest < ClaudeTestCase
  SEARCH = '{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}'
  ADD = '{"type":"object","properties":{"isbn":{"type":"string"},"copies":{"type":"integer"}},"required":["isbn"]}'

  def test_ctx01_ctx02_ctx03_mdl02_mdl03_a_runs_request_matches_the_shared_golden_layout
    api = serve(FakeApi.recorded(testdata('claude/thinking-text.sse')), text_reply)
    claude = model(api, effort: :high, max_output_tokens: 8000, prefix_cache: '1h', conversation_cache: '5m')
    # The tools are given out of order: the request has them sorted.
    agent = Agent.new(model: claude, instructions: 'You are the assistant of a bookshop.', tools: [
                        tool('search', 'Searches the catalogue.', SEARCH),
                        tool('add_to_cart', 'Adds a book to the cart.', ADD)
                      ])
    conversation = Conversation.new

    ['Is Gaudy Night in stock?', 'And its price?'].each do |message|
      assert_instance_of Completed, agent.run(conversation, message, context: 'Today is 2026-10-05.')
    end

    assert_equal '/v1/messages?beta=true', api.requests[1].target
    sent = JSON.parse(api.bodies[1])

    assert sent.delete('stream')
    assert_equal JSON.parse(testdata('claude/request-layout.json')), sent
    # Parsed JSON hides how blocks are written: the stored ones must be on the wire byte for byte.
    conversation.messages[2].blocks.each { assert_includes api.bodies[1], it.raw }
  end

  def test_ctx01_a_request_without_tools_is_laid_out_as_dotnets_is
    # What the .NET implementation sends for the same request, captured from its fake API: an empty tool list
    # included.
    dotnet = '{"model":"claude-opus-5-5","max_tokens":64000,"thinking":{"type":"adaptive"},' \
             '"output_config":{"effort":"medium"},"cache_control":{"type":"ephemeral","ttl":"5m"},"tools":[],' \
             '"system":[{"type":"text","text":"Answer briefly.","cache_control":{"type":"ephemeral","ttl":"5m"}}],' \
             '"messages":[{"role":"user","content":[{"type":"text","text":"Hi"}]}],"stream":true}'
    api = serve(text_reply)

    collect(model(api), hi)

    assert_equal JSON.parse(dotnet), JSON.parse(api.bodies[0])
  end

  def test_mdl02_the_api_key_given_is_sent
    api = serve(text_reply)

    collect(model(api), hi)

    assert_equal 'test-key', api.requests[0].headers['x-api-key']
  end

  def test_mdl05_blocks_the_dotnet_implementation_stored_reach_the_wire_byte_for_byte
    stored = JSON.parse(testdata('claude/blocks.json'))
    assistant = Message.new(role: :assistant, blocks: [Block.new(raw: stored['thinking']),
                                                       Block.new(raw: stored['text'],
                                                                 text: 'Looking up «Café Libro».')])
    api = serve(text_reply)

    collect(model(api), Request.new(tools: [], instructions: 'Answer briefly.',
                                    messages: [*hi.messages, assistant, *hi('And its price?').messages]))

    assert_includes api.bodies[0], %({"role":"assistant","content":[#{stored['thinking']},#{stored['text']}]})
  end

  def test_mdl05_a_reply_is_stored_canonical_and_replayed_byte_for_byte
    api = serve(FakeApi.sse(START, *text_events('<b>Fish & chips</b>', ' cost 5 € to <you>'),
                            '{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}',
                            '{"type":"message_stop"}'),
                text_reply)
    claude = model(api)
    _, reply = collect(claude, hi)

    collect(claude, Request.new(tools: [], instructions: 'Answer briefly.',
                                messages: [*hi.messages, Message.new(role: :assistant, blocks: reply.blocks),
                                           *hi('Thanks.').messages]))

    raw = reply.blocks[0].raw

    assert_equal esc('{"text":"%u003Cb%u003EFish %u0026 chips%u003C/b%u003E cost 5 € to %u003Cyou%u003E",' \
                     '"type":"text"}'), raw
    assert_includes api.bodies[1], %({"role":"assistant","content":[#{raw}]})
  end

  def test_ctx06_tool_results_go_out_as_one_user_message_of_tool_result_blocks
    call = ToolCall.new(id: 'toolu_01', name: 'search', input: '{"q":"Emma"}')
    use = Block.new(raw: '{"id":"toolu_01","input":{"q":"Emma"},"name":"search","type":"tool_use"}', tool_call: call)
    results = [ToolResult.new(call_id: 'toolu_01', content: '3 copies <new>', error: false),
               ToolResult.new(call_id: 'toolu_02', content: 'failed', error: true)]
    api = serve(text_reply)
    messages = [*hi.messages, Message.new(role: :assistant, blocks: [use]),
                Message.new(role: :user, blocks: results.map { Block.new(tool_result: it) })]

    collect(model(api), Request.new(tools: [], instructions: 'Answer briefly.', messages:))

    expected = { 'role' => 'user', 'content' => [
      { 'type' => 'tool_result', 'tool_use_id' => 'toolu_01', 'content' => '3 copies <new>' },
      { 'type' => 'tool_result', 'tool_use_id' => 'toolu_02', 'content' => 'failed', 'is_error' => true }
    ] }

    assert_equal expected, JSON.parse(api.bodies[0])['messages'][2]
    assert_includes api.bodies[0], esc('3 copies %u003Cnew%u003E')
  end

  private

  def tool(name, description, schema)
    Tool.new(name:, description:, input: Schema.new(schema), kind: :read) { 'Found.' }
  end
end
