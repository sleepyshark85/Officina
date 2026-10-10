# frozen_string_literal: true

require 'bigdecimal'
require 'json'
require 'test_helper'
require_relative 'database_server'

# The nine tools against the seeded database, called as the pipeline calls them once their input is valid: their
# results are the JSON .NET's and Go's tools return, amounts exact to the penny.
class ToolsTest < Minitest::Test
  include DatabaseServer

  TIME = /\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ\z/

  def setup
    super
    @tools = Bookshop::Tools.all(shop).to_h { [it.name, it] }
  end

  def test_app05_search_lists_books_without_their_year_and_get_book_gives_one_with_it
    found = call('search_books', '{"genre":"Fantasy","inStock":true,"limit":2}')
    book = call('get_book', '{"bookId":144}')

    assert_equal([144, 216], found.map { it['id'] })
    assert_equal %w[id title author genre price stock], found.first.keys
    assert_equal %w[id title author genre price year stock], book.keys
    assert_equal ['The Winter Archive', 'Fantasy', BigDecimal('6.28')], book.values_at('title', 'genre', 'price')
    assert_includes invoke('get_book', '{"bookId":144}'), '"price":6.28,'
  end

  def test_app05_find_customer_lists_their_orders_and_get_order_gives_one_with_its_lines
    customers = call('find_customer', '{"nameOrEmail":"alice.martin"}')
    orders = call('list_customer_orders', '{"customerId":1}')
    order = call('get_order', %({"orderId":#{orders.first['id']}}))

    assert_equal [{ 'id' => 1, 'name' => 'Alice Martin', 'email' => 'alice.martin@example.com' }], customers
    assert_equal [%w[id status placedAt total copies]], orders.map(&:keys).uniq
    assert_equal [%w[id customerId customer status placedAt total], %w[bookId title quantity unitPrice]],
                 [order['order'].keys, order['lines'].flat_map(&:keys).uniq]
    assert_match TIME, order['order']['placedAt']
    assert_equal(order['order']['total'], order['lines'].sum { it['unitPrice'] * it['quantity'] })
  end

  def test_app06_the_writes_return_what_they_changed_with_totals_to_the_penny
    added = call('add_customer', '{"name":"Zoe Park","email":"zoe.park@example.com"}')
    lines = '[{"bookId":144,"quantity":1},{"bookId":216,"quantity":1}]'
    placed = invoke('place_order', %({"customerId":#{added['id']},"lines":#{lines}}))
    cancelled = call('cancel_order', %({"orderId":#{JSON.parse(placed)['orderId']}}))
    restocked = call('restock_book', '{"bookId":144,"quantity":2}')

    assert_equal ['Zoe Park', 'zoe.park@example.com'], added.values_at('name', 'email')
    assert_includes placed, %("customer":"Zoe Park","lines":[{"bookId":144,"title":"The Winter Archive","quantity":1,)
    assert_includes placed, '"total":13.20}'
    assert_equal ['cancelled', [144, 216]], [cancelled['status'], cancelled['returnedToStock'].map { it['bookId'] }]
    assert_equal({ 'bookId' => 144, 'title' => 'The Winter Archive', 'stock' => stock(144) }, restocked)
  end

  def test_app07_too_few_copies_in_stock_is_refused_for_the_model_and_changes_nothing
    copies = stock(310)

    refusal = assert_raises(Bookshop::RefusedError) do
      invoke('place_order', %({"customerId":1,"lines":[{"bookId":310,"quantity":#{copies + 1}}]}))
    end

    assert_match(/\ANot enough stock for "[^"]+" \(id 310\): #{copies + 1} requested, #{copies} in stock/,
                 refusal.message)
    assert_equal copies, stock(310)
  end

  def test_app18_with_the_database_down_the_tools_fail_and_once_it_is_back_they_work_again
    call('get_book', '{"bookId":144}')
    take_database_down

    assert_raises(PG::ConnectionBad) { invoke('get_book', '{"bookId":144}') }
    assert_raises(PG::ConnectionBad) { invoke('restock_book', '{"bookId":144,"quantity":1}') }

    bring_database_back

    assert_equal 'The Winter Archive', call('get_book', '{"bookId":144}')['title']
    restocked = call('restock_book', '{"bookId":144,"quantity":1}')

    assert_equal stock(144), restocked['stock']
  end

  private

  # The tool's result for the input, as the JSON text the model reads.
  def invoke(name, input) = @tools.fetch(name).invoke(input, Sleepyshark::Officina::Cancellation.new)

  # The tool's result, parsed, with amounts as BigDecimal.
  def call(name, input) = JSON.parse(invoke(name, input), decimal_class: BigDecimal)
end
