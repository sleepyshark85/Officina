# frozen_string_literal: true

require_relative 'claude_test_case'

# The output schema's adjustment on generated closed schemas: every object closed, nothing the API rejects left,
# everything else kept in its order, and adjusting again changes nothing. Reached through const_get, as it is internal.
class OutputFormatPropertyTest < ClaudeTestCase
  OutputFormat = Claude.const_get(:OutputFormat)
  REJECTED = %w[minimum maximum exclusiveMinimum exclusiveMaximum multipleOf maxItems].freeze

  # Random closed schemas, nested up to a depth, boolean ones inside, with the keywords the adjustment drops, keeps
  # or adds.
  class Generated < Pbt::Arbitrary::Arbitrary
    TYPES = ['object', 'array', 'string', 'integer', %w[object null], %w[number null]].freeze
    KEYWORDS = (%w[type properties items anyOf minItems description additionalProperties] + REJECTED).freeze

    # In an array, as pbt passes a Hash value to the property as keywords.
    def generate(rng) = [schema(rng, 3)]

    def shrink(_) = [].each

    private

    def schema(rng, depth)
      KEYWORDS.sample(rng.rand(0..4), random: rng).to_h { [it, keyword(it, rng, depth)] }
    end

    def keyword(name, rng, depth)
      case name
      when 'type' then TYPES.sample(random: rng)
      when 'properties' then %w[a minimum].sample(rng.rand(0..2), random: rng).to_h { [it, inner(rng, depth)] }
      when 'items' then inner(rng, depth)
      when 'anyOf' then Array.new(rng.rand(1..2)) { inner(rng, depth) }
      when 'description' then 'Described.'
      when 'additionalProperties' then false
      else rng.rand(0..3)
      end
    end

    # An inner schema: an object, or now and then a boolean schema.
    def inner(rng, depth)
      return [true, false].sample(random: rng) if rng.rand(8).zero?

      depth.positive? ? schema(rng, depth - 1) : {}
    end
  end

  def test_out01_a_generated_schema_is_closed_and_stripped_keeping_the_rest_in_order_and_adjusts_once
    Pbt.assert(num_runs: 500) do
      Pbt.property(Generated.new) do |(schema)|
        adjusted = OutputFormat.of(JSON.generate(schema)).fetch(:schema)

        assert_adjusted schema, adjusted
        assert_equal adjusted, OutputFormat.of(JSON.generate(adjusted)).fetch(:schema)
      end
    end
  end

  private

  # The adjusted schema keeps the original's keywords the API takes, in order, closes it when it is an object, and
  # holds the inner schemas adjusted; a boolean schema is kept.
  def assert_adjusted(original, adjusted)
    return assert_equal(original, adjusted) unless original.is_a?(Hash)

    object = original.key?('properties') || Array(original['type']).include?('object')
    closed = object ? { 'additionalProperties' => false } : original.slice('additionalProperties')

    assert_equal kept_keys(original, object), adjusted.keys
    assert_equal closed, adjusted.slice('additionalProperties')

    inner_pairs(original, adjusted).each { |(inside, adjusted_inside)| assert_adjusted inside, adjusted_inside }
  end

  def kept_keys(original, object)
    kept = original.keys.reject { REJECTED.include?(it) || (it == 'minItems' && original[it] > 1) }
    object ? kept | ['additionalProperties'] : kept
  end

  def inner_pairs(original, adjusted)
    original.fetch('properties', {}).map { |name, inside| [inside, adjusted['properties'][name]] } +
      [[original['items'], adjusted['items']]].reject { it.first.nil? } +
      original.fetch('anyOf', []).zip(adjusted.fetch('anyOf', []))
  end
end
