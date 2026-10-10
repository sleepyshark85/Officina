# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Testing
      # The prefix stability check, as a Minitest assertion: include it in a test class.
      module PrefixAssertions
        # Fails unless each request's tools (as the model sees them), instructions, output schema and earlier messages
        # equal the previous request's, byte for byte. Pass the requests of a scripted run, or of several runs, saves
        # and resumes, in the order they were sent.
        # @param requests [Array<Request>]
        def assert_stable_prefix(requests)
          (1...requests.size).each { |at| assert_same_prefix(requests.fetch(at - 1), requests.fetch(at), at + 1) }
        end

        private

        def assert_same_prefix(previous, request, number)
          since = "request #{number}, from request #{number - 1}"
          # As named pairs: a failure's diff names the part, and the nil output schema of an agent without an output
          # type compares too, which assert_equal refuses alone.
          assert_equal fixed_parts(previous).to_a, fixed_parts(request).to_a, "The prefix changed in #{since}"
          assert_equal previous.messages, request.messages.first(previous.messages.size),
                       "An earlier message changed in #{since}"
        end

        # The parts of the prefix a request carries: what of each tool reaches the model, the instructions and the
        # output schema.
        def fixed_parts(request)
          { tools: request.tools.map { |tool| [tool.name, tool.description, tool.input_schema] },
            instructions: request.instructions, output_schema: request.output_schema }
        end
      end
    end
  end
end
