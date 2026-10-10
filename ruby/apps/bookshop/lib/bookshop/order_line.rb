# frozen_string_literal: true

module Bookshop
  # A line of an order: copies of one book at the unit price charged, a BigDecimal in dollars.
  OrderLine = Data.define(:book_id, :title, :quantity, :unit_price)
end
