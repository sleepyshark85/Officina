# frozen_string_literal: true

module Sleepyshark
  module Officina
    Approval = Data.define(:approved, :reason)

    # An approver's answer about one tool call.
    class Approval
      # @param approved [Boolean]
      # @param reason [String, nil] why, for a denial; the model is told it
      def initialize(approved:, reason: nil)
        super(approved:, reason: reason && -reason)
      end

      def approved? = approved
    end
  end
end
