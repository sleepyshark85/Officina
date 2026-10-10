# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A model call failed and starts again: whatever the failed attempt streamed is void, as the reply streams anew
    # after it; the usage it reported stays counted. A model streams it, and the run passes it on to the host.
    Retried = Data.define
  end
end
