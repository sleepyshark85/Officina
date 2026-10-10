# frozen_string_literal: true

require 'json'
require 'test_helper'
require_relative 'database_server'

# The catalogue against the seeded database.
class CatalogueTest < Minitest::Test
  include DatabaseServer

  def test_app05_search_filters_by_genre_and_stock_and_lists_the_cheapest_first
    books = catalogue.search(filter(genre: 'fantasy', in_stock: true), limit: 3)
    cheapest = catalogue.search(filter(genre: 'Fantasy'), limit: 1)

    # Book 72 is the cheapest fantasy book, and out of stock.
    assert_equal [144, 216, 288], books.map(&:id)
    assert(books.all? { it.genre == 'Fantasy' && it.stock.positive? })
    assert_equal books.sort_by { [it.price, it.title] }, books
    assert_equal [72], cheapest.map(&:id)
  end

  def test_app05_search_matches_part_of_the_title_or_author_and_a_highest_price
    by_title = catalogue.search(filter(title: 'silver tide'))
    by_author = catalogue.search(filter(author: 'OKAFOR', max_price: 10), limit: 100)

    assert_equal ['The Silver Tide'], by_title.map(&:title)
    refute_empty by_author
    assert(by_author.all? { it.author.include?('Okafor') && it.price <= 10 })
  end

  def test_app05_a_blank_text_filter_filters_nothing
    blank = catalogue.search(filter(title: ' ', author: '', genre: '  '), limit: 50)

    assert_equal catalogue.search(limit: 50).map(&:id), blank.map(&:id)
  end

  def test_app08_search_text_is_taken_literally_so_a_wildcard_matches_only_itself
    assert_empty catalogue.search(filter(title: '_'))
    assert_empty catalogue.search(filter(author: '%'))
    assert_empty catalogue.search(filter(title: '\\'))
  end

  def test_app17_a_broad_search_returns_10_to_15k_tokens_within_the_result_limit
    books = catalogue.search(limit: 1000)
    # About four characters a token for this JSON.
    size = JSON.generate(books.map { it.to_h.merge(price: it.price.to_f) }).bytesize

    assert_equal Bookshop::Catalogue::MAX_SEARCH_RESULTS, books.size
    assert_includes 40_000..60_000, size
    assert_equal 20, catalogue.search.size
  end

  def test_app05_find_reads_one_book
    expected = Bookshop::Book.new(id: 144, title: 'The Winter Archive', author: 'Ingrid Okafor', genre: 'Fantasy',
                                  price: BigDecimal('6.28'), year: 1966, stock: 12)

    assert_equal expected, catalogue.find(144)
  end

  def test_app07_unknown_books_are_refused
    unknown = assert_raises(Bookshop::RefusedError) { catalogue.find(9999) }
    restocked = assert_raises(Bookshop::RefusedError) { catalogue.restock(book_id: 9999, quantity: 1) }

    assert_equal ['There is no book with id 9999.'] * 2, [unknown.message, restocked.message]
  end

  def test_app06_restock_adds_copies
    before = stock(200)

    restocked = catalogue.restock(book_id: 200, quantity: 5)

    assert_equal before + 5, restocked.stock
    assert_equal before + 5, stock(200)
  end

  def test_app07_restocking_no_copies_is_refused_and_changes_nothing
    before = stock(170)

    refused = assert_raises(Bookshop::RefusedError) { catalogue.restock(book_id: 170, quantity: 0) }

    assert_equal 'Restock at least one copy.', refused.message
    assert_equal before, stock(170)
  end

  private

  def catalogue = shop.catalogue

  def filter(**) = Bookshop::BookFilter.new(**)
end
