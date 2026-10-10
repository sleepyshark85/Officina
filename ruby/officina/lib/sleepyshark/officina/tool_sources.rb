# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The sources of an agent's tools, as one run uses them: it connects them before its first model call and records
    # their connection changes in the audit trail.
    class ToolSources
      # @param audit [AuditRecorder]
      def initialize(agent:, audit:)
        @sources = agent.tools.filter_map(&:source).uniq
        @audit = audit
      end

      # Connects each source, in turn, and records their changes. A source's failure is the run's to decide: it is
      # returned, as why the run cannot start; nil when every source connected, or when the run was cancelled.
      def connect(cancel)
        @sources.each do |source|
          source.connect(cancel:)
        rescue StandardError => e
          return "The tool source #{source.name} is not available: #{e}" unless cancel.cancelled?

          break
        end
        nil
      ensure
        record_changes
      end

      # Records each source's connection changes since they were last taken.
      def record_changes
        @sources.each do |source|
          source.take_changes.each do |change|
            @audit.record(:tool_source, tool: source.name, outcome: change.state.to_s, detail: change.detail)
          end
        end
      end
    end
    private_constant :ToolSources
  end
end
