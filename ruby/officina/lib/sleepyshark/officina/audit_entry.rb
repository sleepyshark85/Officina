# frozen_string_literal: true

module Sleepyshark
  module Officina
    AuditEntry = Data.define(:time, :sequence, :run, :conversation, :agent, :kind, :trace_id, :span_id, :tool, :call_id,
                             :input, :outcome, :detail, :duration, :usage)

    # One durable record of a run: when, in which order, of which run, conversation and agent, in which trace and
    # span, and what. Its text has the agent's secrets redacted and is cut at 4,000 characters with its length noted.
    class AuditEntry
      KINDS = %i[run_started run_ended tool_started tool_ended approval_asked approval_answered].freeze

      # @param time [Time] from the agent's clock
      # @param sequence [Integer] the entry's place in its run, from 1; a gap is an entry the sink failed to write
      # @param run [String] the run's random id
      # @param conversation [String] the conversation's id
      # @param agent [String, nil] the agent's name
      # @param kind [Symbol] one of KINDS: +:tool_started+ is a call's attempt, which a write needs before it runs;
      #   +:tool_ended+ a call's outcome, which every call has, whether it ran or not
      # @param trace_id [String, nil] the run's trace, in W3C hex form, so the entry opens as its trace; nil when the
      #   run has none, as without a tracer provider or a host's span
      # @param span_id [String, nil] the span of the step it records: the tool call's for a call and its approval,
      #   else the run's
      # @param tool [String, nil] and +call_id+, for a call's entries
      # @param input [String, nil] a call's input, JSON text
      # @param outcome [String, nil] how it went, in a word or two: +completed+, +stopped: cancelled+, +failed:
      #   model_error+ or +abandoned+ for a run; +ok+ or +error+ for a call; +approved+ or +denied+ for an approval
      # @param detail [String, nil] more: a call's result, a denial's reason, why a run stopped or failed
      # @param duration [Float, nil] seconds a tool ran, on +:tool_ended+ when it ran
      # @param usage [Usage, nil] the run's tokens, on +:run_ended+
      def initialize(time:, sequence:, run:, conversation:, agent:, kind:, trace_id: nil, span_id: nil, tool: nil,
                     call_id: nil, input: nil, outcome: nil, detail: nil, duration: nil, usage: nil)
        super
      end
    end
  end
end
