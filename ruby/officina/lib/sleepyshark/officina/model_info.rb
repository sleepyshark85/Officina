# frozen_string_literal: true

module Sleepyshark
  module Officina
    ModelInfo = Data.define(:provider, :name, :price)

    # A model as telemetry names it, and its price.
    class ModelInfo
      # @param provider [String] the provider's name, such as "anthropic"
      # @param name [String] the model's identifier, such as "claude-opus-5-5"
      # @param price [Price, nil] nil for a price not known: the tokens then cost nothing
      def initialize(provider:, name:, price: nil)
        super(provider: -provider, name: -name, price:)
      end
    end
  end
end
