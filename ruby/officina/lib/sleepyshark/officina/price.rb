# frozen_string_literal: true

require 'bigdecimal'

module Sleepyshark
  module Officina
    Price = Data.define(:input, :output, :cache_read, :cache_write)

    # What a model's tokens cost, in US dollars per million tokens of each kind that Usage counts, each a BigDecimal:
    # +input+ for the input tokens neither read from nor written to the cache, +output+, +cache_read+ and +cache_write+.
    class Price
      # @return [BigDecimal] what the tokens cost, in US dollars
      def cost(usage)
        ((input * usage.input) + (output * usage.output) + (cache_read * usage.cache_read) +
          (cache_write * usage.cache_write)) / 1_000_000
      end
    end
  end
end
