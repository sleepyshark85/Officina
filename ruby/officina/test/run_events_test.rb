# frozen_string_literal: true

require 'test_helper'

# What a run reports to its host's block, and in which order.
class RunEventsTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel

  def test_evt01_a_run_streams_text_and_usage_then_returns_its_result
    events = []
    usage = Usage.new(input: 5, output: 1)

    result = agent_of(Model.new(Model.text('Hello', usage:))).run(Conversation.new, 'Hi') { events << it }

    assert_equal [TextDelta.new(text: 'Hello'), UsageReported.new(usage:)], events.first(2)
    assert_equal(%i[user assistant], events.drop(2).map { it.message.role })
    assert_equal Completed.new(text: 'Hello', usage:), result
  end

  def test_agt08_every_append_is_reported_once_all_of_a_step_is_appended
    conversation = Conversation.new
    model = Model.new(Model.tool_use(Model.tool_use_block('call_1', 'search', '{}')), Model.text('Hello'))
    seen = []

    agent_of(model).run(conversation, 'Hi', context: 'Today is Friday.') do |event|
      seen << [event.message.role, conversation.messages.size] if event.is_a?(ConversationAppended)
    end

    assert_equal [[:user, 3], [:operator, 3], [:assistant, 3], [:user, 4], [:assistant, 5]], seen
    assert_predicate conversation.messages[3].blocks.first, :tool_result
  end

  private

  def agent_of(model) = Agent.new(model:, instructions: 'You help customers of a bookshop.')
end
