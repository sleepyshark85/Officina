# frozen_string_literal: true

require 'test_helper'
require_relative 'support/tool_calls'

# An agent's secrets never reach tool results, the events that show tool calls, a run's result or the audit trail,
# in any form a JSON writer may give them; the conversation keeps what the model wrote (D13).
class RedactionTest < Minitest::Test
  include Sleepyshark::Officina
  include ToolCalls

  cover 'Sleepyshark::Officina*'

  SECRETS = ['hunter2', 'pa/ss"é'].freeze

  def test_evt03_secrets_are_redacted_from_results_and_the_runs_text_but_not_from_the_conversation
    tools = [tool('search') { |input, _| "#{input.title} needs hunter2 and #{JSON.generate('pa/ss"é')}" }]
    model = Model.new(Model.tool_use(call('1', 'search', '{"title":"hunter2"}')), Model.text('Done, hunter2.'))
    conversation = Conversation.new

    result = Agent.new(model:, instructions: 'You help.', tools:, secrets: SECRETS).run(conversation, 'Go')

    assert_equal 'Done, [redacted].', result.text
    assert_equal '[redacted] needs [redacted] and "[redacted]"',
                 conversation.messages[2].blocks.first.tool_result.content
    assert_includes conversation.messages[1].blocks.first.raw, 'hunter2'
  end

  def test_evt03_every_tool_event_shows_the_call_without_the_secrets
    events = []
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    run_calls(tools, call('1', 'order', '{"title":"hunter2"}'), approver: Testing::ScriptedApprover.new(true),
                                                                secrets: SECRETS) { events << it }

    calls = events.grep(ToolCallStarted) + events.grep(ApprovalAsked) + events.grep(ApprovalAnswered) +
            events.grep(ToolCallFinished)

    assert_equal(['{"title":"[redacted]"}'] * 4, calls.map { it.call.input })
  end

  def test_evt03_a_failed_runs_detail_has_no_secret
    model = Model.new([RuntimeError.new('the key hunter2 was refused')])

    result = Agent.new(model:, instructions: 'You help.', secrets: SECRETS).run(Conversation.new, 'Go')

    assert_equal 'the key [redacted] was refused', result.detail
  end

  def test_aud05_no_secret_reaches_the_audit_trail
    sink = Testing::RecordingAuditSink.new

    run_calls([tool('search') { |_, _| 'hunter2' }], call('1', 'search', '{"title":"hunter2"}'), audit_sink: sink,
                                                                                                 secrets: SECRETS)

    refute(sink.entries.any? { |entry| entry.to_h.values.join.include?('hunter2') })
  end

  def test_evt03_secrets_that_overlap_touch_or_contain_one_another_are_redacted_whole
    assert_equal 'x[redacted]y cdef[redacted] z',
                 agent_with('abc', 'abcdef', 'cdefgh', '').redact('xabcdefghy cdefabc z')
    assert_equal 'x[redacted]y', agent_with('abcdef', 'cd').redact('xabcdefy')
    assert_equal 'x[redacted]y', agent_with('ab', 'cd').redact('xabcdy')
    assert_equal '[redacted]', agent_with('aa').redact('aaa')
    assert_equal 'nothing here', agent_with('aa').redact('nothing here')
  end

  def test_evt03_every_form_a_json_writer_may_give_a_secret_is_redacted
    agent = agent_with('é<&/', 'pa"ss', 'a/b/c')
    # As written; non-ASCII escaped in lower case; .NET's escapes in upper case; .NET's in lower case with / escaped;
    # Go's; a quote as JSON writes it, and as written; every slash escaped.
    forms = ['é<&/', '\\u00e9<&/', '\\u00E9\\u003C\\u0026/', '\\u00e9\\u003c\\u0026\\/', 'é\\u003c\\u0026/',
             'pa\\"ss', 'pa"ss', 'a\\/b\\/c']

    assert_equal(['[redacted]'] * forms.size, forms.map { agent.redact(it) })
  end

  private

  def agent_with(*secrets) = Agent.new(model: Model.new, instructions: 'You help.', secrets:)
end
