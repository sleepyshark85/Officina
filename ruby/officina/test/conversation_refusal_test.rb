# frozen_string_literal: true

require 'test_helper'

# JSON that is not a conversation's is refused with an Error that says why, never another exception.
class ConversationRefusalTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  WRONG_SHAPE = "Not a conversation's JSON: a member is missing or of the wrong type"

  def test_agt06_json_of_another_shape_is_refused
    [
      '[]', '{"messages":[]}', '{"id":1,"messages":[]}', '{"id":"c"}', '{"id":"c","messages":{}}',
      '{"id":"c","fingerprint":1,"messages":[]}'
    ].each do |json|
      assert_refused WRONG_SHAPE, json
    end
  end

  def test_agt06_a_message_of_another_shape_is_refused
    ['{"role":"system","blocks":[{"text":"x"}]}', '{"role":"user"}', '"x"'].each do |message|
      assert_refused WRONG_SHAPE, %({"id":"c","messages":[#{message}]})
    end
  end

  def test_agt06_a_message_without_blocks_is_refused
    assert_refused 'A message needs at least one block', '{"id":"c","messages":[{"role":"user","blocks":[]}]}'
  end

  def test_agt06_a_block_of_another_shape_is_refused
    [
      '"x"', '["x"]', '{"text":1}', '{"raw":1}', '{"toolResult":{"callId":"a"}}',
      '{"toolResult":{"callId":"a","content":"b","isError":"no"}}', '{"raw":"{}","toolCall":{"id":"a","name":"b"}}'
    ].each do |block|
      assert_refused WRONG_SHAPE, %({"id":"c","messages":[{"role":"user","blocks":[#{block}]}]})
    end
  end

  def test_agt06_a_block_with_neither_text_raw_json_nor_a_tool_result_is_refused
    assert_refused 'A block needs text, raw JSON or a tool result',
                   '{"id":"c","messages":[{"role":"user","blocks":[{}]}]}'
  end

  def test_agt06_json_that_does_not_parse_is_refused_with_the_parsers_reason
    {
      'not json' => /\ANot a conversation's JSON: unexpected token 'not'/,
      '{"id":"c","id":"d","messages":[]}' => /\ANot a conversation's JSON: duplicate key "id"/,
      '{"id":"c","messages":[{"role":"user","blocks":[{"raw":"{"}]}]}' => /\ANot a conversation's JSON: expected/,
      '{"id":"c","messages":[{"role":"user","blocks":[{"raw":"{\"a\":1,\"a\":2}"}]}]}' =>
        /\ANot a conversation's JSON: duplicate key "a"/
    }.each do |json, reason|
      error = assert_raises(Error, json) { Conversation.from_json(json) }

      assert_match reason, error.message, json
    end
  end

  private

  def assert_refused(message, json)
    error = assert_raises(Error, json) { Conversation.from_json(json) }

    assert_equal message, error.message, json
  end
end
