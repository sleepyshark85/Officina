# frozen_string_literal: true

module Bookshop
  # A pool of connections to the bookshop's PostgreSQL database, each handed out alive: one the server ended, as when
  # it stopped, is reconnected first, so once the database is back no call fails on a connection it ended. A row is a
  # Hash of Symbol column names to values: numbers as Integer and BigDecimal, times as Time. Thread-safe.
  class Database
    # Connections open at most at once.
    POOL_SIZE = 5
    # Seconds a caller waits for a connection when all are in use.
    CHECKOUT_TIMEOUT = 5
    # Each connection's limits, beside its URL: 5 seconds to open, so a database that does not answer fails a call
    # rather than hangs it, and 10 seconds for a query, where the slowest, a broad search, takes milliseconds.
    LIMITS = { connect_timeout: 5, options: '-c statement_timeout=10s' }.freeze
    private_constant :CHECKOUT_TIMEOUT, :LIMITS

    # A LIKE pattern matching text that contains part, its wildcards taken literally; nil for nil or blank text.
    def self.containing(part)
      nil_if_blank(part)&.then { |text| "%#{text.gsub(/[\\%_]/) { |wildcard| "\\#{wildcard}" }}%" }
    end

    # The text, or nil for nil or blank text, which a query takes as no filter.
    def self.nil_if_blank(text)
      text unless text.nil? || text.strip.empty?
    end

    # The password in a PostgreSQL connection URL, nil when it has none.
    def self.password(url)
      PG::Connection.conninfo_parse(url).find { |option| option[:keyword] == 'password' }&.fetch(:val, nil)
    end

    # @param url [String] a PostgreSQL connection URL; nothing connects until the first call
    def initialize(url)
      @pool = ConnectionPool.new(size: POOL_SIZE, timeout: CHECKOUT_TIMEOUT) { connect(url) }
      freeze
    end

    # Yields a connection for the block's queries alone and returns the block's value.
    #
    # @raise [PG::Error] when the database cannot be reached
    def with
      @pool.with do |connection|
        revive(connection)
        yield connection
      end
    end

    # Yields a connection inside a transaction, committed when the block returns and rolled back when it raises.
    #
    # @raise [PG::Error] when the database cannot be reached
    def transaction(&)
      with { |connection| connection.transaction(&) }
    end

    # Closes the connections; the database cannot be used afterwards.
    def close
      @pool.shutdown(&:close)
    end

    private

    def connect(url)
      connection = PG.connect(url, **LIMITS)
      connection.type_map_for_results = PG::BasicTypeMapForResults.new(connection)
      connection.type_map_for_queries = PG::BasicTypeMapForQueries.new(connection)
      connection.field_name_type = :symbol
      connection
    end

    def revive(connection)
      connection.check_socket
    rescue PG::ConnectionBad
      connection.reset
    end
  end
end
