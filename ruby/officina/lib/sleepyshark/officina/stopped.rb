# frozen_string_literal: true

require 'bigdecimal'

module Sleepyshark
  module Officina
    Stopped = Data.define(:reason, :detail, :usage, :cost, :model_calls, :tool_calls, :duration)

    # A run that ended before the model's answer, why, and what the run used.
    class Stopped
      # @param reason [Symbol] +:cancelled+ (the host cancelled), +:refusal+ (the model declined), +:output_limit+ (the
      #   reply reached the output token limit), +:context_full+ (the conversation no longer fits the model's context
      #   window), +:iteration_limit+ (the run made as many model calls as it may) or +:budget+ (a limit of the run's
      #   budget was used up before a model call, or cut the reply short)
      # @param detail [String, nil] a refusal's category, or the budget limit used up
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
