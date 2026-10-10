# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The run began handling a tool call: its approval, if it needs one, then the tool. The +call+'s input has the
    # agent's secrets redacted.
    ToolCallStarted = Data.define(:call)
  end
end
