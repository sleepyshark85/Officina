# frozen_string_literal: true

# The new process of the prefix stability test: resumes the conversation read from standard input with the test's
# agent, then writes its result and the requests its model received, each tool as the parts of it that reach the
# model, as its handler cannot be dumped.

require 'sleepyshark/officina/testing'
require_relative 'prefix_stability_agent'

scripted = Sleepyshark::Officina::Testing::ScriptedModel
model = scripted.new(scripted.text('Resumed.'))
conversation = Sleepyshark::Officina::Conversation.from_json($stdin.read)
result = PrefixStabilityAgent.of(model).run(conversation, 'And now?', context: 'Today is Saturday.')
requests = model.requests.map do |request|
  [request.tools.map { |tool| [tool.name, tool.description, tool.input_schema] }, request.instructions,
   request.messages]
end
$stdout.binmode.write(Marshal.dump([result, requests]))
