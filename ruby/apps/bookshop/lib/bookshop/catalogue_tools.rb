# frozen_string_literal: true

module Bookshop
  # The tools over the catalogue: search_books and get_book read, restock_book writes once approved.
  module CatalogueTools
    Input = Sleepyshark::Officina::Input

    SEARCH = Input.define do
      string :title, 'Part of the title.', optional: true
      string :author, "Part of the author's name.", optional: true
      string :genre, 'The genre: Fantasy, Science Fiction, Mystery, Romance, History, Biography, Poetry, Horror, ' \
                     'Children, Cookery, Travel or Philosophy.', optional: true
      number :max_price, 'The highest price.', optional: true, nullable: true
      boolean :in_stock, 'Only books with copies in stock.', optional: true
      integer :limit, 'The most books to return, 20 if not given.', optional: true
    end

    BOOK = Input.define { integer :book_id, "The book's id." }

    RESTOCK = Input.define do
      integer :book_id, "The book's id."
      integer :quantity, 'How many copies to add, at least 1.'
    end

    private_constant :Input, :SEARCH, :BOOK, :RESTOCK

    class << self
      # @param catalogue [Catalogue]
      # @return [Array<Sleepyshark::Officina::Tool>]
      def all(catalogue) = [search_books_tool(catalogue), get_book_tool(catalogue), restock_book_tool(catalogue)]

      private

      def search_books_tool(catalogue)
        description = 'Searches the catalogue. Every filter is optional; text filters match part of the title or ' \
                      'author name, ignoring case. Returns books cheapest first, at most ' \
                      "#{Catalogue::MAX_SEARCH_RESULTS}."
        Tools.tool(name: 'search_books', description:, input: SEARCH, kind: :read) do |input|
          catalogue.search(filter(input), limit: input.limit).map { |book| book(book).except(:year) }
        end
      end

      def get_book_tool(catalogue)
        Tools.tool(name: 'get_book', input: BOOK, kind: :read,
                   description: 'Gets one book with its author, genre, price, year and copies in stock.') do |input|
          book(catalogue.find(input.book_id))
        end
      end

      def restock_book_tool(catalogue)
        Tools.tool(name: 'restock_book', description: 'Adds copies of a book to its stock.', input: RESTOCK,
                   kind: :write, needs_approval: true) do |input|
          book = catalogue.restock(book_id: input.book_id, quantity: input.quantity)
          { bookId: book.id, title: book.title, stock: book.stock }
        end
      end

      def filter(input)
        BookFilter.new(title: input.title, author: input.author, genre: input.genre, max_price: input.max_price,
                       in_stock: input.in_stock || false)
      end

      def book(book)
        { id: book.id, title: book.title, author: book.author, genre: book.genre, price: Money.json(book.price),
          year: book.year, stock: book.stock }
      end
    end
  end
  private_constant :CatalogueTools
end
