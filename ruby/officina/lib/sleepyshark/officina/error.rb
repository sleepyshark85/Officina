# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The base of every error Officina raises, each for an API misused or an environment broken: a run's outcome is a
    # result, never an error.
    class Error < StandardError
    end
  end
end
