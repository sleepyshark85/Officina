# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # What a tool call returned: its content as text, one line per item (an item that is not text shows as
      # "[<type> content]"), and whether the tool reports it as an error.
      CallResult = Data.define(:text, :error)

      # Reopened rather than given a block, which Steep would not read as the class's body.
      class CallResult
        # Whether the tool reported its result as an error.
        def error? = error
      end
    end
  end
end
