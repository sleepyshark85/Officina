# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Threads whose failure is raised where they are joined, not also printed as they end.
    module QuietThread
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
