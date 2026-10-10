# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Testing
      # The prefix stability check, as a Minitest assertion: include it in a test class.
      module PrefixAssertions
        # Fails unless each request's tools, instructions and earlier messages equal the previous request's, byte for
        # byte. Pass the requests of a scripted run, or of several runs, saves and resumes, in the order they were sent.
        # @param requests [Array<Request>]
        def assert_stable_prefix(requests)
          (1...requests.size).each { |at| assert_same_prefix(requests.fetch(at - 1), requests.fetch(at), at + 1) }
        end

        private

        def assert_same_prefix(previous, request, number)
          since = "request #{number}, from request #{number - 1}"
          assert_equal previous.tools, request.tools, "The tools changed in #{since}"
          assert_equal previous.instructions, request.instructions, "The instructions changed in #{since}"
          assert_equal previous.messages, request.messages.first(previous.messages.size),
                       "An earlier message changed in #{since}"
        end
      end
    end
  end
end
