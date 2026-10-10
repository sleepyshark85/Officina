# frozen_string_literal: true

require 'json'
require 'securerandom'
require 'socket'

module Sleepyshark
  module Officina
    module Testing
      # An MCP server with tools given in advance, over stdio (#serve, run by a program the test starts) or Streamable
      # HTTP on a local port (#serve_http). It lists one tool per page, sends a notification before each tool's result
      # (over HTTP, in an event stream) that clients must skip, and records the calls. It requires what the protocol
      # does, stricter than a real server: the client's capabilities and name and version when it initializes, and over
      # HTTP both content types accepted, then the protocol version and the session. It can go down, or end its
      # session.
      class FakeMcpServer
        NOTIFICATION = JSON.generate({ 'jsonrpc' => '2.0', 'method' => 'notifications/message',
                                       'params' => { 'level' => 'info', 'data' => 'working' } })
        private_constant :NOTIFICATION

        # Plain HTTP/1.1 on a listener, one request per connection.
        module Http
          STATUS = { 200 => 'OK', 202 => 'Accepted', 400 => 'Bad Request', 404 => 'Not Found' }.freeze

          # Answers each connection to listener, until it is closed, with what the block returns for its request's
          # headers (names in lower case) and body: [status, headers, body], or nil to close it without an answer.
          def self.serve(listener, &)
            loop { answer(listener.accept, &) }
          rescue IOError
            # The listener was closed: serving is over.
            nil
          end

          def self.answer(connection)
            request_headers, request_body = read(connection)
            reply = yield(request_headers, request_body) or return
            status, headers, body = reply
            connection.write("HTTP/1.1 #{status} #{STATUS[status]}\r\n",
                             *headers.map { |name, value| "#{name}: #{value}\r\n" },
                             "Content-Length: #{body.bytesize}\r\nConnection: close\r\n\r\n", body)
          rescue IOError, SystemCallError
            # The client went away; the next one is served.
            nil
          ensure
            connection.close
          end

          def self.read(connection)
            connection.gets("\r\n")
            headers = connection.each_line("\r\n").take_while { it != "\r\n" }.to_h do |line|
              name, value = line.chomp.split(':', 2)
              [name.to_s.downcase, value.to_s.strip]
            end
            [headers, connection.read(Integer(headers.fetch('content-length', '0'))).to_s]
          end
          private_class_method :answer, :read
        end
        private_constant :Http

        # tools are FakeMcpTools; protocol_version is the version the server answers with, by default the one the
        # client asks for.
        def initialize(tools:, protocol_version: nil)
          @tools = tools.dup.freeze
          @protocol_version = protocol_version
          @mutex = Mutex.new
          @calls = []
          @session = SecureRandom.hex
          @down = false
        end

        # The tool calls received, oldest first, each [name, arguments].
        def calls = @mutex.synchronize { @calls.dup.freeze }

        # From now on, closes each HTTP connection without an answer, as a server that went down; a tool may call it
        # to fail mid-call.
        def go_down = @mutex.synchronize { @down = true }

        # Forgets the HTTP session, as a restarted server does: a request in it then gets 404.
        def end_session = @mutex.synchronize { @session = SecureRandom.hex }

        # Serves over stdio, one JSON-RPC message per line, until input ends.
        def serve(input, output)
          input.each_line do |line|
            request = JSON.parse(line)
            output.puts(NOTIFICATION) if request['method'] == 'tools/call'
            response = answer(request)
            output.puts(JSON.generate(response)) if response
            output.flush
          end
        end

        # Serves Streamable HTTP on a free local port while the block runs, and yields the endpoint's URL. Requests
        # are answered one at a time, each on a connection of its own.
        def serve_http
          # TCPServer's signature leaves out the host and port form.
          listener = TCPServer.new('127.0.0.1', 0) # steep:ignore
          acceptor = Thread.new { Http.serve(listener) { |headers, body| reply(headers, body) } }
          begin
            yield "http://127.0.0.1:#{listener.addr[1]}/mcp"
          ensure
            listener.close
            acceptor.join
          end
        end

        private

        # The response to a JSON-RPC request, a Hash; nil for a notification.
        def answer(request)
          id = request['id'] or return
          result = result(request['method'], request['params'] || {})
          return { 'jsonrpc' => '2.0', 'id' => id, 'result' => result } if result

          { 'jsonrpc' => '2.0', 'id' => id, 'error' => { 'code' => -32_601, 'message' => 'Method or tool not found.' } }
        end

        def result(method, params)
          case method
          when 'initialize' then initialized(params)
          when 'ping' then {}
          when 'tools/list' then page(Integer(params.fetch('cursor', '0')))
          when 'tools/call' then call(params['name'], params['arguments'] || {})
          end
        end

        # nil, as for an unknown method, unless the client says what the protocol asks of it.
        def initialized(params)
          client = params['clientInfo']
          return unless params['capabilities'].is_a?(Hash) && client.is_a?(Hash) &&
                        client.values_at('name', 'version').all?(String)

          { 'protocolVersion' => @protocol_version || params['protocolVersion'],
            'capabilities' => { 'tools' => { 'listChanged' => false } },
            'serverInfo' => { 'name' => 'fake', 'version' => '1.0.0' } }
        end

        def page(index)
          tools = @tools[index, 1].to_a.map do |tool|
            { 'name' => tool.name, 'description' => tool.description, 'inputSchema' => tool.input_schema }
          end
          index + 1 < @tools.size ? { 'tools' => tools, 'nextCursor' => (index + 1).to_s } : { 'tools' => tools }
        end

        def call(name, arguments)
          @mutex.synchronize { @calls << [name, arguments].freeze }
          tool = @tools.find { it.name == name } or return
          begin
            { 'content' => [{ 'type' => 'text', 'text' => tool.handler.call(arguments) }], 'isError' => false }
          rescue StandardError => e
            # A failing tool is an error result, as a server reports it.
            { 'content' => [{ 'type' => 'text', 'text' => e.message }], 'isError' => true }
          end
        end

        def session = @mutex.synchronize { @session }

        def down? = @mutex.synchronize { @down }

        # [status, headers, body] for an HTTP request; nil while the server is down.
        def reply(headers, body)
          return if down?

          status, extra, text = handle(headers, JSON.parse(body))
          # A tool may have made the server go down while it ran.
          [status, extra, text] unless down?
        end

        def handle(headers, request)
          initializing = request['method'] == 'initialize'
          return [400, {}, ''] unless accepts?(headers) && (initializing || headers.key?('mcp-protocol-version'))
          return [404, {}, ''] unless initializing || headers['mcp-session-id'] == session

          response = answer(request) or return [202, {}, '']
          content(request['method'], JSON.generate(response))
        end

        def accepts?(headers)
          accept = headers['accept'].to_s
          accept.include?('application/json') && accept.include?('text/event-stream')
        end

        def content(method, body)
          case method
          when 'initialize'
            [200, { 'Content-Type' => 'application/json', 'Mcp-Session-Id' => session }, body]
          when 'tools/call'
            # The notification's field has no space after the colon, which the event stream format allows.
            [200, { 'Content-Type' => 'text/event-stream' }, "data:#{NOTIFICATION}\n\ndata: #{body}\n\n"]
          else
            [200, { 'Content-Type' => 'application/json' }, body]
          end
        end
      end
    end
  end
end
