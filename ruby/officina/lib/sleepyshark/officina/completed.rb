# frozen_string_literal: true

require 'bigdecimal'

module Sleepyshark
  module Officina
    Completed = Data.define(:text, :output, :usage, :cost, :model_calls, :tool_calls, :duration)

    # A run that ended with the model's answer, and what the run used.
    class Completed
      # @param text [String] the final reply's text
      # @param output [Data, nil] the reply as a value of the agent's output type; nil when the agent has none
      # @param usage [Usage] the tokens of all its model calls
      # @param cost [BigDecimal] what they cost, in US dollars at the model's price; nothing when it is not known
      # @param model_calls [Integer]
      # @param tool_calls [Integer] the calls it answered, denied and failed ones included
      # @param duration [Float] seconds the run took, on the agent's clock
      def initialize(text:, output: nil, usage: Usage.new, cost: BigDecimal(0), model_calls: 0, tool_calls: 0,
                     duration: 0.0)
        super
      end
    end
  end
end
