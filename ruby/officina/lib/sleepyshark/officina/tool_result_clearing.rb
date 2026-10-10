# frozen_string_literal: true

module Sleepyshark
  module Officina
    ToolResultClearing = Data.define(:after, :keep, :at_least_tokens)

    # When the provider clears old tool results from what a request shows the model, and which it keeps. The
    # conversation keeps them all.
    class ToolResultClearing
      # @param after [Integer] clears once the conversation holds more than this many tool calls, at least 1
      # @param keep [Integer] how many of the latest tool calls keep their results
      # @param at_least_tokens [Integer] clears only when at least this many input tokens go; 0 clears whatever there
      #   is. Each clearing rewrites the cached tail, so this keeps clearings few and worth their cost
      # @raise [Error] when +after+ is below 1, or +keep+ or +at_least_tokens+ below 0
      def initialize(after:, keep: 0, at_least_tokens: 0)
        raise Error, "Tool results are cleared after at least 1 tool call, not #{after}" unless after.positive?
        raise Error, "Tool result clearing cannot keep #{keep} tool calls" if keep.negative?
        raise Error, "Tool result clearing cannot clear at least #{at_least_tokens} tokens" if at_least_tokens.negative?

        super
      end

      # Its settings in the prefix fingerprint, as .NET writes them: +"clearAfter":…,"clearKeep":…,
      # "clearAtLeastTokens":…+.
      # @return [String]
      def fingerprint = %("clearAfter":#{after},"clearKeep":#{keep},"clearAtLeastTokens":#{at_least_tokens})
    end
  end
end
