# frozen_string_literal: true

require 'bookshop'
require 'test_helper'

# The password the agent keeps out of what it shows, read from the database's URL.
class DatabasePasswordTest < Minitest::Test
  def test_evt03_the_password_is_read_from_the_url_decoded
    assert_equal 'shelf-demo-41', Bookshop::Database.password('postgres://bookshop:shelf-demo-41@localhost:5433/bookshop')
    assert_equal 'p@ss:w/rd', Bookshop::Database.password('postgres://bookshop:p%40ss%3Aw%2Frd@localhost/bookshop')
  end

  def test_evt03_a_url_without_a_password_has_none
    assert_nil Bookshop::Database.password('postgres://bookshop@localhost/bookshop')
    assert_nil Bookshop::Database.password('postgres://localhost/bookshop')
  end
end
