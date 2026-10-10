# frozen_string_literal: true

module Sleepyshark
  module Officina
    ToolResult = Data.define(:call_id, :content, :error)

    # A tool call's result as the model gets it; any failure is an error result, which the model reads.
    class ToolResult
      # @param call_id [String] the id of the call it answers
      # @param content [String] what the tool returned, or why the call failed
      # @param error [Boolean] whether the call failed
      def initialize(call_id:, content:, error:)
        super(call_id: -call_id, content: -content, error:)
      end

      def error? = error
    end
  end
end
