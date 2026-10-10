# frozen_string_literal: true

require 'bigdecimal'

module Sleepyshark
  module Officina
    Failed = Data.define(:reason, :detail, :usage, :cost, :model_calls, :tool_calls, :duration)

    # A run that went wrong, why, and what the run used.
    class Failed
      # @param reason [Symbol] +:model_error+ (a model call failed after its retries), +:unexpected_stop+ (the model
      #   stopped in a way the run cannot act on) or +:prefix_mismatch+ (the agent's tools, instructions or model
      #   settings differ from those the conversation was started with)
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
