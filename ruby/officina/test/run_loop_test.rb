# frozen_string_literal: true

require 'test_helper'

# The run loop on a scripted model: its model calls, what it appends, and the result each way it ends.
class RunLoopTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  CALL = Model.tool_use_block('call_1', 'search', '{}')
  NONE = Usage.new
  OUTPUT_LIMIT = Stopped.new(reason: :output_limit, detail: nil, usage: NONE)

  def test_agt02_a_scripted_multi_turn_run_completes_with_text
    conversation = Conversation.new
    agent = agent_of(Model.new(Model.text('Yes, 3 copies.'), Model.text('Ordered.')))

    first = agent.run(conversation, 'Is Gaudy Night in stock?')
    second = agent.run(conversation, 'Order one.')

    assert_equal Completed.new(text: 'Yes, 3 copies.', usage: NONE), first
    assert_equal Completed.new(text: 'Ordered.', usage: NONE), second
    assert_equal ['Is Gaudy Night in stock?', 'Yes, 3 copies.', 'Order one.', 'Ordered.'],
                 conversation.messages.map(&:text)
  end

  def test_agt02_each_tool_call_gets_a_result_and_the_model_is_called_again
    conversation = Conversation.new
    model = Model.new(Model.tool_use(CALL), Model.text('I have no search tool.'))

    result = agent_of(model).run(conversation, 'Search for Gaudy Night.')

    assert_equal Completed.new(text: 'I have no search tool.', usage: NONE), result
    assert_equal %i[user assistant user assistant], conversation.messages.map(&:role)
    assert_equal ToolResult.new(call_id: 'call_1', content: 'There is no tool named search.', error: true),
                 conversation.messages[2].blocks.first.tool_result
    assert_equal conversation.messages.first(3), model.requests.last.messages
  end

  def test_agt03_each_way_a_reply_ends_the_run_gives_its_result_and_keeps_what_the_provider_accepts
    {
      Model.stop(:max_tokens) => [OUTPUT_LIMIT, 2],
      Model.stop(:refusal, detail: 'cyber') => [Stopped.new(reason: :refusal, detail: 'cyber', usage: NONE), 2],
      Model.stop(:context_full) => [Stopped.new(reason: :context_full, detail: nil, usage: NONE), 2],
      Model.stop(:unknown, detail: 'pause_turn') =>
        [failed(:unexpected_stop, "The run cannot act on the model's stop: pause_turn"), 2],
      Model.stop(:tool_use) => [failed(:unexpected_stop, 'The model stopped to use tools but called none'), 2],
      [Reply.new(blocks: [CALL], stop: :end)] =>
        [failed(:unexpected_stop, "The model's reply called tools but did not stop for them"), 0],
      [Reply.new(blocks: [CALL], stop: :max_tokens)] => [OUTPUT_LIMIT, 0],
      Model.stop(:end, text: nil) => [Completed.new(text: '', usage: NONE), 0],
      [TextDelta.new(text: 'Hel'), RuntimeError.new('overloaded')] => [failed(:model_error, 'overloaded'), 0],
      [TextDelta.new(text: 'Hel'), nil] => [failed(:model_error, "The model's reply ended without a stop reason"), 0]
    }.each do |reply, (expected, appended)|
      conversation = Conversation.new

      assert_equal expected, agent_of(Model.new(reply)).run(conversation, 'Hello'), reply.inspect
      assert_equal appended, conversation.messages.size, reply.inspect
    end
  end

  def test_agt03_a_result_carries_the_usage_of_every_model_call
    model = Model.new([UsageReported.new(usage: Usage.new(input: 10, output: 2)), *Model.tool_use(CALL)],
                      Model.text('Done.', usage: Usage.new(input: 1, cache_read: 10, cache_write: 4, output: 3)))

    result = agent_of(model).run(Conversation.new, 'Hello')

    assert_equal Usage.new(input: 11, output: 5, cache_read: 10, cache_write: 4), result.usage
  end

  def test_agt03_a_failed_run_carries_the_usage_reported_before_it_failed
    usage = Usage.new(input: 7, output: 1)
    model = Model.new([UsageReported.new(usage:), RuntimeError.new('overloaded')])

    assert_equal Failed.new(reason: :model_error, detail: 'overloaded', usage:),
                 agent_of(model).run(Conversation.new, 'Hello')
  end

  def test_agt03_a_run_stops_at_the_iteration_limit_after_25_model_calls
    model = Model.new(*(1..26).map { Model.tool_use(Model.tool_use_block("call_#{it}", 'search', '{}')) })
    conversation = Conversation.new

    result = agent_of(model).run(conversation, 'Hello')

    assert_equal Stopped.new(reason: :iteration_limit, detail: nil, usage: NONE), result
    assert_equal 25, model.requests.size
    assert_equal 51, conversation.messages.size
    assert_predicate conversation.messages.last.blocks.first, :tool_result
  end

  def test_ctx01_the_run_context_follows_the_users_message_as_an_operator_message
    model = Model.new(Model.text('Hello'))

    agent_of(model).run(Conversation.new, 'Hi', context: 'Today is Friday.')

    request = model.requests.first

    assert_equal([[:user, 'Hi'], [:operator, 'Today is Friday.']], request.messages.map { [it.role, it.text] })
    assert_equal 'You help customers of a bookshop.', request.instructions
  end

  def test_gen03_a_run_is_stateless_on_a_new_conversation_and_stateful_on_a_kept_one
    model = Model.new(Model.text('One'), Model.text('Two'), Model.text('Three'))
    agent = agent_of(model)
    kept = Conversation.new

    agent.run(Conversation.new, 'Classify this.')
    agent.run(kept, 'Hi')
    agent.run(kept, 'Again')

    assert_equal([1, 1, 3], model.requests.map { it.messages.size })
  end

  def test_a_blank_message_or_run_context_is_refused
    agent = agent_of(Model.new)

    assert_raises(Error) { agent.run(Conversation.new, " \n") }
    assert_raises(Error) { agent.run(Conversation.new, 'Hi', context: ' ') }
  end

  private

  def agent_of(model) = Agent.new(model:, instructions: 'You help customers of a bookshop.')
  def failed(reason, detail) = Failed.new(reason:, detail:, usage: NONE)
end
