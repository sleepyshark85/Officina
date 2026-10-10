# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The subset of JSON Schema (draft 2020-12) the core validates, and the check that a schema keeps to it.
    module SchemaSubset
      # What a value of each type is, as JSON.parse returns it; 1.0 is an integer, as JSON Schema says.
      TYPES = {
        'object' => ->(value) { value.is_a?(Hash) },
        'array' => ->(value) { value.is_a?(Array) },
        'string' => ->(value) { value.is_a?(String) },
        'number' => ->(value) { value.is_a?(Numeric) },
        'integer' => ->(value) { value.is_a?(Integer) || (value.is_a?(Float) && value.finite? && value == value.to_i) },
        'boolean' => ->(value) { [true, false].include?(value) },
        'null' => :nil?.to_proc
      }.freeze
      COUNT = ->(value) { 'must be a non-negative integer' unless value.is_a?(Integer) && !value.negative? }
      NUMBER = ->(value) { 'must be a number' unless value.is_a?(Numeric) }
      # The keywords whose value must have a shape, each with the problem a schema has when its value has not.
      SHAPES = {
        'type' => ->(value) { 'unknown type' if Array(value).empty? || !Array(value).all? { |type| TYPES.key?(type) } },
        'properties' => ->(value) { 'must be an object' unless value.is_a?(Hash) },
        'required' => ->(value) { 'must be an array of names' unless value.is_a?(Array) && value.all?(String) },
        'anyOf' => ->(value) { 'must be a non-empty array' unless value.is_a?(Array) && !value.empty? },
        'enum' => ->(value) { 'must be an array' unless value.is_a?(Array) },
        'minLength' => COUNT, 'maxLength' => COUNT, 'minItems' => COUNT, 'maxItems' => COUNT,
        'minimum' => NUMBER, 'maximum' => NUMBER,
        'pattern' => ->(value) { 'must be a string' unless value.is_a?(String) }
      }.freeze
      # The subset's other keywords, whose value may be anything: a subschema, checked as one, or an annotation.
      UNCHECKED = %w[
        additionalProperties items const
        $schema $id $comment title description default examples format readOnly writeOnly deprecated
      ].freeze
      # Seconds a pattern may take on one value; one that takes longer is that value's problem.
      PATTERN_TIMEOUT = 0.1
      private_constant :COUNT, :NUMBER, :SHAPES, :UNCHECKED, :PATTERN_TIMEOUT

      # @param schema [Object] a schema as JSON.parse returns it.
      # @return [Hash{String => Regexp}] the schema's patterns, compiled, each limited in the time it takes on a value.
      # @raise [SchemaError] naming where the schema leaves the subset.
      def self.check(schema) = patterns_in(schema, '').freeze

      def self.patterns_in(schema, path)
        return {} if [true, false].include?(schema)

        refuse(path, 'a schema must be an object or a boolean') unless schema.is_a?(Hash)
        schema.map { |keyword, value| check_keyword(keyword, value, "#{path}/#{keyword}") }.reduce({}, :merge)
      end

      def self.check_keyword(keyword, value, at)
        refuse(at, 'is not a supported keyword') unless SHAPES.key?(keyword) || UNCHECKED.include?(keyword)
        problem = SHAPES[keyword]&.call(value)
        refuse(at, problem) if problem
        return { value => compile(value, at) } if keyword == 'pattern'

        subschemas(keyword, value).map { |name, subschema| patterns_in(subschema, "#{at}#{name}") }.reduce({}, :merge)
      end

      def self.subschemas(keyword, value)
        case keyword
        when 'properties' then value.map { |name, schema| ["/#{name}", schema] }
        when 'anyOf' then value.each_with_index.map { |schema, index| ["/#{index}", schema] }
        when 'additionalProperties', 'items' then [['', value]]
        else []
        end
      end

      def self.compile(pattern, at)
        Regexp.new(pattern, timeout: PATTERN_TIMEOUT)
      rescue RegexpError => e
        refuse(at, e.message)
      end

      def self.refuse(path, problem)
        raise SchemaError, "The schema at '#{path.empty? ? '/' : path}' is outside the supported subset: #{problem}."
      end
      private_class_method :patterns_in, :check_keyword, :subschemas, :compile, :refuse
    end
    private_constant :SchemaSubset
  end
end
