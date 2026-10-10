# frozen_string_literal: true

require 'test_helper'
require_relative 'database_server'

# The connections to the database, and the composition root that builds the shop over it.
class DatabaseTest < Minitest::Test
  include DatabaseServer

  Line = Data.define(:book_id, :quantity)

  def test_app18_with_the_database_down_calls_fail_and_once_it_is_back_they_work_again
    # Several pooled connections, all of which the stop ends.
    Array.new(5) { Thread.new { shop.catalogue.search } }.each(&:join)
    take_database_down
    down = [-> { shop.catalogue.find(144) },
            -> { shop.orders.place(customer_id: 1, lines: [Line.new(144, 1)]) }].map do |call|
      assert_raises(PG::Error, &call)
    end
    bring_database_back

    assert_equal 2, down.size
    5.times { assert_equal 144, shop.catalogue.find(144).id }
    assert_equal 'The Winter Archive', shop.catalogue.restock(book_id: 144, quantity: 1).title
  end

  def test_app04_build_reads_the_database_from_the_settings
    # Its connection is ended with the test's database, which is dropped by force.
    built = Bookshop.build(env: { 'BOOKSHOP_DATABASE' => database_url })

    assert_equal 'Alice Martin', built.customers.search('alice').first.name
  end
end
