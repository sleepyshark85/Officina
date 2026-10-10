# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# Long conversations: the provider compacts and clears on its side, the run reports each, and the core never edits the
# conversation; a provider that cannot compact ends the run once the window is full.
class LongConversationTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  # A summary with characters a re-encoder would change.
  SUMMARY = 'Summary: shelf Q2, «Café» <new> & a\\/b'

  def test_hist01_hist04_a_compaction_and_a_clearing_are_reported_audited_and_the_block_replayed_as_received
    model = compacting(compacting_reply, Model.text("You're welcome."))
    sink = Testing::RecordingAuditSink.new
    agent = Agent.new(model:, instructions: 'You help.', audit_sink: sink, context_management: managed)
    conversation = Conversation.new
    events = []

    agent.run(conversation, 'Which shelf?') { events << it }
    agent.run(Conversation.from_json(conversation.to_json), 'Thanks.')

    assert_equal([ToolResultsCleared.new(tokens: 4_892, tool_calls: 2),
                  ConversationCompacted.new(tokens: 52_753, summary_tokens: 578)],
                 events.select { [ToolResultsCleared, ConversationCompacted].include?(it.class) })
    assert_equal Model.compaction_block(SUMMARY).raw, model.requests.last.messages[1].blocks.first.raw
    assert_equal [managed, managed], model.requests.map(&:context_management)
    assert_equal([[:cleared, 'Results of 2 tool calls cleared: 4,892 tokens.'],
                  [:compacted, '52,753 tokens summarized into 578.']],
                 sink.entries.filter_map { [it.kind, it.detail] if %i[cleared compacted].include?(it.kind) })
  end

  def test_hist04_the_audit_trail_writes_every_count_with_commas_between_its_thousands
    edits = [ConversationCompacted.new(tokens: 1_234_567, summary_tokens: 12_345),
             ToolResultsCleared.new(tokens: 1_000, tool_calls: 1_000)]
    sink = Testing::RecordingAuditSink.new

    Agent.new(model: compacting([*edits, *Model.text('Hi.')]), instructions: 'You help.', audit_sink: sink,
              context_management: managed).run(Conversation.new, 'Hi')

    details = sink.entries.filter_map { it.detail if %i[cleared compacted].include?(it.kind) }

    assert_equal ['1,234,567 tokens summarized into 12,345.', 'Results of 1,000 tool calls cleared: 1,000 tokens.'],
                 details
  end

  def test_hist01_the_events_come_as_the_model_streams_them_before_the_reply_is_appended
    events = []

    Agent.new(model: compacting(compacting_reply), instructions: 'You help.', context_management: managed)
         .run(Conversation.new, 'Which shelf?') { events << it.class }

    assert_equal [ToolResultsCleared, ConversationCompacted, TextDelta, UsageReported, ConversationAppended,
                  ConversationAppended], events
  end

  def test_hist03_a_provider_without_compaction_ends_with_context_full_and_appends_nothing
    model = Model.new(Model.text('Hello.'), Model.stop(:context_full, text: nil, usage: usage(1_000_000, 0)))
    agent = Agent.new(model:, instructions: 'You help.')
    conversation = Conversation.new
    agent.run(conversation, 'Hi')
    before = conversation.messages

    result = agent.run(conversation, 'And now?')

    assert_equal Stopped.new(reason: :context_full, detail: nil, usage: usage(1_000_000, 0), model_calls: 1),
                 result.with(duration: 0.0)
    assert_equal before, conversation.messages
  end

  def test_ctx04_hist01_context_management_is_part_of_the_prefix
    model = compacting(Model.text('Hi.'))
    clearings = [{ after: 12 }, { after: 1 }, { after: 12, keep: 1 }, { after: 12, at_least_tokens: 1 }]
    settings = [nil, ContextManagement.new, ContextManagement.new(compact_at: 50_000),
                ContextManagement.new(compact_at: 60_000), managed,
                *clearings.map { ContextManagement.new(clear_tool_results: ToolResultClearing.new(**it)) }]

    fingerprints = settings.map do |context_management|
      Agent.new(model:, instructions: 'You help.', context_management:).fingerprint
    end

    assert_equal fingerprints[0], fingerprints[1]
    assert_equal fingerprints.size - 1, fingerprints.uniq.size
  end

  def test_evt02_hist04_a_compaction_and_a_clearing_show_on_the_model_calls_span_and_are_counted
    collector = Collector.new

    traced(collector, compacting(compacting_reply), context_management: managed).run(Conversation.new, 'Which shelf?')

    assert_equal [52_753, 578, 4_892, 2],
                 collector.attributes('chat scripted', 'officina.compaction.tokens',
                                      'officina.compaction.summary_tokens', 'officina.clearing.tokens',
                                      'officina.clearing.tool_calls')
    counts = %w[officina.model.compactions officina.model.clearings].map do |metric|
      collector.points_of(metric, SCRIPTED).map(&:sum)
    end

    assert_equal [[1], [1]], counts
  end

  private

  # A scripted model whose provider compacts conversations and clears old tool results.
  def compacting(*replies)
    Model.new(*replies, info: ModelInfo.new(provider: 'scripted', name: 'scripted', compacts: true,
                                            clears_tool_results: true))
  end

  # Context management that compacts and clears, as a demo does.
  def managed
    ContextManagement.new(compact_at: 50_000, clear_tool_results: ToolResultClearing.new(after: 12, keep: 10))
  end

  # A reply for which the provider cleared old tool results, then compacted the conversation into a summary block.
  def compacting_reply
    [ToolResultsCleared.new(tokens: 4_892, tool_calls: 2),
     ConversationCompacted.new(tokens: 52_753, summary_tokens: 578),
     TextDelta.new(text: 'Shelf Q2.'), UsageReported.new(usage: usage(50, 663, 5_230, 50_648)),
     Reply.new(blocks: [Model.compaction_block(SUMMARY), Model.text_block('Shelf Q2.')], stop: :end)]
  end
end
