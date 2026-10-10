# frozen_string_literal: true

module Sleepyshark
  module Officina
    # One run of an agent on a conversation it holds: the loop that calls the model, appends what it answered, runs
    # the tools it asked for, and decides the result.
    class RunEngine
      MAX_MODEL_CALLS = 25
      PREFIX_MISMATCH = "The agent's tools, instructions or model settings differ from those the conversation was " \
                        'started with; start a new conversation'
      # The reason a run stops for each stop of the model that stops it.
      STOPPED = { max_tokens: :output_limit, refusal: :refusal, context_full: :context_full }.freeze

      def initialize(agent:, conversation:, append:, cancel:, on_event:)
        @agent = agent
        @conversation = conversation
        @append = append
        @cancel = cancel
        @on_event = on_event
        @usage = Usage.new
        @model_calls = 0
        @host_failure = nil
        @audit = AuditRecorder.new(agent:, conversation:)
      end

      # The run's result, between the run's two audit entries; a run the host left has none, and its end is recorded
      # as abandoned.
      def run(input, context)
        # @type var result: result?
        # A missing run entry blocks nothing: only a write's attempt depends on the trail.
        @audit.record(:run_started)
        result = decide(input, context)
      ensure
        @audit.record_end(result, @usage)
      end

      private

      def decide(input, context)
        bound = @conversation.fingerprint
        return failed(:prefix_mismatch, PREFIX_MISMATCH) unless bound.nil? || bound == @agent.fingerprint

        call_until_stopped([text_message(:user, input), *(context ? [text_message(:operator, context)] : [])])
      end

      def call_until_stopped(pending)
        loop do
          return stopped(:cancelled) if @cancel.cancelled?
          return stopped(:iteration_limit) if @model_calls == MAX_MODEL_CALLS

          reply = call_model([*@conversation.messages, *pending])
          return reply unless reply.is_a?(Reply)

          result = take(reply, pending)
          return result if result

          pending = []
        end
      end

      # The reply, or the result of a call that got none.
      def call_model(messages)
        @model_calls += 1
        request = Request.new(tools: @agent.tools, instructions: @agent.instructions, messages:)
        reply = @agent.model.stream(request, cancel: @cancel) { |event| relay(event) }
        reply || cut_off("The model's reply ended without a stop reason")
      rescue StandardError => e
        raise if e.equal?(@host_failure)

        cut_off(e.message)
      end

      def relay(event)
        @usage += event.usage if event.is_a?(UsageReported)
        emit(event)
      end

      # Passes the event to the host's block. An exception from the block ends the run as the host's own, never as a
      # model failure.
      def emit(event)
        @on_event&.call(event)
      rescue StandardError => e
        @host_failure = e
        raise
      end

      def cut_off(why) = @cancel.cancelled? ? stopped(:cancelled) : failed(:model_error, why)

      # Appends the pending messages and the reply, and the results of its calls, as far as the conversation keeps
      # them, and returns the result, or nil when the loop goes on. The provider rejects a call without its result, so
      # a reply whose calls will not be answered is not kept.
      def take(reply, pending)
        calls = reply.blocks.filter_map(&:tool_call)
        result = finish(reply, calls)
        return result if reply.blocks.empty? || (result && calls.any?)

        assistant = Message.new(role: :assistant, blocks: reply.blocks)
        result ? append(*pending, assistant) : answer_after(calls) { append(*pending, assistant) }
        result
      end

      # Reports the reply through the block, runs its calls, relaying the pipeline's events, then appends their
      # results. A host that leaves the run at one of these events cancels it: the calls still get their results,
      # appended, as the provider rejects a call without its result, but not reported.
      def answer_after(calls)
        # @type var reported: bool
        # @type var pipeline: ToolPipeline?
        reported = false
        yield
        (pipeline = tools(calls)).each_event { |event| emit(event) }
        reported = true
      ensure
        answer(pipeline, calls, reported)
      end

      # Appends the calls' results once the pipeline has them all; a run the host left is cancelled first, and its
      # append not reported. Without a pipeline, the host left before it started, and one starts on the cancelled run.
      def answer(pipeline, calls, reported)
        @cancel.cancel unless reported
        results = (pipeline || tools(calls)).results
        message = Message.new(role: :user, blocks: results.map { |result| Block.new(tool_result: result) })
        @append.call([message], @agent.fingerprint)
        emit(ConversationAppended.new(message:)) if reported
      end

      # The result a reply's stop leads to, or nil when its calls are to be answered.
      def finish(reply, calls)
        case [reply.stop, calls.empty?]
        in [:end, true] then Completed.new(text: @agent.redact(reply.text), usage: @usage)
        in [:end, false] then failed(:unexpected_stop, "The model's reply called tools but did not stop for them")
        in [:tool_use, true] then failed(:unexpected_stop, 'The model stopped to use tools but called none')
        in [:tool_use, false] then nil
        in [:unknown, _] then failed(:unexpected_stop, "The run cannot act on the model's stop: #{reply.detail}")
        in [stop, _] then stopped(STOPPED.fetch(stop), reply.detail)
        end
      end

      # Every message is appended before the first is reported, so a host that stops reading midway still holds a
      # conversation where the reply follows the messages it answers.
      def append(*messages)
        @append.call(messages, @agent.fingerprint)
        messages.each { |message| emit(ConversationAppended.new(message:)) }
      end

      def tools(calls) = ToolPipeline.new(agent: @agent, audit: @audit, cancel: @cancel, calls:)
      def text_message(role, text) = Message.new(role:, blocks: [Block.new(text:)])
      def stopped(reason, detail = nil) = Stopped.new(reason:, detail: detail && @agent.redact(detail), usage: @usage)
      def failed(reason, detail) = Failed.new(reason:, detail: @agent.redact(detail), usage: @usage)
    end
    private_constant :RunEngine
  end
end
