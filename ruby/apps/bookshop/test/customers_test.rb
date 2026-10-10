# frozen_string_literal: true

require 'test_helper'
require_relative 'database_server'

# The customers against the seeded database.
class CustomersTest < Minitest::Test
  include DatabaseServer

  def test_app05_search_finds_customers_by_part_of_their_name_or_email
    alice = Bookshop::Customer.new(id: 1, name: 'Alice Martin', email: 'alice.martin@example.com')

    assert_equal [alice], shop.customers.search('alice')
    assert_equal [alice], shop.customers.search('MARTIN@example')
    assert_empty shop.customers.search('nobody at all')
  end

  def test_app08_search_text_is_taken_literally_and_blank_text_names_no_one
    assert_empty shop.customers.search('_')
    assert_empty shop.customers.search('alice\\')
    assert_empty shop.customers.search('  ')
  end

  def test_app06_add_adds_one_and_refuses_a_second_with_the_same_email
    added = shop.customers.add(name: ' Zoe Park ', email: 'zoe.park@example.com')
    again = assert_raises(Bookshop::RefusedError) { shop.customers.add(name: 'Zoe P', email: 'zoe.park@example.com') }

    assert_equal 'Zoe Park', added.name
    assert_equal [added], shop.customers.search('zoe.park')
    assert_equal 'A customer with the email zoe.park@example.com already exists.', again.message
  end

  def test_app07_a_customer_without_a_name_is_refused
    refused = assert_raises(Bookshop::RefusedError) { shop.customers.add(name: ' ', email: 'x@example.com') }

    assert_equal 'A customer needs a name and an email.', refused.message
    assert_empty shop.customers.search('x@example.com')
  end
end
