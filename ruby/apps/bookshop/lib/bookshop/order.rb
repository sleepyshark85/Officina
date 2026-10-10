# frozen_string_literal: true

module Bookshop
  # An order with its lines (OrderLine, by book id): status "placed" or "cancelled", placed_at a Time, total a
  # BigDecimal in dollars, customer the customer's name.
  Order = Data.define(:id, :customer_id, :customer, :status, :placed_at, :total, :lines)
end
