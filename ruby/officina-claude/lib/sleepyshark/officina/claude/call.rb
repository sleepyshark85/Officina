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

        # Yields each text delta as it arrives and, once the call's usage is known, one UsageReported. Returns the
        # reply, or nil once the cancellation stopped it; a prompt longer than the context window is a reply that
        # stops for +:context_full+. A failure, reported after the usage of what streamed before it, is raised as the
        # SDK raised it, or as an IncompleteError. What the block raises passes through as it is.
        def reply(&)
          # @type var stream: untyped
          stream = @client.beta.messages.stream(**@params)
          read(stream, &)
        rescue Anthropic::Errors::BadRequestError => e
          raise unless Failure.new(e).prompt_too_long?

          Reply.new(blocks: [], stop: :context_full)
        rescue Anthropic::Errors::Error, IncompleteError
          # The tokens of an attempt that failed mid-stream are billed, so they are reported too.
          report(&)
          raise
        ensure
          stream&.close
        end

        private

        def read(stream, &)
          # @type var blocks: Array[Block]
          blocks = []
          # @type var reply: Reply?
          reply = nil
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
          when :message_delta then @usage = delta(event.usage)
          when :message_stop then return finish(event.message[:stop_reason], event.message, blocks, &)
          end
          nil
        end

        def finish(word, message, blocks, &)
          raise IncompleteError, "Claude's reply stopped without a stop reason" unless word

          report(&)
          stop = STOPS.fetch(word.to_sym, :unknown)
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

        def usage(reported)
          Usage.new(input: reported[:input_tokens] || 0, output: reported[:output_tokens] || 0,
                    cache_read: reported[:cache_read_input_tokens] || 0,
                    cache_write: reported[:cache_creation_input_tokens] || 0)
        end

        # The usage after a message delta, which carries the final output count and repeats the input counts it has.
        def delta(reported)
          (@usage || Usage.new).with(**{ input: reported[:input_tokens], output: reported[:output_tokens],
                                         cache_read: reported[:cache_read_input_tokens],
                                         cache_write: reported[:cache_creation_input_tokens] }.compact)
        end
      end
      private_constant :Call
    end
  end
end
