# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    Tool = Data.define(:name, :description, :input_schema)

    # An action the model may request. All of it reaches the model, so it is part of the prefix.
    class Tool
      # @param name [String] unique among an agent's tools
      # @param description [String]
      # @param input_schema [String] the JSON Schema of the tool's input, a JSON object; it reaches the model, and the
      #   prefix fingerprint, exactly as given
      # @raise [Error] when the name is blank or the schema is not a JSON object
      def initialize(name:, description:, input_schema:)
        raise Error, 'A tool needs a name' if name.strip.empty?
        raise Error, "The input schema of tool #{name} is not a JSON object" unless object?(input_schema)

        super(name: -name, description: -description, input_schema: -input_schema)
      end

      private

      def object?(json)
        JSON.parse(json, allow_duplicate_key: false).is_a?(Hash)
      rescue JSON::ParserError
        false
      end
    end
  end
end
