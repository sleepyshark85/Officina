# frozen_string_literal: true

require 'test_helper'

# A message: a role and its blocks.
class MessageTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  def test_a_message_of_an_unknown_role_is_refused
    error = assert_raises(Error) { Message.new(role: :system, blocks: [Block.new(text: 'Hi')]) }

    assert_equal "A message's role is one of user, assistant, operator, not :system", error.message
  end

  def test_a_message_needs_a_block
    error = assert_raises(Error) { Message.new(role: :user, blocks: []) }

    assert_equal 'A message needs at least one block', error.message
  end

  def test_a_message_keeps_a_frozen_copy_of_its_blocks
    blocks = [Block.new(text: 'Hi')]

    message = Message.new(role: :operator, blocks:)
    blocks << Block.new(text: ' there')

    assert_equal [Block.new(text: 'Hi')], message.blocks
    assert_predicate message.blocks, :frozen?
  end

  def test_a_messages_text_joins_its_blocks
    message = Message.new(role: :assistant, blocks: [Block.new(text: 'Hel'), Block.new(text: 'lo')])

    assert_equal 'Hello', message.text
  end
end
