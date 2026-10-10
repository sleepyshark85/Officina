# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Claude
      # A call the API rejected as it is, which sending again cannot help. Its cause is the API's answer.
      class InvalidRequestError < Error
      end
    end
  end
end
