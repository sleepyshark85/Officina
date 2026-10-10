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

      def initialize(agent:, audit:, trace:, cancel:, calls:)
        @agent = agent
        @trace = trace
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
        @calls.each_with_index.map { |call, index| @results[index] || finish(call, NOT_STARTED, :error) }
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
        step = @trace.tool_call(tool, call)
        @events << ToolCallStarted.new(call: shown(call))
        return run_if_allowed(tool, step, index) if tool

        @results[index] = finish(call, "There is no tool named #{call.name}.", :error, step:)
      end

      def run_if_allowed(tool, step, index)
        call = step.call
        refusal = tool.input_problem(call.input) || (@approvals.denial(tool, shown(call), step) if tool.needs_approval?)
        return @results[index] = finish(call, refusal, :error, step:) if refusal
        # The host may have cancelled while the approver decided, or while this write waited for the reads.
        return if @cancel.cancelled?

        if tool.write?
          @results[index] = write(tool, step)
        else
          read(tool, step, index)
        end
      end

      # Runs the write once its attempt is in the trail.
      def write(tool, step)
        return invoke(tool, step) if @report.attempt(step)

        finish(step.call, 'The call was not run: its attempt could not be recorded in the audit trail.', :blocked,
               step:)
      end

      # Starts the read on a thread of its own; its attempt is recorded, but it runs without.
      def read(tool, step, index)
        @report.attempt(step)
        @reads.start(index) { invoke(tool, step) }
      end

      def invoke(tool, step)
        started = @agent.clock.call
        content = tool.invoke(step.call.input, @cancel)
      rescue StandardError => e
        why = @cancel.cancelled? ? 'The call was cancelled while it ran' : 'The tool failed'
        finish(step.call, "#{why}: #{e}", :error, step:, started:)
      else
        finish(step.call, content, :ok, step:, started:)
      end

      # Redacts and cuts the call's result, records its outcome, ends its step, if it started, and reports it.
      # +outcome+ is +:ok+, +:error+ or +:blocked+; +started+ is when the tool started running, nil when it did not
      # run.
      def finish(call, content, outcome, step: nil, started: nil)
        redacted = @agent.redact(content)
        result = ToolResult.new(call_id: call.id, content: cut(redacted), error: outcome != :ok)
        ran = started && (@agent.clock.call - started)
        @report.ended(ToolCallFinished.new(call: shown(call), result:), span: step&.span, duration: ran) do
          step&.finish(outcome, ran:, length: redacted.length, truncated: redacted.length > MAX_RESULT,
                                result: result.content)
        end
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
