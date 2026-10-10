# frozen_string_literal: true

require 'test_helper'

# Schemas the core's validator refuses when they are defined: outside the JSON Schema subset, or not JSON.
class SchemaRefusalTest < Minitest::Test
  cover 'Sleepyshark::Officina::Schema*'
  cover 'Sleepyshark::Officina::SchemaSubset*'

  Schema = Sleepyshark::Officina::Schema
  SchemaError = Sleepyshark::Officina::SchemaError

  # Schemas outside the subset, with where they leave it and why.
  OUTSIDE = {
    '{"oneOf":[true]}' => ['/oneOf', 'is not a supported keyword'],
    '{"properties":{"a":{"items":{"$ref":"#"}}}}' => ['/properties/a/items/$ref', 'is not a supported keyword'],
    '{"anyOf":[{},{"type":"date"}]}' => ['/anyOf/1/type', 'unknown type'],
    '{"type":[]}' => ['/type', 'unknown type'],
    '{"anyOf":[]}' => ['/anyOf', 'must be a non-empty array'],
    '{"minLength":-1}' => ['/minLength', 'must be a non-negative integer'],
    '{"maxItems":1.0}' => ['/maxItems', 'must be a non-negative integer'],
    '{"minimum":"1"}' => ['/minimum', 'must be a number'],
    '{"required":[1]}' => ['/required', 'must be an array of names'],
    '{"properties":[]}' => ['/properties', 'must be an object'],
    '{"enum":"a"}' => ['/enum', 'must be an array'],
    '{"pattern":1}' => ['/pattern', 'must be a string'],
    '{"pattern":"("}' => ['/pattern', 'end pattern with unmatched parenthesis: /(/'],
    '{"additionalProperties":1}' => ['/additionalProperties', 'a schema must be an object or a boolean'],
    '[]' => ['/', 'a schema must be an object or a boolean']
  }.freeze

  def test_test08_a_schema_outside_the_subset_is_refused_when_it_is_defined_saying_where_and_why
    OUTSIDE.each do |schema, (path, problem)|
      assert_equal "The schema at '#{path}' is outside the supported subset: #{problem}.",
                   assert_raises(SchemaError, schema) { Schema.new(schema) }.message
    end
  end

  def test_test08_a_schema_that_is_not_json_is_refused_with_the_parsers_detail
    {
      '{"type":' => 'unexpected end of input at line 1 column 9',
      '{"a":{},"a":{}}' => 'duplicate key "a" at line 1 column 1'
    }.each do |schema, detail|
      refused = assert_raises(SchemaError, schema) { Schema.new(schema) }

      assert_equal "The schema is not JSON: #{detail}", refused.message
    end
  end
end
