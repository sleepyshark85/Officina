# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A piece of the reply's text, for display as it streams: a model streams it, and the run passes it on to the host.
    # The complete block comes with the reply.
    TextDelta = Data.define(:text)
  end
end
