# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Claude
      # An API key that is missing, wrong, or not allowed to make the call. Its cause is the API's answer.
      class AuthenticationError < Error
      end
    end
  end
end
