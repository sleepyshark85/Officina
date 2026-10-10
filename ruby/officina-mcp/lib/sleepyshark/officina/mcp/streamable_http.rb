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
      #
      # Its lock guards the session and the reason the connection was lost. Mutation testing leaves out the methods
      # that only take it: a test cannot make two threads meet inside a lock under the VM lock.
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
            return unless worker.join(timeout)

            case worker.value
            in Exception => failure then raise Error, transport.lose(failure)
            in answer then answer
            end
          end

          # Waits for the request's thread, ending its connection until it has ended. A connection still opening cannot
          # be closed, so it is closed once open, or the opening fails within OPEN_TIMEOUT. Net::HTTP closes the
          # connection of a request that has ended.
          def release
            stop until worker.join(POLL)
          end

          private

          # mutant:disable -- its survivors leave out the rescue of a connection not open yet, or no longer, which a
          #   cancel meets only in the moment a connection opens or a request ends
          def stop
            http.finish
          rescue IOError
            nil # Not open: not yet, or not any more.
          end
        end
        private_constant :Pending

        # Reaches server's url, sending its headers with every request.
        #
        # @raise [ArgumentError] when the url is not an http or https URL with a host.
        def initialize(server)
          @name = server.name
          # Mcp.connect makes this transport only for a server with a url.
          @uri = parse(server.url) # steep:ignore ArgumentTypeMismatch
          # Without the brackets of an IPv6 address, which Net::HTTP would take for part of a name.
          @host = @uri.hostname.to_s
          raise ArgumentError, "MCP server #{@name}: #{@uri} is not an http or https URL" unless http?

          @headers = server.headers
          @mutex = Mutex.new
        end

        # Sends text, a request with that id, or a notification when id is nil, and returns what to wait on.
        # Raises Mcp::Error once the connection is lost.
        def post(text, id, version)
          request = build(text, version)
          http = connection
          Pending.new(self, http, Thread.new { exchange(http, request, id) })
        end

        # Marks the connection lost, once, for reason, a message or an exception, and returns why it was.
        # mutant:disable -- see the class: the lock
        def lose(reason)
          @mutex.synchronize do
            @lost ||= reason
            lost_reason
          end
        end

        # Whether the connection is lost.
        # mutant:disable -- see the class: the lock
        def lost? = @mutex.synchronize { !@lost.nil? }

        # Nothing to stop: each request has its own connection, closed when it ends.
        def close = lose('the connection was closed')

        private

        def parse(url)
          URI(url)
        rescue URI::InvalidURIError
          raise ArgumentError, "MCP server #{@name}: #{url} is not an http or https URL"
        end

        def lost_reason = "MCP server #{@name} could not be reached: #{@lost}"

        def http? = @uri.is_a?(URI::HTTP) && !@host.empty?

        def build(text, version)
          request = Net::HTTP::Post.new(@uri, @headers)
          # Each chunk is read as it arrives, so none may be compressed. A nil leaves its header out.
          { 'Content-Type' => 'application/json', 'Accept' => 'application/json, text/event-stream',
            'Accept-Encoding' => 'identity', 'MCP-Protocol-Version' => version, 'Mcp-Session-Id' => session }
            .each { |name, value| request[name] = value }
          request.body = text
          request
        end

        # The session the server gave, nil before it has. Raises Mcp::Error once the connection is lost.
        # mutant:disable -- see the class: the lock
        def session
          @mutex.synchronize do
            raise Error, lost_reason if @lost

            @session
          end
        end

        # A connection of the request's own, opened on its thread.
        def connection
          http = limited_connection(@host, @uri.port)
          http.use_ssl = @uri.scheme == 'https'
          http
        end

        # A connection to host and port whose opening may take OPEN_TIMEOUT seconds; reading and writing have no
        # limit, as a run's cancellation ends a call. Net::HTTP takes nil for none, which its signature leaves out.
        # mutant:disable -- each setting shows only once a server has kept a request waiting for 10 or 60 seconds
        def limited_connection(host, port)
          http = Net::HTTP.new(host, port)
          http.open_timeout = OPEN_TIMEOUT
          http.read_timeout = nil # steep:ignore
          http.write_timeout = nil # steep:ignore
          http
        end

        # Runs on the request's own thread: opens the connection (Net::HTTP#request does, as it is not open yet),
        # sends the request and reads the answer, or returns why it failed. A failure is returned, not raised: a thread
        # that ends with an exception raises it in the main thread too where Thread.abort_on_exception is set, as a
        # host may set it, and mutant does. Net::HTTP#request's signature has its block return nothing, so Steep takes
        # the method's value for a response, and refuses the break.
        def exchange(http, request, id) # steep:ignore MethodBodyTypeMismatch
          # Breaking out of the block leaves the rest of the body unread, as a stream may stay open after the response.
          http.request(request) do |response|
            raise_unless_ok(response, request)
            keep_session(response)
            break id ? answer(response, id) : ACCEPTED # steep:ignore BreakTypeMismatch
          end
        rescue *FAILURES => e
          e
        end

        # A 404 to a request in a session means the server ended the session; to one before, that the URL is wrong.
        def raise_unless_ok(response, request)
          raise Error, 'the server ended the session' if response.code == '404' && request.key?('Mcp-Session-Id')
          return if response.is_a?(Net::HTTPSuccess)

          raise Error, "the server answered HTTP #{response.code} #{response.message}"
        end

        # The session a server gives is sent back with every later message.
        # mutant:disable -- see the class: its one survivor leaves out the lock
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
