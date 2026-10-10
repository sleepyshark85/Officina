# frozen_string_literal: true

module Bookshop
  # The tools over the orders: list_customer_orders and get_order read; place_order and cancel_order write once
  # approved.
  module OrderTools
    Tool = Sleepyshark::Officina::Tool
    Input = Sleepyshark::Officina::Input

    CUSTOMER = Input.define { integer :customer_id, "The customer's id." }

    ORDER = Input.define { integer :order_id, "The order's id." }

    PLACE = Input.define do
      integer :customer_id, "The customer's id."
      array :lines, 'The books and copies to order, each book once.' do
        integer :book_id, "The book's id."
        integer :quantity, 'How many copies, at least 1.'
      end
    end

    private_constant :Tool, :Input, :CUSTOMER, :ORDER, :PLACE

    class << self
      # @param orders [Orders]
      # @return [Array<Sleepyshark::Officina::Tool>]
      def all(orders)
        [list_customer_orders_tool(orders), get_order_tool(orders), place_order_tool(orders), cancel_order_tool(orders)]
      end

      private

      def list_customer_orders_tool(orders)
        Tool.new(name: 'list_customer_orders', input: CUSTOMER, kind: :read,
                 description: "Lists a customer's orders, newest first, with status and total.") do |input|
          orders.of_customer(input.customer_id).map do |order|
            { id: order.id, status: order.status, placedAt: time(order.placed_at), total: Money.json(order.total),
              copies: order.copies }
          end
        end
      end

      def get_order_tool(orders)
        Tool.new(name: 'get_order', description: 'Gets one order with its customer, status, lines and total.',
                 input: ORDER, kind: :read) do |input|
          order = orders.find(input.order_id)
          { order: { id: order.id, customerId: order.customer_id, customer: order.customer, status: order.status,
                     placedAt: time(order.placed_at), total: Money.json(order.total) },
            lines: lines(order) }
        end
      end

      def place_order_tool(orders)
        description = "Places an order for a customer at the books' current prices, taking the copies from stock. " \
                      'Fails, changing nothing, if a book is unknown or has too few copies in stock.'
        Tool.new(name: 'place_order', description:, input: PLACE, kind: :write, needs_approval: true) do |input|
          order = orders.place(customer_id: input.customer_id, lines: input.lines)
          { orderId: order.id, customerId: order.customer_id, customer: order.customer, lines: lines(order),
            total: Money.json(order.total) }
        end
      end

      def cancel_order_tool(orders)
        Tool.new(name: 'cancel_order', description: 'Cancels a placed order and returns its copies to stock.',
                 input: ORDER, kind: :write, needs_approval: true) do |input|
          order = orders.cancel(input.order_id)
          { orderId: order.id, status: order.status, returnedToStock: lines(order) }
        end
      end

      def lines(order)
        order.lines.map do |line|
          { bookId: line.book_id, title: line.title, quantity: line.quantity, unitPrice: Money.json(line.unit_price) }
        end
      end

      # In UTC, as .NET and Go write a time: 2026-10-10T09:30:00Z.
      def time(time) = time.getutc.iso8601
    end
  end
end
