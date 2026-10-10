# frozen_string_literal: true

module Sleepyshark
  module Officina
    # One run of an agent on a conversation it holds: the loop that calls the model, appends what it answered, runs
    # the tools it asked for, and decides the result.
    class RunEngine
      MAX_MODEL_CALLS = 25
      PREFIX_MISMATCH = "The agent's tools, instructions or model settings differ from those the conversation was " \
                        'started with; start a new conversation'

      def initialize(agent:, conversation:, append:, cancel:, on_event:)
        @agent = agent
        @conversation = conversation
        @reporter = Reporter.new(append:, fingerprint: agent.fingerprint, on_event:)
        @cancel = cancel
        @usage = Usage.new
        @model_calls = 0
        @audit = AuditRecorder.new(agent:, conversation:)
        @tool_step = ToolStep.new(agent:, audit: @audit, cancel:, reporter: @reporter)
      end

      # The run's result, its text and detail without the agent's secrets, between the run's two audit entries; a run
      # the host left has none, and its end is recorded as abandoned.
      def run(input, context)
        # @type var result: result?
        @audit.record(:run_started)
        result = redacted(decide(input, context))
      ensure
        @audit.record_end(result, @usage)
      end

      private

      def decide(input, context)
        bound = @conversation.fingerprint
        return failed(:prefix_mismatch, PREFIX_MISMATCH) unless bound.nil? || bound == @agent.fingerprint

        pending = [text_message(:user, input)]
        pending << text_message(:operator, context) if context
        call_until_stopped(pending)
      end

      def call_until_stopped(pending)
        loop do
          return stopped(:cancelled) if @cancel.cancelled?
          return stopped(:iteration_limit) if @model_calls == MAX_MODEL_CALLS

          result = step(pending)
          return result if result

          pending = []
        end
      end

      # One model call and what follows it: the run's result, or nil when the loop goes on.
      def step(pending)
        case call_model(@conversation.messages + pending)
        in Reply => reply then after_reply(reply, pending)
        in result then result
        end
      end

      # The reply, the result of a call that got none, or nil when the run was cancelled, which the loop then stops.
      def call_model(messages)
        @model_calls += 1
        stream(Request.new(tools: @agent.tools, instructions: @agent.instructions, messages:)) ||
          no_reply("The model's reply ended without a stop reason")
      end

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
        in UsageReported(usage:) then @usage += usage
        in TextDelta then nil
        end
        @reporter.emit(event)
      rescue StandardError
        @host_raised = true
        raise
      end

      def no_reply(why)
        failed(:model_error, why) unless @cancel.cancelled?
      end

      # Keeps the reply, with the messages it answers and its calls' results, unless the provider would reject it, and
      # returns the run's result, or nil when the loop goes on.
      def after_reply(reply, pending)
        calls = reply.blocks.filter_map(&:tool_call)
        result = ReplyOutcome.of(reply, @usage)
        keep(reply, pending, calls) unless rejected?(reply, calls, result)
        result
      end

      # The provider rejects an empty reply, and a call without its result; a reply that ends the run gets none.
      def rejected?(reply, calls, result) = reply.blocks.empty? || (result && calls.any?)

      def keep(reply, pending, calls)
        assistant = Message.new(role: :assistant, blocks: reply.blocks)
        if calls.empty?
          @reporter.append(*pending, assistant)
        else
          @tool_step.call(calls) { @reporter.append(*pending, assistant) }
        end
      end

      def redacted(result)
        case result
        in Completed(text:) then result.with(text: @agent.redact(text))
        in { detail: String => detail } then result.with(detail: @agent.redact(detail))
        else result
        end
      end

      def text_message(role, text) = Message.new(role:, blocks: [Block.new(text:)])
      def stopped(reason) = Stopped.new(reason:, detail: nil, usage: @usage)
      def failed(reason, detail) = Failed.new(reason:, detail:, usage: @usage)
    end
    private_constant :RunEngine
  end
end
