# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The calls a crash left without results: those of a reply saved while its tools ran, before their results were.
    # The provider rejects a call without its result, so a run on such a conversation first answers each with an error
    # result, from which the model learns the call may have taken effect.
    module InterruptedCalls
      RESULT = 'The call was interrupted: the application stopped before its result was recorded, so it may or may ' \
               'not have taken effect.'
      private_constant :RESULT

      # Audits each call of the last message left without a result, as ended interrupted.
      # @param audit [AuditRecorder]
      # @return [Message, nil] the message of their error results; nil when the last message is not a reply with calls
      def self.answer(messages, audit)
        # @type var calls: Array[ToolCall]
        last = messages.last
        calls = last&.role == :assistant ? last.blocks.filter_map(&:tool_call) : []
        return if calls.empty?

        results = calls.map do |call|
          audit.record(:tool_ended, call:, outcome: 'interrupted', detail: RESULT)
          Block.new(tool_result: ToolResult.new(call_id: call.id, content: RESULT, error: true))
        end
        Message.new(role: :user, blocks: results)
      end
    end
    private_constant :InterruptedCalls
  end
end
