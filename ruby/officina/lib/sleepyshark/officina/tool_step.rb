# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The step that answers one reply's tool calls: it reports the reply, runs the calls through the tool pipeline
    # while relaying its events, then appends their results as one message. A host that leaves the run at one of these
    # events cancels it: the calls still get their results, appended, as the provider rejects a call without its
    # result, but not reported.
    class ToolStep
      def initialize(agent:, audit:, trace:, cancel:, reporter:)
        @agent = agent
        @audit = audit
        @trace = trace
        @cancel = cancel
        @reporter = reporter
      end

      # Runs the calls once the block has reported the reply.
      def call(calls)
        # @type var reported: bool
        # @type var pipeline: ToolPipeline?
        reported = false
        yield
        (pipeline = start_pipeline(calls)).each_event { |event| @reporter.emit(event) }
        reported = true
      ensure
        append_results(pipeline, calls, reported)
      end

      private

      # Appends the calls' results once the pipeline has them all; a run the host left is cancelled first, and its
      # append not reported. Without a pipeline, the host left before it started, and one starts on the cancelled run.
      def append_results(pipeline, calls, reported)
        @cancel.cancel unless reported
        results = (pipeline || start_pipeline(calls)).results
        message = Message.new(role: :user, blocks: results.map { |result| Block.new(tool_result: result) })
        if reported
          @reporter.append(message)
        else
          @reporter.append_unreported(message)
        end
      end

      def start_pipeline(calls) = ToolPipeline.new(agent: @agent, audit: @audit, trace: @trace, cancel: @cancel, calls:)
    end
    private_constant :ToolStep
  end
end
