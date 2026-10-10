# frozen_string_literal: true

require 'test_helper'
require_relative 'fixtures/shared_session'

# The sessions the implementations share in testdata/session, each saved while its last reply's tool ran: Ruby writes
# its own byte for byte as stored, and resumes .NET's and Go's, which it never rewrites.
class SharedSessionTest < Minitest::Test
  include Sleepyshark::Officina
  include Testing::PrefixAssertions

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  INTERRUPTED = 'The call was interrupted: the application stopped before its result was recorded, so it may or ' \
                'may not have taken effect.'
  # Each implementation's session, and the call its crash left without a result.
  SAVED = { 'dotnet-session.json' => 'toolu_02', 'go-session.json' => 'go_02', 'ruby-session.json' => 'rb_02' }.freeze

  def test_app10_a_session_saved_mid_reply_is_written_as_the_shared_fixture_holds_it
    assert_equal SharedSession.file('ruby-session.json').chomp, SharedSession.saved_mid_reply
  end

  def test_app10_ctx04_a_session_each_implementation_saved_mid_reply_resumes_with_its_prefix_and_the_call_answered
    SAVED.each do |name, call_id|
      conversation = Conversation.from_json(SharedSession.file(name))
      stored = conversation.messages
      model = SharedSession.model(Model.text('The order may not have gone through.'))

      result = SharedSession.agent(model).run(conversation, 'Did the order go through?')

      assert_equal 'The order may not have gone through.', result.text, name
      request = model.requests.first
      # The stored messages, raw blocks included, go out as stored: the prefix the saving request cached.
      assert_stable_prefix [request.with(messages: stored), request]
      assert_equal [ToolResult.new(call_id:, content: INTERRUPTED, error: true)],
                   request.messages[stored.size].blocks.map(&:tool_result), name
    end
  end
end
