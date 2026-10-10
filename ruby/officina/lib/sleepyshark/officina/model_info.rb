# frozen_string_literal: true

module Sleepyshark
  module Officina
    ModelInfo = Data.define(:provider, :name, :price, :compacts, :clears_tool_results)

    # A model as telemetry names it, its price, and how its provider can shorten a long conversation.
    class ModelInfo
      # @param provider [String] the provider's name, such as "anthropic"
      # @param name [String] the model's identifier, such as "claude-opus-5-5"
      # @param price [Price, nil] nil for a price not known: the tokens then cost nothing
      # @param compacts [Boolean] whether the provider compacts a conversation on its side; a run on a model whose
      #   provider does not stops with +:context_full+ once the conversation fills the model's context window
      # @param clears_tool_results [Boolean] whether the provider clears old tool results on its side
      def initialize(provider:, name:, price: nil, compacts: false, clears_tool_results: false)
        super(provider: -provider, name: -name, price:, compacts:, clears_tool_results:)
      end

      # @return [Boolean] whether the provider compacts a conversation on its side
      alias compacts? compacts
      # @return [Boolean] whether the provider clears old tool results on its side
      alias clears_tool_results? clears_tool_results
      private :compacts, :clears_tool_results
    end
  end
end
