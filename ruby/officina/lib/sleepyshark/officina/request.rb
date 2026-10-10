# frozen_string_literal: true

module Sleepyshark
  module Officina
    # One model call: the prefix, which stays the same for a conversation (the tools, sorted by name, and the frozen
    # instructions; the model's settings are its own), then the conversation's messages, ending with the run's pending
    # ones: the user's message and, as an +:operator+ message, the run context. A model must not change it.
    Request = Data.define(:tools, :instructions, :messages)
  end
end
