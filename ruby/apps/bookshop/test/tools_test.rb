# frozen_string_literal: true

require 'bigdecimal'
require 'json'
require 'time'
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

  def test_app05_search_without_in_stock_finds_books_with_no_copies_too
    select_integer('update stock set quantity = 0 where book_id = $1 returning quantity', 310)
    title = JSON.generate(call('get_book', '{"bookId":310}')['title'])

    every = call('search_books', %({"title":#{title}})).map { it['id'] }
    in_stock = call('search_books', %({"title":#{title},"inStock":true})).map { it['id'] }

    assert_includes every, 310
    refute_includes in_stock, 310
  end

  def test_app05_find_customer_matches_part_of_an_email_and_list_customer_orders_lists_theirs
    customers = call('find_customer', '{"nameOrEmail":"alice.martin"}')
    orders = call('list_customer_orders', '{"customerId":1}')

    assert_equal [{ 'id' => 1, 'name' => 'Alice Martin', 'email' => 'alice.martin@example.com' }], customers
    assert_equal [%w[id status placedAt total copies]], orders.map(&:keys).uniq
  end

  def test_app05_get_order_gives_the_order_and_its_lines_which_add_up_to_its_total
    id = call('list_customer_orders', '{"customerId":1}').first['id']
    order = call('get_order', %({"orderId":#{id}}))

    assert_equal %w[id customerId customer status placedAt total], order['order'].keys
    assert_equal [%w[bookId title quantity unitPrice]], order['lines'].map(&:keys).uniq
    assert_match TIME, order['order']['placedAt']
    assert_equal(order['order']['total'], order['lines'].sum { it['unitPrice'] * it['quantity'] })
  end

  def test_app05_an_order_placed_now_shows_its_time_to_the_microsecond_as_dotnet_does
    id = call('place_order', '{"customerId":1,"lines":[{"bookId":144,"quantity":1}]}')['orderId']
    shown = call('get_order', %({"orderId":#{id}}))['order']['placedAt']
    stored = select_integer('select (extract(epoch from placed_at) * 1000000)::bigint from orders where id = $1', id)

    assert_match(/\A[^.]+(\.\d*[1-9])?Z\z/, shown)
    assert_equal stored, (Time.iso8601(shown).to_r * 1_000_000).to_i
  end

  def test_app06_add_customer_and_restock_book_return_what_they_changed
    added = call('add_customer', '{"name":"Zoe Park","email":"zoe.park@example.com"}')
    restocked = call('restock_book', '{"bookId":144,"quantity":2}')

    assert_equal({ 'name' => 'Zoe Park', 'email' => 'zoe.park@example.com' }, added.slice('name', 'email'))
    assert_equal({ 'bookId' => 144, 'title' => 'The Winter Archive', 'stock' => stock(144) }, restocked)
  end

  def test_app06_place_order_totals_to_the_penny_and_cancel_order_returns_the_copies
    lines = '[{"bookId":144,"quantity":1},{"bookId":216,"quantity":1}]'
    placed = invoke('place_order', %({"customerId":1,"lines":#{lines}}))
    cancelled = call('cancel_order', %({"orderId":#{JSON.parse(placed)['orderId']}}))

    assert_includes placed, '"customer":"Alice Martin","lines":[{"bookId":144,"title":"The Winter Archive",' \
                            '"quantity":1,'
    assert_includes placed, '"total":13.20}'
    assert_equal 'cancelled', cancelled['status']
    assert_equal([144, 216], cancelled['returnedToStock'].map { it['bookId'] })
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
