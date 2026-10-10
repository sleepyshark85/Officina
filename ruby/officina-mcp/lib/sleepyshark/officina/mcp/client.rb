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
        # How long connecting may take, in seconds.
        CONNECT_TIMEOUT = 30
        private_constant :PROTOCOL_VERSION, :PROTOCOL_VERSIONS, :CONNECT_TIMEOUT

        # Agrees on the protocol with the server called name over transport, which only Mcp.connect makes; see
        # Mcp.connect.
        def initialize(transport, name:, cancel:, clock:)
          @transport = transport
          @name = name
          @clock = clock
          @mutex = Mutex.new
          @last_id = 0
          # The version the handshake agrees on; none is sent before.
          @version = nil
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
            cursor = next_cursor(pages.fetch(-1)) or break
            raise Error, "MCP server #{@name} gave the cursor #{cursor.inspect} twice" if cursors.include?(cursor)

            cursors << cursor
          end
          pages.flat_map { listed(it) }.freeze
        end

        # Calls the tool name on the server with arguments, a Hash that becomes its JSON input. A tool that fails
        # returns a result that is an error; a server that fails raises.
        #
        # @raise [Mcp::Error] when the server has gone, fails, answers with an error, or cancel is cancelled while
        #   it waits; it does not wait for the tool to stop.
        def call_tool(name, arguments, cancel: nil)
          result = request('tools/call', { 'name' => name, 'arguments' => arguments }, cancel:)
          content = result['content']
          unreadable('tools/call') unless content.is_a?(Array) && content.all? { content?(it) }
          text = content.map { |item| item['type'] == 'text' ? item['text'] : "[#{item['type']} content]" }
          CallResult.new(text: text.join("\n").freeze, error: result['isError'] == true)
        end

        # Ends the connection. A server it started has its input closed, so it can exit by itself; one that has not
        # within 5 seconds is killed with every process it started (on Unix, its process group). Returns once the
        # server has exited and been reaped, and its readers have ended. Each request's own thread ends with the
        # request, so close it once no request is in flight.
        def close = @transport.close

        private

        def handshake(cancel)
          # @type var connected: bool
          connected = false
          connect_deadline = @clock.call + CONNECT_TIMEOUT
          result = request('initialize', { 'protocolVersion' => PROTOCOL_VERSION, 'capabilities' => {},
                                           'clientInfo' => { 'name' => 'officina', 'version' => VERSION } },
                           cancel:, connect_deadline:)
          @version = agreed(result['protocolVersion'])
          exchange('notifications/initialized', nil, nil, cancel:, connect_deadline:)
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

        def listed(page)
          tools = page['tools']
          unreadable('tools/list') unless tools.is_a?(Array) && tools.all? { listed?(it) }
          tools.map { Tool.new(name: it['name'], description: it['description'].to_s, input_schema: it['inputSchema']) }
        end

        def next_cursor(page)
          cursor = page['nextCursor']
          cursor if cursor.is_a?(String) && !cursor.empty?
        end

        def listed?(tool) = tool.is_a?(Hash) && tool['name'].is_a?(String) && tool['inputSchema'].is_a?(Hash)

        # A content item has a type, and one of text its text.
        def content?(item)
          item.is_a?(Hash) && item['type'].is_a?(String) && (item['type'] != 'text' || item['text'].is_a?(String))
        end

        # Sends a request and returns its result.
        def request(method, params, cancel:, connect_deadline: nil)
          id = @mutex.synchronize { @last_id += 1 }
          response = exchange(method, params, id, cancel:, connect_deadline:)
          if (error = response['error'])
            message = error.is_a?(Hash) ? error['message'] : error
            raise Error, "MCP server #{@name} answered #{method} with an error: #{message}"
          end
          response['result'].tap { unreadable(method) unless it.is_a?(Hash) }
        end

        def unreadable(method)
          raise Error, "MCP server #{@name} answered #{method} with an unreadable result"
        end

        # Sends a message, a request when id is not nil, and returns its response, waiting until cancel is
        # cancelled or, while connecting, the connect deadline passes.
        def exchange(method, params, id, cancel:, connect_deadline:)
          raise_if_cancelled_or_late(method, cancel, connect_deadline)
          message = { 'jsonrpc' => '2.0', 'id' => id, 'method' => method, 'params' => params }.compact
          pending = @transport.post(JSON.generate(message), id, @version)
          begin
            wait(pending, method, cancel, connect_deadline)
          ensure
            pending.release
          end
        end

        def wait(pending, method, cancel, connect_deadline)
          loop do
            answer = pending.take(POLL)
            return answer if answer

            raise_if_cancelled_or_late(method, cancel, connect_deadline)
          end
        end

        def raise_if_cancelled_or_late(method, cancel, connect_deadline)
          raise Error, "MCP server #{@name}, #{method}: cancelled" if cancel&.cancelled?
          return unless connect_deadline && @clock.call >= connect_deadline

          raise Error, "MCP server #{@name} did not answer within #{CONNECT_TIMEOUT} seconds"
        end
      end
    end
  end
end
