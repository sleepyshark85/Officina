# frozen_string_literal: true

module Sleepyshark
  module Officina
    ToolFailure = Data.define(:message)

    # What a tool's handler returns to fail the call on purpose, such as a request its application refuses: the model
    # gets an error result with the message as written. A handler that raises fails the call too, but as something
    # unexpected, so the model reads that the tool failed.
    #
    # @example
    #   next ToolFailure.new(message: 'Not enough stock.') if stock < quantity
    class ToolFailure
      # @param message [String] what the model reads: why the call failed, and what it can do instead
      def initialize(message:)
        super(message: -message)
      end
    end
  end
end
