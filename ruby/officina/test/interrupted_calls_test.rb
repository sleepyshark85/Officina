# frozen_string_literal: true

require 'test_helper'
require_relative 'fixtures/shared_session'

# A run on a conversation saved while its last reply's tools ran: it first answers each of their calls as
# interrupted, audits it and reports it, so the conversation stays valid however often it is saved and resumed.
class InterruptedCallsTest < Minitest::Test
  include Sleepyshark::Officina
  include Testing::PrefixAssertions

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  SEARCHES = Model.tool_use(Model.tool_use_block('c1', 'search', '{"query":"a"}'),
                            Model.tool_use_block('c2', 'search', '{"query":"b"}'))
  # A run of the property: its reply (0 text, 1 a search then text, 2 a search while which the application stops),
  # and whether the conversation is saved and resumed before it.
  RUN = Pbt.tuple(Pbt.integer(min: 0, max: 2), Pbt.boolean)

  def test_aud01_app10_interrupted_calls_are_audited_and_reported_before_the_run_goes_on
    sink = Testing::RecordingAuditSink.new
    agent = SharedSession.agent(SharedSession.model(SEARCHES, Model.text('Sorry, where were we?')), audit_sink: sink)
    resumed = Conversation.from_json(SharedSession.crash(agent, Conversation.new(id: 's1'), 'Search twice.'))
    events = []

    agent.run(resumed, 'Hello?') { events << it }

    assert_equal(%w[c1 c2], events.first.message.blocks.map { it.tool_result.call_id })
    interrupted = sink.entries.select { it.outcome == 'interrupted' }

    assert_equal([[:tool_ended, 'c1', 'search', '{"query":"a"}', SharedSession::INTERRUPTED],
                  [:tool_ended, 'c2', 'search', '{"query":"b"}', SharedSession::INTERRUPTED]],
                 interrupted.map { [it.kind, it.call_id, it.tool, it.input, it.detail] })
    assert_nil Testing::ConversationRules.problem(resumed.messages)
  end

  def test_app10_a_complete_conversation_gets_no_interrupted_results
    agent = SharedSession.agent(SharedSession.model(Model.text('One.'), Model.text('Two.')))
    conversation = Conversation.new
    agent.run(conversation, 'Hi')
    events = []

    agent.run(conversation, 'Again') { events << it }

    refute_kind_of ConversationAppended, events.first
    assert_equal 4, conversation.messages.size
  end

  def test_test07_test02_the_prefix_stays_byte_identical_across_generated_saves_and_resumes
    Pbt.assert do
      Pbt.property(Pbt.array(RUN, min: 1, max: 6)) do |runs|
        requests = []
        conversation = runs.each_with_index.reduce(Conversation.new) do |held, ((reply, resume), at)|
          held = Conversation.from_json(held.to_json) if resume
          run_once(held, reply, "Message #{at}") { requests.concat(it) }
        end
        # A last run answers what the last stop left without results.
        run_once(conversation, 0, 'Still there?') { requests.concat(it) }

        assert_stable_prefix requests
        assert_nil Testing::ConversationRules.problem(conversation.messages)
      end
    end
  end

  private

  # Runs a freshly built agent of the shared prefix on the conversation, and yields the requests its model received.
  # @return [Conversation] the conversation after the run; when the application stopped during it, as saved then
  def run_once(conversation, reply, input)
    model = SharedSession.model(*(reply.zero? ? [] : [SEARCHES]), Model.text('Done.'))
    agent = SharedSession.agent(model)
    if reply == 2
      conversation = Conversation.from_json(SharedSession.crash(agent, conversation, input))
    else
      agent.run(conversation, input)
    end
    yield model.requests
    conversation
  end
end
