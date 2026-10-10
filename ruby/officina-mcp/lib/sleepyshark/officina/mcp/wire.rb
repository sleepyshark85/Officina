# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Mcp
      # Reading what a server writes: JSON-RPC messages, and the event streams Streamable HTTP may answer with.
      module Wire
        # The longest message a server may send, in bytes; a longer one loses the connection.
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
            stream.feed(chunk) { |data| response(data)&.then { |message| return message if message['id'] == id } }
          end
          raise Error, 'the server ended its event stream without a response'
        end

        def self.from_body(body, id)
          text = String.new(encoding: Encoding::BINARY)
          each_chunk(body) { text << it }
          response(text.force_encoding(Encoding::UTF_8))&.then { return it if it['id'] == id }
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
        # without, as it is incomplete.
        class EventStream
          def initialize
            # Bytes, as a chunk may end inside a character.
            @buffer = String.new(encoding: Encoding::BINARY)
            @data = nil
          end

          # Adds a chunk of the stream, and yields the data of each event it completes, as UTF-8.
          def feed(chunk)
            @buffer << chunk.b
            while (line = @buffer.slice!(/\A[^\n]*\n/n))
              field = line.chomp
              if field.start_with?('data:')
                @data = Array(@data) << field.delete_prefix('data:').delete_prefix(' ')
              elsif field.empty? && (data = @data)
                yield data.join("\n").force_encoding(Encoding::UTF_8)
                @data = nil
              end
            end
          end
        end
      end
      private_constant :Wire
    end
  end
end
