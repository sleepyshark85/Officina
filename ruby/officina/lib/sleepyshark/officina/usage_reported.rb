# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The Usage a model call reported since its previous report: reports are increments. A model streams it, and the
    # run passes it on to the host.
    UsageReported = Data.define(:usage)
  end
end
