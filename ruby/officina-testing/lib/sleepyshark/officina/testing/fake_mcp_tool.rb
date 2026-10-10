# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Testing
      # A tool of a FakeMcpServer. Its handler is called with a call's arguments, a Hash parsed from JSON; the String
      # it returns is the result, and an exception it raises is an error result with the exception's message.
      FakeMcpTool = Data.define(:name, :handler, :description, :input_schema)

      # Reopened rather than given a block, which Steep would not read as the class's body.
      class FakeMcpTool
        # The description is "The <name> tool." unless given; the input schema, an object with a string text.
        def initialize(name:, handler:, description: "The #{name} tool.",
                       input_schema: Ractor.make_shareable({ 'type' => 'object',
                                                             'properties' => { 'text' => { 'type' => 'string' } } }))
          super
        end
      end
    end
  end
end
