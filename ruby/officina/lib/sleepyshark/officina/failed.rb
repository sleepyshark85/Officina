# frozen_string_literal: true

require 'bigdecimal'

module Sleepyshark
  module Officina
    Failed = Data.define(:reason, :detail, :usage, :cost, :model_calls, :tool_calls, :duration)

    # A run that went wrong, why, and what the run used.
    class Failed
      # @param reason [Symbol] +:model_error+ (a model call failed after its retries), +:unexpected_stop+ (the model
      #   stopped in a way the run cannot act on), +:prefix_mismatch+ (the agent's tools, instructions, output type or
      #   model settings differ from those the conversation was started with), +:invalid_output+ (the reply is not
      #   a value of the agent's output type) or +:tool_source_unavailable+ (a source of the agent's tools, such as
      #   an MCP server, could not connect at the start of the run)
      # @param detail [String] what happened
      # @param usage [Usage] the tokens of all its model calls
      # @param cost [BigDecimal] what they cost, in US dollars at the model's price; nothing when it is not known
      # @param model_calls [Integer]
      # @param tool_calls [Integer] the calls it answered, denied and failed ones included
      # @param duration [Float] seconds the run took, on the agent's clock
      def initialize(reason:, detail:, usage: Usage.new, cost: BigDecimal(0), model_calls: 0, tool_calls: 0,
                     duration: 0.0)
        super
      end
    end
  end
end
