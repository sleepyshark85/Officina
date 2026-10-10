# frozen_string_literal: true

require 'test_helper'
require_relative 'support/collector'
require_relative 'support/fake_clock'

# A run of an agent with an output type: the schema it sends, and the reply read as a value of the type, or the run
# failed when the reply is not one.
class TypedOutputTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  Summary = Input.define do
    string :title, 'A title of a few words.'
    integer :copies, minimum: 0
    array :changes, of: :string
    string :note, nullable: true, optional: true
  end
  REPLY = '{"title":"Gaudy Night","copies":2,"changes":["Order 7 placed"],"note":null}'

  def test_out01_gen05_the_schema_goes_with_each_request_and_the_reply_comes_back_as_a_value_of_the_type
    model = Model.new(Model.text(REPLY))
    expected = Summary.new(title: 'Gaudy Night', copies: 2, changes: ['Order 7 placed'], note: nil)

    result = agent_of(model).run(Conversation.new, 'Summarize the session.')

    assert_equal Completed.new(text: REPLY, output: expected, model_calls: 1), result
    assert_equal [Summary.schema.to_s], model.requests.map(&:output_schema)
  end

  def test_gen05_an_agent_without_an_output_type_completes_with_text_and_sends_no_schema
    model = Model.new(Model.text(REPLY))
    agent = Agent.new(model:, instructions: 'You summarize sessions.', clock: FakeClock.new)

    result = agent.run(Conversation.new, 'Summarize the session.')

    assert_equal Completed.new(text: REPLY, model_calls: 1), result
    assert_equal [nil], model.requests.map(&:output_schema)
  end

  def test_out02_a_reply_that_does_not_match_the_schema_fails_the_run_with_every_problem
    reply = '{"title":7,"copies":-1,"changes":["Order 7 placed",null],"extra":true}'
    detail = 'The output does not match its schema: /title: must be string; /copies: must be at least 0; ' \
             '/changes/1: must be string; /extra: is not allowed'

    result = agent_of(Model.new(Model.text(reply))).run(Conversation.new, 'Summarize the session.')

    assert_equal Failed.new(reason: :invalid_output, detail:, model_calls: 1), result
  end

  def test_out02_evt04_a_reply_that_is_not_json_fails_the_run_saying_where_without_its_text
    collector = Collector.new
    model = Model.new(Model.text(%({"title":"x" Ana Lopez, 2 copies})))
    agent = Agent.new(model:, instructions: 'You summarize sessions.', output: Summary, telemetry: collector.telemetry,
                      clock: FakeClock.new)
    detail = 'The output is not JSON: it breaks off at line 1, column 14'

    result = agent.run(Conversation.new, 'Summarize the session.')

    assert_equal Failed.new(reason: :invalid_output, detail:, model_calls: 1), result
    assert_equal detail, collector.span('invoke_agent').status.description
    refute_match(/Ana/, collector.dump)
  end

  def test_out02_the_reply_is_kept_in_the_conversation_and_the_failure_audited
    sink = Testing::RecordingAuditSink.new
    conversation = Conversation.new
    agent = Agent.new(model: Model.new(Model.text('[]')), instructions: 'You summarize sessions.', output: Summary,
                      audit_sink: sink, clock: FakeClock.new)

    agent.run(conversation, 'Summarize the session.')

    assert_equal ['Summarize the session.', '[]'], conversation.messages.map(&:text)
    assert_equal ['failed: invalid_output', 'The output does not match its schema: /: must be object'],
                 [sink.entries.last.outcome, sink.entries.last.detail]
  end

  def test_out01_a_run_that_does_not_complete_is_not_read
    result = agent_of(Model.new(Model.stop(:max_tokens))).run(Conversation.new, 'Summarize the session.')

    assert_equal Stopped.new(reason: :output_limit, detail: nil, model_calls: 1), result
  end

  def test_out01_the_reply_is_read_once_its_secrets_are_redacted
    model = Model.new(Model.text(REPLY.sub('Order 7 placed', 'Paid with s3cret')))
    agent = Agent.new(model:, instructions: 'You summarize sessions.', output: Summary, secrets: ['s3cret'],
                      clock: FakeClock.new)

    result = agent.run(Conversation.new, 'Summarize the session.')

    assert_equal ['Paid with [redacted]'], result.output.changes
  end

  private

  def agent_of(model)
    Agent.new(model:, instructions: 'You summarize sessions.', output: Summary, clock: FakeClock.new)
  end
end
