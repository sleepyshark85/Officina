# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The provider compacted the conversation during a model call: its summary block is among the reply's blocks, and
    # replaces what came before it for the provider. A model streams it, and the run passes it on to the host.
    # +tokens+ are the input tokens summarized, +summary_tokens+ the summary's size.
    ConversationCompacted = Data.define(:tokens, :summary_tokens)
  end
end
