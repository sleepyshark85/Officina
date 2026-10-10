# frozen_string_literal: true

module Bookshop
  # Bookshop Assistant, built by Bookshop.build: run it, then close it.
  class Application
    def initialize(database:, telemetry:, console:)
      @database = database
      @telemetry = telemetry
      @console = console
      freeze
    end

    # Runs the console until the staff member quits or the input ends. A reply that fails is shown, and the session
    # goes on.
    def run = @console.run

    # Closes the database's connections, and sends the telemetry not yet sent.
    def close
      @database.close
      @telemetry.close
    end
  end
end
