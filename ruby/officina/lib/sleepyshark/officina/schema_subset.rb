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
      ANY = ->(_) { true }
      COUNT = ->(value) { value.is_a?(Integer) && !value.negative? }
      # The keywords of the subset: the problem a schema has when the keyword's value is malformed, and the check of
      # that value. The annotations only describe a value.
      KEYWORDS = {
        'type' => ['unknown type', ->(value) { !Array(value).empty? && Array(value).all? { |type| TYPES.key?(type) } }],
        'properties' => ['must be an object', ->(value) { value.is_a?(Hash) }],
        'required' => ['must be an array of names', ->(value) { value.is_a?(Array) && value.all?(String) }],
        'additionalProperties' => [nil, ANY],
        'items' => [nil, ANY],
        'anyOf' => ['must be a non-empty array', ->(value) { value.is_a?(Array) && !value.empty? }],
        'enum' => ['must be an array', ->(value) { value.is_a?(Array) }],
        'const' => [nil, ANY],
        'minLength' => ['must be a non-negative integer', COUNT],
        'maxLength' => ['must be a non-negative integer', COUNT],
        'minItems' => ['must be a non-negative integer', COUNT],
        'maxItems' => ['must be a non-negative integer', COUNT],
        'minimum' => ['must be a number', ->(value) { value.is_a?(Numeric) }],
        'maximum' => ['must be a number', ->(value) { value.is_a?(Numeric) }],
        'pattern' => ['must be a string', ->(value) { value.is_a?(String) }]
      }.merge(
        %w[$schema $id $comment title description default examples format readOnly writeOnly deprecated]
          .to_h { |annotation| [annotation, [nil, ANY]] }
      ).transform_values(&:freeze).freeze
      private_constant :ANY, :COUNT, :KEYWORDS

      # @param schema [Object] a schema as JSON.parse returns it.
      # @param timeout [Float] seconds a pattern may take on one value.
      # @return [Hash{String => Regexp}] the schema's patterns, compiled.
      # @raise [SchemaError] naming where the schema leaves the subset.
      def self.check(schema, timeout:)
        # @type var patterns: Hash[String, Regexp]
        patterns = {}
        check_at(schema, '', patterns, timeout)
        patterns.freeze
      end

      def self.check_at(schema, path, patterns, timeout)
        return if [true, false].include?(schema)

        refuse(path, 'a schema must be an object or a boolean') unless schema.is_a?(Hash)
        schema.each { |keyword, value| check_keyword(keyword, value, "#{path}/#{keyword}", patterns, timeout) }
      end

      def self.check_keyword(keyword, value, at, patterns, timeout)
        problem, valid = KEYWORDS.fetch(keyword) { refuse(at, 'is not a supported keyword') }
        refuse(at, problem) unless valid.call(value)
        subschemas(keyword, value).each { |name, subschema| check_at(subschema, "#{at}#{name}", patterns, timeout) }
        patterns[value] ||= compile(value, at, timeout) if keyword == 'pattern'
      end

      def self.subschemas(keyword, value)
        case keyword
        when 'properties' then value.map { |name, schema| ["/#{name}", schema] }
        when 'anyOf' then value.each_with_index.map { |schema, index| ["/#{index}", schema] }
        when 'additionalProperties', 'items' then [['', value]]
        else []
        end
      end

      def self.compile(pattern, at, timeout)
        Regexp.new(pattern, timeout:)
      rescue RegexpError => e
        refuse(at, e.message)
      end

      def self.refuse(path, problem)
        raise SchemaError, "The schema at '#{path.empty? ? '/' : path}' is outside the supported subset: #{problem}."
      end
      private_class_method :check_at, :check_keyword, :subschemas, :compile, :refuse
    end
    private_constant :SchemaSubset
  end
end
