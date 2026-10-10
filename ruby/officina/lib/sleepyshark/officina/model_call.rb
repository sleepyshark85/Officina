# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A run's model calls: each sends the conversation to the model, relays what it streams to the run's spending,
    # audit trail, trace and host as it arrives, and ends in the reply or in what the run makes of a call that got none.
    class ModelCall
      def initialize(agent:, cancel:, spending:, trace:, audit:, reporter:)
        @agent = agent
        @cancel = cancel
        @spending = spending
        @trace = trace
        @audit = audit
        @reporter = reporter
      end

      # The reply, the result of a call that got none, or nil when the run was cancelled, which the loop then stops.
      def call(messages, limit)
        @spending.count_model_call
        request = Request.new(tools: @agent.tools, instructions: @agent.instructions,
                              output_schema: @agent.output&.schema&.to_s, messages:, max_output_tokens: limit,
                              context_management: @agent.context_management)
        @trace.model_call { stream(request) || no_reply("The model's reply ended without a stop reason") }
      end

      private

      # The reply; nil for a stream that ended without one; what #no_reply makes of a stream that failed. What the
      # host's block raised passes through.
      def stream(request)
        @agent.model.stream(request, cancel: @cancel) { |event| relay(event) }
      rescue StandardError => e
        raise if @host_raised

        no_reply(e.message)
      end

      def relay(event)
        # @type var usage: Usage
        case event
        in UsageReported(usage:) then @spending.add(usage)
        in ConversationCompacted | ToolResultsCleared then @audit.record_edit(event)
        else nil
        end
        @trace.observe(event)
        @reporter.emit(event)
      rescue StandardError
        @host_raised = true
        raise
      end

      def no_reply(why)
        Failed.new(reason: :model_error, detail: why) unless @cancel.cancelled?
      end
    end
    private_constant :ModelCall
  end
end
