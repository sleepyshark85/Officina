# frozen_string_literal: true

require 'test_helper'

# What must never happen, whatever a session does: generated sequences of runs on one conversation, each with
# scripted replies that call tools of every kind, fail or stop, cancelled or left at a generated event, with an audit
# sink that works, fails at times, fails always or is absent.
class RunPropertyTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  Id = Input.define { string :id }
  # A call: 0 a read, 1 a read that raises, 2 a write, 3 a write that needs approval, 4 an unknown tool, 5 invalid
  # input. A reply: 0 text, 1 or 2 tool calls, 3 a model failure, 4 the output limit.
  REPLY = Pbt.tuple(Pbt.integer(min: 0, max: 4), Pbt.array(Pbt.integer(min: 0, max: 5), min: 1, max: 4))
  # A run: its replies; how the host interrupts it (0 not, 1 cancels, 2 leaves the block) and at which event; its
  # sink (0 none, 1 working, 2 failing every third entry, 3 failing every entry).
  RUN = Pbt.tuple(Pbt.array(REPLY, min: 1, max: 4), Pbt.integer(min: 0, max: 2), Pbt.integer(min: 1, max: 20),
                  Pbt.integer(min: 0, max: 3))
  CALLS = [%w[look {"id":"%s"}], %w[fail {"id":"%s"}], %w[save {"id":"%s"}], %w[order {"id":"%s"}],
           %w[gone {"id":"%s"}], %w[look {"id":1}]].freeze

  def test_test07_any_session_keeps_the_conversation_valid_answers_every_call_once_and_audits_every_write_first
    Pbt.assert do
      Pbt.property(Pbt.array(RUN, min: 1, max: 4)) do |session|
        conversation = Conversation.new
        unaudited = []
        session.each_with_index { |run, at| run_once(conversation, run, at, unaudited) }

        assert_empty unaudited, 'A write ran before its attempt was in the audit trail'
        calls = conversation.messages.flat_map(&:blocks).filter_map { it.tool_call&.id }
        answers = conversation.messages.flat_map(&:blocks).filter_map { it.tool_result&.call_id }

        assert_equal calls.sort, answers.sort
      end
    end
  end

  private

  def run_once(conversation, (replies, how, stop_at, sink_kind), at, unaudited)
    sink = sink(sink_kind)
    agent = Agent.new(model: Model.new(*scripted(replies, at), Model.text('Done.')), instructions: 'You help.',
                      tools: tools(sink, unaudited), audit_sink: sink,
                      approver: Testing::ScriptedApprover.new(*Array.new(20) { it.even? || 'No.' }))
    cancel = Cancellation.new
    seen = 0
    result = agent.run(conversation, "Message #{at}", cancel:) do
      seen += 1
      break if seen == stop_at && how == 2

      cancel.cancel if seen == stop_at && how == 1
    end
    check(conversation, result)
  end

  def check(conversation, result)
    assert_nil Testing::ConversationRules.problem(conversation.messages) unless conversation.messages.empty?
    refute_match(/is invalid/, result.detail.to_s) if result.is_a?(Failed)
  end

  def sink(kind)
    case kind
    when 1 then Testing::RecordingAuditSink.new
    when 2 then Testing::RecordingAuditSink.new(fails: ->(entry) { (entry.sequence % 3).zero? })
    when 3 then Testing::RecordingAuditSink.new(fails: ->(_) { true })
    end
  end

  # The run's tools; each write notes its call when the trail lacks its attempt.
  def tools(sink, unaudited)
    write = lambda do |input, _|
      audited = sink.nil? || sink.entries.any? { it.kind == :tool_started && it.call_id == input.id }
      unaudited << input.id unless audited
      'Saved.'
    end
    [tool('look', :read) { |_, _| 'Found.' }, tool('fail', :read) { |_, _| raise IOError, 'down' },
     tool('save', :write, &write), tool('order', :write, needs_approval: true, &write)]
  end

  def tool(name, kind, needs_approval: false, &)
    Tool.new(name:, description: "Does #{name}.", input: Id, kind:, needs_approval:, &)
  end

  def scripted(replies, run)
    replies.each_with_index.map do |(kind, calls), at|
      case kind
      when 0 then Model.text("Reply #{at}.")
      when 1, 2 then Model.tool_use(*calls.each_with_index.map { |call, index| block(call, "c#{run}_#{at}_#{index}") })
      when 3 then [RuntimeError.new('overloaded')]
      else Model.stop(:max_tokens)
      end
    end
  end

  def block(call, id)
    name, input = CALLS.fetch(call)
    Model.tool_use_block(id, name, input.sub('%s', id))
  end
end
