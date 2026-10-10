# frozen_string_literal: true

require 'test_helper'

# The core's validator for the JSON Schema subset, by example; schema_property_test.rb compares it with a reference.
class SchemaTest < Minitest::Test
  cover 'Sleepyshark::Officina::Schema*'
  cover 'Sleepyshark::Officina::SchemaSubset*'

  Schema = Sleepyshark::Officina::Schema
  SchemaError = Sleepyshark::Officina::SchemaError

  # Each keyword with a value it accepts and one it rejects, and the problems the model is told.
  EXAMPLES = [
    [{ type: 'string' }, 'a', 1, ['/: must be string']],
    [{ type: %w[number null] }, nil, 'a', ['/: must be number or null']],
    [{ type: 'integer' }, 2.0, 2.5, ['/: must be integer']],
    [{ type: 'boolean' }, false, 0, ['/: must be boolean']],
    [{ type: 'object' }, {}, [], ['/: must be object']],
    [{ type: 'array' }, [], {}, ['/: must be array']],
    [{ type: 'null' }, nil, false, ['/: must be null']],
    [{ enum: ['a', 1, nil] }, 1.0, 'b', ['/: must be one of ["a",1,null]']],
    [{ const: { a: [1] } }, { 'a' => [1] }, { 'a' => [2] }, ['/: must be {"a":[1]}']],
    [{ anyOf: [{ type: 'string' }, { minimum: 3 }] }, 4, 2, ['/: matches none of the allowed schemas']],
    [{ minLength: 2 }, 'éé', 'é', ['/: must have at least 2 characters']],
    [{ maxLength: 1 }, '😀', 'ab', ['/: must have at most 1 characters']],
    [{ minItems: 1 }, [nil], [], ['/: must have at least 1 items']],
    [{ maxItems: 1 }, [1], [1, 2], ['/: must have at most 1 items']],
    [{ minimum: 1.5 }, 2, 1, ['/: must be at least 1.5']],
    [{ maximum: 1 }, 1.0, 2, ['/: must be at most 1']],
    [{ pattern: '[0-9]' }, 'x1', 'x', ['/: must match [0-9]']],
    [{ properties: { a: { type: 'string' } } }, { 'a' => 'x', 'b' => 1 }, { 'a' => 1 }, ['/a: must be string']],
    [{ required: %w[a b] }, { 'a' => 1, 'b' => nil }, { 'c' => 1 }, ['/a: is required', '/b: is required']],
    [{ properties: { a: {} }, additionalProperties: false }, { 'a' => 1 }, { 'z' => 2 }, ['/z: is not allowed']],
    [{ additionalProperties: { type: 'integer' } }, { 'a' => 1 }, { 'a' => 'x' }, ['/a: must be integer']],
    [{ items: { type: 'integer' } }, [1, 2], [1, 'x'], ['/1: must be integer']],
    [{ description: 'Any', title: 'T', format: 'date-time', default: 1 }, 'not a date', nil, nil],
    [true, 1, nil, nil],
    [{ properties: { a: false } }, {}, { 'a' => nil }, ['/a: is not allowed']]
  ].freeze

  def test_tool01_a_schema_is_the_input_type_of_its_own_json_values
    schema = Schema.new('{"type":"object"}')

    assert_same schema, schema.schema
    assert_equal({ 'id' => 7 }, schema.from_json({ 'id' => 7 }))
  end

  def test_tool02_each_keyword_accepts_valid_input_and_names_the_problem_with_invalid_input
    EXAMPLES.each do |schema, valid, invalid, problems|
      checked = Schema.new(JSON.generate(schema))

      assert_empty checked.validate(valid), "#{schema} on #{valid.inspect}"
      assert_equal problems, checked.validate(invalid), "#{schema} on #{invalid.inspect}" if problems
    end
  end

  def test_tool02_the_false_schema_allows_nothing
    assert_equal ['/: is not allowed'], Schema.new('false').validate(nil)
  end

  def test_tool02_keywords_for_other_kinds_of_value_do_not_apply
    schema = Schema.new('{"minLength":5,"maxItems":0,"minimum":9,"pattern":"x","items":false}')

    ['xxxxx', [], {}, nil, true].each { assert_empty schema.validate(it), it.inspect }
  end

  def test_tool02_problems_of_nested_values_are_named_by_their_json_pointer
    schema = Schema.new(<<~JSON)
      {"type":"object","properties":{"lines":{"type":"array","items":{"type":"object",
        "properties":{"bookId":{"type":"integer"}},"required":["bookId"],"additionalProperties":false}}},
        "required":["lines"],"additionalProperties":false}
    JSON

    assert_equal ['/lines/0/bookId: must be integer', '/lines/1/bookId: is required', '/lines/1/x: is not allowed'],
                 schema.validate({ 'lines' => [{ 'bookId' => '7' }, { 'x' => 1 }] })
  end

  def test_tool02_a_pattern_that_takes_too_long_is_a_problem_of_the_value_not_an_error
    # A backreference turns off Ruby's memoization, so this backtracks for ages.
    schema = Schema.new(JSON.generate({ pattern: '^(a|a)*\\1$' }))

    assert_equal ['/: could not be matched against ^(a|a)*\\1$ in time'], schema.validate("#{'a' * 40}!")
  end

  def test_tool02_a_nested_pattern_has_the_same_timeout
    schema = Schema.new(JSON.generate({ properties: { a: { items: { pattern: '^(a|a)*\\1$' } } } }))

    assert_equal ['/a/0: could not be matched against ^(a|a)*\\1$ in time'],
                 schema.validate({ 'a' => ["#{'a' * 40}!"] })
  end

  def test_tool01_the_schema_keeps_its_text_as_given_for_the_request
    text = '{ "type" : "object", "description": "caf\\u00e9" }'

    assert_equal text, Schema.new(text).to_s
    assert_predicate Schema.new(+text).to_s, :frozen?
    assert_predicate Schema.new(text), :frozen?
  end
end
