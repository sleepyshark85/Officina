# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A tool call got its +result+, as the model will see it; every call gets one, whether it ran or not. The +call+'s
    # input has the agent's secrets redacted.
    ToolCallFinished = Data.define(:call, :result)
  end
end
