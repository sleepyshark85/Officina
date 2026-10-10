# frozen_string_literal: true

module Bookshop
  Shop = Data.define(:catalogue, :customers, :orders, :database)

  # The bookshop over its database: its catalogue, customers and orders, which the tools work on.
  class Shop
    # The shop over a fresh database pool, which nothing connects until the first call.
    #
    # @param url [String] a PostgreSQL connection URL
    def self.open(url)
      database = Database.new(url)
      new(catalogue: Catalogue.new(database:), customers: Customers.new(database:), orders: Orders.new(database:),
          database:)
    end

    # Closes the database's connections; the shop cannot be used afterwards.
    def close = database.close
  end
end
