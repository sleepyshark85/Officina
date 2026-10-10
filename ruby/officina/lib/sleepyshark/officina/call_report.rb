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

      # Runs the block, if given, between the entry and the event: a span it ends has taken the entry's failure, if
      # any, and has ended before the host hears of the step.
      # @param span [OpenTelemetry::Trace::Span, nil] the call's, or nil for a call that never started
      # @param fields [Hash] the entry's other members: outcome, detail, duration
      def call(kind, event, span:, **fields)
        @audit.record(kind, call: event.call, span:, **fields)
        yield if block_given?
        @events << event
      end

      # Records the call's attempt, which has no event of its own, as the started event comes before approval.
      # @param step [ToolCallTrace]
      # @return [Boolean] whether the attempt is in the trail, which a write needs before it runs
      def attempt(step) = @audit.record(:tool_started, call: step.call, span: step.span)

      # Records the call's outcome, and sends its event, as #call.
      # @param event [ToolCallFinished]
      # @param duration [Float, nil] seconds the tool ran; nil when it did not run
      def ended(event, span:, duration:, &)
        result = event.result
        call(:tool_ended, event, span:, outcome: result.error? ? 'error' : 'ok', detail: result.content, duration:, &)
      end
    end
    private_constant :CallReport
  end
end
