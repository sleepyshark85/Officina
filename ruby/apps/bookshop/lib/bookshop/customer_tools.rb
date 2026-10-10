# frozen_string_literal: true

module Bookshop
  # The tools over the customers: find_customer reads, add_customer writes once approved.
  module CustomerTools
    Tool = Sleepyshark::Officina::Tool
    Input = Sleepyshark::Officina::Input

    FIND = Input.define { string :name_or_email, "Part of the customer's name or email." }

    ADD = Input.define do
      string :name, "The customer's full name."
      string :email, "The customer's email."
    end

    private_constant :Tool, :Input, :FIND, :ADD

    class << self
      # @param customers [Customers]
      # @return [Array<Sleepyshark::Officina::Tool>]
      def all(customers) = [find_customer_tool(customers), add_customer_tool(customers)]

      private

      def find_customer_tool(customers)
        Tool.new(name: 'find_customer', input: FIND, kind: :read,
                 description: 'Finds customers whose name or email contains the given text, ignoring case.') do |input|
          customers.search(input.name_or_email).map { |customer| customer(customer) }
        end
      end

      def add_customer_tool(customers)
        Tool.new(name: 'add_customer', description: 'Adds a new customer. Emails are unique.', input: ADD,
                 kind: :write, needs_approval: true) do |input|
          customer(customers.add(name: input.name, email: input.email))
        end
      end

      def customer(customer) = { id: customer.id, name: customer.name, email: customer.email }
    end
  end
end
