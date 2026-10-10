# frozen_string_literal: true

require 'test_helper'

# The check every test runs for threads it leaves running.
class ThreadLeakCheckTest < Minitest::Test
  cover 'Sleepyshark::Officina::Testing*'

  def test_a_test_that_joins_its_threads_passes
    result = run_probe { Thread.new { nil }.join }

    assert_predicate result, :passed?
  end

  def test_a_test_that_leaves_any_number_of_threads_running_fails
    Pbt.assert do
      Pbt.property(Pbt.integer(min: 1, max: 3)) do |count|
        result = with_gate { |start| run_probe { count.times { start.call } } }

        refute_predicate result, :passed?
        assert_match "left #{count} thread(s) running", result.failures.first.message
      end
    end
  end

  private

  # Runs the block as the only test of a test class of its own, which the runner never runs itself, as none of its
  # methods starts with test_, and returns the result.
  def run_probe(&)
    Class.new(Minitest::Test) { define_method(:probe, &) }.new(:probe).run
  end

  # Yields a callable that starts a thread waiting until this method returns, and joins every thread it started.
  def with_gate
    gate = Thread::Queue.new
    threads = []
    yield -> { threads << Thread.new { gate.pop } }
  ensure
    gate.close
    threads.each(&:join)
  end
end
