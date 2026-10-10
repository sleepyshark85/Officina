# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Claude
      # Credentials that are missing, wrong, or not allowed to make the call. Its cause is the API's answer, or the
      # SDK's error when it found no credentials it could use.
      class AuthenticationError < Error
      end
    end
  end
end
