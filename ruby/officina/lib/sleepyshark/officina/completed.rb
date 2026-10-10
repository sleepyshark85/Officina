# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A run that ended with the model's answer: the final reply's +text+, and the +usage+ of all its model calls.
    Completed = Data.define(:text, :usage)
  end
end
