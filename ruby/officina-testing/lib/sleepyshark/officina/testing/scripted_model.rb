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
        # The provider and name it has unless given, with no price.
        INFO = ModelInfo.new(provider: 'scripted', name: 'scripted')

        # @return [String] the settings it was made with, which enter the prefix fingerprint
        attr_reader :settings
        # @return [ModelInfo] the provider, name and price it was made with, which telemetry reports
        attr_reader :info

        # @param replies [Array<Array>] each the steps of one reply
        # @param settings [String]
        # @param info [ModelInfo]
        def initialize(*replies, settings: 'scripted', info: INFO)
          @settings = -settings
          @info = info
          @replies = replies.map { |reply| reply.dup.freeze }
          @requests = []
          @lock = Mutex.new
        end

        # The steps of a reply that streams the text and ends the turn, reporting the usage if given.
        def self.text(text, usage: nil)
          [TextDelta.new(text:), *(UsageReported.new(usage:) if usage),
           Reply.new(blocks: [text_block(text)], stop: :end)]
        end

        # The steps of a reply that asks for the calls of the blocks, made by tool_use_block, reporting the usage if
        # given.
        def self.tool_use(*blocks, usage: nil)
          [*(UsageReported.new(usage:) if usage), Reply.new(blocks:, stop: :tool_use)]
        end

        # The steps of a reply that ends with the stop, holding a text block unless the text is nil, reporting the
        # usage if given.
        def self.stop(stop, text: 'Partial', detail: nil, usage: nil)
          [*(UsageReported.new(usage:) if usage), Reply.new(blocks: text ? [text_block(text)] : [], stop:, detail:)]
        end

        # A text block as a provider adapter stores it, its raw JSON in the canonical form.
        def self.text_block(text) = Block.new(text:, raw: Block.canonical(JSON.generate({ type: 'text', text: })))

        # A compaction block holding the summary, as a provider adapter stores it: the provider replays it in place of
        # the conversation before it.
        def self.compaction_block(summary)
          Block.new(raw: Block.canonical(JSON.generate({ type: 'compaction', content: summary })))
        end

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
            problem = ConversationRules.problem(request.messages)
            raise Error, "Scripted model: request #{@requests.size} is invalid: #{problem}" if problem
            raise Error, "Scripted model: request #{@requests.size} has no reply left" if @replies.empty?

            @replies.shift
          end
        end
      end
    end
  end
end
