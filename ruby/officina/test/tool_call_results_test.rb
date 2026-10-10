# frozen_string_literal: true

require 'test_helper'
require_relative 'support/tool_calls'

# What each of a reply's calls gets back, and in which order: results, error results and cut results.
class ToolCallResultsTest < Minitest::Test
  include Sleepyshark::Officina
  include ToolCalls

  cover 'Sleepyshark::Officina*'

  def test_tool01_each_call_runs_its_tool_and_the_model_reads_the_results
    tools = [tool('search') { |input, _| "Found #{input.title}." }, tool('count', kind: :write) { |_, _| 3 }]

    ran = run_calls(tools, call('1', 'search'), call('2', 'count'))

    assert_equal Completed.new(text: 'Done.', usage: Usage.new), ran.result
    assert_equal [ToolResult.new(call_id: '1', content: 'Found Dune.', error: false),
                  ToolResult.new(call_id: '2', content: '3', error: false)], ran.results
    assert_equal ran.conversation.messages.first(3), ran.model.requests.last.messages
  end

  def test_tool02_invalid_input_comes_back_as_an_error_result_and_the_handler_never_runs
    handled = []
    tools = [tool('search') { |input, _| handled << input }]

    ran = run_calls(tools, call('1', 'search', '{"title":1}'), call('2', 'search', '{"title":'), call('3', 'lookup'))

    assert_equal ['Done.', []], [ran.result.text, handled]
    assert_equal "The input does not match the tool's schema:\n/title: must be string", ran.results[0].content
    assert_match(/\AThe input is not valid JSON: /, ran.results[1].content)
    assert_equal 'There is no tool named lookup.', ran.results[2].content
    assert(ran.results.all?(&:error?))
  end

  def test_tool05_a_handler_that_raises_gives_an_error_result_and_the_run_goes_on
    ran = run_calls([tool('search') { |_, _| raise IOError, 'the catalogue is down' }], call('1', 'search'))

    assert_equal 'Done.', ran.result.text
    assert_equal ToolResult.new(call_id: '1', content: 'The tool failed: the catalogue is down', error: true),
                 ran.results[0]
  end

  def test_tool05_a_failure_the_handler_returns_reaches_the_model_as_written_redacted_and_cut_and_the_run_goes_on
    tools = [tool('order') { |_, _| ToolFailure.new(message: 'Not enough stock for s3cret.') },
             tool('long') { |_, _| ToolFailure.new(message: 'x' * 64_001) }]

    ran = run_calls(tools, call('1', 'order'), call('2', 'long'), secrets: ['s3cret'])

    assert_equal 'Done.', ran.result.text
    assert_equal ToolResult.new(call_id: '1', content: 'Not enough stock for [redacted].', error: true), ran.results[0]
    assert_equal "#{'x' * 64_000}\n[Truncated: the result had 64001 characters; only the first 64000 are shown.]",
                 ran.results[1].content
    assert_predicate ran.results[1], :error?
  end

  def test_tool06_a_result_over_64000_characters_is_cut_with_a_note_and_one_at_the_limit_is_not
    tools = [tool('long') { |_, _| 'é' * 70_000 }, tool('limit') { |_, _| 'x' * 64_000 }]

    ran = run_calls(tools, call('1', 'long'), call('2', 'limit'))

    assert_equal "#{'é' * 64_000}\n[Truncated: the result had 70000 characters; only the first 64000 are shown.]",
                 ran.results[0].content
    assert_equal 'x' * 64_000, ran.results[1].content
  end

  def test_ctx06_results_of_one_reply_return_in_one_message_in_call_order
    second_done = Thread::Queue.new
    tools = [tool('first') { |_, _| second_done.pop(timeout: 5) && 'First.' },
             tool('second') { |_, _| (second_done << true) && 'Second.' }]
    finished = []

    ran = run_calls(tools, call('1', 'first'), call('2', 'second')) do |event|
      finished << event.call.id if event.is_a?(ToolCallFinished)
    end

    assert_equal %w[2 1], finished
    assert_equal %i[user assistant user assistant], ran.conversation.messages.map(&:role)
    assert_equal(%w[First. Second.], ran.results.map(&:content))
  end
end
