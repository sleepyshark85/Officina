# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Claude
      # One attempt of a model call: it streams the request and turns what arrives into the model's events and reply.
      class Call
        # The stream ended cleanly before the reply did, as a dropped connection can.
        class IncompleteError < Error
        end

        # Stop reasons by the API's word; any other keeps its word as the reply's detail.
        STOPS = { end_turn: :end, tool_use: :tool_use, max_tokens: :max_tokens, refusal: :refusal,
                  model_context_window_exceeded: :context_full }.freeze

        def initialize(client:, params:, cancel:)
          @client = client
          @params = params
          @cancel = cancel
          @usage = nil
        end

        # Yields each text delta as it arrives and, once the reply is whole, what the provider did to shorten the
        # conversation (ConversationCompacted, ToolResultsCleared) and one UsageReported. Returns the
        # reply, or nil once the cancellation stopped it; a prompt longer than the context window is a reply that
        # stops for +:context_full+. A failure, reported after the usage of what streamed before it, is raised as the
        # SDK raised it, or as an IncompleteError. What the block raises passes through as it is.
        def reply(&)
          read(@client.beta.messages.stream(**@params), &)
        rescue Anthropic::Errors::BadRequestError => e
          raise unless Failure.prompt_too_long?(e)

          Reply.new(blocks: [], stop: :context_full)
        rescue Anthropic::Errors::Error, IncompleteError
          # The tokens of an attempt that failed mid-stream are billed, so they are reported too.
          report(&)
          raise
        end

        private

        def read(stream, &)
          # @type var blocks: Array[Block]
          blocks = []
          # @type var reply: Reply?
          reply = nil
          # However the loop is left (a break, a raise, the end), the SDK's enumerator closes the connection itself.
          stream.each do |event|
            break if @cancel.cancelled?

            reply = take(event, blocks, &)
            break if reply
          end
          return reply if reply || @cancel.cancelled?

          raise IncompleteError, "Claude's reply ended before its stop reason"
        end

        # Takes in one event of the stream; returns the reply once it is whole.
        def take(event, blocks, &)
          case event.type
          when :message_start then @usage = usage(event.message.usage)
          when :text then yield TextDelta.new(text: event.text)
          when :content_block_stop then blocks << block(event.content_block)
          when :message_delta then @usage = usage_after_delta(event.usage)
          when :message_stop then return finish(event.message, blocks, &)
          end
          nil
        end

        def finish(message, blocks, &)
          word = message[:stop_reason]
          raise IncompleteError, "Claude's reply stopped without a stop reason" unless word

          ContextEditing.reported(message).each { yield it }
          report(&)
          # The gem gives a stop reason as a Symbol.
          stop = STOPS.fetch(word, :unknown)
          detail = case stop
                   when :refusal then message[:stop_details]&.[](:category)&.to_s
                   when :unknown then word.to_s
                   end
          Reply.new(blocks:, stop:, detail:)
        end

        # Yields the call's usage once the stream has reported any.
        def report
          usage = @usage
          yield UsageReported.new(usage:) if usage
        end

        # A reply block as the conversation keeps it: the gem's JSON for it in the canonical form, with its text or
        # its call. The gem keeps no raw JSON, and has decoded a tool's input when its block ended.
        def block(content)
          raw = Block.canonical(content.to_json)
          case content.type
          when :text then Block.new(raw:, text: content[:text])
          when :tool_use
            call = ToolCall.new(id: content[:id], name: content[:name], input: JSON.generate(content[:input]))
            Block.new(raw:, tool_call: call)
          else Block.new(raw:)
          end
        end

        # The usage a message starts with, or of one of its iterations: the API always counts its input and output
        # tokens, not always the cache's. Only these break the cache writes down by how long they are kept.
        def usage(reported)
          Usage.new(input: reported[:input_tokens], output: reported[:output_tokens],
                    cache_read: reported[:cache_read_input_tokens] || 0,
                    cache_write: reported[:cache_creation_input_tokens] || 0,
                    cache_write_hour: reported[:cache_creation]&.[](:ephemeral_1h_input_tokens) || 0)
        end

        # The usage after a message delta, which carries the final output count and repeats the input counts it has.
        # The SDK raises on a delta before the message's start, so there is a usage to update. A call that ran several
        # iterations (a compaction, then the reply) counts only the last in those totals, so its usage is the sum of its
        # iterations, whatever their kind.
        def usage_after_delta(reported)
          iterations = reported[:iterations]
          return iterations.sum(Usage.new) { usage(it) } if iterations&.any?

          # @type ivar @usage: Usage
          @usage.with(**{ input: reported[:input_tokens], output: reported[:output_tokens],
                          cache_read: reported[:cache_read_input_tokens],
                          cache_write: reported[:cache_creation_input_tokens] }.compact)
        end
      end
      private_constant :Call
    end
  end
end
