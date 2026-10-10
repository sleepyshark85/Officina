# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A JSON schema the core cannot validate against: not JSON, or outside the subset it supports. Raised when the
    # schema is defined, so none is ever half checked.
    class SchemaError < Error
    end
  end
end
