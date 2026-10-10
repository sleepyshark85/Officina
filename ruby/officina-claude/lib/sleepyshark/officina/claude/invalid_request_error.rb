# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Claude
      # A call the API rejected as it is, or whose answer the SDK could not read: sending it again cannot help. Its
      # cause is the SDK's error.
      class InvalidRequestError < Error
      end
    end
  end
end
