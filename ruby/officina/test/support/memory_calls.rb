# frozen_string_literal: true

require 'json'
require 'tmpdir'

# What the tests of the memory tool share: each built-in store, and a run whose model calls the memory tool.
module MemoryCalls
  Officina = Sleepyshark::Officina
  Model = Officina::Testing::ScriptedModel

  # Yields a new store of each built-in kind.
  def each_store
    yield Officina::HashMemoryStore.new
    Dir.mktmpdir { yield Officina::FileMemoryStore.new(it) }
  end

  # Runs one reply's memory calls, one per input (a Hash, or JSON text), in the scope, and returns their results.
  def run_memory(store, scope, *inputs)
    calls = inputs.each_with_index.map do |input, index|
      Model.tool_use_block("m#{index}", 'memory', input.is_a?(String) ? input : JSON.generate(input))
    end
    model = Model.new(Model.tool_use(*calls), Model.text('Done.'))
    conversation = Officina::Conversation.new
    Officina::Agent.new(model:, instructions: 'You remember.', tools: [Officina::MemoryTool.new(store)])
                   .run(conversation, 'Go.', memory_scope: scope)
    conversation.messages.fetch(2).blocks.map(&:tool_result)
  end

  # The scope's files and their text.
  def contents(store, scope) = store.list(scope).to_h { [it.path, store.read(scope, it.path)] }
end
