# frozen_string_literal: true

require 'test_helper'

# Runs of an agent on a scripted model: the loop, its results, its events and cancellation.
class RunTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel

  def test_agt02_a_scripted_multi_turn_run_completes_with_text
    conversation = Conversation.new
    agent = agent_of(Model.text('Yes, 3 copies.'), Model.text('Ordered.'))

    first = agent.run(conversation, 'Is Gaudy Night in stock?')
    second = agent.run(conversation, 'Order one.')

    assert_equal 'Yes, 3 copies.', first.text
    assert_equal 'Ordered.', second.text
    assert_equal %i[user assistant user assistant], conversation.messages.map(&:role)
    assert_equal ['Is Gaudy Night in stock?', 'Yes, 3 copies.', 'Order one.', 'Ordered.'],
                 conversation.messages.map(&:text)
  end

  def test_agt02_each_tool_call_gets_a_result_and_the_model_is_called_again
    conversation = Conversation.new
    call = Model.tool_use_block('call_1', 'search', '{"query":"Gaudy Night"}')
    agent = agent_of(Model.tool_use(call), Model.text('None of my tools can run.'))

    result = agent.run(conversation, 'Search for Gaudy Night.')

    assert_instance_of Completed, result
    assert_equal %i[user assistant user assistant], conversation.messages.map(&:role)
    answer = conversation.messages[2].blocks.first.tool_result

    assert_equal ['call_1', 'No tool named search can run.', true], [answer.call_id, answer.content, answer.error?]
    assert_equal conversation.messages.first(3), @model.requests.last.messages
  end

  def test_agt03_every_stop_reason_maps_to_its_result
    call = Model.tool_use_block('call_1', 'search', '{}')
    {
      Model.stop(:max_tokens) => [Stopped, :output_limit, nil, 2],
      Model.stop(:refusal, detail: 'cyber') => [Stopped, :refusal, 'cyber', 2],
      Model.stop(:context_full) => [Stopped, :context_full, nil, 2],
      Model.stop(:unknown, detail: 'pause_turn') => [Failed, :unexpected_stop, /pause_turn/, 2],
      Model.stop(:tool_use) => [Failed, :unexpected_stop, /called none/, 2],
      [Reply.new(blocks: [call], stop: :end)] => [Failed, :unexpected_stop, /did not stop for them/, 0],
      [Reply.new(blocks: [call], stop: :max_tokens)] => [Stopped, :output_limit, nil, 0],
      Model.stop(:end, text: nil) => [Completed, nil, nil, 0],
      [TextDelta.new(text: 'Hel'), RuntimeError.new('overloaded')] => [Failed, :model_error, 'overloaded', 0],
      [TextDelta.new(text: 'Hel'), nil] => [Failed, :model_error, /without a stop reason/, 0]
    }.each do |reply, (type, reason, detail, appended)|
      conversation = Conversation.new
      result = agent_of(reply).run(conversation, 'Hello')

      assert_instance_of type, result, reply.inspect
      assert_equal reason, result.reason, reply.inspect unless type == Completed
      if detail
        assert_match detail, result.detail, reply.inspect
      elsif type != Completed
        assert_nil result.detail, reply.inspect
      end

      assert_equal appended, conversation.messages.size, reply.inspect
    end
  end

  def test_agt03_a_result_carries_the_usage_of_every_model_call
    call = Model.tool_use_block('call_1', 'search', '{}')
    agent = agent_of([UsageReported.new(usage: Usage.new(input: 10, output: 2)), *Model.tool_use(call)],
                     Model.text('Done.', usage: Usage.new(input: 1, cache_read: 10, cache_write: 4, output: 3)))

    result = agent.run(Conversation.new, 'Hello')

    assert_equal Usage.new(input: 11, output: 5, cache_read: 10, cache_write: 4), result.usage
  end

  def test_agt03_a_run_stops_at_the_iteration_limit_after_25_model_calls
    replies = (1..26).map { Model.tool_use(Model.tool_use_block("call_#{it}", 'search', '{}')) }
    conversation = Conversation.new

    result = agent_of(*replies).run(conversation, 'Hello')

    assert_equal Stopped.new(reason: :iteration_limit, detail: nil, usage: Usage.new), result
    assert_equal 25, @model.requests.size
    assert_equal 51, conversation.messages.size
    assert_predicate conversation.messages.last.blocks.first, :tool_result
  end

  def test_agt05_cancelling_mid_stream_appends_nothing
    conversation = Conversation.new
    cancel = Cancellation.new
    events = []

    result = agent_of(Model.text('Hello there')).run(conversation, 'Hi', cancel:) do |event|
      events << event
      cancel.cancel
    end

    assert_equal Stopped.new(reason: :cancelled, detail: nil, usage: Usage.new), result
    assert_empty conversation.messages
    assert_equal [TextDelta.new(text: 'Hello there')], events
  end

  def test_agt05_a_run_cancelled_before_it_starts_calls_no_model
    cancel = Cancellation.new
    cancel.cancel

    result = agent_of(Model.text('Hi')).run(Conversation.new, 'Hello', cancel:)

    assert_equal :cancelled, result.reason
    assert_empty @model.requests
  end

  def test_agt05_cancelling_after_a_reply_with_calls_answers_them_and_calls_the_model_no_more
    conversation = Conversation.new
    cancel = Cancellation.new
    agent = agent_of(Model.tool_use(Model.tool_use_block('call_1', 'search', '{}')), Model.text('Never sent'))

    result = agent.run(conversation, 'Hello', cancel:) do |event|
      cancel.cancel if event in ConversationAppended(message: { role: :assistant })
    end

    assert_equal :cancelled, result.reason
    assert_equal %i[user assistant user], conversation.messages.map(&:role)
    assert_equal 1, @model.requests.size
  end

  def test_agt05_a_block_that_breaks_mid_run_ends_it_and_leaves_no_thread
    conversation = Conversation.new
    agent = agent_of(Model.text('Hello there'), Model.text('Again'))

    agent.run(conversation, 'Hi') { break }

    assert_empty conversation.messages
    assert_equal 'Again', agent.run(conversation, 'Hi').text
  end

  def test_evt01_an_exception_from_the_hosts_block_is_the_hosts_own
    conversation = Conversation.new
    agent = agent_of(Model.text('Hello there'))

    error = assert_raises(KeyError) { agent.run(conversation, 'Hi') { raise KeyError, 'the host failed' } }

    assert_equal 'the host failed', error.message
    assert_empty conversation.messages
  end

  def test_agt04_one_hundred_concurrent_runs_of_one_agent_each_complete_on_their_own_conversation
    replies = Array.new(100) { Model.text('Hello') }
    agent = agent_of(*replies)
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
    agent = agent_of(Model.text('Hello'), Model.text('Again'))
    refused = nil

    agent.run(conversation, 'Hi') do
      refused ||= assert_raises(Error) { agent.run(conversation, 'Hi') }
    end

    assert_match 'Another run is using the conversation', refused.message
    assert_equal 'Again', agent.run(conversation, 'Hi').text
  end

  def test_agt08_every_append_is_reported_once_all_of_a_step_is_appended
    conversation = Conversation.new
    seen = []

    agent_of(Model.text('Hello')).run(conversation, 'Hi', context: 'Today is Friday.') do |event|
      seen << [event.message.role, conversation.messages.size] if event.is_a?(ConversationAppended)
    end

    assert_equal [[:user, 3], [:operator, 3], [:assistant, 3]], seen
  end

  def test_evt01_a_run_streams_text_and_usage_then_returns_its_result
    events = []
    usage = Usage.new(input: 5, output: 1)

    result = agent_of(Model.text('Hello', usage:)).run(Conversation.new, 'Hi') { events << it }

    assert_equal [TextDelta.new(text: 'Hello'), UsageReported.new(usage:)], events.first(2)
    assert_equal(%i[user assistant], events.drop(2).map { it.message.role })
    assert_equal Completed.new(text: 'Hello', usage:), result
  end

  def test_ctx01_the_run_context_follows_the_users_message_as_an_operator_message
    agent_of(Model.text('Hello')).run(Conversation.new, 'Hi', context: 'Today is Friday.')

    request = @model.requests.first

    assert_equal([[:user, 'Hi'], [:operator, 'Today is Friday.']], request.messages.map { [it.role, it.text] })
    assert_equal 'You help customers of a bookshop.', request.instructions
  end

  def test_gen03_a_run_is_stateless_on_a_new_conversation_and_stateful_on_a_kept_one
    agent = agent_of(Model.text('One'), Model.text('Two'), Model.text('Three'))
    kept = Conversation.new

    agent.run(Conversation.new, 'Classify this.')
    agent.run(kept, 'Hi')
    agent.run(kept, 'Again')

    assert_equal([1, 1, 3], @model.requests.map { it.messages.size })
  end

  def test_a_blank_message_or_run_context_is_refused
    agent = agent_of

    assert_raises(Error) { agent.run(Conversation.new, " \n") }
    assert_raises(Error) { agent.run(Conversation.new, 'Hi', context: ' ') }
  end

  private

  def agent_of(*replies)
    @model = Model.new(*replies)
    Agent.new(model: @model, instructions: 'You help customers of a bookshop.')
  end
end
