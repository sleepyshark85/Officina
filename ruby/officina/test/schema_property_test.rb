# frozen_string_literal: true

require 'test_helper'
require 'json_schemer'

# The core's validator against an established one, json_schemer, on generated schemas in the subset and generated
# values; and every schema outside the subset refused.
class SchemaPropertyTest < Minitest::Test
  cover 'Sleepyshark::Officina::Schema*'
  cover 'Sleepyshark::Officina::SchemaSubset*'

  Schema = Sleepyshark::Officina::Schema
  SchemaError = Sleepyshark::Officina::SchemaError

  # Random schemas in the subset, nested up to a depth, and JSON values from a small alphabet, so that values often
  # meet a schema's constraints and often just miss them. Drawn from pbt's seeded generator, so a run repeats with its
  # seed; values are not shrunk, so a failure prints the whole case.
  class Generated < Pbt::Arbitrary::Arbitrary
    NAMES = %w[a b c].freeze
    TYPES = %w[object array string number integer boolean null].freeze
    PATTERNS = ['^a', 'b$', '[0-9]', '^$', 'a|é', '\A\d+\z'].freeze
    SCALARS = [nil, true, false, 0, 1, 2, -1, 1.0, 2.5, -0.5, '', 'a', 'ab', 'b', '1', 'é', 'aé1'].freeze
    COUNTED = %w[minLength maxLength minItems maxItems].freeze
    KEYWORDS = (%w[type properties required additionalProperties items anyOf enum const minimum maximum pattern
                   description] + COUNTED).freeze

    def generate(rng) = [schema(rng, 2), value(rng, 2)]

    def shrink(_) = [].each

    def schema(rng, depth)
      return [true, false].sample(random: rng) if rng.rand(10).zero?

      KEYWORDS.sample(rng.rand(0..3), random: rng).to_h { [it, keyword(it, rng, depth)] }
    end

    def value(rng, depth)
      case depth.positive? ? rng.rand(6) : 0
      when 0..3 then SCALARS.sample(random: rng)
      when 4 then Array.new(rng.rand(0..3)) { value(rng, depth - 1) }
      else NAMES.sample(rng.rand(0..3), random: rng).to_h { [it, value(rng, depth - 1)] }
      end
    end

    private

    def keyword(name, rng, depth)
      case name
      when 'type' then type(rng)
      when 'properties' then NAMES.sample(rng.rand(0..3), random: rng).to_h { [it, schema(rng, depth - 1)] }
      when 'required' then NAMES.sample(rng.rand(0..2), random: rng)
      when 'additionalProperties', 'items' then schema(rng, depth - 1)
      when 'anyOf' then Array.new(rng.rand(1..2)) { schema(rng, depth - 1) }
      else constraint(name, rng)
      end
    end

    def type(rng) = rng.rand(2).zero? ? TYPES.sample(random: rng) : TYPES.sample(rng.rand(1..2), random: rng)

    def constraint(name, rng)
      case name
      when 'enum' then Array.new(rng.rand(0..3)) { value(rng, 1) }
      when 'const' then value(rng, 1)
      when 'minimum', 'maximum' then [rng.rand(-2..2), rng.rand(-2..2) + 0.5].sample(random: rng)
      when 'pattern' then PATTERNS.sample(random: rng)
      when 'description' then 'Described.'
      else rng.rand(0..3)
      end
    end
  end

  # Keywords outside the subset, each with a value the reference would take.
  OUTSIDE = {
    'oneOf' => [true], 'allOf' => [true], 'not' => false, 'if' => true, '$ref' => '#', '$defs' => {},
    'patternProperties' => {}, 'prefixItems' => [], 'exclusiveMinimum' => 0, 'multipleOf' => 2, 'uniqueItems' => true,
    'dependentRequired' => {}, 'minProperties' => 1, 'contains' => true, 'propertyNames' => true, 'unknown' => 1
  }.freeze

  def test_test08_on_generated_schemas_and_values_it_accepts_and_rejects_as_json_schemer_does
    Pbt.assert(num_runs: 1000) do
      Pbt.property(Generated.new) do |(schema, value)|
        expected = JSONSchemer.schema(schema, regexp_resolver: 'ruby', format: false).valid?(value)
        problems = Schema.new(JSON.generate(schema)).validate(value)

        assert_equal expected, problems.empty?, "#{JSON.generate(schema)} on #{JSON.generate(value)}: #{problems}"
      end
    end
  end

  def test_test08_a_generated_schema_with_any_keyword_outside_the_subset_is_refused_where_it_is
    Pbt.assert do
      Pbt.property(Generated.new, Pbt.one_of(*OUTSIDE.keys), Pbt.integer(min: 0, max: 99)) do |(schema, _), keyword, at|
        text, path = with_keyword(schema, keyword, at)
        next if text.nil?

        error = assert_raises(SchemaError) { Schema.new(text) }

        assert_equal "The schema at '#{path}/#{keyword}' is outside the supported subset: is not a supported keyword.",
                     error.message
      end
    end
  end

  private

  # The schema as JSON with the keyword added to one of its object schemas, the one at index at (modulo their count),
  # and that schema's JSON pointer; nil when it has none.
  def with_keyword(schema, keyword, at)
    nodes = objects(schema, '')
    return if nodes.empty?

    node, path = nodes[at % nodes.size]
    node[keyword] = OUTSIDE.fetch(keyword)
    [JSON.generate(schema), path]
  end

  # The object schemas in a generated schema with their JSON pointers, itself first.
  def objects(schema, path)
    return [] unless schema.is_a?(Hash)

    nested = schema.slice('additionalProperties', 'items').map { |name, child| [child, "#{path}/#{name}"] } +
             schema.fetch('properties', {}).map { |name, child| [child, "#{path}/properties/#{name}"] } +
             schema.fetch('anyOf', []).each_with_index.map { |child, index| [child, "#{path}/anyOf/#{index}"] }
    [[schema, path]] + nested.flat_map { |child, at| objects(child, at) }
  end
end
