# frozen_string_literal: true

module Sleepyshark
  module Officina
    # One run of an agent on a conversation it holds: the loop that calls the model, appends what it answered, runs
    # the tools it asked for, and decides the result.
    class RunEngine
      MAX_MODEL_CALLS = 25
      PREFIX_MISMATCH = "The agent's tools, instructions or model settings differ from those the conversation was " \
                        'started with; start a new conversation'

      # @param trace [RunTrace] the run's, whose span has started
      # @param budget [Budget, nil]
      def initialize(agent:, conversation:, append:, cancel:, on_event:, trace:, budget:)
        @agent = agent
        @conversation = conversation
        @reporter = Reporter.new(append:, fingerprint: agent.fingerprint, on_event:)
        @cancel = cancel
        @spending = Spending.new(budget:, price: agent.model.info.price, clock: agent.clock)
        @trace = trace
        @audit = AuditRecorder.new(agent:, conversation:, trace:)
        @tool_step = ToolStep.new(agent:, audit: @audit, trace:, cancel:, reporter: @reporter)
      end

      # The run's result, its text and detail without the agent's secrets, its typed output read from that text, with
      # what the run used, between the run's two audit entries, and its span's end; a run the host left has none, and
      # its end is recorded as abandoned.
      def run(input, context)
        # @type var result: result?
        @audit.record(:run_started)
        result = @spending.report(TypedOutput.read(redacted(decide(input, context)), @agent.output))
      ensure
        @audit.record_end(result, @spending.usage, @spending.cost)
        @trace.finish(result)
      end

      private

      def decide(input, context)
        bound = @conversation.fingerprint
        return failed(:prefix_mismatch, PREFIX_MISMATCH) unless bound.nil? || bound == @agent.fingerprint

        interrupted = InterruptedCalls.answer(@conversation.messages, @audit)
        @reporter.append(interrupted) if interrupted
        pending = [text_message(:user, input)]
        pending << text_message(:operator, context) if context
        call_until_stopped(pending)
      end

      def call_until_stopped(pending)
        loop do
          return stopped(:cancelled) if @cancel.cancelled?
          return stopped(:iteration_limit) if @spending.model_calls == MAX_MODEL_CALLS

          reached = @spending.reached
          return stopped(:budget, reached) if reached

          result = step(pending)
          return result if result

          pending = []
        end
      end

      # One model call, its output limit lowered to what the budget has left, and what follows it: the run's result,
      # or nil when the loop goes on.
      def step(pending)
        limit = @spending.output_limit
        case call_model(@conversation.messages + pending, limit)
        in Reply => reply then after_reply(reply, pending, limit)
        in result then result
        end
      end

      # The reply, the result of a call that got none, or nil when the run was cancelled, which the loop then stops.
      def call_model(messages, limit)
        @spending.count_model_call
        request = Request.new(tools: @agent.tools, instructions: @agent.instructions,
                              output_schema: @agent.output&.schema&.to_s, messages:, max_output_tokens: limit)
        @trace.model_call { stream(request) || no_reply("The model's reply ended without a stop reason") }
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
        in UsageReported(usage:) then @spending.add(usage)
        else nil
        end
        @trace.observe(event)
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
      def after_reply(reply, pending, limit)
        calls = reply.blocks.filter_map(&:tool_call)
        result = ReplyOutcome.of(reply, budget_used_up: limit && @spending.reached)
        keep(reply, pending, calls) unless rejected?(reply, calls, result)
        result
      end

      # The provider rejects an empty reply, and a call without its result; a reply that ends the run gets none.
      def rejected?(reply, calls, result) = reply.blocks.empty? || (result && calls.any?)

      def keep(reply, pending, calls)
        assistant = Message.new(role: :assistant, blocks: reply.blocks)
        @spending.count_tool_calls(calls.size)
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
      def stopped(reason, detail = nil) = Stopped.new(reason:, detail:)
      def failed(reason, detail) = Failed.new(reason:, detail:)
    end
    private_constant :RunEngine
  end
end
