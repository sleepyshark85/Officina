# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Claude
      # Claude's server-side context management: what a request asks for, and what a reply says it did.
      module ContextEditing
        CLEARING = 'clear_tool_uses_20250919'
        CLEARING_BETA = 'context-management-2025-06-27'
        COMPACTION = 'compact_20260112'
        COMPACTION_BETA = 'compact-2026-01-12'
        # The input of an iteration, which a compaction summarized.
        SUMMARIZED_INPUT = %i[input_tokens cache_read_input_tokens cache_creation_input_tokens].freeze

        # The request parameters of the context management, with the betas it needs: clearing, then threshold
        # compaction, never the on-demand kind, which has the client drop the compacted messages. None when it asks for
        # nothing.
        # @param settings [ContextManagement, nil]
        # @return [Hash{Symbol => Object}]
        def self.params(settings)
          edits = [clearing(settings&.clear_tool_results), compaction(settings&.compact_at)].compact
          return {} if edits.empty?

          { context_management: { edits: edits.map(&:first) }, betas: edits.map(&:last) }
        end

        # What the provider did to shorten the conversation for the reply: a compaction from the reply's compaction
        # iteration, which read what it summarized and wrote the summary; a clearing from the edits the API applied,
        # which do not include compaction.
        # @param message [Anthropic::Models::Beta::BetaMessage] the whole reply, as the SDK gathered it
        # @return [Array<ConversationCompacted, ToolResultsCleared>]
        def self.reported(message)
          compactions(message) + clearings(message)
        end

        def self.compactions(message)
          (message.usage[:iterations] || []).filter_map do |iteration|
            next unless iteration[:type] == :compaction

            read = SUMMARIZED_INPUT.sum { iteration[it] }
            ConversationCompacted.new(tokens: read, summary_tokens: iteration[:output_tokens])
          end
        end

        def self.clearings(message)
          (message[:context_management]&.[](:applied_edits) || []).filter_map do |edit|
            next unless edit[:type].to_s == CLEARING

            ToolResultsCleared.new(tokens: edit[:cleared_input_tokens], tool_calls: edit[:cleared_tool_uses])
          end
        end

        # The clearing edit and its beta, or nil for none. Clearing at least nothing is the API's default, so it is
        # left out. The SDK writes the only type a setting's count may have (+tool_uses+ for what it keeps,
        # +input_tokens+ for how much it clears), not the trigger's, which has two.
        def self.clearing(clearing)
          return unless clearing

          edit = { type: CLEARING, trigger: { type: :tool_uses, value: clearing.after },
                   keep: { value: clearing.keep } }
          at_least = clearing.at_least_tokens
          edit[:clear_at_least] = { value: at_least } if at_least.positive?
          [edit, CLEARING_BETA]
        end

        # The compaction edit and its beta, or nil for none. The SDK writes its trigger's only type, +input_tokens+.
        def self.compaction(threshold)
          [{ type: COMPACTION, trigger: { value: threshold } }, COMPACTION_BETA] if threshold
        end
        private_class_method :compactions, :clearings, :clearing, :compaction
      end
      private_constant :ContextEditing
    end
  end
end
