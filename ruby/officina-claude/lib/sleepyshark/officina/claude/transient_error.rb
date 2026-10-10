# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Claude
      # A rate limit, overload, server or network failure that lasted through every attempt of a call. Its cause is
      # the last attempt's failure.
      class TransientError < Error
      end
    end
  end
end
