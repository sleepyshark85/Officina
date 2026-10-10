# frozen_string_literal: true

module Sleepyshark
  module Officina
    ToolSourceChange = Data.define(:state, :detail)

    # A change to a tool source's connection, which the audit trail records as its outcome.
    class ToolSourceChange
      # @param state [Symbol] +:connected+, +:failed+ (an attempt to connect failed) or +:disconnected+ (a connection
      #   was lost)
      # @param detail [String, nil] why it failed or was lost
      def initialize(state:, detail: nil)
        super(state:, detail: detail && -detail)
      end
    end
  end
end
