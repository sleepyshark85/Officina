# frozen_string_literal: true

module Sleepyshark
  module Officina
    Request = Data.define(:tools, :instructions, :messages, :max_output_tokens, :context_management)

    # One model call: the prefix, which stays the same for a conversation (the tools, sorted by name, the frozen
    # instructions and the context management; the model's settings are its own), then the conversation's messages,
    # ending with the run's pending ones: the user's message and, as an +:operator+ message, the run context. A model
    # must not change it.
    class Request
      # @param tools [Array<Tool>]
      # @param instructions [String]
      # @param messages [Array<Message>]
      # @param max_output_tokens [Integer, nil] the most output tokens the reply may use, when the run's budget lowers
      #   the model's own limit; the model keeps the lower of the two. nil leaves the model's own
      # @param context_management [ContextManagement, nil] how the provider shortens the conversation on its side; nil
      #   for not at all
      def initialize(tools:, instructions:, messages:, max_output_tokens: nil, context_management: nil) = super
    end
  end
end
