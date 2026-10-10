# frozen_string_literal: true

module Sleepyshark
  module Officina
    # What a model's reply means for its run, read from its stop and its calls.
    module ReplyOutcome
      # The reason a run stops for each stop of the model that stops it.
      STOP_REASONS = { max_tokens: :output_limit, refusal: :refusal, context_full: :context_full }.freeze
      private_constant :STOP_REASONS

      # The result the reply ends the run with, or nil when the run goes on to answer its calls.
      # @param usage [Usage] of every model call of the run
      def self.of(reply, usage)
        reason = STOP_REASONS[reply.stop]
        return Stopped.new(reason:, detail: reply.detail, usage:) if reason

        why = unexpected(reply)
        return Failed.new(reason: :unexpected_stop, detail: why, usage:) if why

        Completed.new(text: reply.text, usage:) if reply.stop == :end
      end

      # Why the run cannot act on the reply's stop, or nil when it can: the reply ends the turn without calls, or
      # stops to use the tools it calls.
      def self.unexpected(reply)
        calls = reply.blocks.any?(&:tool_call)
        case reply.stop
        in :end then "The model's reply called tools but did not stop for them" if calls
        in :tool_use then 'The model stopped to use tools but called none' unless calls
        in :unknown then "The run cannot act on the model's stop: #{reply.detail}"
        end
      end
      private_class_method :unexpected
    end
    private_constant :ReplyOutcome
  end
end
