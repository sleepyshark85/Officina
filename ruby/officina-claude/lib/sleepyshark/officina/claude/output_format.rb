# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Claude
      # A typed output schema as Claude's structured output takes it: every object closed with additionalProperties
      # false, and the numeric bounds, maxItems and a minItems above 1 left out, as the API rejects them. The rest is
      # kept as written, in its order, which is the order the model writes the members in. The core still validates
      # the reply against the schema as written. Not probed live: an enum holding null, and a true schema.
      module OutputFormat
        UNSUPPORTED = %w[minimum maximum exclusiveMinimum exclusiveMaximum multipleOf maxItems].freeze
        private_constant :UNSUPPORTED

        # @param schema [String] the request's output schema, JSON text
        # @return [Hash] the output format of the request's output config
        # @raise [InvalidRequestError] when the schema has an open object, such as a map's, which cannot be closed
        #   without changing its meaning
        def self.of(schema) = { type: :json_schema, schema: adjust(JSON.parse(schema)) }

        # The schema adjusted, and the schemas inside it; anything but an object is kept.
        def self.adjust(schema)
          return schema unless schema.is_a?(Hash)

          unless schema.fetch('additionalProperties', false) == false
            raise InvalidRequestError, 'The output schema has an open object, which structured output cannot express'
          end

          adjusted = schema.filter_map do |keyword, value|
            [keyword, inner(keyword, value)] unless rejected?(keyword, value)
          end.to_h
          object?(adjusted) ? adjusted.merge('additionalProperties' => false) : adjusted
        end

        # Whether the API rejects the keyword with that value.
        def self.rejected?(keyword, value) = UNSUPPORTED.include?(keyword) || (keyword == 'minItems' && value > 1)

        # A keyword's value with the schemas it holds adjusted.
        def self.inner(keyword, value)
          case keyword
          when 'properties' then value.transform_values { adjust(it) }
          when 'items' then adjust(value)
          when 'anyOf' then value.map { adjust(it) }
          else value
          end
        end

        def self.object?(schema) = schema.key?('properties') || Array(schema['type']).include?('object')
        private_class_method :adjust, :rejected?, :inner, :object?
      end
      private_constant :OutputFormat
    end
  end
end
