# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Mcp
      # A tool as its server lists it. The input schema is a JSON object's text, as the server wrote it: the prefix
      # fingerprint hashes it as it is, as every implementation does, so a conversation resumes in any of them.
      Tool = Data.define(:name, :description, :input_schema)
    end
  end
end
