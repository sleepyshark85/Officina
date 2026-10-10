# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # How to reach an MCP server: a program to start, over stdio, or a URL, over Streamable HTTP. name names the
      # server in errors and prefixes its tools' names. command is the program and its arguments, run without a shell,
      # with env (names to values; nil unsets one) added to this process's environment. url is a Streamable HTTP
      # endpoint, and headers are sent with every request to it, such as a credential.
      Server = Data.define(:name, :command, :env, :url, :headers)

      # Reopened rather than given a block, which Steep would not read as the class's body.
      class Server
        # What a tool's name may hold, which the server's name prefixes.
        NAME = /\A[A-Za-z0-9_-]+\z/
        private_constant :NAME

        # @raise [ArgumentError] when the name holds anything but ASCII letters, digits, '_' and '-', which a tool's
        #   name may hold; unless exactly one of command and url is given; when the command is empty; or when env
        #   comes without a command or headers without a url, where they would mean nothing.
        def initialize(name:, command: nil, env: nil, url: nil, headers: nil)
          unless name.match?(NAME)
            raise ArgumentError, "MCP server #{name.inspect}: a name may hold only ASCII letters, digits, _ and -"
          end

          check_reach(name, command, url, env, headers)
          command = command&.map(&:-@).freeze
          # Data's own initialize, which takes the members, has no signature Steep reads.
          super(name: -name, command:, env: frozen(env), url: url && -url, headers: frozen(headers))
        end

        # The values that may hold credentials: those of env and headers. A ToolSource redacts them from what it
        # reports; give them to the agent as secrets too, so they never reach its events, telemetry or audit trail.
        # @return [Array<String>]
        def secrets = [*env.values, *headers.values].compact.freeze

        private

        def check_reach(name, command, url, env, headers)
          stdio = !command.nil?
          raise ArgumentError, "MCP server #{name}: give either a command or a url" unless stdio ^ !url.nil?
          raise ArgumentError, "MCP server #{name}: the command is empty" if command && command.empty?
          raise ArgumentError, "MCP server #{name}: env is for a command, headers for a url" if stdio ? headers : env
        end

        # A Hash freezes its String keys itself.
        def frozen(pairs) = pairs.to_h.transform_values { it && -it }.freeze
      end
    end
  end
end
