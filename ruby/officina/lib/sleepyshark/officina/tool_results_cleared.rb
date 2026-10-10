# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The provider cleared old tool results from what a model call showed the model. The conversation keeps them, so
    # the provider clears and reports them again on every later call. A model streams it, and the run passes it on to
    # the host. +tokens+ are the input tokens cleared, +tool_calls+ the calls whose results went.
    ToolResultsCleared = Data.define(:tokens, :tool_calls)
  end
end
