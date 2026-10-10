# frozen_string_literal: true

require 'test_helper'

# Many runs of one agent at once, and one run per conversation.
class RunConcurrencyTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel

  def test_agt04_one_hundred_concurrent_runs_of_one_agent_each_complete_on_their_own_conversation
    agent = agent_of(Model.new(*Array.new(100) { Model.text('Hello') }))
    conversations = Array.new(100) { Conversation.new }

    threads = conversations.map { |conversation| Thread.new { agent.run(conversation, 'Hi') } }
    results = threads.map(&:value)

    assert_equal [Completed.new(text: 'Hello', usage: Usage.new)], results.uniq
    assert(conversations.all? { it.messages.map(&:text) == %w[Hi Hello] })
  ensure
    threads&.each(&:join)
  end

  def test_agt04_a_second_run_on_a_conversation_in_use_is_refused
    conversation = Conversation.new
    agent = agent_of(Model.new(Model.text('Hello'), Model.text('Again')))
    refused = nil

    agent.run(conversation, 'Hi') do
      refused ||= assert_raises(Error) { agent.run(conversation, 'Hi') }
    end

    assert_match 'Another run is using the conversation', refused.message
    assert_equal 'Again', agent.run(conversation, 'Hi').text
  end

  private

  def agent_of(model) = Agent.new(model:, instructions: 'You help customers of a bookshop.')
end
