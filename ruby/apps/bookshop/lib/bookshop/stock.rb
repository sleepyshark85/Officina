# frozen_string_literal: true

module Bookshop
  # The copies in stock that orders take and give back, inside the caller's transaction. Both lock the stock rows they
  # change in book id order, held until the transaction ends, so concurrent orders and cancellations cannot deadlock
  # or take a copy twice.
  module Stock
    LOCK_BOOKS = <<~SQL
      select b.id, b.title, b.price, s.quantity
      from books b
      join stock s on s.book_id = b.id
      where b.id = any($1::integer[])
      order by b.id
      for update of s
    SQL

    TAKE = 'update stock set quantity = quantity - $2 where book_id = $1'

    LOCK_ORDER = <<~SQL
      select s.book_id
      from stock s
      join order_lines l on l.book_id = s.book_id
      where l.order_id = $1
      order by s.book_id
      for update of s
    SQL

    GIVE_BACK = <<~SQL
      update stock s set quantity = s.quantity + l.quantity
      from order_lines l
      where l.order_id = $1 and s.book_id = l.book_id
    SQL

    private_constant :LOCK_BOOKS, :TAKE, :LOCK_ORDER, :GIVE_BACK

    class << self
      # Takes each line's copies from stock.
      #
      # @param lines [Array<#book_id, #quantity>] each book once
      # @return [Hash{Integer => BigDecimal}] each book's current price by id
      # @raise [RefusedError] when a book is unknown or has too few copies
      def take(connection, lines)
        books = connection.exec_params(LOCK_BOOKS, [lines.map(&:book_id)]).to_h { |row| [row[:id], row] }
        lines.each do |line|
          check(books[line.book_id], line)
          connection.exec_params(TAKE, [line.book_id, line.quantity])
        end
        books.transform_values { |book| book[:price] }
      end

      # Gives an order's copies back to stock.
      def give_back(connection, order_id)
        connection.exec_params(LOCK_ORDER, [order_id])
        connection.exec_params(GIVE_BACK, [order_id])
      end

      private

      def check(book, line)
        raise RefusedError, "There is no book with id #{line.book_id}. Nothing was ordered." unless book
        return if book[:quantity] >= line.quantity

        raise RefusedError, "Not enough stock for \"#{book[:title]}\" (id #{line.book_id}): #{line.quantity} " \
                            "requested, #{book[:quantity]} in stock. Nothing was ordered."
      end
    end
  end
end
