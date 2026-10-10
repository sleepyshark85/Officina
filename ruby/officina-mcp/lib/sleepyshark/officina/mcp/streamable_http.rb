# frozen_string_literal: true

require 'net/http'
require 'openssl'
require 'uri'

module Sleepyshark
  module Officina
    module Mcp
      # The Streamable HTTP transport: each message is a POST, answered with a JSON body or an event stream that ends
      # with the response. A session id the server gives is sent back with every later message; a 404 in a session
      # loses the connection, as the protocol asks the client to start a new one. Any failure loses it: every later
      # request fails.
      class StreamableHttp
        # What the network and Net::HTTP raise when a server cannot be reached, breaks off or answers with what is
        # not HTTP.
        FAILURES = [IOError, SystemCallError, SocketError, Timeout::Error, Net::ProtocolError, Net::HTTPBadResponse,
                    Net::HTTPHeaderSyntaxError, OpenSSL::SSL::SSLError, Error].freeze
        # How long opening a connection may take, in seconds. Net::HTTP cannot stop it sooner, so it bounds how long
        # a request cancelled while its connection opens still waits for its thread.
        OPEN_TIMEOUT = 10

        # A request waiting for its response, which a thread of its own reads. The request's connection is its own,
        # so closing it is how a cancelled request stops that thread.
        Pending = Data.define(:transport, :http, :worker)

        # Reopened rather than given a block, which Steep would not read as the class's body.
        class Pending
          # The response, ACCEPTED for a notification the server accepted, or nil if none came within timeout
          # seconds. Raises Mcp::Error when the request failed, which loses the connection.
          def take(timeout)
            worker.value if worker.join(timeout)
          rescue *FAILURES => e
            raise Error, transport.lose(e.message)
          end

          # Ends the request, if still running, and waits for its thread. A connection still opening cannot be closed,
          # so it is closed once open, or the opening fails within OPEN_TIMEOUT.
          def release
            loop do
              stop
              break if worker.join(POLL)
            end
          rescue *FAILURES
            # Taken already, or abandoned: either way, how it ended no longer matters.
            nil
          ensure
            stop
          end

          private

          def stop
            http.finish if http.started?
          rescue IOError
            nil # Finished meanwhile.
          end
        end
        private_constant :Pending

        # Reaches server's url, sending its headers with every request.
        def initialize(server)
          @name = server.name
          uri = URI(server.url.to_s)
          raise ArgumentError, "MCP server #{@name}: #{uri} is not an http or https URL" unless uri.is_a?(URI::HTTP)

          @uri = uri

          @headers = server.headers
          @mutex = Mutex.new
          @session = nil
          @lost = nil
        end

        # Sends text, a request with that id, or a notification when id is nil, and returns what to wait on.
        # Raises Mcp::Error once the connection is lost.
        def post(text, id, version)
          request = build(text, version)
          http = connection
          Pending.new(self, http, Thread.new { exchange(http, request, id) })
        end

        # Marks the connection lost, once, and returns why it was.
        def lose(reason)
          @mutex.synchronize do
            @lost ||= reason
            lost_reason
          end
        end

        # Nothing to stop: each request has its own connection, closed when it ends.
        def close = lose('the connection was closed')

        private

        def lost_reason = "MCP server #{@name} could not be reached: #{@lost}"

        def build(text, version)
          session = @mutex.synchronize do
            raise Error, lost_reason if @lost

            @session
          end
          request = Net::HTTP::Post.new(@uri, @headers)
          # Each chunk is read as it arrives, so none may be compressed. A nil leaves its header out.
          { 'Content-Type' => 'application/json', 'Accept' => 'application/json, text/event-stream',
            'Accept-Encoding' => 'identity', 'MCP-Protocol-Version' => version, 'Mcp-Session-Id' => session }
            .each { |name, value| request[name] = value }
          request.body = text
          request
        end

        # A connection of the request's own, opened on its thread.
        def connection
          http = Net::HTTP.new(@uri.host.to_s, @uri.port)
          http.use_ssl = @uri.scheme == 'https'
          http.open_timeout = OPEN_TIMEOUT
          # A run's cancellation ends a call, not a timeout. Net::HTTP takes nil for none, which its signature leaves
          # out.
          http.read_timeout = nil # steep:ignore
          http.write_timeout = nil # steep:ignore
          http
        end

        # Runs on the request's own thread: opens the connection (Net::HTTP#request does, as it is not open yet),
        # sends the request and reads the answer. Net::HTTP#request's signature has its block return nothing, so Steep
        # takes the method's value for a response, and refuses the break.
        def exchange(http, request, id) # steep:ignore MethodBodyTypeMismatch
          Thread.current.report_on_exception = false
          # Breaking out of the block leaves the rest of the body unread, as a stream may stay open after the response.
          http.request(request) do |response|
            raise_unless_ok(response, request)
            keep_session(response)
            break id ? answer(response, id) : ACCEPTED # steep:ignore BreakTypeMismatch
          end
        end

        # A 404 to a request in a session means the server ended the session; to one before, that the URL is wrong.
        def raise_unless_ok(response, request)
          raise Error, 'the server ended the session' if response.is_a?(Net::HTTPNotFound) && request['Mcp-Session-Id']
          return if response.is_a?(Net::HTTPSuccess)

          raise Error, "the server answered HTTP #{response.code} #{response.message}"
        end

        # The session a server gives is sent back with every later message.
        def keep_session(response)
          session = response['Mcp-Session-Id']
          @mutex.synchronize { @session = session } if session
        end

        # The response to request id, from an event stream or a JSON body.
        def answer(response, id)
          Wire.answer(response, id, events: response.content_type.to_s.casecmp?('text/event-stream'))
        end
      end
      private_constant :StreamableHttp
    end
  end
end
