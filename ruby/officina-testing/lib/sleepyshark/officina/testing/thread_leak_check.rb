# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Testing
      # Fails a Minitest test that ends with a thread it started still alive: every thread needs an owner that joins
      # it. Include it in Minitest::Test, and run the tests one at a time, as another test's threads would count.
      module ThreadLeakCheck
        # Notes the threads alive before the test; Minitest calls it before setup.
        def before_setup
          @threads_before_test = Thread.list
          super
        end

        # Fails the test when threads it started are still alive; Minitest calls it after teardown.
        def after_teardown
          super
          leaked = Thread.list - @threads_before_test
          assert_empty leaked, "The test left #{leaked.size} thread(s) running; join each one before it ends"
        end
      end
    end
  end
end
