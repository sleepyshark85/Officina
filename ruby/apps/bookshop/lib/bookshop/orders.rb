# frozen_string_literal: true

module Bookshop
  # The customers' orders: read, placed and cancelled through fixed SQL with bound values, placing and cancelling
  # each in one transaction with the stock it takes or gives back. A refused request raises RefusedError and changes
  # nothing; a database that cannot be reached raises PG::Error. Thread-safe.
  class Orders
    CUSTOMER_EXISTS = 'select 1 from customers where id = $1'

    OF_CUSTOMER = <<~SQL
      select o.id, o.status, o.placed_at, o.total,
             (select sum(quantity) from order_lines l where l.order_id = o.id) as copies
      from orders o
      where o.customer_id = $1
      order by o.placed_at desc, o.id desc
    SQL

    FIND = <<~SQL
      select o.id, o.customer_id, c.name as customer, o.status, o.placed_at, o.total
      from orders o
      join customers c on c.id = o.customer_id
      where o.id = $1
    SQL

    LINES = <<~SQL
      select l.book_id, b.title, l.quantity, l.unit_price
      from order_lines l
      join books b on b.id = l.book_id
      where l.order_id = $1
      order by l.book_id
    SQL

    INSERT = "insert into orders (customer_id, status, total) values ($1, 'placed', $2) returning id"

    INSERT_LINE = 'insert into order_lines (order_id, book_id, quantity, unit_price) values ($1, $2, $3, $4)'

    LOCK = 'select status from orders where id = $1 for update'

    CANCEL = "update orders set status = 'cancelled' where id = $1"

    private_constant :CUSTOMER_EXISTS, :OF_CUSTOMER, :FIND, :LINES, :INSERT, :INSERT_LINE, :LOCK, :CANCEL

    def initialize(database:)
      @database = database
      freeze
    end

    # @raise [RefusedError] when there is no such order
    def find(id)
      @database.with { |connection| read(connection, id) }
    end

    # A customer's orders, newest first.
    #
    # @raise [RefusedError] when there is no such customer
    def of_customer(customer_id)
      @database.with do |connection|
        check_customer(connection, customer_id)
        connection.exec_params(OF_CUSTOMER, [customer_id]).map do |row|
          OrderSummary.new(id: row[:id], status: row[:status], placed_at: row[:placed_at], total: row[:total],
                           copies: row[:copies])
        end
      end
    end

    # Places an order at the books' current prices, taking the copies from stock.
    #
    # @param lines [Array<#book_id, #quantity>] each book once, each for at least one copy
    # @return [Order] the order placed
    # @raise [RefusedError] when the lines are wrong, the customer or a book is unknown, or a book has too few copies
    def place(customer_id:, lines:)
      check_lines(lines)
      @database.transaction do |connection|
        check_customer(connection, customer_id)
        prices = Stock.take(connection, lines)
        read(connection, insert(connection, customer_id, lines, prices))
      end
    end

    # Cancels a placed order and returns its copies to stock.
    #
    # @return [Order] the order cancelled, its lines the copies returned
    # @raise [RefusedError] when there is no such order or it is already cancelled
    def cancel(id)
      @database.transaction do |connection|
        status = connection.exec_params(LOCK, [id]).first&.fetch(:status)
        raise RefusedError, "There is no order with id #{id}." unless status
        raise RefusedError, "Order #{id} is already cancelled." if status == 'cancelled'

        Stock.give_back(connection, id)
        connection.exec_params(CANCEL, [id])
        read(connection, id)
      end
    end

    private

    def read(connection, id)
      row = connection.exec_params(FIND, [id]).first
      raise RefusedError, "There is no order with id #{id}." unless row

      Order.new(id: row[:id], customer_id: row[:customer_id], customer: row[:customer], status: row[:status],
                placed_at: row[:placed_at], total: row[:total], lines: read_lines(connection, id))
    end

    def read_lines(connection, id)
      connection.exec_params(LINES, [id]).map do |row|
        OrderLine.new(book_id: row[:book_id], title: row[:title], quantity: row[:quantity],
                      unit_price: row[:unit_price])
      end.freeze
    end

    # Inserts the order and its lines at the prices given by book id; returns the order's id.
    def insert(connection, customer_id, lines, prices)
      total = lines.sum(BigDecimal(0)) { |line| prices.fetch(line.book_id) * line.quantity }
      id = connection.exec_params(INSERT, [customer_id, total]).getvalue(0, 0)
      lines.each do |line|
        connection.exec_params(INSERT_LINE, [id, line.book_id, line.quantity, prices.fetch(line.book_id)])
      end
      id
    end

    def check_customer(connection, id)
      return if connection.exec_params(CUSTOMER_EXISTS, [id]).ntuples.positive?

      raise RefusedError, "There is no customer with id #{id}."
    end

    def check_lines(lines)
      ids = lines.map(&:book_id)
      return unless ids.empty? || ids.uniq.size != ids.size || lines.any? { |line| line.quantity < 1 }

      raise RefusedError, 'An order needs at least one line, each book once, each for at least one copy.'
    end
  end
end
