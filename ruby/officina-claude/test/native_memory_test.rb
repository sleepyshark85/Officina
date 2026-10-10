# frozen_string_literal: true

require_relative 'claude_test_case'

# The memory tool on Claude: sent as Claude's own, its calls run in the run's memory scope.
class NativeMemoryTest < ClaudeTestCase
  OBJECT = '{"type":"object"}'
  # A reply that asks the memory tool to create a file, as Claude streams it.
  CREATE = [
    START,
    '{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_m1","name":"memory",' \
    '"input":{}}}',
    '{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":' \
    '"{\\"command\\":\\"create\\",\\"path\\":\\"/memories/prefs.md\\","}}',
    '{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":' \
    '"\\"file_text\\":\\"Prices with tax.\\"}"}}',
    '{"type":"content_block_stop","index":0}',
    '{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":5}}',
    '{"type":"message_stop"}'
  ].freeze

  def test_mem01_the_memory_tool_is_sent_as_claudes_own_in_sorted_order_and_its_calls_run_in_the_runs_scope
    api = serve(FakeApi.sse(*CREATE), text_reply)
    store = HashMemoryStore.new
    tools = [tool('search'), MemoryTool.new(store), tool('add')]

    result = Agent.new(model: model(api), instructions: 'Answer briefly.', tools:)
                  .run(Conversation.new, 'I prefer prices with tax.', memory_scope: 'sam')

    assert_instance_of Completed, result
    assert_equal 'Prices with tax.', store.read('sam', 'prefs.md')
    api.bodies.each do |body|
      sent = JSON.parse(body).fetch('tools')

      assert_equal(%w[add memory search], sent.map { it.fetch('name') })
      assert_equal({ 'name' => 'memory', 'type' => 'memory_20250818' }, sent[1])
    end
  end

  private

  def tool(name) = Tool.new(name:, description: "Does #{name}.", input: Schema.new(OBJECT), kind: :read) { 'Done.' }
end
