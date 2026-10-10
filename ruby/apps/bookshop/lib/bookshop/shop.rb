# frozen_string_literal: true

module Bookshop
  # The bookshop over its database: its catalogue, customers and orders, which the tools work on. It owns the
  # database's connections until it is closed.
  class Shop
    attr_reader :catalogue, :customers, :orders

    # The shop over a fresh connection pool, which nothing connects until the first call.
    #
    # @param url [String] a PostgreSQL connection URL
    def self.open(url) = new(database: Database.new(url))

    def initialize(database:)
      @database = database
      @catalogue = Catalogue.new(database:)
      @customers = Customers.new(database:)
      @orders = Orders.new(database:)
      freeze
    end

    # Closes the database's connections; the shop cannot be used afterwards.
    def close = @database.close
  end
end
