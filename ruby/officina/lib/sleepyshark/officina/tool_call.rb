# frozen_string_literal: true

module Sleepyshark
  module Officina
    ToolCall = Data.define(:id, :name, :input)

    # The core's view of the model's request to run a tool, read from the block that holds it.
    class ToolCall
      # @param id [String] the provider's id for the call, which its result answers
      # @param name [String] the tool's name
      # @param input [String] the tool's input, JSON as the model wrote it, not yet validated
      def initialize(id:, name:, input:)
        super(id: -id, name: -name, input: -input)
      end
    end
  end
end
