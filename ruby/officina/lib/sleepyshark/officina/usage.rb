# frozen_string_literal: true

module Sleepyshark
  module Officina
    Usage = Data.define(:input, :output, :cache_read, :cache_write)

    # Tokens as the provider bills them, each a count of tokens. Input counts those neither read from nor written to
    # the cache.
    class Usage
      # @param input [Integer] input tokens neither read from nor written to the cache
      # @param output [Integer]
      # @param cache_read [Integer] input tokens read from the cache
      # @param cache_write [Integer] input tokens written to the cache
      def initialize(input: 0, output: 0, cache_read: 0, cache_write: 0) = super

      # The tokens of both, kind by kind.
      def +(other)
        with(input: input + other.input, output: output + other.output, cache_read: cache_read + other.cache_read,
             cache_write: cache_write + other.cache_write)
      end
    end
  end
end
