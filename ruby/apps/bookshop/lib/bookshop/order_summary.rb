# frozen_string_literal: true

module Bookshop
  # An order in a customer's list: status "placed" or "cancelled", placed_at a Time, total a BigDecimal in dollars,
  # copies the copies of all its lines.
  OrderSummary = Data.define(:id, :status, :placed_at, :total, :copies)
end
