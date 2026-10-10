# frozen_string_literal: true

module Bookshop
  # The shop's customers: searched and added through fixed SQL with bound values. A refused request raises
  # RefusedError; a database that cannot be reached raises PG::Error. Thread-safe.
  class Customers
    SEARCH = <<~SQL
      select id, name, email
      from customers
      where lower(name) like lower($1::text) or lower(email) like lower($1::text)
      order by name, id
      limit 20
    SQL

    ADD = <<~SQL
      insert into customers (name, email) values ($1, $2)
      on conflict (email) do nothing
      returning id
    SQL

    private_constant :SEARCH, :ADD

    def initialize(database:)
      @database = database
      freeze
    end

    # Customers whose name or email contains the text, ignoring case, by name: at most 20, none for blank text.
    def search(text)
      @database.with do |connection|
        connection.exec_params(SEARCH, [Database.containing(text)]).map do |row|
          Customer.new(id: row[:id], name: row[:name], email: row[:email])
        end
      end
    end

    # Adds a customer, the name and email stripped of surrounding space.
    #
    # @raise [RefusedError] when the name or email is blank, or another customer has the email
    def add(name:, email:)
      name = name.strip
      email = email.strip
      raise RefusedError, 'A customer needs a name and an email.' if name.empty? || email.empty?

      @database.with do |connection|
        row = connection.exec_params(ADD, [name, email]).first
        raise RefusedError, "A customer with the email #{email} already exists." unless row

        Customer.new(id: row[:id], name:, email:)
      end
    end
  end
end
