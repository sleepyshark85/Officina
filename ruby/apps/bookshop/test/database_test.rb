# frozen_string_literal: true

require 'test_helper'
require_relative 'database_server'

# The database's connections when it stops and comes back.
class DatabaseTest < Minitest::Test
  include DatabaseServer

  Line = Data.define(:book_id, :quantity)

  def setup
    super
    @database = Bookshop::Database.new(database_url)
  end

  def teardown
    @database&.close
    super
  end

  def test_app18_with_the_database_down_calls_fail_and_once_it_is_back_they_work_again
    on_every_connection { |connection| connection.exec('select 1') }
    take_database_down
    assert_raises(PG::ConnectionBad) { @database.with { |connection| connection.exec('select 1') } }
    assert_raises(PG::ConnectionBad) { shop.catalogue.find(144) }
    assert_raises(PG::ConnectionBad) { shop.orders.place(customer_id: 1, lines: [Line.new(144, 1)]) }
    bring_database_back
    revived = on_every_connection { |connection| connection.exec('select 1').getvalue(0, 0) }

    assert_equal [1] * Bookshop::Database::POOL_SIZE, revived
    assert_equal 'The Winter Archive', shop.catalogue.restock(book_id: 144, quantity: 1).title
  end

  def test_app18_each_connection_gives_up_on_a_silent_server_and_a_slow_query_within_seconds
    limits = @database.with do |connection|
      [connection.conninfo_hash[:connect_timeout], connection.exec('show statement_timeout').getvalue(0, 0)]
    end

    assert_equal %w[5 10s], limits
  end

  private

  # Runs the block on every connection of the pool at once, each held by its own thread until all have run, so none
  # is handed out twice; returns the block's values, or raises what a thread raised.
  def on_every_connection(&)
    ready = Thread::Queue.new
    release = Thread::Queue.new
    threads = Array.new(Bookshop::Database::POOL_SIZE) { Thread.new { hold_connection(ready, release, &) } }
    threads.size.times { ready.pop }
    release.close
    threads.map(&:value)
  end

  # Checks out a connection, runs the block on it and holds it until released. It says it is ready once it holds the
  # connection, or once it has failed, so the caller never waits for a thread that will not hold one.
  def hold_connection(ready, release)
    Thread.current.report_on_exception = false
    holding = false
    @database.with do |connection|
      value = yield connection
      holding = true
      ready << :holding
      release.pop
      value
    end
  ensure
    ready << :failed unless holding
  end
end
