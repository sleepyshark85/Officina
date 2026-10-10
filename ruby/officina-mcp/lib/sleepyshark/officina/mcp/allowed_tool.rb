# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # A tool of an MCP server that the host lets the agent use, by its name on the server, and how it runs.
      AllowedTool = Data.define(:name, :kind, :needs_approval)

      # Reopened rather than given a block, which Steep would not read as the class's body.
      class AllowedTool
        # kind is :write unless the host says :read: the server's read-only annotation is not trusted, as a write
        # wrongly marked read would run alongside other calls, and even when its attempt could not be audited.
        # needs_approval says whether the agent's approver must approve each call.
        def initialize(name:, kind: :write, needs_approval: false)
          super(name: -name, kind:, needs_approval:)
        end
      end
    end
  end
end
