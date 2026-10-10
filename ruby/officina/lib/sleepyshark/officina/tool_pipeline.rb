# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Runs one reply's tool calls on a thread of its own, in call order, each through the same steps: find the tool,
    # validate its input, ask approval if it needs it, record the attempt, invoke, redact, cut. Read calls run
    # concurrently, each on a thread; a write waits for every call before it and runs alone; approvals are asked one at
    # a time. Every call gets exactly one result, and every failure is an error result. Its events go to a queue the
    # run drains, so it never waits for the host.
    class ToolPipeline
      # The longest result the model gets, in characters: about 16k tokens.
      MAX_RESULT = 64_000
      NOT_STARTED = 'The call was cancelled before it started.'
      private_constant :MAX_RESULT, :NOT_STARTED

      def initialize(agent:, audit:, cancel:, calls:)
        @agent = agent
        @audit = audit
        @cancel = cancel
        @calls = calls
        @results = {}
        @reads = RunningReads.new(cancel)
        @events = Queue.new
        @report = CallReport.new(audit:, events: @events)
        @approvals = ApprovalDesk.new(approver: agent.approver, cancel:, report: @report)
        @thread = QuietThread.start { answer }
      end

      # Yields each event as the pipeline sends it, until it has answered every call.
      # mutant:disable -- its one surviving mutation swaps Queue#pop and Queue#shift, which are the same method
      def each_event
        while (event = @events.pop)
          yield event
        end
      end

      # Waits for the pipeline, also when the run was left: the results, in call order.
      # @return [Array<ToolResult>]
      def results = @thread.value

      private

      # Runs on the pipeline's thread, the only one that touches @results and @reads. What it raises, #results raises.
      def answer
        @calls.each_with_index { |call, index| start(call, index) unless @cancel.cancelled? }
        @results.merge!(@reads.join)
        @calls.each_with_index.map { |call, index| @results[index] || finish(call, NOT_STARTED, error: true) }
      ensure
        begin
          @reads.stop
        ensure
          @events.close
        end
      end

      def start(call, index)
        tool = @agent.tool(call.name)
        @results.merge!(@reads.join) if tool&.write?
        @events << ToolCallStarted.new(call: shown(call))
        return run_if_allowed(tool, call, index) if tool

        @results[index] = finish(call, "There is no tool named #{call.name}.", error: true)
      end

      def run_if_allowed(tool, call, index)
        refusal = tool.input_problem(call.input) || (@approvals.denial(tool, shown(call)) if tool.needs_approval?)
        return @results[index] = finish(call, refusal, error: true) if refusal
        # The host may have cancelled while the approver decided, or while this write waited for the reads.
        return if @cancel.cancelled?

        if tool.write?
          @results[index] = write(tool, call)
        else
          read(tool, call, index)
        end
      end

      # Runs the write once its attempt is in the trail.
      def write(tool, call)
        return invoke(tool, call) if @audit.record(:tool_started, call:)

        finish(call, 'The call was not run: its attempt could not be recorded in the audit trail.', error: true)
      end

      # Starts the read on a thread of its own; its attempt is recorded, but it runs without.
      def read(tool, call, index)
        @audit.record(:tool_started, call:)
        @reads.start(index) { invoke(tool, call) }
      end

      def invoke(tool, call)
        started = @agent.clock.call
        content = tool.invoke(call.input, @cancel)
      rescue StandardError => e
        why = @cancel.cancelled? ? 'The call was cancelled while it ran' : 'The tool failed'
        finish(call, "#{why}: #{e}", error: true, started:)
      else
        finish(call, content, error: false, started:)
      end

      # Redacts and cuts the call's result, records its outcome and reports it. +started+ is when the tool started
      # running, nil when it did not run.
      def finish(call, content, error:, started: nil)
        result = ToolResult.new(call_id: call.id, content: cut(@agent.redact(content)), error:)
        duration = started && (@agent.clock.call - started)
        @report.call(:tool_ended, ToolCallFinished.new(call: shown(call), result:),
                     outcome: error ? 'error' : 'ok', detail: result.content, duration:)
        result
      end

      def cut(content)
        return content if content.length <= MAX_RESULT

        "#{content[0, MAX_RESULT]}\n[Truncated: the result had #{content.length} characters; only the first " \
          "#{MAX_RESULT} are shown.]"
      end

      # The call as events and the approver see it: its input without the agent's secrets.
      def shown(call) = call.with(input: @agent.redact(call.input))
    end
    private_constant :ToolPipeline
  end
end
