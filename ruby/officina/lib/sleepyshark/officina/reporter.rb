# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A run's link to its host: appends to the conversation the run holds, and passes events to the host's block.
    class Reporter
      def initialize(append:, fingerprint:, on_event:)
        @append = append
        @fingerprint = fingerprint
        @on_event = on_event
      end

      def emit(event) = @on_event&.call(event)

      # Appends the messages, then reports each. All are appended before the first is reported, so a host that stops
      # reading midway still holds a conversation where a reply follows the messages it answers.
      def append(*messages)
        append_unreported(*messages)
        messages.each { |message| emit(ConversationAppended.new(message:)) }
      end

      # Appends the messages for a host that has left the run.
      def append_unreported(*messages) = @append.call(messages, @fingerprint)
    end
    private_constant :Reporter
  end
end
