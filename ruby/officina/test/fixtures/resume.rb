# frozen_string_literal: true

# The new process of the prefix stability test: resumes the conversation read from standard input with the test's
# agent, then writes its result and the requests its model received.

require 'sleepyshark/officina/testing'
require_relative 'resumed_agent'

scripted = Sleepyshark::Officina::Testing::ScriptedModel
model = scripted.new(scripted.text('Resumed.'))
conversation = Sleepyshark::Officina::Conversation.from_json($stdin.read)
result = ResumedAgent.of(model).run(conversation, 'And now?', context: 'Today is Saturday.')
$stdout.binmode.write(Marshal.dump([result, model.requests]))
