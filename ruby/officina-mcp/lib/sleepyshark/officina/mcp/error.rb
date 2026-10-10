# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # An MCP server that could not be started or reached, went away, answered with an error or not in time; or a
      # request cancelled. The message names the server and says which.
      class Error < Officina::Error
      end
    end
  end
end
