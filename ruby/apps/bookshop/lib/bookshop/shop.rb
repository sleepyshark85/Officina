# frozen_string_literal: true

module Bookshop
  # The bookshop over its database: its catalogue, customers and orders, which the tools work on.
  Shop = Data.define(:catalogue, :customers, :orders)
end
