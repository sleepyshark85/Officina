# frozen_string_literal: true

require 'sleepyshark/officina'

# The agent of the prefix stability tests, defined once for the test and for the new process it resumes the
# conversation in, so a difference in the prefix can only come from the save and the resume.
module PrefixStabilityAgent
  # @param model [Sleepyshark::Officina::Testing::ScriptedModel]
  # @return [Sleepyshark::Officina::Agent]
  def self.of(model)
    schema = Sleepyshark::Officina::Schema.new('{ "type": "object" }')
    tool = Sleepyshark::Officina::Tool.new(name: 'search', description: 'Searches «the» catalogue.', input: schema,
                                           kind: :read) { 'Found.' }
    Sleepyshark::Officina::Agent.new(model:, instructions: 'You help <customers> & staff.', tools: [tool])
  end
end
