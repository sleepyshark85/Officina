# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # A tool as its server lists it. The input schema is the server's JSON object, parsed and deeply frozen, its
      # keys in the server's order.
      Tool = Data.define(:name, :description, :input_schema)
    end
  end
end
