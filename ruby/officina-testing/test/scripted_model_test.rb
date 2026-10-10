# frozen_string_literal: true

require 'test_helper'

# The scripted model: replies given in advance, requests recorded, and requests the provider would reject rejected.
class ScriptedModelTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina::Testing*'

  Model = Testing::ScriptedModel

  def test_test01_it_streams_its_replies_in_order_and_records_each_request
    model = Model.new(Model.text('One'), Model.text('Two'), settings: 'scripted fast')
    events = []

    replies = [request(user('Hi')), request(user('Hello'))].map do |r|
      model.stream(r, cancel: Cancellation.new) { events << it }
    end

    assert_equal(%w[One Two], replies.map { it.blocks.first.text })
    assert_equal [TextDelta.new(text: 'One'), TextDelta.new(text: 'Two')], events
    assert_equal([%w[Hi], %w[Hello]], model.requests.map { |r| r.messages.map(&:text) })
    assert_equal 'scripted fast', model.settings
  end

  def test_test01_it_rejects_messages_the_provider_would_reject
    call = Model.tool_use_block('call_1', 'search', '{}')
    {
      'the first message is not' => [assistant('Hi')],
      'the user follows the user' => [user('Hi'), user('Hello')],
      'the operator follows the assistant' => [user('Hi'), assistant('Yes'), operator('Friday')],
      'the user follows the operator' => [user('Hi'), operator('Friday'), user('Hello')],
      'the last message has calls' => [user('Hi'), Message.new(role: :assistant, blocks: [call])],
      'answers the calls ["other"]' => [user('Hi'), Message.new(role: :assistant, blocks: [call]), result('other'),
                                        assistant('Done')]
    }.each do |problem, messages|
      model = Model.new(Model.text('Never sent'))

      error = assert_raises(Error) { model.stream(request(*messages), cancel: Cancellation.new) { nil } }

      assert_match problem, error.message
    end
  end

  def test_test01_it_accepts_a_conversation_with_answered_calls_and_a_run_context
    call = Model.tool_use_block('call_1', 'search', '{}')
    messages = [user('Hi'), operator('Friday'), Message.new(role: :assistant, blocks: [call]), result('call_1'),
                user('And?')]

    reply = Model.new(Model.text('Fine')).stream(request(*messages), cancel: Cancellation.new) { nil }

    assert_equal 'Fine', reply.blocks.first.text
  end

  def test_test01_a_run_on_a_model_with_no_reply_left_fails
    agent = Agent.new(model: Model.new, instructions: 'You help.')

    result = agent.run(Conversation.new, 'Hi')

    assert_equal :model_error, result.reason
    assert_match 'no reply left', result.detail
  end

  private

  def request(*messages) = Request.new(tools: [], instructions: 'You help.', messages:)
  def user(text) = Message.new(role: :user, blocks: [Block.new(text:)])
  def operator(text) = Message.new(role: :operator, blocks: [Block.new(text:)])
  def assistant(text) = Message.new(role: :assistant, blocks: [Model.text_block(text)])

  def result(call_id)
    Message.new(role: :user, blocks: [Block.new(tool_result: ToolResult.new(call_id:, content: 'ok', error: false))])
  end
end
