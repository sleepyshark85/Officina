# frozen_string_literal: true

require 'bigdecimal'
require 'connection_pool'
require 'pg'
require 'sleepyshark/officina'
require_relative 'bookshop/book'
require_relative 'bookshop/customer'
require_relative 'bookshop/order_line'
require_relative 'bookshop/order'
require_relative 'bookshop/order_summary'
require_relative 'bookshop/refused_error'
require_relative 'bookshop/database'
require_relative 'bookshop/catalogue'
require_relative 'bookshop/customers'
require_relative 'bookshop/orders'
require_relative 'bookshop/shop'

# Bookshop Assistant, Officina's reference application: a console chatbot for the staff of a bookshop.
module Bookshop
  # The compose file's database (apps/BookshopAssistant/compose.yaml), with its demo password.
  COMPOSE_DATABASE = 'postgres://bookshop:shelf-demo-41@localhost:5432/bookshop'

  # The composition root: builds the application from its settings. So far the shop over its database; the console
  # and the agent come next.
  #
  # @param env [#fetch] the settings: BOOKSHOP_DATABASE, a PostgreSQL URL, the compose file's database if not set
  # @return [Shop]
  def self.build(env: ENV)
    shop(Database.new(env.fetch('BOOKSHOP_DATABASE', COMPOSE_DATABASE)))
  end

  # The shop over a database.
  #
  # @param database [Database]
  # @return [Shop]
  def self.shop(database)
    Shop.new(catalogue: Catalogue.new(database:), customers: Customers.new(database:), orders: Orders.new(database:))
  end
end
