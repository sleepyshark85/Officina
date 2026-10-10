# frozen_string_literal: true

require 'sleepyshark/officina'
require_relative 'mcp/error'
require_relative 'mcp/tool'
require_relative 'mcp/call_result'
require_relative 'mcp/server'
require_relative 'mcp/wire'
require_relative 'mcp/child_process'
require_relative 'mcp/stdio'
require_relative 'mcp/streamable_http'
require_relative 'mcp/client'

module Sleepyshark
  module Officina
    # Officina's MCP client: tools from MCP servers, over stdio and Streamable HTTP.
    module Mcp
      # How often a request waiting for its response checks whether it was cancelled or is late, in seconds.
      POLL = 0.05
      # The time waits are measured by, in seconds.
      MONOTONIC = -> { Process.clock_gettime(Process::CLOCK_MONOTONIC) }
      # What a transport answers a notification with, once the server has it. Steep asks a type of an empty literal,
      # which its annotation cannot give inside an expression.
      ACCEPTED = {}.freeze # steep:ignore
      private_constant :POLL, :MONOTONIC, :ACCEPTED

      # Connects to server: starts its command and speaks over the process's standard input and output, or reaches
      # its Streamable HTTP endpoint; then agrees on the protocol with it, within 30 seconds. cancel is anything with
      # a cancelled? method, checked while it waits; clock returns monotonic seconds, and is replaced only by tests.
      #
      # @raise [Mcp::Error] when the server cannot be started or reached, does not answer in time, speaks another
      #   protocol version, or cancel is cancelled; a server it started is stopped by then.
      def self.connect(server, cancel: nil, clock: MONOTONIC)
        transport = server.command ? Stdio.new(server, clock:) : StreamableHttp.new(server)
        Client.new(transport, name: server.name, cancel:, clock:)
      end
    end
  end
end
