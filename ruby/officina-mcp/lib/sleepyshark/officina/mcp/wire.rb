# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Mcp
      # Reading what a server writes: JSON-RPC messages, and the event streams Streamable HTTP may answer with.
      module Wire
        # The most a server may send for one message: a line over stdio, its line end aside; the whole answer to a
        # request over Streamable HTTP, with an event stream's framing and any messages before the response. More
        # loses the connection.
        MAX_MESSAGE_MB = 16
        MAX_MESSAGE = MAX_MESSAGE_MB * 1024 * 1024

        # The id and message of the response that text holds, the message parsed and deeply frozen; nil for anything
        # else a server may write: a request or notification of its own, what is not a response to a request of this
        # client's, whose ids are positive integers, or what is not JSON (JSON.parse refuses a key given twice).
        def self.response(text)
          message = JSON.parse(text, freeze: true)
          # JSON.parse makes each value an instance of the class itself, never of a subclass.
          id = response_id(message) if message.instance_of?(Hash)
          [id, message] if id
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
              case response(data)
              in [^id, message] then return message
              else nil
              end
            end
          end
          raise Error, 'the server ended its event stream without a response'
        end

        def self.from_body(body, id)
          # Bytes, as a chunk may end inside a character; JSON.parse reads them as UTF-8.
          text = ''.b
          each_chunk(body) { text << it }
          case response(text)
          in [^id, message] then return message
          else nil
          end

          raise Error, "the server's answer is not a response to the request"
        end

        def self.each_chunk(body)
          size = 0
          body.read_body do |chunk|
            size += chunk.bytesize
            raise Error, "it sent a message longer than #{MAX_MESSAGE_MB} MB" if size > MAX_MESSAGE

            yield chunk
          end
        end

        # Its id, when message is a response: one with a result or an error, a positive integer id and no method.
        def self.response_id(message)
          id = message['id']
          id if id.instance_of?(Integer) && id.positive? && !message.key?('method') &&
                (message.key?('result') || message.key?('error'))
        end
        private_class_method :from_events, :from_body, :each_chunk, :response_id

        # Splits an event stream, fed in chunks of any size, into events, and yields each event's data: its data
        # lines, joined with line feeds. Other fields and comments are skipped, and so is an event the stream ends
        # without, as it is incomplete. Lines end in LF or CRLF; a lone CR, which the format allows but no MCP server
        # is known to send, is not taken as a line end.
        class EventStream
          def initialize
            # What follows the last line end, as bytes, as a chunk may end inside a character.
            @buffer = ''.b
            # How much of the buffer is known to hold no line end, so each byte is searched once however long a line.
            @searched = 0
            # The data lines of the event being read.
            @data = []
          end

          # Adds a chunk of the stream, and yields the data of each event it completes, as UTF-8. The slices are within
          # the buffer, never nil, which String#byteslice's signature allows.
          def feed(chunk, &)
            @buffer << chunk.b
            start = 0
            while (stop = @buffer.index("\n", @searched))
              line(@buffer.byteslice(start...stop).delete_suffix("\r"), &) # steep:ignore NoMethod
              start = @searched = stop + 1
            end
            @buffer = @buffer.byteslice(start..) if start.positive? # steep:ignore IncompatibleAssignment
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
