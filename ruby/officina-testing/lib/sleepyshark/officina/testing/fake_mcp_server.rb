# frozen_string_literal: true

require 'json'
require 'securerandom'
require 'socket'

module Sleepyshark
  module Officina
    module Testing
      # An MCP server with tools given in advance, over stdio (#serve, run by a program the test starts) or Streamable
      # HTTP on a local port (#serve_http). It lists one tool per page, sends a notification before each tool's result
      # (over HTTP, in an event stream) that clients must skip, and records the calls. Over HTTP it requires the
      # headers the protocol does (both content types accepted; the protocol version and the session after
      # initializing), stricter than a real server. It can go down, or end its session.
      class FakeMcpServer
        NOTIFICATION = JSON.generate({ 'jsonrpc' => '2.0', 'method' => 'notifications/message',
                                       'params' => { 'level' => 'info', 'data' => 'working' } })
        private_constant :NOTIFICATION

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
          acceptor = Thread.new { Http.new(self).accept(listener) }
          begin
            yield "http://127.0.0.1:#{listener.addr[1]}/mcp"
          ensure
            listener.close
            acceptor.join
          end
        end

        # The response to a JSON-RPC request, a Hash; nil for a notification. For the HTTP front.
        def answer(request)
          id = request['id'] or return
          result = result(request['method'], request['params'] || {})
          return { 'jsonrpc' => '2.0', 'id' => id, 'result' => result } if result

          { 'jsonrpc' => '2.0', 'id' => id, 'error' => { 'code' => -32_601, 'message' => 'Method or tool not found.' } }
        end

        # The current HTTP session's id. For the HTTP front.
        def session = @mutex.synchronize { @session }

        # Whether the server went down. For the HTTP front.
        def down? = @mutex.synchronize { @down }

        private

        def result(method, params)
          case method
          when 'initialize' then initialized(params)
          when 'ping' then {}
          when 'tools/list' then page(Integer(params.fetch('cursor', '0')))
          when 'tools/call' then call(params['name'], params['arguments'] || {})
          end
        end

        def initialized(params)
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

        # The HTTP front: reads each request from a connection, and writes the server's answer as Streamable HTTP
        # does.
        class Http
          STATUS = { 200 => 'OK', 202 => 'Accepted', 400 => 'Bad Request', 404 => 'Not Found' }.freeze

          def initialize(server)
            @server = server
          end

          # Answers connections until the listener is closed.
          def accept(listener)
            loop { serve(listener.accept) }
          rescue IOError
            # The listener was closed: serving is over.
            nil
          end

          private

          def serve(connection)
            respond(connection)
          rescue IOError, SystemCallError
            # The client went away; the next one is served.
            nil
          ensure
            connection.close
          end

          def respond(connection)
            headers, body = read(connection)
            return if @server.down?

            status, extra, text = reply(headers, JSON.parse(body))
            return if @server.down?

            connection.write("HTTP/1.1 #{status} #{STATUS[status]}\r\n",
                             *extra.map { |name, value| "#{name}: #{value}\r\n" },
                             "Content-Length: #{text.bytesize}\r\nConnection: close\r\n\r\n", text)
          end

          def read(connection)
            connection.gets("\r\n")
            headers = connection.each_line("\r\n").take_while { it != "\r\n" }.to_h do |line|
              name, value = line.chomp.split(':', 2)
              [name.to_s.downcase, value.to_s.strip]
            end
            [headers, connection.read(Integer(headers.fetch('content-length', '0'))).to_s]
          end

          # [status, headers, body] for a request.
          def reply(headers, request)
            initializing = request['method'] == 'initialize'
            return [400, {}, ''] unless accepts?(headers) && (initializing || headers.key?('mcp-protocol-version'))
            return [404, {}, ''] unless initializing || headers['mcp-session-id'] == @server.session

            response = @server.answer(request) or return [202, {}, '']
            content(request['method'], JSON.generate(response))
          end

          def accepts?(headers)
            accept = headers['accept'].to_s
            accept.include?('application/json') && accept.include?('text/event-stream')
          end

          def content(method, body)
            case method
            when 'initialize'
              [200, { 'Content-Type' => 'application/json', 'Mcp-Session-Id' => @server.session }, body]
            when 'tools/call'
              # The notification's field has no space after the colon, which the event stream format allows.
              [200, { 'Content-Type' => 'text/event-stream' }, "data:#{NOTIFICATION}\n\ndata: #{body}\n\n"]
            else
              [200, { 'Content-Type' => 'application/json' }, body]
            end
          end
        end
        private_constant :Http
      end
    end
  end
end
