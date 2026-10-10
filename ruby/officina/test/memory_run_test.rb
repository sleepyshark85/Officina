# frozen_string_literal: true

require 'json'
require 'test_helper'
require_relative 'support/traced_runs'

# A run with memory: its scope, which its audit entries and span name; memory writes through the pipeline, audited and
# approved like any write; and a prefix that memory never changes.
class MemoryRunTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  # A store whose writes check that the call's attempt is already in the audit trail.
  AuditedStore = Data.define(:store, :sink) do
    def list(scope) = store.list(scope)
    def read(scope, path) = store.read(scope, path)
    def delete(scope, path) = store.delete(scope, path)

    def write(scope, path, text)
      attempted = sink.entries.any? { it.kind == :tool_started && it.input.include?(path) }
      raise IOError, "the write of #{path} ran before its attempt was audited" unless attempted

      store.write(scope, path, text)
    end
  end

  def test_mem03_a_run_of_an_agent_with_memory_needs_a_valid_scope
    with_memory = agent(Model.new, [MemoryTool.new(HashMemoryStore.new)])
    without = agent(Model.new)

    { [with_memory, nil] => 'The agent has memory, so a run needs a memory scope',
      [with_memory, '../ana'] => '"../ana" is not a valid memory scope.',
      [without, 'a/b'] => '"a/b" is not a valid memory scope.' }.each do |(agent, memory_scope), message|
      assert_equal message, assert_raises(Error) { agent.run(Conversation.new, 'Hi', memory_scope:) }.message
    end
  end

  def test_mem04_memory_writes_are_audited_before_they_run_and_ask_approval_while_views_do_not
    sink = Testing::RecordingAuditSink.new
    store = AuditedStore.new(store: HashMemoryStore.new, sink:)
    approver = Testing::ScriptedApprover.new(true, 'Not now.')
    calls = memory_calls({ command: 'view', path: '/memories' },
                         { command: 'create', path: '/memories/a.md', file_text: 'x' },
                         { command: 'delete', path: '/memories/a.md' })
    memory = MemoryTool.new(store, needs_approval: true)

    agent(Model.new(calls, Model.text('Done.')), [memory], audit_sink: sink, approver:)
      .run(Conversation.new, 'Go.', memory_scope: 'sam')

    assert_equal %w[m1 m2], approver.asked.map(&:id)
    assert_equal 'x', store.read('sam', 'a.md')
    ended = sink.entries.select { it.kind == :tool_ended }

    assert_equal %w[ok ok error], ended.map(&:outcome)
    assert_equal ['File created successfully at: /memories/a.md', 'The call was denied: Not now.'],
                 ended.drop(1).map(&:detail)
  end

  def test_tool04_the_memory_tool_asks_no_approval_unless_told_to
    approver = Testing::ScriptedApprover.new
    memory = MemoryTool.new(HashMemoryStore.new)
    model = Model.new(memory_calls({ command: 'create', path: '/memories/a.md', file_text: 'x' }), Model.text('Done.'))

    agent(model, [memory], approver:).run(Conversation.new, 'Go.', memory_scope: 'sam')

    assert_empty approver.asked
    refute_predicate memory, :needs_approval?
  end

  def test_aud03_evt02_every_audit_entry_and_the_run_span_name_the_memory_scope_and_the_memory_tool_its_source
    sink = Testing::RecordingAuditSink.new
    collector = Collector.new
    model = Model.new(memory_calls({ command: 'create', path: '/memories/a.md', file_text: 'x' }), Model.text('Done.'))

    traced(collector, model, tools: [MemoryTool.new(HashMemoryStore.new)], audit_sink: sink)
      .run(Conversation.new, 'Go.', memory_scope: 'sam')

    assert_equal([%i[run_started sam], %i[tool_started sam], %i[tool_ended sam], %i[run_ended sam]],
                 sink.entries.map { [it.kind, it.memory_scope.to_sym] })
    assert_equal ['sam'], collector.attributes('invoke_agent', 'officina.memory.scope')
    assert_equal ['memory'], collector.attributes('execute_tool memory', 'officina.tool.source')
  end

  def test_aud03_a_run_without_memory_names_no_scope
    sink = Testing::RecordingAuditSink.new
    collector = Collector.new

    traced(collector, Model.new(Model.text('Hi.')), audit_sink: sink).run(Conversation.new, 'Hi')

    assert_equal [nil, nil], sink.entries.map(&:memory_scope)
    refute collector.span('invoke_agent').attributes.key?('officina.memory.scope')
  end

  def test_mem05_memory_never_reaches_the_instructions_and_the_prefix_stays_stable_as_it_changes
    store = HashMemoryStore.new
    store.write('sam', 'prefs.md', 'Prices with tax.')
    model = Model.new(memory_calls({ command: 'view', path: '/memories/prefs.md' }),
                      memory_calls({ command: 'create', path: '/memories/prefs.md', file_text: 'Prices without tax.' }),
                      Model.text('Noted.'), Model.text('Hello again.'))
    agent = agent(model, [MemoryTool.new(store)])
    conversation = Conversation.new

    agent.run(conversation, 'What do I prefer?', memory_scope: 'sam')
    agent.run(conversation, 'Hi.', memory_scope: 'sam')

    assert_equal ['Answer briefly.'] * 4, model.requests.map(&:instructions)
    assert_stable_prefix model.requests
    assert_includes model.requests[1].messages.last.blocks[0].tool_result.content, 'Prices with tax.'
    assert_equal 'Prices without tax.', store.read('sam', 'prefs.md')
    assert_equal agent.fingerprint, conversation.fingerprint
  end

  def test_ctx04_an_agent_with_memory_is_fingerprinted_as_dotnets_is
    # What .NET computes for RequestPrefix("scripted", [MemoryTool.Create(…)], "Answer briefly."), as Go's test has it.
    dotnet = '4d845546ce9d9d98fbaa9895456764834a5f2c3eb4c60051547da1162319120f'

    assert_equal dotnet, agent(Model.new, [MemoryTool.new(HashMemoryStore.new)]).fingerprint
  end

  private

  include Testing::PrefixAssertions

  def agent(model, tools = [], **parts) = Agent.new(model:, instructions: 'Answer briefly.', tools:, **parts)

  def memory_calls(*inputs)
    Model.tool_use(*inputs.each_with_index.map { |input, index| call("m#{index}", 'memory', JSON.generate(input)) })
  end
end
