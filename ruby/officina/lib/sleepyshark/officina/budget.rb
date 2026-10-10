# frozen_string_literal: true

module Sleepyshark
  module Officina
    Budget = Data.define(:cost, :tokens, :model_calls, :time)

    # Limits on one run, each optional, checked before every model call: a limit already used up, zero included, stops
    # the run before its next call. Each call's output limit is lowered to what the remaining cost and tokens allow, so
    # a call overshoots by at most its input (about twice that when it compacts). A call in flight is never stopped.
    class Budget
      # @param cost [BigDecimal, nil] the most the run may spend, in US dollars at the model's price; it needs a model
      #   with a price
      # @param tokens [Integer, nil] the most tokens the run may use: input, output, cache reads and writes
      # @param model_calls [Integer, nil] the most model calls the run may make
      # @param time [Integer, Float, nil] the longest the run may take, in seconds; a call that starts in time may end
      #   after it
      def initialize(cost: nil, tokens: nil, model_calls: nil, time: nil) = super
    end
  end
end
