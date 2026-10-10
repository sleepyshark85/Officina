# frozen_string_literal: true

require 'test_helper'
require_relative 'support/fake_clock'

# Cancelling a run, and a host that leaves its block: what the conversation keeps, and what the host's own
# exception does.
class RunLeavingTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  CALL = Model.tool_use_block('call_1', 'search', '{}')

  def test_agt05_cancelling_mid_stream_appends_nothing
    conversation = Conversation.new
    cancel = Cancellation.new
    events = []

    result = agent_of(Model.new(Model.text('Hello there'))).run(conversation, 'Hi', cancel:) do |event|
      events << event
      cancel.cancel
    end

    assert_equal Stopped.new(reason: :cancelled, detail: nil, model_calls: 1), result
    assert_empty conversation.messages
    assert_equal [TextDelta.new(text: 'Hello there')], events
  end

  def test_agt05_a_run_cancelled_before_it_starts_calls_no_model
    cancel = Cancellation.new
    cancel.cancel
    model = Model.new(Model.text('Hi'))

    result = agent_of(model).run(Conversation.new, 'Hello', cancel:)

    assert_equal :cancelled, result.reason
    assert_empty model.requests
  end

  def test_agt05_cancelling_after_a_reply_with_calls_answers_them_and_calls_the_model_no_more
    conversation = Conversation.new
    cancel = Cancellation.new
    model = Model.new(Model.tool_use(CALL), Model.text('Never sent'))

    result = agent_of(model).run(conversation, 'Hello', cancel:) do |event|
      cancel.cancel if event in ConversationAppended(message: { role: :assistant })
    end

    assert_equal :cancelled, result.reason
    assert_equal %i[user assistant user], conversation.messages.map(&:role)
    assert_equal 1, model.requests.size
  end

  def test_agt05_a_block_that_breaks_mid_run_ends_it_and_leaves_no_thread
    conversation = Conversation.new
    agent = agent_of(Model.new(Model.text('Hello there'), Model.text('Again')))

    agent.run(conversation, 'Hi') { break }

    assert_empty conversation.messages
    assert_equal 'Again', agent.run(conversation, 'Hi').text
  end

  def test_agt05_breaking_at_a_reply_with_calls_still_answers_them_and_reports_nothing_more
    conversation = Conversation.new
    agent = agent_of(Model.new(Model.tool_use(CALL), Model.text('Next')))
    events = []

    agent.run(conversation, 'Hello') do |event|
      events << event
      break if event in ConversationAppended(message: { role: :assistant })
    end

    assert_equal %i[user assistant user], conversation.messages.map(&:role)
    assert_equal(%i[user assistant], events.map { it.message.role })
    assert_equal 'Next', agent.run(conversation, 'Again').text
  end

  def test_agt05_raising_at_a_reply_with_calls_still_answers_them
    conversation = Conversation.new
    agent = agent_of(Model.new(Model.tool_use(CALL), Model.text('Next')))

    assert_raises(KeyError) do
      agent.run(conversation, 'Hello') { raise KeyError if it in ConversationAppended(message: { role: :assistant }) }
    end

    assert_equal %i[user assistant user], conversation.messages.map(&:role)
    assert_equal 'Next', agent.run(conversation, 'Again').text
  end

  def test_evt01_an_exception_from_the_hosts_block_is_the_hosts_own
    conversation = Conversation.new
    agent = agent_of(Model.new(Model.text('Hello there')))

    error = assert_raises(KeyError) { agent.run(conversation, 'Hi') { raise KeyError, 'the host failed' } }

    assert_equal 'the host failed', error.message
    assert_empty conversation.messages
  end

  private

  def agent_of(model) = Agent.new(model:, instructions: 'You help customers of a bookshop.', clock: FakeClock.new)
end
