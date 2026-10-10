# frozen_string_literal: true

require 'test_helper'

# A model's whole reply.
class ReplyTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  def test_a_reply_of_an_unknown_stop_is_refused
    error = assert_raises(Error) { Reply.new(blocks: [], stop: :pause_turn) }

    assert_equal 'A reply stops for one of end, tool_use, max_tokens, refusal, context_full, unknown, not :pause_turn',
                 error.message
  end

  def test_a_reply_keeps_frozen_copies_of_its_blocks_and_detail
    blocks = [Block.new(text: 'No.')]
    detail = +'cyber'

    reply = Reply.new(blocks:, stop: :refusal, detail:)
    blocks << Block.new(text: ' Sorry.')
    detail << '!'

    assert_equal [Block.new(text: 'No.')], reply.blocks
    assert_predicate reply.blocks, :frozen?
    assert_equal 'cyber', reply.detail
    assert_predicate reply.detail, :frozen?
  end

  def test_a_reply_without_a_detail_has_none
    assert_nil Reply.new(blocks: [], stop: :end).detail
  end
end
