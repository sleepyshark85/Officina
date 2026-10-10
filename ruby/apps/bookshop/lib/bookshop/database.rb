# frozen_string_literal: true

module Bookshop
  # A pool of connections to the bookshop's PostgreSQL database, each handed out alive: one the server ended, as when
  # it stopped, is reconnected first, so once the database is back no call fails on a connection it ended. A row is a
  # Hash of Symbol column names to values: numbers as Integer and BigDecimal, times as Time. Thread-safe.
  class Database
    # Connections open at most at once, and the seconds a caller waits for one.
    POOL_SIZE = 5
    CHECKOUT_TIMEOUT = 5
    private_constant :POOL_SIZE, :CHECKOUT_TIMEOUT

    # A LIKE pattern matching text that contains part, its wildcards taken literally; nil for nil or blank text.
    #
    # @return [String, nil]
    def self.containing(part)
      return if part.nil? || part.strip.empty?

      "%#{part.gsub(/[\\%_]/) { |wildcard| "\\#{wildcard}" }}%"
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
      connection = PG.connect(url)
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
