# frozen_string_literal: true

require 'test_helper'

# A block of a message, and the canonical form of a provider's JSON for one.
class BlockTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  def test_a_block_needs_text_raw_json_or_a_tool_result
    error = assert_raises(Error) { Block.new(tool_call: ToolCall.new(id: 'call_1', name: 'search', input: '{}')) }

    assert_equal 'A block needs text, raw JSON or a tool result', error.message
  end

  def test_empty_text_is_no_text_in_a_block_with_raw_json
    assert_nil Block.new(text: '', raw: '{}').text
  end

  def test_empty_text_is_no_text_in_a_block_with_a_tool_result
    result = ToolResult.new(call_id: 'call_1', content: 'ok', error: false)

    assert_nil Block.new(text: '', tool_result: result).text
  end

  def test_empty_text_alone_is_kept
    assert_equal '', Block.new(text: '').text
  end

  def test_a_block_keeps_frozen_copies_of_its_strings
    text = +'Hello'
    raw = +'{"type":"text","text":"Hello"}'

    block = Block.new(text:, raw:)
    text << '!'
    raw << ' '

    assert_equal 'Hello', block.text
    assert_predicate block.text, :frozen?
    assert_equal '{"type":"text","text":"Hello"}', block.raw
    assert_predicate block.raw, :frozen?
  end

  def test_a_block_is_stored_in_the_canonical_form_once
    canonical = Block.canonical(%({ "type" : "text",\n "text" : "<b> & \\"x\\" \\u00e9 é" }))

    assert_equal '{"type":"text","text":"\\u003Cb\\u003E \\u0026 \\"x\\" \\u00e9 é"}', canonical
    assert_equal canonical, Block.canonical(canonical)
    assert_predicate canonical, :frozen?
  end

  def test_the_canonical_form_keeps_the_whitespace_inside_strings
    assert_equal %({"text":" a \\t\tb "}), Block.canonical(%({\t"text" :\r\n" a \\t\tb " }))
  end
end
