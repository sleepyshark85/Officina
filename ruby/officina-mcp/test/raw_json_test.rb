# frozen_string_literal: true

require 'json'
require 'test_helper'
require 'sleepyshark/officina/mcp'

# Reading each member of an object, or element of an array, as it was written, from generated JSON. The reader is
# internal to the gem, so the test reaches it through const_get, to run hundreds of cases in a fraction of a second.
class RawJsonTest < Minitest::Test
  cover 'Sleepyshark::Officina::Mcp*'

  RawJson = Sleepyshark::Officina::Mcp.const_get(:RawJson)
  # Values whose text a reader could take apart wrongly: brackets, quotes and escapes in strings, numbers' forms, and
  # nested objects and arrays.
  PIECES = ['"a\\"]}"', '"{[\\\\"', '-1.5E+3', '0', 'true', 'false', 'null', '"é"', '"\\u00e9"', '{}', '[]',
            '{"k": [1, {"x": "]"}]}', '[ "}" , 2 ]'].freeze
  SPACES = ['', ' ', "\n  ", "\t", "\r\n"].freeze
  # A value: one piece, or an array of them.
  VALUE = Pbt.array(Pbt.integer(min: 0, max: PIECES.size - 1), max: 3)

  def test_mcp01_each_member_and_element_is_read_as_written
    Pbt.assert do
      Pbt.property(Pbt.array(VALUE, max: 5), Pbt.integer(min: 0, max: SPACES.size - 1)) do |drawn, spacing|
        values = drawn.map { value(it) }
        space = SPACES.fetch(spacing)
        members = values.each_with_index.map { |text, index| %("k\\"#{index}"#{space}:#{space}#{text}) }
        object = "{#{space}#{members.join("#{space},#{space}")}#{space}}"

        assert_equal values.each_with_index.to_h { |text, index| [%(k"#{index}), text] }, RawJson.members(object)
        assert_equal values, RawJson.elements("[#{space}#{values.join("#{space},#{space}")}#{space}]")
      end
    end
  end

  private

  def value(pieces) = pieces.size == 1 ? PIECES.fetch(pieces.first) : "[#{pieces.map { PIECES.fetch(it) }.join(', ')}]"
end
