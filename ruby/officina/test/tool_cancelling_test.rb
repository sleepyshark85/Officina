# frozen_string_literal: true

require 'test_helper'
require_relative 'support/tool_calls'

# Tools that are running when the run is cancelled, left, or fails: they stop, are waited for, and every call is
# answered.
class ToolCancellingTest < Minitest::Test
  include Sleepyshark::Officina
  include ToolCalls

  cover 'Sleepyshark::Officina*'

  NOT_STARTED = 'The call was cancelled before it started.'

  def test_agt05_cancelling_while_tools_run_stops_them_and_answers_every_call
    cancel = Cancellation.new

    ran = run_calls(slow_tools, call('1', 'slow'), call('2', 'write'), cancel:) do |event|
      cancel.cancel if event in ToolCallStarted(call: { name: 'slow' })
    end

    assert_equal :cancelled, ran.result.reason
    assert_equal ['The call was cancelled while it ran: interrupted', NOT_STARTED], ran.results.map(&:content)
    assert_equal 1, ran.model.requests.size
  end

  def test_agt05_breaking_while_tools_run_cancels_them_waits_for_them_and_still_answers_every_call
    conversation = Conversation.new

    result = agent.run(conversation, 'Go') { |event| break if event in ToolCallStarted(call: { name: 'slow' }) }

    assert_nil result
    assert_equal %i[user assistant user], conversation.messages.map(&:role)
    assert_equal ['The call was cancelled while it ran: interrupted', NOT_STARTED], results_in(conversation)
  end

  def test_agt05_breaking_before_the_tools_start_answers_every_call_without_running_one
    conversation = Conversation.new

    agent.run(conversation, 'Go') { |event| break if event in ConversationAppended(message: { role: :assistant }) }

    assert_equal [NOT_STARTED, NOT_STARTED], results_in(conversation)
  end

  def test_agt05_a_run_cancelled_before_its_tools_start_starts_none_and_answers_every_call
    cancel = Cancellation.new
    events = []

    ran = run_calls(slow_tools, call('1', 'gone'), call('2', 'write'), cancel:) do |event|
      events << event
      cancel.cancel if event in ConversationAppended(message: { role: :assistant })
    end

    assert_equal [NOT_STARTED, NOT_STARTED], ran.results.map(&:content)
    assert(ran.results.all?(&:error?))
    assert_empty events.grep(ToolCallStarted)
  end

  # A read that fails beyond what a handler's error result covers takes the run down, but not before the other reads
  # are cancelled and have stopped: none outlives the run. The failure is raised, not also printed by its thread.
  def test_agt05_a_read_that_fails_the_run_stops_the_other_reads_first
    stopped = Thread::Queue.new
    tools = [tool('fails') { |_, _| raise ScriptError, 'broken tool' },
             tool('waits') { |_, cancel| stop_late(cancel, stopped) }]

    error = nil
    _, printed = with_threads_ending_quietly do
      error = assert_raises(ScriptError) do
        run_calls(tools, call('1', 'fails'), call('2', 'waits'))
      end
    end

    assert_equal ['broken tool', 1, '', true], [error.message, stopped.size, printed, Thread.report_on_exception]
  end

  private

  # An agent whose model asks for the slow tools; a host that breaks out of its block leaves the run, whose result
  # run_calls could then not return.
  def agent
    model = Model.new(Model.tool_use(call('1', 'slow'), call('2', 'write')), Model.text('Done.'))
    Agent.new(model:, instructions: 'You help.', tools: slow_tools)
  end

  def results_in(conversation) = conversation.messages.fetch(2).blocks.map { it.tool_result.content }

  # Waits for the cancellation, then a little longer before it notes that it stopped, so a run that did not join it
  # would end before the note.
  def stop_late(cancel, stopped)
    sleep 0.001 until cancel.cancelled?
    sleep 0.05
    stopped << true
  end

  # Captures what is printed. mutant makes a thread that raises end the whole process; the tests run, as the core
  # does, without that.
  def with_threads_ending_quietly(&)
    aborting = Thread.abort_on_exception
    Thread.abort_on_exception = false
    capture_io(&)
  ensure
    Thread.abort_on_exception = aborting
  end

  # A read that runs until the run is cancelled, then fails, and a write after it.
  def slow_tools
    slow = tool('slow') do |_, cancel|
      sleep 0.001 until cancel.cancelled?
      raise IOError, 'interrupted'
    end
    [slow, tool('write', kind: :write) { |_, _| 'Written.' }]
  end
end
