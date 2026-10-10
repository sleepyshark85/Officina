# frozen_string_literal: true

require 'json'
require 'test_helper'
require 'sleepyshark/officina/mcp'

# Text the raw JSON reader cannot follow, generated: what a JSON parser that accepts comments (json before 3.0) would
# let through, and garbage. The reader raises rather than loop, and a tool list holding such text cannot be read. Both
# are internal to the gem, so the test reaches them through const_get.
class RawJsonRefusalTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  Mcp = Sleepyshark::Officina::Mcp
  RawJson = Mcp.const_get(:RawJson)
  Results = Mcp.const_get(:Results)
  Response = Mcp.const_get(:Wire)::Response
  # A page of tools/list, token by token, so a comment can go between any two.
  PAGE = ['{', '"jsonrpc"', ':', '"2.0"', ',', '"id"', ':', '2', ',', '"result"', ':', '{', '"tools"', ':', '[', '{',
          '"name"', ':', '"echo"', ',', '"inputSchema"', ':', '{', '"type"', ':', '"object"', '}', '}', ']', '}',
          '}'].freeze
  # Comments holding what the reader counts or skips: brackets, quotes, and a line comment's end.
  COMMENTS = ['/* ] */', '/*{*/', '/* " */', "// }\n", '/**/', '/* [ { */', '/* "x" } */'].freeze
  GARBAGE = ['{', '}', '[', ']', '"', '"s"', ':', ',', '/*', '*/', '//', "\n", ' ', '1', 'x', '\\'].freeze

  def test_mcp04_garbage_is_read_or_refused_never_looped_on
    Pbt.assert do
      Pbt.property(Pbt.array(Pbt.integer(min: 0, max: GARBAGE.size - 1), max: 12)) do |drawn|
        text = drawn.map { GARBAGE.fetch(it) }.join

        assert_includes [Hash, JSON::ParserError], read(:members, text).class
        assert_includes [Array, JSON::ParserError], read(:elements, text).class
      end
    end
  end

  def test_mcp04_brackets_in_a_comment_are_refused_not_looped_on
    assert_raises(JSON::ParserError) { RawJson.members('{"a":{"x":1} /*{*/}') }
    assert_raises(JSON::ParserError) { RawJson.members('{"a": /* ] */ 1}') }
  end

  def test_mcp04_a_tool_list_with_comments_is_read_as_written_or_not_at_all
    Pbt.assert do
      insert = Pbt.tuple(Pbt.integer(min: 1, max: PAGE.size - 1), Pbt.integer(min: 0, max: COMMENTS.size - 1))
      Pbt.property(Pbt.array(insert, max: 3)) do |drawn|
        text = commented(drawn)
        response = Response.new(message: JSON.parse(text, allow_comments: true, freeze: true), text:)

        assert_includes [Array, NilClass], Results.tools(response).class
      end
    end
  end

  def test_mcp04_a_tool_list_whose_text_cannot_be_followed_cannot_be_read
    text = '{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"echo","inputSchema":{"type":"object"} /*{*/}]}}'

    assert_nil Results.tools(Response.new(message: JSON.parse(text, allow_comments: true), text:))
  end

  private

  def read(how, text)
    RawJson.public_send(how, text)
  rescue JSON::ParserError => e
    e
  end

  # The page with each drawn comment before the token at its position.
  def commented(inserts)
    tokens = PAGE.dup
    inserts.sort_by { -it.first }.each { |position, comment| tokens.insert(position, COMMENTS.fetch(comment)) }
    tokens.join
  end
end
