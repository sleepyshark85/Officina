# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Testing
      # The rules the provider's API holds messages to, which every conversation and request must keep. Roles: the
      # user speaks first; no two messages in a row have one role, except a user message after one holding only tool
      # results, which the provider joins; the operator follows the user, and only the assistant follows the operator.
      # Calls: each message answers exactly the calls of the one before it, in call order, and the last leaves none
      # unanswered, so every call has exactly one result.
      module ConversationRules
        # Each role that may follow another, besides a user message after one holding only tool results.
        FOLLOWS = [%i[user assistant], %i[user operator], %i[operator assistant], %i[assistant user]].freeze
        private_constant :FOLLOWS

        # What the provider would reject in the messages, or nil.
        # @param messages [Array<Message>]
        # @return [String, nil]
        def self.problem(messages)
          return "the first message is not the user's" unless messages.first&.role == :user
          return 'the last message has calls without results' if messages.fetch(-1).blocks.any?(&:tool_call)

          (1...messages.size).each do |at|
            why = pair_problem(messages.fetch(at - 1), messages.fetch(at))
            return "message #{at + 1}: #{why}" if why
          end
          nil
        end

        def self.pair_problem(previous, message) = role_problem(previous, message) || answer_problem(previous, message)

        def self.role_problem(previous, message)
          pair = [previous.role, message.role]
          return if FOLLOWS.include?(pair) || (pair == %i[user user] && previous.blocks.all?(&:tool_result))

          "the #{message.role} follows the #{previous.role}"
        end

        def self.answer_problem(previous, message)
          calls = previous.blocks.filter_map { |block| block.tool_call&.id }
          answers = message.blocks.filter_map { |block| block.tool_result&.call_id }
          "it answers the calls #{answers} instead of #{calls}" unless answers == calls
        end
        private_class_method :pair_problem, :role_problem, :answer_problem
      end
    end
  end
end
