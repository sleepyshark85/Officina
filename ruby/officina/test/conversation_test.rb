# frozen_string_literal: true

require 'test_helper'

# The conversation's JSON form, which keeps every block byte for byte.
class ConversationTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  SHARED = File.expand_path('../../../testdata', __dir__)

  def test_agt06_the_shared_conversation_reads_and_writes_back_byte_for_byte
    json = File.read(File.join(SHARED, 'conversation', 'conversation.json'), encoding: Encoding::UTF_8).chomp

    conversation = Conversation.from_json(json)

    assert_equal json, conversation.to_json
    assert_equal 'c1', conversation.id
    assert_equal 'abc', conversation.fingerprint
    assert_equal %i[user operator assistant], conversation.messages.map(&:role)
  end

  def test_agt06_a_round_trip_keeps_blocks_with_escapes_and_non_ascii_byte_for_byte
    raw = '{"type":"text","text":"\\u003Cb\\u003E \\u0026 caf\\u00e9 «Zoë» \\"q\\" \\u2028 \\ud83d\\ude00"}'
    call = ToolCall.new(id: 'call_1', name: 'search', input: '{"q":"<Ann & Bob>"}')
    messages = [
      Message.new(role: :user, blocks: [Block.new(text: 'Order for <Ann & Bob>, «Zoë».')]),
      Message.new(role: :assistant, blocks: [Block.new(text: 'Done <b>.', raw:), Block.new(raw:, tool_call: call)])
    ]
    conversation = Conversation.new(id: 'c2', fingerprint: 'f', messages:)

    read = Conversation.from_json(conversation.to_json)

    assert_equal conversation.to_json, read.to_json
    assert_equal conversation.messages, read.messages
    assert_equal raw.b, read.messages[1].blocks.first.raw.b
  end

  def test_agt06_a_tool_result_reads_back_with_its_error_flag
    failed = ToolResult.new(call_id: 'call_1', content: 'No such book.', error: true)
    succeeded = ToolResult.new(call_id: 'call_2', content: '3 copies.', error: false)
    blocks = [Block.new(tool_result: failed), Block.new(tool_result: succeeded)]

    read = Conversation.from_json(Conversation.new(messages: [Message.new(role: :user, blocks:)]).to_json)

    assert_equal [failed, succeeded], read.messages.first.blocks.map(&:tool_result)
  end

  def test_agt06_any_text_round_trips
    Pbt.assert do
      Pbt.property(Pbt.array(Pbt.char, max: 30)) do |chars|
        text = chars.join.scrub
        user = Message.new(role: :user, blocks: [Block.new(text:)])
        conversation = Conversation.new(messages: [user, assistant(text)])

        read = Conversation.from_json(conversation.to_json)

        assert_equal conversation.messages, read.messages
        assert_equal conversation.to_json, read.to_json
      end
    end
  end

  def test_agt06_a_conversation_no_run_has_bound_is_written_without_a_fingerprint
    json = Conversation.new(id: 'c1').to_json

    assert_equal '{"id":"c1","messages":[]}', json
    assert_nil Conversation.from_json(json).fingerprint
  end

  def test_agt06_a_new_conversation_has_a_random_id_of_32_hex_digits
    first = Conversation.new
    second = Conversation.new

    assert_match(/\A\h{32}\z/, first.id)
    refute_equal first.id, second.id
  end

  def test_a_conversation_keeps_frozen_copies_of_what_it_is_given
    messages = [Message.new(role: :user, blocks: [Block.new(text: 'Hi')])]

    conversation = Conversation.new(id: +'c1', fingerprint: +'f', messages:)
    messages << messages.first

    assert_predicate conversation.id, :frozen?
    assert_predicate conversation.fingerprint, :frozen?
    assert_predicate conversation.messages, :frozen?
    assert_equal 1, conversation.messages.size
  end

  def test_agt06_what_a_run_appends_is_frozen
    conversation = Conversation.new
    agent = Agent.new(model: Model.new(Model.text('Hello')), instructions: 'You help.')

    agent.run(conversation, 'Hi')

    assert_equal 2, conversation.messages.size
    assert_predicate conversation.messages, :frozen?
  end

  private

  def assistant(text) = Message.new(role: :assistant, blocks: [Model.text_block(text)])
end
