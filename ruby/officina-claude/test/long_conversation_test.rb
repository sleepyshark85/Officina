# frozen_string_literal: true

require_relative 'claude_test_case'

# Claude's server-side context management: what a request asks for, what a reply reports, and the compaction block
# kept and sent back as received.
class LongConversationTest < ClaudeTestCase
  # Context management that asks for clearing and compaction, as .NET's tests do.
  BOTH = ContextManagement.new(compact_at: 50_000, clear_tool_results: ToolResultClearing.new(
    after: 3, keep: 1, at_least_tokens: 5_000
  ))

  # What .NET sends for each of these settings, from its own test of them, and the betas they need.
  DOTNET = {
    BOTH => ['{"edits":[{"type":"clear_tool_uses_20250919","trigger":{"type":"tool_uses","value":3},' \
             '"keep":{"type":"tool_uses","value":1},"clear_at_least":{"type":"input_tokens","value":5000}},' \
             '{"type":"compact_20260112","trigger":{"type":"input_tokens","value":50000}}]}',
             %w[context-management-2025-06-27 compact-2026-01-12]],
    ContextManagement.new(compact_at: 60_000) =>
      ['{"edits":[{"type":"compact_20260112","trigger":{"type":"input_tokens","value":60000}}]}',
       %w[compact-2026-01-12]],
    ContextManagement.new(clear_tool_results: ToolResultClearing.new(after: 12)) =>
      ['{"edits":[{"type":"clear_tool_uses_20250919","trigger":{"type":"tool_uses","value":12},' \
       '"keep":{"type":"tool_uses","value":0}}]}', %w[context-management-2025-06-27]]
  }.freeze

  def test_hist01_hist02_context_management_asks_for_clearing_then_threshold_compaction_with_their_betas
    DOTNET.each do |context_management, (dotnet, betas)|
      request = sent(hi.with(context_management:))

      assert_equal JSON.parse(dotnet), JSON.parse(request.body)['context_management']
      assert_equal betas, request.headers['anthropic-beta'].split(', ')
    end
  end

  def test_hist01_without_context_management_the_request_has_none_and_no_betas
    [nil, ContextManagement.new].each do |context_management|
      request = sent(hi.with(context_management:))

      refute JSON.parse(request.body).key?('context_management')
      assert_nil request.headers['anthropic-beta']
    end
  end

  def test_hist04_a_compaction_is_reported_from_its_iteration_and_priced_with_the_reply
    api = serve(FakeApi.recorded(testdata('claude/compaction-iterations.sse')))

    events, reply = collect(model(api), hi)

    # The compaction iteration read 48 + 2,615 cached + 50,090 written tokens, and wrote a 578-token summary.
    assert_equal [ConversationCompacted.new(tokens: 52_753, summary_tokens: 578),
                  UsageReported.new(usage: Usage.new(input: 50, output: 663, cache_read: 5_230, cache_write: 50_648))],
                 events.last(2)
    assert_equal [nil, 'Shelf Q2.'], reply.blocks.map(&:text)
  end

  def test_hist04_a_clearing_is_reported_from_the_applied_edits
    # With an edit of another kind, which is no clearing of tool results.
    thinking = '{"type":"clear_thinking_20251015","cleared_input_tokens":100,"cleared_thinking_turns":1},'
    api = serve(FakeApi.recorded(testdata('claude/clearing.sse').sub('"applied_edits":[', "\\0#{thinking}")))

    events, reply = collect(model(api), hi)

    assert_equal [ToolResultsCleared.new(tokens: 4_892, tool_calls: 2)],
                 events.grep(ToolResultsCleared) + events.grep(ConversationCompacted)
    assert_equal :tool_use, reply.stop
  end

  def test_ctx05_hist04_an_empty_list_of_iterations_leaves_the_usage_of_the_message
    api = serve(FakeApi.sse(START, *text_events('Hi'),
                            '{"type":"message_delta","delta":{"stop_reason":"end_turn"},' \
                            '"usage":{"output_tokens":7,"iterations":[]}}', '{"type":"message_stop"}'))

    events, = collect(model(api), hi)

    assert_equal [UsageReported.new(usage: Usage.new(input: 10, output: 7))], events.grep(UsageReported)
  end

  def test_hist01_mdl05_the_compaction_block_is_kept_and_replayed_as_received_and_the_run_reports_it
    api = serve(FakeApi.recorded(testdata('claude/compaction-iterations.sse')), text_reply)
    agent = Agent.new(model: model(api), instructions: 'Answer briefly.', context_management: BOTH)
    conversation = Conversation.new
    events = []

    agent.run(conversation, 'Which shelf?') { events << it }
    agent.run(Conversation.from_json(conversation.to_json), 'Thanks.')

    assert_equal [ConversationCompacted.new(tokens: 52_753, summary_tokens: 578)], events.grep(ConversationCompacted)
    blocks = conversation.messages[1].blocks.map(&:raw)

    assert_equal '{"content":"Summary: the customer asked about shelf Q2.","type":"compaction",' \
                 '"encrypted_content":null}', blocks.first
    assert_includes api.bodies.last, %({"role":"assistant","content":[#{blocks.join(',')}]})
    assert(api.bodies.all? { it.include?('"compact_20260112"') })
  end

  private

  # The one request the model sent for the request given, as the API received it.
  def sent(request)
    api = serve(text_reply)
    collect(model(api), request)
    api.requests => [sent]
    sent
  end
end
