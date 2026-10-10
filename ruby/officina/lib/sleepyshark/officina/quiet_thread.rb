# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Threads whose failure is raised where they are joined, not also printed as they end.
    module QuietThread
      # mutant:disable -- the one mutation, Thread.report_on_exception= in place of the thread's own setting, changes
      #   the default only for threads started later, which no test can tell apart
      def self.start
        Thread.new do
          Thread.current.report_on_exception = false
          yield
        end
      end
    end
    private_constant :QuietThread
  end
end
