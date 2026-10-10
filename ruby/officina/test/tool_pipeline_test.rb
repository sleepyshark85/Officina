# frozen_string_literal: true

require 'test_helper'

# One reply's tool calls through a run: validation, approval, failures, concurrency, order, truncation, redaction and
# cancellation.
class ToolPipelineTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  Search = Input.define { string :title, 'Part of the title.' }
  TOOL_EVENTS = [ToolCallStarted, ApprovalAsked, ApprovalAnswered, ToolCallFinished].freeze

  def test_tool01_each_call_runs_its_tool_and_the_model_reads_the_results
    tools = [tool('search') { |input, _| "Found #{input.title}." }, tool('count', kind: :write) { |_, _| 3 }]

    result = run_calls(tools, call('1', 'search'), call('2', 'count'))

    assert_equal Completed.new(text: 'Done.', usage: Usage.new), result
    assert_equal([['Found Dune.', false], ['3', false]], results.map { [it.content, it.error?] })
    assert_equal @conversation.messages.first(3), @model.requests.last.messages
  end

  def test_tool02_invalid_input_comes_back_as_an_error_result_and_the_handler_never_runs
    ran = []
    tools = [tool('search') { |input, _| ran << input }]

    result = run_calls(tools, call('1', 'search', '{"title":1}'), call('2', 'search', '{"title":'),
                       call('3', 'lookup'))

    assert_equal ['Done.', []], [result.text, ran]
    assert_equal "The input does not match the tool's schema:\n/title: must be string", results[0].content
    assert_equal 'There is no tool named lookup.', results[2].content
    assert_match(/\AThe input is not valid JSON: /, results[1].content)
    assert(results.all?(&:error?))
  end

  def test_tool05_a_handler_that_raises_gives_an_error_result_and_the_run_goes_on
    tools = [tool('search') { |_, _| raise IOError, 'the catalogue is down' }]

    result = run_calls(tools, call('1', 'search'))

    assert_equal 'Done.', result.text
    assert_equal ToolResult.new(call_id: '1', content: 'The tool failed: the catalogue is down', error: true),
                 results[0]
  end

  def test_tool04_a_denial_goes_back_to_the_model_as_the_calls_result
    ran = []
    approver = Testing::ScriptedApprover.new(true, 'Not today.', false)
    tools = [tool('order', kind: :write, needs_approval: true) { |input, _| ran.push(input.title).last }]

    run_calls(tools, call('1', 'order'), call('2', 'order', '{"title":"Emma"}'), call('3', 'order'), approver:)

    assert_equal ['Dune'], ran
    assert_equal ['Dune', 'The call was denied: Not today.', 'The call was denied.'], results.map(&:content)
    assert_equal [false, true, true], results.map(&:error?)
    assert_equal %w[1 2 3], approver.asked.map(&:id)
  end

  def test_tool04_an_approver_that_raises_denies_the_call
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    run_calls(tools, call('1', 'order'), approver: Testing::ScriptedApprover.new)

    assert_equal 'The call was denied: asking for approval failed: Scripted approver: no answer left for call 1',
                 results[0].content
  end

  def test_gen04_an_unattended_run_denies_calls_that_need_approval_and_runs_the_rest
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }, tool('search') do |_, _|
      'Found.'
    end]

    result = run_calls(tools, call('1', 'order'), call('2', 'search'))

    assert_equal 'Done.', result.text
    assert_equal ['The call needs approval, and this run is unattended, so it was denied.', 'Found.'],
                 results.map(&:content)
  end

  def test_tool03_reads_overlap_and_a_write_waits_for_every_call_before_it_and_runs_alone
    log = Thread::Queue.new
    arrived = { 'a' => Thread::Queue.new, 'b' => Thread::Queue.new }
    meet = lambda do |name, other|
      tool(name) do |_, _|
        log << [:start, name]
        arrived.fetch(name) << true
        met = arrived.fetch(other).pop(timeout: 5)
        log << [:end, name]
        met ? 'Met.' : 'Alone.'
      end
    end
    logged = ->(name) { tool(name, kind: name == 'w' ? :write : :read) { |_, _| (log << [:run, name]) && 'Done.' } }

    run_calls([meet['a', 'b'], meet['b', 'a'], logged['w'], logged['c']], *%w[a b w c].map { call(it, it) })

    order = Array.new(log.size) { log.pop }

    assert_equal %w[Met. Met.], results.first(2).map(&:content)
    assert_operator order.index([:end, 'a']), :<, order.index([:run, 'w'])
    assert_operator order.index([:end, 'b']), :<, order.index([:run, 'w'])
    assert_equal [[:run, 'w'], [:run, 'c']], order.last(2)
  end

  def test_ctx06_results_of_one_reply_return_in_one_message_in_call_order
    second_done = Thread::Queue.new
    tools = [tool('first') { |_, _| second_done.pop(timeout: 5) && 'First.' },
             tool('second') { |_, _| (second_done << true) && 'Second.' }]
    finished = []

    run_calls(tools, call('1', 'first'), call('2', 'second')) do |event|
      finished << event.call.id if event.is_a?(ToolCallFinished)
    end

    assert_equal %w[2 1], finished
    assert_equal %i[user assistant user assistant], @conversation.messages.map(&:role)
    assert_equal([%w[1 First.], %w[2 Second.]], results.map { [it.call_id, it.content] })
  end

  def test_tool06_a_result_over_64000_characters_is_cut_with_a_note_and_one_at_the_limit_is_not
    tools = [tool('long') { |_, _| 'é' * 70_000 }, tool('limit') { |_, _| 'x' * 64_000 }]

    run_calls(tools, call('1', 'long'), call('2', 'limit'))

    assert_equal "#{'é' * 64_000}\n[Truncated: the result had 70000 characters; only the first 64000 are shown.]",
                 results[0].content
    assert_equal 'x' * 64_000, results[1].content
  end

  def test_evt01_a_calls_events_come_started_asked_answered_finished_then_its_results_are_appended
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]
    events = []

    run_calls(tools, call('1', 'order'), approver: Testing::ScriptedApprover.new(true)) { events << it }

    kinds = events.map { it.is_a?(ConversationAppended) ? it.message.role : it.class }

    assert_equal [:user, :assistant, *TOOL_EVENTS, :user, TextDelta, :assistant], kinds
    assert_equal [true, '1'], [events[4].approved, events[5].result.call_id]
  end

  def test_tool04_a_host_may_answer_approval_from_its_events_in_either_order
    answers = Thread::Queue.new
    approver = Object.new
    approver.define_singleton_method(:approve) do |_tool, _call, cancel:|
      Approval.new(approved: answers.pop(timeout: 5) && !cancel.cancelled?)
    end
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    run_calls(tools, call('1', 'order'), call('2', 'order'), approver:) do |event|
      answers << true if event.is_a?(ApprovalAsked)
    end

    assert_equal %w[Ordered. Ordered.], results.map(&:content)
  end

  def test_agt05_cancelling_while_waiting_for_approval_denies_the_call
    cancel = Cancellation.new
    approver = Object.new
    approver.define_singleton_method(:approve) do |_tool, _call, cancel:|
      sleep 0.001 until cancel.cancelled?
      Approval.new(approved: false)
    end
    tools = [tool('order', kind: :write, needs_approval: true) { |_, _| 'Ordered.' }]

    result = run_calls(tools, call('1', 'order'), call('2', 'order'), approver:, cancel:) do |event|
      cancel.cancel if event.is_a?(ApprovalAsked)
    end

    assert_equal :cancelled, result.reason
    assert_equal ['The call was cancelled while waiting for approval.', 'The call was cancelled before it started.'],
                 results.map(&:content)
    assert_equal 1, @model.requests.size
  end

  def test_agt05_cancelling_while_tools_run_stops_them_and_answers_every_call
    cancel = Cancellation.new

    result = run_calls(slow_tools, call('1', 'slow'), call('2', 'write'), cancel:) do |event|
      cancel.cancel if event in ToolCallStarted(call: { name: 'slow' })
    end

    assert_equal :cancelled, result.reason
    assert_equal ['The call was cancelled while it ran: interrupted', 'The call was cancelled before it started.'],
                 results.map(&:content)
    assert_equal 1, @model.requests.size
  end

  def test_agt05_breaking_while_tools_run_cancels_them_waits_for_them_and_still_answers_every_call
    run_calls(slow_tools, call('1', 'slow'), call('2', 'write')) do |event|
      break if event in ToolCallStarted(call: { name: 'slow' })
    end

    assert_equal %i[user assistant user], @conversation.messages.map(&:role)
    assert_equal ['The call was cancelled while it ran: interrupted', 'The call was cancelled before it started.'],
                 results.map(&:content)
  end

  def test_evt03_secrets_are_redacted_from_results_tool_events_the_runs_result_and_the_audit_trail
    sink = Testing::RecordingAuditSink.new
    tools = [tool('search') { |input, _| "#{input.title} needs hunter2 and #{JSON.generate('pa/ss"é')}" }]
    events = []
    @model = Model.new(Model.tool_use(call('1', 'search', '{"title":"hunter2"}')), Model.text('Done, hunter2.'))
    agent = Agent.new(model: @model, instructions: 'You help.', tools:, audit_sink: sink,
                      secrets: ['hunter2', 'pa/ss"é'])
    @conversation = Conversation.new

    result = agent.run(@conversation, 'Go') { events << it }

    assert_equal 'Done, [redacted].', result.text
    assert_equal '[redacted] needs [redacted] and "[redacted]"', results[0].content
    assert_equal(['{"title":"[redacted]"}'], events.grep(ToolCallStarted).map { it.call.input })
    refute(sink.entries.any? { |entry| entry.to_h.values.join.include?('hunter2') })
    assert_includes @conversation.messages[1].blocks.first.raw, 'hunter2'
  end

  def test_evt03_every_form_of_every_secret_is_redacted_whole_however_they_overlap
    agent = Agent.new(model: Model.new, instructions: 'You help.', secrets: ['abc', 'abcdef', 'cdefgh', '', 'é<&/'])

    assert_equal 'x[redacted]y cdef[redacted] z', agent.redact('xabcdefghy cdefabc z')
    # As written; non-ASCII escaped in lower case; .NET's escapes in upper case; .NET's in lower case with / escaped;
    # Go's.
    forms = ['é<&/', '\\u00e9<&/', '\\u00E9\\u003C\\u0026/', '\\u00e9\\u003c\\u0026\\/', 'é\\u003c\\u0026/']

    assert_equal(['[redacted]'] * 5, forms.map { agent.redact(it) })
    assert_equal 'nothing here', agent.redact('nothing here')
  end

  private

  def tool(name, kind: :read, needs_approval: false, &)
    Tool.new(name:, description: "Does #{name}.", input: Search, kind:, needs_approval:, &)
  end

  # A read that runs until the run is cancelled, then fails, and a write after it.
  def slow_tools
    [tool('slow') do |_, cancel|
      sleep 0.001 until cancel.cancelled?
      raise IOError, 'interrupted'
    end, tool('write', kind: :write) { |_, _| 'Written.' }]
  end

  def call(id, name, input = '{"title":"Dune"}') = Model.tool_use_block(id, name, input)

  def run_calls(tools, *calls, approver: nil, cancel: nil, &)
    @model = Model.new(Model.tool_use(*calls), Model.text('Done.'))
    @conversation = Conversation.new
    Agent.new(model: @model, instructions: 'You help.', tools:, approver:).run(@conversation, 'Go', cancel:, &)
  end

  def results = @conversation.messages.fetch(2).blocks.map(&:tool_result)
end
