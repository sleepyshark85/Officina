# frozen_string_literal: true

require 'bigdecimal'
require 'bookshop'
require 'json'
require 'test_helper'

# Amounts as the tools write them.
class MoneyTest < Minitest::Test
  def test_app06_an_amount_is_a_json_number_to_the_penny_without_going_through_a_float
    amounts = %w[13.2 7 0.05 1234567890123.45 0.1].map { Bookshop::Money.json(BigDecimal(it)) }

    assert_equal '[13.20,7.00,0.05,1234567890123.45,0.10]', JSON.generate(amounts)
  end
end
