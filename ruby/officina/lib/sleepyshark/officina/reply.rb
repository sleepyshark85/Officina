# frozen_string_literal: true

module Sleepyshark
  module Officina
    Reply = Data.define(:blocks, :stop, :detail)

    # A model's whole reply, which its stream returns once the model has stopped.
    class Reply
      # Why a model stops: it ended its turn, asked for tools, reached its output limit, refused, or found the
      # conversation too long for its context window; +:unknown+ for any other reason the provider gave.
      STOPS = %i[end tool_use max_tokens refusal context_full unknown].freeze

      # @param blocks [Array<Block>] the reply's blocks, in reply order, as the provider sent them
      # @param stop [Symbol] one of STOPS
      # @param detail [String, nil] a refusal's category, or the provider's own word for an unknown stop
      # @raise [Error] when the stop is not one of STOPS
      def initialize(blocks:, stop:, detail: nil)
        raise Error, "A reply stops for one of #{STOPS.join(', ')}, not #{stop.inspect}" unless STOPS.include?(stop)

        super(blocks: blocks.dup.freeze, stop:, detail: detail && -detail)
      end

      # The text of its blocks, joined.
      def text = blocks.map(&:text).join
    end
  end
end
