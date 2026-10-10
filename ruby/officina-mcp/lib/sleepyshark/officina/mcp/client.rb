# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Mcp
      # A connection to one MCP server: JSON-RPC 2.0 over stdio or Streamable HTTP, with what a tool source needs:
      # the handshake, the tool list and tool calls. Made by Mcp.connect. A server that fails loses the connection for
      # good: every later request raises. Requests may come from several threads at once. Close it when done.
      class Client
        # The protocol version this client asks for, and those it accepts in answer.
        PROTOCOL_VERSION = '2025-06-18'
        PROTOCOL_VERSIONS = %w[2025-06-18 2025-03-26 2024-11-05].freeze
        # How long connecting, or a ping, may take, in seconds.
        TIMEOUT = 30
        private_constant :PROTOCOL_VERSION, :PROTOCOL_VERSIONS, :TIMEOUT

        # Agrees on the protocol with the server called name over transport, which only Mcp.connect makes; see
        # Mcp.connect.
        def initialize(transport, name:, cancel:, clock:)
          @transport = transport
          @name = name
          @clock = clock
          @mutex = Mutex.new
          @last_id = 0
          handshake(cancel)
        end

        # Every tool the server lists, from every page of its list.
        #
        # @raise [Mcp::Error] when the server has gone, fails, answers with an error, gives a page's cursor twice, or
        #   cancel is cancelled.
        def list_tools(cancel: nil)
          # @type var cursors: Array[String]
          cursors = []
          # @type var pages: Array[Hash[String, json]]
          pages = []
          loop do
            pages << request('tools/list', { 'cursor' => cursors.last }.compact, cancel:)
            cursor = Results.next_cursor(pages.fetch(-1)) or break
            raise Error, "MCP server #{@name} gave the cursor #{cursor.inspect} twice" if cursors.include?(cursor)

            cursors << cursor
          end
          pages.flat_map { Results.tools(it) || unreadable('tools/list') }.freeze
        end

        # Calls the tool name on the server with arguments, a Hash that becomes its JSON input. A tool that fails
        # returns a result that is an error; a server that fails raises.
        #
        # @raise [Mcp::Error] when the server has gone, fails, answers with an error, or cancel is cancelled while
        #   it waits; it does not wait for the tool to stop.
        def call_tool(name, arguments, cancel: nil)
          result = request('tools/call', { 'name' => name, 'arguments' => arguments }, cancel:)
          Results.call_result(result) || unreadable('tools/call')
        end

        # Checks that the server still answers.
        #
        # @raise [Mcp::Error] when the server has gone, fails, answers with an error or not within 30 seconds, or
        #   cancel is cancelled.
        def ping(cancel: nil)
          request('ping', nil, cancel:, deadline: @clock.call + TIMEOUT)
          nil
        end

        # Whether the connection is lost for good, as a server that went away loses it: every request then raises.
        def lost? = @transport.lost?

        # Ends the connection. A server it started has its input closed, so it can exit by itself; one that has not
        # within 5 seconds is killed with every process it started (on Unix, its process group). Returns once the
        # server has exited and been reaped, and its readers have ended. Each request's own thread ends with the
        # request, so close it once no request is in flight.
        def close = @transport.close

        private

        def handshake(cancel)
          # @type var connected: bool
          connected = false
          deadline = @clock.call + TIMEOUT
          result = request('initialize', { 'protocolVersion' => PROTOCOL_VERSION, 'capabilities' => {},
                                           'clientInfo' => { 'name' => 'officina', 'version' => VERSION } },
                           cancel:, deadline:)
          # The version agreed on, sent with every later message; none before.
          @version = agreed(result['protocolVersion'])
          exchange('notifications/initialized', nil, nil, cancel:, deadline:)
          connected = true
        ensure
          # However it failed, a server it started does not outlive it.
          close unless connected
        end

        def agreed(version)
          return version if PROTOCOL_VERSIONS.include?(version)

          raise Error, "MCP server #{@name} speaks protocol #{version.inspect}; this client speaks " \
                       "#{PROTOCOL_VERSIONS.join(', ')}"
        end

        # Sends a request and returns its result. JSON.parse makes each value an instance of the class itself, never of
        # a subclass, so the check is instance_of?.
        def request(method, params, cancel:, deadline: nil)
          error, result = exchange(method, params, next_id, cancel:, deadline:).values_at('error', 'result')
          refused(method, error.instance_of?(Hash) ? error['message'] : error) if error
          unreadable(method) unless result.instance_of?(Hash)
          result
        end

        # mutant:disable -- its one survivor leaves out the lock, which no test can show racing under the VM lock
        def next_id = @mutex.synchronize { @last_id += 1 }

        def refused(method, message)
          raise Error, "MCP server #{@name} answered #{method} with an error: #{message}"
        end

        def unreadable(method)
          raise Error, "MCP server #{@name} answered #{method} with an unreadable result"
        end

        # Sends a message, a request when id is not nil, and returns its response, waiting until cancel is
        # cancelled or the deadline, if any, passes.
        def exchange(method, params, id, cancel:, deadline:)
          raise_if_cancelled_or_late(method, cancel, deadline)
          message = { 'jsonrpc' => '2.0', 'id' => id, 'method' => method, 'params' => params }.compact
          pending = @transport.post(JSON.generate(message), id, @version)
          begin
            wait(pending, method, cancel, deadline)
          ensure
            pending.release
          end
        end

        def wait(pending, method, cancel, deadline)
          loop do
            answer = pending.take(POLL)
            return answer if answer

            raise_if_cancelled_or_late(method, cancel, deadline)
          end
        end

        def raise_if_cancelled_or_late(method, cancel, deadline)
          raise Error, "MCP server #{@name}, #{method}: cancelled" if cancel&.cancelled?
          return unless deadline && @clock.call >= deadline

          raise Error, "MCP server #{@name} did not answer within #{TIMEOUT} seconds"
        end
      end
    end
  end
end
