# frozen_string_literal: true

module Sleepyshark
  module Officina
    # One tool call's span and metrics, from its start to its result, its approval included. The pipeline's thread
    # starts it, and a read's thread may end it.
    class ToolCallTrace
      # The error type of each outcome but :ok: an error result, or a write that did not run as its attempt could not
      # be audited.
      ERRORS = { error: 'tool_error', blocked: 'audit_unavailable' }.freeze
      private_constant :ERRORS

      # @return [ToolCall]
      attr_reader :call
      # @return [OpenTelemetry::Trace::Span] which the call's audit entries name
      attr_reader :span

      # @param tool [Tool, nil] nil when the agent has no tool of the call's name
      def initialize(run, tool, call)
        @run = run
        @call = call
        @started = run.now
        @span = run.start("execute_tool #{call.name}", attributes: started(tool))
      end

      # Notes that the approver is being asked, now.
      def asked
        @asked = @run.now
      end

      # Records the wait for the approver and its answer. A run cancelled while it waited records the wait only: the
      # approver had no time to answer.
      def answered(approved, cancelled:)
        @span.set_attribute('officina.tool.approval_wait', @run.now - @asked)
        return if cancelled && !approved

        answer = approved ? 'approved' : 'denied'
        @run.add(:approvals, 1,
                 { **@run.dimensions, 'gen_ai.tool.name' => @call.name, 'officina.tool.approval' => answer })
        @span.set_attribute('officina.tool.approval', answer)
      end

      # Records the call's metrics and ends its span.
      # @param outcome [Symbol] +:ok+, +:error+ or +:blocked+
      # @param ran [Float, nil] seconds the tool ran; nil when it did not run
      # @param length [Integer] the result's length in characters, before it was cut
      # @param truncated [Boolean] whether it was cut
      # @param result [String] the result as the model gets it
      def finish(outcome, ran:, length:, truncated:, result:)
        @run.add(:tool_calls, 1, measured(outcome))
        @run.record(:tool_duration, @run.now - @started, measured(outcome))
        @span.add_attributes({ 'officina.tool.outcome' => outcome.to_s, 'officina.tool.ran' => ran,
                               'officina.tool.truncated' => truncated, 'officina.tool.result_length' => length,
                               **@run.content('gen_ai.tool.call.result', result) }.compact)
        @run.stop(@span, ERRORS[outcome])
      end

      private

      # The attributes of a measurement of the call: new each time, as RunTrace#dimensions says.
      def measured(outcome)
        { **@run.dimensions, 'gen_ai.tool.name' => @call.name, 'officina.tool.outcome' => outcome.to_s }
      end

      def started(tool)
        { 'gen_ai.operation.name' => 'execute_tool', 'gen_ai.tool.name' => @call.name,
          'gen_ai.tool.call.id' => @call.id, 'gen_ai.tool.type' => 'function', 'officina.tool.source' => 'application',
          'officina.tool.kind' => tool&.kind&.to_s, **@run.content('gen_ai.tool.call.arguments', @call.input) }.compact
      end
    end
    private_constant :ToolCallTrace
  end
end
