# frozen_string_literal: true

module Bookshop
  # The books, their prices and stock: searched, read and restocked through fixed SQL with bound values. A refused
  # request raises RefusedError; a database that cannot be reached raises PG::Error. Thread-safe.
  class Catalogue
    # The most books one search returns: a broad search's result of 10-15k tokens.
    MAX_SEARCH_RESULTS = 400
    # The books a search returns when it is given no limit.
    SEARCH_RESULTS = 20

    BOOKS = <<~SQL
      select b.id, b.title, a.name as author, g.name as genre, b.price, b.published_year as year, s.quantity as stock
      from books b
      join authors a on a.id = b.author_id
      join genres g on g.id = b.genre_id
      join stock s on s.book_id = b.id
    SQL

    # Never prepared: a generic plan could not use the trigram indexes for a LIKE pattern it does not know, so
    # exec_params plans it with each call's values.
    SEARCH = <<~SQL.freeze
      #{BOOKS}
      where ($1::text is null or lower(b.title) like lower($1))
        and ($2::text is null or lower(a.name) like lower($2))
        and ($3::text is null or lower(g.name) = lower($3))
        and ($4::numeric is null or b.price <= $4)
        and (not $5::boolean or s.quantity > 0)
      order by b.price, b.title
      limit $6
    SQL

    FIND = "#{BOOKS} where b.id = $1".freeze

    RESTOCK = 'update stock set quantity = quantity + $2 where book_id = $1'

    private_constant :SEARCH_RESULTS, :BOOKS, :SEARCH, :FIND, :RESTOCK

    def initialize(database:)
      @database = database
      freeze
    end

    # The books the filter keeps, cheapest first. Text filters take their wildcards literally; a blank one keeps
    # every book.
    #
    # @param limit [Integer] the most books to return, kept between 1 and MAX_SEARCH_RESULTS
    def search(filter = BookFilter.new, limit: SEARCH_RESULTS)
      values = [Database.containing(filter.title), Database.containing(filter.author), present(filter.genre),
                filter.max_price, filter.in_stock, limit.clamp(1, MAX_SEARCH_RESULTS)]
      @database.with { |connection| connection.exec_params(SEARCH, values).map { |row| book(row) } }
    end

    # @raise [RefusedError] when there is no such book
    def find(id)
      @database.with { |connection| read(connection, id) }
    end

    # Adds copies of a book to its stock.
    #
    # @return [Book] the book restocked
    # @raise [RefusedError] when the quantity is under 1 or there is no such book
    def restock(book_id:, quantity:)
      raise RefusedError, 'Restock at least one copy.' if quantity < 1

      @database.transaction do |connection|
        restocked = connection.exec_params(RESTOCK, [book_id, quantity]).cmd_tuples
        raise RefusedError, "There is no book with id #{book_id}." if restocked.zero?

        read(connection, book_id)
      end
    end

    private

    def read(connection, id)
      row = connection.exec_params(FIND, [id]).first
      raise RefusedError, "There is no book with id #{id}." unless row

      book(row)
    end

    def book(row)
      Book.new(id: row[:id], title: row[:title], author: row[:author], genre: row[:genre], price: row[:price],
               year: row[:year], stock: row[:stock])
    end

    def present(text)
      text unless text.nil? || text.strip.empty?
    end
  end
end
