# frozen_string_literal: true

require 'securerandom'

module Sleepyshark
  module Officina
    # Numbers one run's audit entries and writes them to the agent's sink one at a time, from any of the run's
    # threads. Without a sink it records nothing, and every record succeeds.
    class AuditRecorder
      MAX_TEXT = 4_000
      private_constant :MAX_TEXT

      def initialize(agent:, conversation:)
        @agent = agent
        @conversation = conversation.id
        @run = SecureRandom.hex(16)
        @sequence = 0
        @lock = Mutex.new
      end

      # Fills in the entry's time, sequence and identity, and the tool call's, if it is about one, redacts and cuts
      # its text, and writes it. A failure is returned for the caller to decide what a missing entry means: only a
      # write's attempt depends on it.
      # @return [Boolean] whether the entry is in the trail
      def record(kind, call: nil, detail: nil, **fields)
        sink = @agent.audit_sink
        return true unless sink

        @lock.synchronize do
          @sequence += 1
          written?(sink, AuditEntry.new(time: @agent.clock.call, sequence: @sequence, run: @run,
                                        conversation: @conversation, agent: @agent.name, kind:, tool: call&.name,
                                        call_id: call&.id, input: clean(call&.input), detail: clean(detail), **fields))
        end
      end

      # Records the run's end: how it ended (nil when the host left it) and its usage.
      def record_end(result, usage)
        record(:run_ended, usage:, **ended(result))
      end

      private

      # A sink's failure is a gap in the trail, which the caller acts on.
      def written?(sink, entry)
        sink.write(entry)
        true
      rescue StandardError
        false
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
