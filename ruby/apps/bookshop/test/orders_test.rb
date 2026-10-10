# frozen_string_literal: true

require 'test_helper'
require_relative 'database_server'

# The orders against the seeded database.
class OrdersTest < Minitest::Test
  include DatabaseServer

  Line = Data.define(:book_id, :quantity)
  NO_LINES = 'An order needs at least one line, each book once, each for at least one copy.'

  def test_app05_reads_a_customers_orders_and_an_order_with_its_lines
    orders = shop.orders.of_customer(1)
    order = shop.orders.find(orders.first.id)

    assert_equal orders.sort_by { [it.placed_at, it.id] }.reverse, orders
    assert_equal 'Alice Martin', order.customer
    assert_equal(order.total, order.lines.sum { it.unit_price * it.quantity })
    assert_equal orders.first.copies, order.lines.sum(&:quantity)
  end

  def test_app07_unknown_ids_are_refused
    { -> { shop.orders.find(9999) } => 'There is no order with id 9999.',
      -> { shop.orders.cancel(9999) } => 'There is no order with id 9999.',
      -> { shop.orders.of_customer(9999) } => 'There is no customer with id 9999.' }.each do |call, message|
      assert_equal message, assert_raises(Bookshop::RefusedError, &call).message
    end
  end

  def test_app06_place_takes_the_copies_from_stock_and_charges_the_current_prices
    stock160 = stock(160)
    stock161 = stock(161)
    prices = [160, 161].map { shop.catalogue.find(it).price }

    placed = shop.orders.place(customer_id: 1, lines: [Line.new(160, 2), Line.new(161, 1)])
    lines = placed.lines.map { [it.book_id, it.quantity, it.unit_price] }

    assert_equal [[160, 2, prices[0]], [161, 1, prices[1]]], lines
    assert_equal (prices[0] * 2) + prices[1], placed.total
    assert_equal placed, shop.orders.find(placed.id)
    assert_equal ['placed', stock160 - 2, stock161 - 1], [placed.status, stock(160), stock(161)]
  end

  def test_app07_business_rule_failures_are_refused_and_change_nothing
    before = [stock(170), count('select count(*) from orders')]
    available = stock(170)

    { [1, [Line.new(171, 1), Line.new(170, available + 1)]] =>
        "Not enough stock for \"The Crimson Clockmaker\" (id 170): #{available + 1} requested, #{available} in " \
        'stock. Nothing was ordered.',
      [9999, [Line.new(170, 1)]] => 'There is no customer with id 9999.',
      [1, [Line.new(9999, 1)]] => 'There is no book with id 9999. Nothing was ordered.',
      [1, [Line.new(170, 1), Line.new(170, 1)]] => NO_LINES,
      [1, [Line.new(170, 0)]] => NO_LINES,
      [1, []] => NO_LINES }.each do |(customer_id, lines), message|
      refused = assert_raises(Bookshop::RefusedError) { shop.orders.place(customer_id:, lines:) }

      assert_equal message, refused.message
    end
    assert_equal before, [stock(170), count('select count(*) from orders')]
  end

  def test_app06_concurrent_orders_never_take_more_copies_than_are_in_stock
    available = stock(180)

    outcomes = Array.new(available + 3) do
      Thread.new do
        shop.orders.place(customer_id: 1, lines: [Line.new(180, 1)])
      rescue Bookshop::RefusedError => e
        e.message
      end
    end.map(&:value)

    assert_equal(available, outcomes.count { it.is_a?(Bookshop::Order) })
    assert(outcomes.grep(String).all? { it.start_with?('Not enough stock') })
    assert_equal 0, stock(180)
  end

  def test_app06_cancel_returns_the_copies_once
    before = stock(190)
    placed = shop.orders.place(customer_id: 1, lines: [Line.new(190, 2)])

    cancelled = shop.orders.cancel(placed.id)
    after_cancel = stock(190)
    again = assert_raises(Bookshop::RefusedError) { shop.orders.cancel(placed.id) }

    assert_equal ['cancelled', [[190, 2]]], [cancelled.status, cancelled.lines.map { [it.book_id, it.quantity] }]
    assert_equal "Order #{placed.id} is already cancelled.", again.message
    assert_equal [before, before], [after_cancel, stock(190)]
  end
end
