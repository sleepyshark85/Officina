# frozen_string_literal: true

require 'test_helper'
require_relative 'console_session'

# The tools through the console end to end: reads, approved and declined writes, a refusal, a multi-step request
# and the database going down.
class ConsoleToolsTest < Minitest::Test
  include ConsoleSession

  def test_app05_the_read_tools_of_one_reply_all_answer_from_the_database
    model = ScriptedModel.new(
      say_then_call('Looking.', call('c1', 'find_customer', '{"nameOrEmail":"Alice Martin"}'),
                    call('c2', 'list_customer_orders', '{"customerId":1}'), call('c3', 'get_book', '{"bookId":216}'),
                    call('c4', 'search_books', '{"genre":"Fantasy","inStock":true,"limit":2}')),
      ScriptedModel.text('Here is what I found.')
    )

    session(model, 'Sam', 'What do we know about Alice Martin?', '/quit')
    customer, orders, book, books = results(model, -1)

    assert_includes customer.content, '"name":"Alice Martin"'
    assert_includes orders.content, '"status":'
    assert_includes book.content, '"title":"The Hollow Island"'
    assert_includes books.content, '"id":144'
    assert_equal [false] * 4, [customer, orders, book, books].map(&:error?)
  end

  def test_app05_get_order_answers_and_the_reply_goes_on
    model = ScriptedModel.new(say_then_call('Looking.', call('c1', 'get_order', '{"orderId":77}')),
                              ScriptedModel.text('Here is order 77.'))

    transcript = session(model, 'Sam', 'What is in order 77?', '/quit')

    assert_includes results(model, -1).first.content, '"order":{"id":77,'
    assert_in_order transcript, '  < get_order: ok', 'Here is order 77.'
  end

  def test_app06_a_write_shows_its_exact_input_for_approval_and_runs_only_if_approved
    copies = [stock(300), stock(301)]
    model = ScriptedModel.new(say_then_call("I'll add five.",
                                            call('c1', 'restock_book', '{"bookId":300,"quantity":5}')),
                              ScriptedModel.text('Done: five more copies.'),
                              say_then_call("I'll add three.",
                                            call('c2', 'restock_book', '{"bookId":301,"quantity":3}')),
                              ScriptedModel.text('Understood, I left the stock as it was.'))

    transcript = session(model, 'Sam', 'Restock book 300 with 5.', 'y', 'Restock book 301 with 3.', 'n', '/quit')

    assert_in_order transcript, "  ? restock_book needs your approval. Its exact input:\n",
                    %(    {"bookId":300,"quantity":5}\n), "    Approve? [y/N] y\n", "  < restock_book: ok\n",
                    'Done: five more copies.', %(    {"bookId":301,"quantity":3}\n), "    Approve? [y/N] n\n",
                    "  < restock_book: error: The call was denied: the staff member declined\n",
                    'Understood, I left the stock as it was.'
    assert_equal [copies[0] + 5, copies[1]], [stock(300), stock(301)]
    assert_predicate results(model, -1).first, :error?
  end

  def test_app07_too_few_copies_comes_back_as_an_error_result_and_the_model_recovers_in_the_same_reply
    copies = stock(310)
    order = lambda { |id, quantity|
      call(id, 'place_order', %({"customerId":1,"lines":[{"bookId":310,"quantity":#{quantity}}]}))
    }
    model = ScriptedModel.new(say_then_call('Placing the order.', order.call('c1', copies + 10)),
                              say_then_call("Only #{copies} are in stock; I'll order those.", order.call('c2', copies)),
                              ScriptedModel.text("Ordered all #{copies} copies."))

    transcript = session(model, 'Sam', "Order #{copies + 10} copies of book 310 for Alice.", 'y', 'y', '/quit')

    refused = results(model, 1).first

    assert_predicate refused, :error?
    assert_includes refused.content, "Not enough stock for \"#{book_title(310)}\" (id 310)"
    assert_in_order transcript, '  < place_order: error: ', 'Not enough stock', "Only #{copies} are in stock",
                    '  < place_order: ok', "Ordered all #{copies} copies."
    assert_equal 0, stock(310)
  end

  def test_app09_a_multi_step_request_finds_searches_orders_after_approval_and_answers
    before = order_counts
    alice = '{"nameOrEmail":"Alice Martin"}'
    lines = '{"customerId":1,"lines":[{"bookId":144,"quantity":1},{"bookId":216,"quantity":1}]}'
    model = cheapest_fantasy_order(alice, lines)

    transcript = session(model, 'Sam', 'Order the two cheapest fantasy books in stock for Alice Martin and tell me ' \
                                       'the total', 'y', '/quit')

    # The two reads run at once, so only each one's own lines are in order, both before the write.
    assert_in_order transcript, "  > find_customer #{alice}", '  < find_customer: ok', '  > place_order'
    assert_in_order transcript, '  > search_books', '  < search_books: ok', "  > place_order #{lines}",
                    "    #{lines}\n", "    Approve? [y/N] y\n", "  < place_order: ok\n", "Total £13.20.\n"
    assert_includes results(model, -1).first.content, '"total":13.20}'
    assert_equal [before[0] - 1, before[1] - 1, before[2] + 1], order_counts
    assert_equal 1320, select_integer('select (total * 100)::int from orders order by id desc limit 1')
  end

  def test_app18_with_the_database_down_tools_return_errors_and_once_it_is_back_the_session_works_again
    model = ScriptedModel.new(say_then_call('Checking.', call('c1', 'get_book', '{"bookId":144}')),
                              ScriptedModel.text("I can't reach the database right now; please try again shortly."),
                              say_then_call('Checking again.', call('c2', 'get_book', '{"bookId":144}')),
                              ScriptedModel.text('The Winter Archive is in stock.'))
    take_database_down

    transcript = session(model, 'Sam', 'Is book 144 in stock?', -> { bring_database_back }, 'And now?', '/quit')

    assert_in_order transcript, '  < get_book: error: ', "I can't reach the database", '  < get_book: ok',
                    'The Winter Archive is in stock.'
    assert_predicate results(model, 1).first, :error?
    refute_predicate results(model, -1).first, :error?
  end

  private

  # The copies of books 144 and 216 in stock, and the number of orders.
  def order_counts = [stock(144), stock(216), select_integer('select count(*) from orders')]

  # The model's replies to the multi-step request: it finds Alice and the books at once, orders them, and answers.
  def cheapest_fantasy_order(alice, lines)
    ScriptedModel.new(
      say_then_call('Let me find Alice and the cheapest fantasy books in stock.', call('c1', 'find_customer', alice),
                    call('c2', 'search_books', '{"genre":"Fantasy","inStock":true,"limit":2}')),
      say_then_call("I'll order The Winter Archive and The Hollow Island.", call('c3', 'place_order', lines)),
      ScriptedModel.text('Order placed for Alice Martin: The Winter Archive (£6.28) and The Hollow Island (£6.92). ' \
                         'Total £13.20.')
    )
  end
end
