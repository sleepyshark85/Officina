# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # How to reach an MCP server: a program to start, over stdio, or a URL, over Streamable HTTP. name names the
      # server in errors. command is the program and its arguments, run without a shell, with env (names to values;
      # nil unsets one) added to this process's environment. url is a Streamable HTTP endpoint, and headers are sent
      # with every request to it, such as a credential.
      Server = Data.define(:name, :command, :env, :url, :headers)

      # Reopened rather than given a block, which Steep would not read as the class's body.
      class Server
        # @raise [ArgumentError] unless exactly one of command and url is given, or when env comes without a command
        #   or headers without a url, where they would mean nothing.
        def initialize(name:, command: nil, env: nil, url: nil, headers: nil)
          stdio = !command.nil?
          raise ArgumentError, "MCP server #{name}: give either a command or a url" unless stdio ^ !url.nil?
          raise ArgumentError, "MCP server #{name}: env is for a command, headers for a url" if stdio ? headers : env

          command = command&.map(&:-@).freeze
          # Data's own initialize, which takes the members, has no signature Steep reads.
          super(name: -name, command:, env: frozen(env), url: url && -url, headers: frozen(headers))
        end

        private

        def frozen(pairs) = pairs.to_h.to_h { |key, value| [-key, value && -value] }.freeze
      end
    end
  end
end
