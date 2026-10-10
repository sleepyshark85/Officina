# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Testing
      # A model that answers with replies scripted in advance, in order, and records every request; it needs no
      # network or key, and many runs may use it at once. Each reply is a list of steps: the events it streams, then
      # what the stream ends with: a Reply it returns, an exception it raises (a failure left after retries), or nil (a
      # stream that ends without a stop reason). It stops early, returning nil, once the run is cancelled. Like the
      # provider's API, it rejects a request whose messages the API would reject.
      class ScriptedModel
        # Each role that may follow another, besides a user message after one holding only tool results.
        FOLLOWS = [%i[user assistant], %i[user operator], %i[operator assistant], %i[assistant user]].freeze
        private_constant :FOLLOWS
        # @return [String] the settings it was made with, which enter the prefix fingerprint
        attr_reader :settings

        # @param replies [Array<Array>] each the steps of one reply
        # @param settings [String]
        def initialize(*replies, settings: 'scripted')
          @settings = -settings
          @replies = replies.map { |reply| reply.dup.freeze }
          @requests = []
          @lock = Mutex.new
        end

        # The steps of a reply that streams the text and ends the turn, reporting the usage if given.
        def self.text(text, usage: nil)
          [TextDelta.new(text:), *(UsageReported.new(usage:) if usage),
           Reply.new(blocks: [text_block(text)], stop: :end)]
        end

        # The steps of a reply that asks for the calls of the blocks, made by tool_use_block.
        def self.tool_use(*blocks) = [Reply.new(blocks:, stop: :tool_use)]

        # The steps of a reply that ends with the stop, holding a text block unless the text is nil.
        def self.stop(stop, text: 'Partial', detail: nil)
          [Reply.new(blocks: text ? [text_block(text)] : [], stop:, detail:)]
        end

        # A text block as a provider adapter stores it, its raw JSON in the canonical form.
        def self.text_block(text) = Block.new(text:, raw: Block.canonical(JSON.generate({ type: 'text', text: })))

        # A block that calls the tool with the input, a JSON object, as a provider adapter stores it.
        def self.tool_use_block(id, name, input)
          raw = JSON.generate({ type: 'tool_use', id:, name:, input: JSON::Fragment.new(input) })
          Block.new(raw: Block.canonical(raw), tool_call: ToolCall.new(id:, name:, input:))
        end

        # @return [Array<Request>] the requests received so far, in order
        def requests = @lock.synchronize { @requests.dup.freeze }

        # Records the request and streams the next reply.
        # @raise [Error] when the request is invalid or no reply is left, or as the reply's script says
        def stream(request, cancel:)
          *events, last = next_reply(request)
          events.each do |event|
            return nil if cancel.cancelled?

            yield event
          end
          return nil if cancel.cancelled?
          raise last if last.is_a?(Exception)

          last
        end

        private

        def next_reply(request)
          @lock.synchronize do
            @requests << request
            problem = problem(request.messages)
            raise Error, "Scripted model: request #{@requests.size} is invalid: #{problem}" if problem
            raise Error, "Scripted model: request #{@requests.size} has no reply left" if @replies.empty?

            @replies.shift
          end
        end

        # What the provider's API would reject in the messages, or nil. Roles: the user speaks first; no two messages
        # in a row have one role, except a user message after one holding only tool results, which the provider
        # joins; the operator follows the user, and only the assistant follows the operator. Calls: each message
        # answers exactly the calls of the one before it, in call order, and the last leaves none unanswered.
        def problem(messages)
          return "the first message is not the user's" unless messages.first&.role == :user
          return 'the last message has calls without results' if messages.fetch(-1).blocks.any?(&:tool_call)

          (1...messages.size).each do |at|
            why = pair_problem(messages.fetch(at - 1), messages.fetch(at))
            return "message #{at + 1}: #{why}" if why
          end
          nil
        end

        def pair_problem(previous, message) = role_problem(previous, message) || answer_problem(previous, message)

        def role_problem(previous, message)
          pair = [previous.role, message.role]
          return if FOLLOWS.include?(pair) || (pair == %i[user user] && previous.blocks.all?(&:tool_result))

          "the #{message.role} follows the #{previous.role}"
        end

        def answer_problem(previous, message)
          calls = previous.blocks.filter_map { |block| block.tool_call&.id }
          answers = message.blocks.filter_map { |block| block.tool_result&.call_id }
          "it answers the calls #{answers} instead of #{calls}" unless answers == calls
        end
      end
    end
  end
end
