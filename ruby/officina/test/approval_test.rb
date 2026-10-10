# frozen_string_literal: true

require 'test_helper'
require_relative 'support/tool_calls'

# Calls that need approval: the approver's answer, a denial, a run without an approver, and the events around it.
class ApprovalTest < Minitest::Test
  include Sleepyshark::Officina
  include ToolCalls

  cover 'Sleepyshark::Officina*'

  def test_tool04_a_denial_goes_back_to_the_model_as_the_calls_result
    ordered = []
    approver = Testing::ScriptedApprover.new(true, 'Not today.', false)
    tools = [tool('order', kind: :write, needs_approval: true) { |input, _| ordered.push(input.title).last }]

    ran = run_calls(tools, call('1', 'order'), call('2', 'order', '{"title":"Emma"}'), call('3', 'order'), approver:)

    assert_equal ['Dune'], ordered
    assert_equal ['Dune', 'The call was denied: Not today.', 'The call was denied.'], ran.results.map(&:content)
    assert_equal [false, true, true], ran.results.map(&:error?)
    assert_equal %w[1 2 3], approver.asked.map(&:id)
  end

  def test_tool04_the_approver_gets_the_tool_and_the_call_without_the_agents_secrets
    asked = []
    approver = Object.new
    approver.define_singleton_method(:approve) do |tool, call, cancel:|
      asked << [tool.name, call.input, cancel.cancelled?]
      Approval.new(approved: true)
    end
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    run_calls(tools, call('1', 'order', '{"title":"s3cret"}'), approver:, secrets: ['s3cret'])

    assert_equal [['order', '{"title":"[redacted]"}', false]], asked
  end

  def test_tool04_an_approver_that_raises_denies_the_call
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    ran = run_calls(tools, call('1', 'order'), approver: Testing::ScriptedApprover.new)

    assert_equal 'The call was denied: asking for approval failed: Scripted approver: no answer left for call 1',
                 ran.results[0].content
  end

  def test_gen04_an_unattended_run_denies_calls_that_need_approval_and_runs_the_rest
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }, tool('search') do |_, _|
      'Found.'
    end]

    ran = run_calls(tools, call('1', 'order'), call('2', 'search'))

    assert_equal 'Done.', ran.result.text
    assert_equal ['The call needs approval, and this run is unattended, so it was denied.', 'Found.'],
                 ran.results.map(&:content)
  end

  def test_tool04_a_host_may_answer_approval_from_its_events_in_either_order
    answers = Thread::Queue.new
    approver = Object.new
    approver.define_singleton_method(:approve) { |_tool, _call, **| Approval.new(approved: answers.pop(timeout: 5)) }
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    ran = run_calls(tools, call('1', 'order'), call('2', 'order'), approver:) do |event|
      answers << true if event.is_a?(ApprovalAsked)
    end

    assert_equal %w[Ordered. Ordered.], ran.results.map(&:content)
  end

  def test_agt05_cancelling_while_waiting_for_approval_denies_the_call_and_starts_no_other
    cancel = Cancellation.new
    approver = Object.new
    approver.define_singleton_method(:approve) do |_tool, _call, cancel:|
      sleep 0.001 until cancel.cancelled?
      Approval.new(approved: true)
    end
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    ran = run_calls(tools, call('1', 'order'), call('2', 'order'), approver:, cancel:) do |event|
      cancel.cancel if event.is_a?(ApprovalAsked)
    end

    assert_equal ['The call was cancelled before it started.'] * 2, ran.results.map(&:content)
  end

  def test_agt05_a_denial_while_the_run_is_cancelled_says_so
    cancel = Cancellation.new
    approver = Object.new
    approver.define_singleton_method(:approve) do |_tool, _call, cancel:|
      sleep 0.001 until cancel.cancelled?
      Approval.new(approved: false, reason: 'No.')
    end
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    ran = run_calls(tools, call('1', 'order'), approver:, cancel:) do |event|
      cancel.cancel if event.is_a?(ApprovalAsked)
    end

    assert_equal :cancelled, ran.result.reason
    assert_equal ['The call was cancelled while waiting for approval.'], ran.results.map(&:content)
  end

  def test_evt01_a_calls_events_come_started_asked_answered_finished_then_its_results_are_appended
    events = []
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    run_calls(tools, call('1', 'order'), approver: Testing::ScriptedApprover.new(true)) { events << it }

    kinds = events.map { it.is_a?(ConversationAppended) ? it.message.role : it.class }

    assert_equal [:user, :assistant, ToolCallStarted, ApprovalAsked, ApprovalAnswered, ToolCallFinished, :user,
                  TextDelta, :assistant], kinds
    assert_equal [true, '1'], [events[4].approved, events[5].result.call_id]
  end

  def test_evt01_a_denied_call_is_reported_as_not_approved
    events = []
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    run_calls(tools, call('1', 'order'), approver: Testing::ScriptedApprover.new(false)) { events << it }

    assert_equal [false], events.grep(ApprovalAnswered).map(&:approved)
  end

  def test_tool04_an_approval_keeps_a_frozen_copy_of_its_reason
    reason = +'Not today.'

    approval = Approval.new(approved: false, reason:)
    reason << '!'

    assert_equal Approval.new(approved: false, reason: 'Not today.'), approval
    assert_predicate approval.reason, :frozen?
  end
end
