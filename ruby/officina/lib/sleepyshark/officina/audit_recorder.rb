# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Numbers one run's audit entries and writes them to the agent's sink one at a time, from any of the run's
    # threads, each with the trace and span of the step it records. Without a sink it records nothing, and every
    # record succeeds.
    class AuditRecorder
      MAX_TEXT = 4_000
      private_constant :MAX_TEXT

      # @param trace [RunTrace] the run's, whose id and span an entry names, unless a step's span, and which counts the
      #   entries the sink fails to write
      def initialize(agent:, conversation:, trace:)
        @agent = agent
        @conversation = conversation.id
        @run = trace.run
        @trace = trace
        @sequence = 0
        @lock = Mutex.new
      end

      # Fills in the entry's time, sequence, identity and span, and the tool call's, if it is about one, redacts and
      # cuts its text, and writes it. A failure shows in telemetry, and is returned for the caller to decide what a
      # missing entry means: only a write's attempt depends on it.
      # @param span [OpenTelemetry::Trace::Span, nil] the step's; nil for the run's
      # @return [Boolean] whether the entry is in the trail
      def record(kind, call: nil, span: nil, detail: nil, **fields)
        sink = @agent.audit_sink
        return true unless sink

        span ||= @trace.span
        @lock.synchronize do
          @sequence += 1
          written?(sink, span, AuditEntry.new(time: @agent.clock.call, sequence: @sequence, run: @run,
                                              conversation: @conversation, agent: @agent.name, kind:, **ids(span),
                                              tool: call&.name, call_id: call&.id, input: clean(call&.input),
                                              detail: clean(detail), **fields))
        end
      end

      # Records the run's end: how it ended (nil when the host left it) and its usage.
      def record_end(result, usage)
        record(:run_ended, usage:, **ended(result))
      end

      private

      # A sink's failure is a gap in the trail, which telemetry shows and the caller acts on.
      def written?(sink, span, entry)
        sink.write(entry)
        true
      rescue StandardError
        @trace.audit_failed(span, entry.kind)
        false
      end

      # The span's trace and span ids, which a span of no trace, as without a tracer provider or a host's span, lacks.
      def ids(span)
        context = span.context
        context.valid? ? { trace_id: context.hex_trace_id, span_id: context.hex_span_id } : {}
      end

      def clean(text)
        return unless text

        text = @agent.redact(text)
        text.length > MAX_TEXT ? "#{text[0, MAX_TEXT]}… [truncated: #{text.length} characters]" : text
      end

      def ended(result)
        case result
        in Completed then { outcome: 'completed' }
        in Stopped then { outcome: "stopped: #{result.reason}", detail: result.detail }
        in Failed then { outcome: "failed: #{result.reason}", detail: result.detail }
        in nil then { outcome: 'abandoned' }
        end
      end
    end
    private_constant :AuditRecorder
  end
end
