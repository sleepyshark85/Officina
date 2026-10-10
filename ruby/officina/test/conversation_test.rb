# frozen_string_literal: true

require 'test_helper'

# The conversation's JSON form, which keeps every block byte for byte.
class ConversationTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  SHARED = File.expand_path('../../../testdata', __dir__)

  def test_agt06_the_shared_conversation_reads_and_writes_back_byte_for_byte
    json = File.read(File.join(SHARED, 'conversation', 'conversation.json'), encoding: Encoding::UTF_8).chomp

    conversation = Conversation.from_json(json)

    assert_equal json, conversation.to_json
    assert_equal ['c1', 'abc', %i[user operator assistant]],
                 [conversation.id, conversation.fingerprint, conversation.messages.map(&:role)]
  end

  def test_agt06_a_round_trip_keeps_blocks_with_escapes_and_non_ascii_byte_for_byte
    raw = '{"type":"text","text":"\\u003Cb\\u003E \\u0026 caf\\u00e9 «Zoë» \\"q\\" \\u2028 \\ud83d\\ude00"}'
    call = ToolCall.new(id: 'call_1', name: 'search', input: '{"q":"<Ann & Bob>"}')
    answer = ToolResult.new(call_id: 'call_1', content: '&', error: false)
    messages = [
      Message.new(role: :user, blocks: [Block.new(text: 'Order for <Ann & Bob>, «Zoë».')]),
      Message.new(role: :assistant, blocks: [Block.new(text: 'Done <b>.', raw:), Block.new(raw:, tool_call: call)]),
      Message.new(role: :user, blocks: [Block.new(tool_result: answer)])
    ]
    conversation = Conversation.new(id: 'c2', fingerprint: 'f', messages:)

    json = conversation.to_json
    read = Conversation.from_json(json)

    assert_equal json, read.to_json
    assert_equal conversation.messages, read.messages
    assert_equal raw.b, read.messages[1].blocks.first.raw.b
  end

  def test_agt06_any_text_round_trips
    Pbt.assert do
      Pbt.property(Pbt.array(Pbt.char, max: 30)) do |chars|
        text = chars.join.scrub
        block = Testing::ScriptedModel.text_block(text)
        conversation = Conversation.new(messages: [Message.new(role: :user, blocks: [Block.new(text:)]),
                                                   Message.new(role: :assistant, blocks: [block])])

        read = Conversation.from_json(conversation.to_json)

        assert_equal conversation.messages, read.messages
        assert_equal conversation.to_json, read.to_json
      end
    end
  end

  def test_agt06_json_that_is_not_a_conversation_is_refused
    [
      'not json', '[]', '{"messages":[]}', '{"id":"c","messages":[{"role":"system","blocks":[{"text":"x"}]}]}',
      '{"id":"c","messages":[{"role":"user","blocks":[]}]}', '{"id":"c","messages":[{"role":"user","blocks":[{}]}]}',
      '{"id":"c","messages":[{"role":"user","blocks":[{"raw":"{"}]}]}', '{"id":"c","id":"d","messages":[]}',
      '{"id":"c","messages":[{"role":"user","blocks":[{"toolResult":{"callId":"a"}}]}]}'
    ].each do |json|
      assert_raises(Error, json) { Conversation.from_json(json) }
    end
  end

  def test_a_block_is_stored_in_the_canonical_form_once
    canonical = Block.canonical(%({ "type" : "text",\n "text" : "<b> & \\"x\\" \\u00e9 é" }))

    assert_equal '{"type":"text","text":"\\u003Cb\\u003E \\u0026 \\"x\\" \\u00e9 é"}', canonical
    assert_equal canonical, Block.canonical(canonical)
    assert_predicate canonical, :frozen?
  end
end
