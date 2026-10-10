# frozen_string_literal: true

module Bookshop
  # Amounts of money as the tools show them to the model.
  module Money
    # The amount as a JSON number to the penny, such as 13.20, as .NET and Go write it: exact, never through a Float.
    #
    # @param amount [BigDecimal] whole pennies, as the database's amounts and their sums are
    # @return [JSON::Fragment]
    def self.json(amount)
      whole, cents = amount.to_s('F').split('.')
      JSON::Fragment.new("#{whole}.#{cents.to_s.ljust(2, '0')}")
    end
  end
end
