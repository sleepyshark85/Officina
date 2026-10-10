# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Mcp
      # Reading what a server writes: JSON-RPC messages, and the event streams Streamable HTTP may answer with.
      module Wire
        # The most a server may send for one message, in bytes: a line over stdio, its line end aside; the whole answer
        # to a request over Streamable HTTP, with an event stream's framing and any messages before the response. More
        # loses the connection.
        MAX_MESSAGE = 16 * 1024 * 1024

        # The response that text holds, parsed and deeply frozen, or nil for anything else a server may write: a
        # request or notification of its own, or what is not a response to a request of this client's, whose ids
        # are positive integers.
        def self.response(text)
          message = JSON.parse(text, allow_duplicate_key: false, freeze: true)
          message if message.is_a?(Hash) && response?(message)
        rescue JSON::ParserError, EncodingError
          nil
        end

        # The response to request id in body, anything whose read_body yields its chunks as they arrive: an event
        # stream when events, where the server's own requests and notifications before the response are skipped,
        # else one JSON message. Raises Mcp::Error when it holds none, or more than MAX_MESSAGE bytes.
        def self.answer(body, id, events:) = events ? from_events(body, id) : from_body(body, id)

        def self.from_events(body, id)
          stream = EventStream.new
          each_chunk(body) do |chunk|
            stream.feed(chunk) do |data|
              message = response(data)
              return message if message && message['id'] == id
            end
          end
          raise Error, 'the server ended its event stream without a response'
        end

        def self.from_body(body, id)
          text = String.new(encoding: Encoding::BINARY)
          each_chunk(body) { text << it }
          message = response(text.force_encoding(Encoding::UTF_8))
          return message if message && message['id'] == id

          raise Error, "the server's answer is not a response to the request"
        end

        def self.each_chunk(body)
          size = 0
          body.read_body do |chunk|
            size += chunk.bytesize
            raise Error, "it sent a message longer than #{MAX_MESSAGE / 1024 / 1024} MB" if size > MAX_MESSAGE

            yield chunk
          end
        end

        def self.response?(message)
          id = message['id']
          !message.key?('method') && (message.key?('result') || message.key?('error')) && id.is_a?(Integer) &&
            id.positive?
        end
        private_class_method :from_events, :from_body, :each_chunk, :response?

        # Splits an event stream, fed in chunks of any size, into events, and yields each event's data: its data
        # lines, joined with line feeds. Other fields and comments are skipped, and so is an event the stream ends
        # without, as it is incomplete. Lines end in LF or CRLF; a lone CR, which the format allows but no MCP server
        # is known to send, is not taken as a line end.
        class EventStream
          def initialize
            # What follows the last line end, as bytes, as a chunk may end inside a character.
            @buffer = String.new(encoding: Encoding::BINARY)
            # How much of the buffer is known to hold no line end, so each byte is searched once however long a line.
            @searched = 0
            # The data lines of the event being read.
            @data = []
          end

          # Adds a chunk of the stream, and yields the data of each event it completes, as UTF-8.
          def feed(chunk, &)
            @buffer << chunk.b
            start = 0
            while (stop = @buffer.index("\n", @searched))
              line(@buffer.byteslice(start...stop).to_s.chomp, &)
              start = @searched = stop + 1
            end
            @buffer = @buffer.byteslice(start..).to_s if start.positive?
            @searched = @buffer.bytesize
          end

          private

          def line(text)
            if text.start_with?('data:')
              @data << text.delete_prefix('data:').delete_prefix(' ')
            elsif text.empty? && !@data.empty?
              yield @data.join("\n").force_encoding(Encoding::UTF_8)
              @data = []
            end
          end
        end
      end
      private_constant :Wire
    end
  end
end
