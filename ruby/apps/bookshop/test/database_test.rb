# frozen_string_literal: true

require 'test_helper'
require_relative 'database_server'

# The shop's connections when its database stops and comes back.
class DatabaseTest < Minitest::Test
  include DatabaseServer

  Line = Data.define(:book_id, :quantity)
  # The connections the shop's pool holds at most.
  POOL_SIZE = 5

  def test_app18_with_the_database_down_calls_fail_and_once_it_is_back_they_work_again
    on_every_connection { |connection| connection.exec('select 1') }
    take_database_down
    assert_raises(PG::ConnectionBad) { shop.catalogue.find(144) }
    assert_raises(PG::ConnectionBad) { shop.orders.place(customer_id: 1, lines: [Line.new(144, 1)]) }
    bring_database_back
    revived = on_every_connection { |connection| connection.exec('select 1').getvalue(0, 0) }

    assert_equal [1] * POOL_SIZE, revived
    assert_equal 'The Winter Archive', shop.catalogue.restock(book_id: 144, quantity: 1).title
  end

  private

  # Runs the block on every connection of the pool at once, each held by its own thread until all have run, so none
  # is handed out twice; returns the block's values, or raises what a thread raised.
  def on_every_connection(&)
    finished = Thread::Queue.new
    release = Thread::Queue.new
    threads = Array.new(POOL_SIZE) { Thread.new { hold_connection(finished, release, &) } }
    POOL_SIZE.times { finished.pop }
    release.close
    threads.map(&:value)
  end

  # Checks out a connection, runs the block on it and holds it until released. Says it finished either way, so the
  # caller never waits for a thread that failed.
  def hold_connection(finished, release)
    Thread.current.report_on_exception = false
    shop.database.with do |connection|
      value = yield connection
      finished << :held
      release.pop
      value
    end
  rescue PG::Error
    finished << :failed
    raise
  end
end
