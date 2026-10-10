# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # An MCP server's allowed tools, as tools of the core: it reads the server's tool list once, keeps the tools the
      # host allows, names each <server>__<tool> and pins them, so #tools stays the same for every conversation. It is
      # their tool source: each run checks the connection and reconnects a server that was lost, and fails if it
      # cannot; a server lost during a run gives error results for its calls. The server's credentials (see
      # Server#secrets) never leave it: its results and the reasons it reports have them redacted. Many runs may share
      # it. Close it when done.
      #
      # Mutation testing leaves out the methods that only take its locks: a test cannot make two threads meet inside a
      # lock under the VM lock.
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
        # @raise [Mcp::Error] when the server cannot be started or reached, does not answer, speaks another protocol
        #   version, lacks an allowed tool or gives one a schema the core cannot validate against; the server is
        #   stopped by then
        # @raise [Officina::Error] when an allowed tool's kind is not :read or :write
        def initialize(server, allowed:)
          @server = server
          @secrets = Secrets.new(server.secrets)
          # Guards the client, the client whose loss is recorded (so a loss many calls meet is recorded once) and
          # whether it is closed. @connecting lets one connect or close at a time replace the client.
          @mutex = Mutex.new
          @connecting = Mutex.new
          # Each change is pushed once and taken once.
          @changes = Queue.new
          @tools = pin(connect_client(nil), allowed)
        end

        # @return [String] the server's name, which prefixes its tools' names
        def name = @server.name

        # Checks that the server still answers, and reconnects one that does not; the tools stay pinned. Each run
        # calls it before its first model call.
        # @param cancel [#cancelled?]
        # @raise [Mcp::Error] when the server cannot be reached again, cancel is cancelled, or the source is closed
        # mutant:disable -- see the class: the lock, which keeps two runs from each starting a server
        def connect(cancel:) = @connecting.synchronize { reconnect(cancel) }

        # @return [Array<ToolSourceChange>] the connection changes since it was last called, oldest first
        def take_changes
          # @type var taken: Array[ToolSourceChange]
          taken = []
          while (change = @changes.pop(timeout: 0))
            taken << change
          end
          taken
        end

        # Closes the connection (see Client#close); its tools then give error results, and runs fail to connect it.
        # mutant:disable -- see the class: the locks
        def close
          @connecting.synchronize do
            @mutex.synchronize { @closed = true }
            drop
          end
        end

        private

        def reconnect(cancel)
          raise Error, "MCP server #{name}: the tool source is closed" if closed?

          client = current
          return if client && answers?(client, cancel)

          drop
          connect_client(cancel)
        end

        # mutant:disable -- see the class: the lock
        def current = @mutex.synchronize { @client }

        # mutant:disable -- see the class: the lock
        def closed? = @mutex.synchronize { @closed }

        # Makes client the current one, and returns the one it replaces.
        # mutant:disable -- see the class: the lock
        def swap(client) = @mutex.synchronize { @client.tap { @client = client } }

        # Connects a new client, and records whether it did; one whose connect was cancelled records nothing.
        def connect_client(cancel)
          client = Mcp.connect(@server, cancel:)
        rescue Error => e
          change(:failed, e.message) unless cancel&.cancelled?
          raise
        else
          swap(client)
          change(:connected)
          client
        end

        # Whether the client answers a ping; one that does not is lost, unless the ping was cancelled.
        def answers?(client, cancel)
          client.ping(cancel:)
          true
        rescue Error => e
          raise if cancel.cancelled?

          lost(client, e.message)
          false
        end

        # Records the client's loss, once however many calls meet it.
        # mutant:disable -- see the class: the lock
        def lost(client, reason)
          @mutex.synchronize do
            return if @lost_client.equal?(client)

            @lost_client = client
          end
          change(:disconnected, reason)
        end

        # Closes the client, if any, once calls no longer find it.
        def drop = swap(nil)&.close

        def change(state, detail = nil)
          @changes << ToolSourceChange.new(state:, detail: detail && @secrets.redact(detail))
        end

        # Pins the core's tool for each allowed tool of the server's list; on any failure, closes the connection.
        def pin(client, allowed)
          # @type var pinned: Array[Officina::Tool]?
          listed = client.list_tools.to_h { [it.name, it] }
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

        # The tool's schema, kept as the server wrote it.
        def schema(tool)
          Schema.new(tool.input_schema)
        rescue SchemaError => e
          raise Error, "MCP server #{name}'s tool #{tool.name} cannot be used: #{e}"
        end

        # Calls the tool on the server, on a call's thread.
        def call(tool, arguments, cancel)
          client = current or return ToolFailure.new(message: "MCP server #{name} is not connected")
          answer(client, tool, arguments, cancel)
        end

        # The client's answer to the call: the tool's error is an error result, as is a call that failed, cancelled or
        # met a server that was lost during it.
        def answer(client, tool, arguments, cancel)
          result = client.call_tool(tool, arguments, cancel:)
          text = @secrets.redact(result.text)
          result.error? ? ToolFailure.new(message: text) : text
        rescue Error => e
          lost(client, e.message) if client.lost?
          ToolFailure.new(message: @secrets.redact(e.message))
        end
      end
    end
  end
end
