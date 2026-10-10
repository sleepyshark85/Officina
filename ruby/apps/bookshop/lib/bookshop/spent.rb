# frozen_string_literal: true

module Bookshop
  # How the console shows what replies spent, as .NET and Go show it: tokens with their thousands separated by commas,
  # and US dollars rounded half away from zero, never through a Float.
  module Spent
    # Such as "tokens: 5,200 in (77% from cache), 300 out": all the input, cache reads and writes included, with the
    # share of it read from the cache.
    #
    # @param usage [Sleepyshark::Officina::Usage]
    def self.tokens(usage)
      input = usage.all_input
      share = input.zero? ? 0 : (Rational(usage.cache_read, input) * 100).round
      "tokens: #{thousands(input)} in (#{share}% from cache), #{thousands(usage.output)} out"
    end

    # The amount to four decimals, such as 0.0123.
    #
    # @param amount [BigDecimal] US dollars
    def self.dollars(amount) = decimals(amount, 4)

    # A budget with two to four decimals, such as 5.00 or 0.125.
    #
    # @param amount [BigDecimal] US dollars
    def self.budget(amount) = decimals(amount, 4).sub(/(\.\d\d\d?)0+\z/, '\1')

    # The count, not negative, with its thousands separated by commas, such as 1,000.
    def self.thousands(count) = count.to_s.gsub(/\B(?=(\d{3})+\z)/, ',')

    def self.decimals(amount, places)
      whole, fraction = amount.round(places, half: :up).to_s('F').split('.')
      "#{whole}.#{fraction.to_s.ljust(places, '0')}"
    end
    private_class_method :decimals
  end
  private_constant :Spent
end
