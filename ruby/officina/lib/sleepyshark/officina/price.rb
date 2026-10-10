# frozen_string_literal: true

require 'bigdecimal'

module Sleepyshark
  module Officina
    Price = Data.define(:input, :output, :cache_read, :cache_write, :cache_write_hour)

    # What a model's tokens cost, in US dollars per million tokens of each kind that Usage counts, each a BigDecimal:
    # +input+ for the input tokens neither read from nor written to the cache, +output+, +cache_read+, +cache_write+
    # for the cache writes kept for the short default time and +cache_write_hour+ for those kept for an hour.
    class Price
      # @return [BigDecimal] what the tokens cost, in US dollars
      def cost(usage) = (read(usage) + written(usage)) / 1_000_000

      private

      # What the input, output and cache reads cost, per million.
      def read(usage) = (input * usage.input) + (output * usage.output) + (cache_read * usage.cache_read)

      # What the cache writes cost, per million: those kept an hour at their rate, the rest at five minutes'.
      def written(usage)
        (cache_write * (usage.cache_write - usage.cache_write_hour)) + (cache_write_hour * usage.cache_write_hour)
      end
    end
  end
end
