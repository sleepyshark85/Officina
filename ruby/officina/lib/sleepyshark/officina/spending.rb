# frozen_string_literal: true

require 'bigdecimal'

module Sleepyshark
  module Officina
    # What a run has used so far, against its budget: its tokens and their cost, its model and tool calls, and its
    # time on the agent's clock.
    class Spending
      # The price of a model whose price is not known: its tokens cost nothing.
      FREE = Price.new(input: BigDecimal(0), output: BigDecimal(0), cache_read: BigDecimal(0),
                       cache_write: BigDecimal(0), cache_write_hour: BigDecimal(0))

      # @return [Usage] the tokens of every model call so far
      attr_reader :usage
      # @return [BigDecimal] what they cost, in US dollars; nothing when the model's price is not known
      attr_reader :cost
      # @return [Integer] the model calls made so far, the one in flight included
      attr_reader :model_calls

      # @param budget [Budget, nil] none limits nothing
      # @param price [Price, nil] the model's
      # @param clock [#call] the agent's
      def initialize(budget:, price:, clock:)
        @budget = budget || Budget.new
        @price = price || FREE
        @clock = clock
        @started = clock.call
        @usage = Usage.new
        @cost = BigDecimal(0)
        @model_calls = 0
        @tool_calls = 0
      end

      # Counts the usage a model call reported, and its cost.
      def add(usage)
        @usage += usage
        @cost += @price.cost(usage)
      end

      def count_model_call = @model_calls += 1
      def count_tool_calls(count) = @tool_calls += count

      # @return [Integer, nil] the most output tokens the next call may use, at least one once #reached is nil; nil when
      #   the budget does not limit them
      def output_limit = [tokens_left, affordable].compact.min

      # @return [String, nil] which limit is used up, as a sentence; nil when the run may call the model again
      def reached = calls_used_up || time_used_up || tokens_used_up || cost_used_up

      # @return [Completed, Stopped, Failed] the result with what the run used
      def report(result)
        result.with(usage: @usage, cost: @cost, model_calls: @model_calls, tool_calls: @tool_calls, duration: elapsed)
      end

      private

      def elapsed = @clock.call - @started

      def tokens_left = @budget.tokens&.-(@usage.total)

      # The output tokens what is left of the cost budget buys; nil when cost is not limited or output costs nothing.
      def affordable
        limit = @budget.cost
        output = @price.output
        ((limit - @cost) * 1_000_000 / output).floor if limit && output.positive?
      end

      def calls_used_up
        limit = @budget.model_calls
        "The model call budget is used up: #{@model_calls} of #{limit}." if limit && @model_calls >= limit
      end

      def time_used_up
        limit = @budget.time
        spent = elapsed
        "The time budget is used up: #{seconds(spent)} s of #{seconds(limit)} s." if limit && spent >= limit
      end

      def tokens_used_up
        limit = @budget.tokens
        total = @usage.total
        "The token budget is used up: #{thousands(total)} of #{thousands(limit)} tokens." if limit && total >= limit
      end

      # Used up too once what is left buys less than one output token.
      def cost_used_up
        limit = @budget.cost
        return unless limit && (@cost >= limit || affordable&.zero?)

        "The cost budget is used up: $#{dollars(@cost)} of $#{dollars(limit)}."
      end

      # As .NET writes them: seconds with at most one decimal, dollars with at most six, each rounded half away from
      # zero (for dollars whatever rounding mode the host's thread gives BigDecimal), and thousands with commas.
      def seconds(value) = value.round(1).to_s.delete_suffix('.0')
      def dollars(value) = value.round(6, half: :up).to_s('F').delete_suffix('.0')
      def thousands(value) = value.to_s.gsub(/\B(?=(\d{3})+\z)/, ',')
    end
    private_constant :Spending
  end
end
