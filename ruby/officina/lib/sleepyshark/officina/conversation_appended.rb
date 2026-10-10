# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A message the run appended to the conversation. The run waits while the host handles it, so the host can save
    # the conversation after every step.
    ConversationAppended = Data.define(:message)
  end
end
