# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    module Mcp
      # An MCP server's allowed tools, as tools of the core: it reads the server's tool list once, keeps the tools the
      # host allows, names each <server>__<tool> and pins them, so #tools stays the same for every conversation. It is
      # their tool source: each run checks the connection and reconnects a server that was lost, and fails if it
      # cannot; a server lost during a run gives error results for its calls. Many runs may share it. Close it when
      # done.
      #
      # @example
      #   source = Mcp::ToolSource.new(Mcp::Server.new(name: 'files', url:),
      #                                allowed: [Mcp::AllowedTool.new(name: 'write_file', needs_approval: true)])
      #   agent = Agent.new(model:, instructions:, tools: [*shop_tools, *source.tools])
      class ToolSource
        # @return [Array<Officina::Tool>] the allowed tools, in the order they were allowed
        attr_reader :tools

        # Connects to server, and pins its allowed tools.
        # @param allowed [Array<AllowedTool>]
        # @param cancel [#cancelled?, nil] checked while it connects
        # @raise [Mcp::Error] when the server cannot be started or reached, does not answer, speaks another protocol
        #   version, lacks an allowed tool or gives one a schema the core cannot validate against; the server is
        #   stopped by then
        # @raise [Officina::Error] when an allowed tool's kind is not :read or :write
        def initialize(server, allowed:, cancel: nil)
          @server = server
          # Guards the state below; @connecting lets one connect or close at a time check or replace the client.
          @mutex = Mutex.new
          @connecting = Mutex.new
          @client = nil
          @changes = []
          # The client whose loss is recorded, so a loss many calls meet is recorded once.
          @lost_client = nil
          @closed = false
          @tools = pin(connect_client(cancel), allowed, cancel)
        end

        # @return [String] the server's name, which prefixes its tools' names
        def name = @server.name

        # Checks that the server still answers, and reconnects one that does not; the tools stay pinned. Each run
        # calls it before its first model call.
        # @param cancel [#cancelled?, nil]
        # @raise [Mcp::Error] when the server cannot be reached again, cancel is cancelled, or the source is closed
        def connect(cancel: nil)
          @connecting.synchronize do
            raise Error, "MCP server #{name}: the tool source is closed" if closed?

            client = current
            return if client && answers?(client, cancel)

            drop
            connect_client(cancel)
          end
        end

        # @return [Array<ToolSourceChange>] the connection changes since it was last called, oldest first
        def take_changes
          @mutex.synchronize do
            taken = @changes
            @changes = []
            taken.freeze
          end
        end

        # Closes the connection (see Client#close); its tools then give error results, and runs fail to connect it.
        def close
          @connecting.synchronize do
            @mutex.synchronize { @closed = true }
            drop
          end
        end

        private

        def current = @mutex.synchronize { @client }

        def closed? = @mutex.synchronize { @closed }

        # Connects a new client, and records whether it did; one whose connect was cancelled records nothing.
        def connect_client(cancel)
          client = Mcp.connect(@server, cancel:)
        rescue Error => e
          change(:failed, e.message) unless cancel&.cancelled?
          raise
        else
          @mutex.synchronize { @client = client }
          change(:connected, nil)
          client
        end

        # Whether the client answers a ping; one that does not is lost, unless the ping was cancelled.
        def answers?(client, cancel)
          client.ping(cancel:)
          true
        rescue Error => e
          raise if cancel&.cancelled?

          lost(client, e.message)
          false
        end

        # Records the client's loss, once, while it is the source's.
        def lost(client, reason)
          @mutex.synchronize do
            if @client.equal?(client) && !@lost_client.equal?(client)
              @lost_client = client
              @changes << ToolSourceChange.new(state: :disconnected, detail: reason)
            end
          end
        end

        # Closes the client, if any, once calls no longer find it.
        def drop
          client = @mutex.synchronize { @client.tap { @client = nil } }
          client&.close
        end

        def change(state, detail) = @mutex.synchronize { @changes << ToolSourceChange.new(state:, detail:) }

        # Pins the core's tool for each allowed tool of the server's list; on any failure, closes the connection.
        def pin(client, allowed, cancel)
          # @type var pinned: Array[Officina::Tool]?
          pinned = nil
          listed = client.list_tools(cancel:).to_h { [it.name, it] }
          pinned = allowed.map { pinned_tool(it, listed) }.freeze
        ensure
          close unless pinned
        end

        def pinned_tool(allowed, listed)
          tool = listed.fetch(allowed.name) { missing(allowed, listed) }
          Officina::Tool.new(name: "#{name}__#{tool.name}", description: tool.description, input: schema(tool),
                             kind: allowed.kind, needs_approval: allowed.needs_approval,
                             source: self) { |input, cancel| call(tool.name, input, cancel) }
        end

        def missing(allowed, listed)
          raise Error, "MCP server #{name} has no tool #{allowed.name}; it has: #{listed.keys.sort.join(', ')}"
        end

        def schema(tool)
          Schema.new(JSON.generate(tool.input_schema))
        rescue SchemaError => e
          raise Error, "MCP server #{name}'s tool #{tool.name} cannot be used: #{e.message}"
        end

        # Calls the tool on the server, on a call's thread: the tool's error is an error result, as is a server that is
        # not connected or is lost during the call; any other failure raises, which the run makes an error result too.
        def call(tool, arguments, cancel)
          client = current or return ToolFailure.new(message: "MCP server #{name} is not connected")
          result = client.call_tool(tool, arguments, cancel:)
          result.error? ? ToolFailure.new(message: result.text) : result.text
        rescue Error => e
          raise unless client&.lost?

          lost(client, e.message)
          ToolFailure.new(message: e.message)
        end
      end
    end
  end
end
