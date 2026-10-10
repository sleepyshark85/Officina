# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    # A JSON schema (draft 2020-12) in the subset the core validates: the schemas its schema DSL writes, and those MCP
    # servers commonly send. A schema outside the subset is refused when it is created, so none is ever half checked.
    # A pattern is a Ruby regular expression, found anywhere in the string as JSON Schema says, so its `^` and `$`
    # match at line ends too, unlike JavaScript's.
    class Schema
      # The keywords that check a value of one kind alone: the kind, and the problem the value has, nil for none.
      RULES = {
        'type' => [BasicObject, lambda { |rule, value|
          types = Array(rule)
          "must be #{types.join(' or ')}" if types.none? { |type| SchemaSubset::TYPES.fetch(type).call(value) }
        }],
        'enum' => [BasicObject, lambda { |rule, value|
          "must be one of #{JSON.generate(rule)}" unless rule.include?(value)
        }],
        'const' => [BasicObject, ->(rule, value) { "must be #{JSON.generate(rule)}" unless rule == value }],
        'minLength' => [String, ->(rule, value) { "must have at least #{rule} characters" if value.length < rule }],
        'maxLength' => [String, ->(rule, value) { "must have at most #{rule} characters" if value.length > rule }],
        'minItems' => [Array, ->(rule, value) { "must have at least #{rule} items" if value.length < rule }],
        'maxItems' => [Array, ->(rule, value) { "must have at most #{rule} items" if value.length > rule }],
        'minimum' => [Numeric, ->(rule, value) { "must be at least #{rule}" if value < rule }],
        'maximum' => [Numeric, ->(rule, value) { "must be at most #{rule}" if value > rule }]
      }.freeze
      # Seconds a pattern may take on one value; one that takes longer is that value's problem.
      PATTERN_TIMEOUT = 0.1
      private_constant :RULES, :PATTERN_TIMEOUT

      # @param json [String] the schema as JSON text.
      # @raise [SchemaError] when it is not JSON, or uses anything outside the subset.
      def initialize(json)
        @text = -json
        @root = parse(json)
        @patterns = SchemaSubset.check(@root, timeout: PATTERN_TIMEOUT)
        freeze
      end

      # @return [String] the schema as it was given, which is what a request sends.
      def to_s = @text

      # @param value [Object] a JSON value as JSON.parse returns it.
      # @return [Array<String>] what is wrong with the value, each as "<JSON pointer>: <problem>"; empty when it is
      #   valid.
      def validate(value) = problems(@root, value, '')

      private

      def parse(json)
        JSON.parse(json, allow_duplicate_key: false)
      rescue JSON::ParserError => e
        raise SchemaError, "The schema is not JSON: #{e.message}"
      end

      def problems(schema, value, path)
        return [] if schema == true
        return ["#{shown(path)}: is not allowed"] if schema == false

        own = schema.filter_map { |keyword, rule| problem(keyword, rule, value) }
        own.map { |problem| "#{shown(path)}: #{problem}" } + inner_problems(schema, value, path)
      end

      def problem(keyword, rule, value)
        case keyword
        when 'anyOf' then any_of_problem(rule, value)
        when 'pattern' then pattern_problem(rule, value)
        else
          kind, check = RULES[keyword]
          check.call(rule, value) if kind && value.is_a?(kind)
        end
      end

      def any_of_problem(options, value)
        'matches none of the allowed schemas' if options.none? { |option| problems(option, value, '').empty? }
      end

      def pattern_problem(pattern, value)
        return unless value.is_a?(String)

        "must match #{pattern}" unless @patterns.fetch(pattern).match?(value)
      rescue Regexp::TimeoutError
        "could not be matched against #{pattern} in time"
      end

      def inner_problems(schema, value, path)
        case value
        when Hash then object_problems(schema, value, path)
        when Array
          items = schema.fetch('items', true)
          value.each_with_index.flat_map { |item, index| problems(items, item, "#{path}/#{index}") }
        else []
        end
      end

      def object_problems(schema, value, path)
        missing = schema.fetch('required', []).reject { |name| value.key?(name) }
        missing.map { |name| "#{path}/#{name}: is required" } + value.flat_map do |name, child|
          rule = schema.dig('properties', name)
          problems(rule.nil? ? schema.fetch('additionalProperties', true) : rule, child, "#{path}/#{name}")
        end
      end

      def shown(path) = path.empty? ? '/' : path
    end
  end
end
