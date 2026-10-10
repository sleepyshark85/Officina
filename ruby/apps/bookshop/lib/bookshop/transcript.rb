# frozen_string_literal: true

module Bookshop
  # A session's transcript, as the summarizer reads it: the staff member's messages, the assistant's replies, and each
  # tool call with its input and outcome, one a line, so it can tell which changes were made; without the run context.
  module Transcript
    # The most characters of one tool result a transcript keeps.
    RESULT_LENGTH = 1_000
    private_constant :RESULT_LENGTH

    # @param conversation [Sleepyshark::Officina::Conversation]
    # @return [String]
    def self.of(conversation)
      # Each call's name by its id, which its result names.
      # @type var calls: Hash[String, String]
      calls = {}
      lines = conversation.messages.reject { it.role == :operator }.flat_map do |message|
        message.blocks.filter_map { |block| line(message.role, block, calls) }
      end
      lines.map { "#{it}\n" }.join
    end

    def self.line(role, block, calls)
      if (call = block.tool_call)
        calls[call.id] = call.name
        "Tool call #{call.name} #{call.input}"
      elsif (result = block.tool_result)
        "Tool result of #{calls.fetch(result.call_id, 'a call')}#{' (error)' if result.error?}: #{cut(result.content)}"
      elsif block.text
        "#{role == :user ? 'Staff' : 'Assistant'}: #{block.text}"
      end
    end

    # The text, or its first RESULT_LENGTH characters and a mark that it goes on.
    def self.cut(text) = text.length <= RESULT_LENGTH ? text : "#{text[0, RESULT_LENGTH]} […]"

    private_class_method :line, :cut
  end
  private_constant :Transcript
end
