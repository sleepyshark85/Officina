# frozen_string_literal: true

module Sleepyshark
  module Officina
    ContextManagement = Data.define(:compact_at, :clear_tool_results)

    # How the provider shortens a long conversation on its side: it compacts the conversation into a summary block, and
    # clears old tool results from what each request shows the model. The core never edits the conversation itself.
    # It is part of the prefix, so it is fixed for a conversation.
    class ContextManagement
      # @param compact_at [Integer, nil] compacts once a request's input reaches this many tokens; nil for never.
      #   Claude's minimum is 50,000
      # @param clear_tool_results [ToolResultClearing, nil] nil for never
      # @raise [Error] when +compact_at+ is not positive
      def initialize(compact_at: nil, clear_tool_results: nil)
        unless compact_at.nil? || compact_at.positive?
          raise Error, "The compaction threshold must be positive, not #{compact_at}"
        end

        super
      end

      # Its part of the prefix fingerprint, as .NET writes it: +,"contextManagement":{"compactAt":…,"clearAfter":…,
      # "clearKeep":…,"clearAtLeastTokens":…}+, each setting only when it is set; nothing when it asks for nothing.
      # @return [String]
      def fingerprint
        parts = [(%("compactAt":#{compact_at}) if compact_at), clear_tool_results&.fingerprint].compact
        parts.empty? ? '' : %(,"contextManagement":{#{parts.join(',')}})
      end
    end
  end
end
