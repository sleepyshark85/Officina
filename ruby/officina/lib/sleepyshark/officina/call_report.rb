# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Records a step of a tool call in the audit trail, then sends its event to the run. A missing entry blocks
    # nothing: only a write's attempt depends on the trail.
    class CallReport
      def initialize(audit:, events:)
        @audit = audit
        @events = events
      end

      # @param fields [Hash] the entry's other members: outcome, detail, duration
      def call(kind, event, **fields)
        @audit.record(kind, call: event.call, **fields)
        @events << event
      end
    end
    private_constant :CallReport
  end
end
