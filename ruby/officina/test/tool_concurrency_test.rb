# frozen_string_literal: true

require 'test_helper'
require_relative 'support/tool_calls'

# Read calls run together; a write waits for every call before it and runs alone. Each test meets or orders its tools
# through queues, so it passes or fails the same way every time.
class ToolConcurrencyTest < Minitest::Test
  include Sleepyshark::Officina
  include ToolCalls

  cover 'Sleepyshark::Officina*'

  def test_tool03_the_reads_of_a_reply_overlap
    arrived = { 'a' => Thread::Queue.new, 'b' => Thread::Queue.new }
    meet = lambda do |name, other|
      tool(name) do |_, _|
        arrived.fetch(name) << true
        arrived.fetch(other).pop(timeout: 5) ? 'Met.' : 'Alone.'
      end
    end

    ran = run_calls([meet['a', 'b'], meet['b', 'a']], call('1', 'a'), call('2', 'b'))

    assert_equal %w[Met. Met.], ran.results.map(&:content)
  end

  # The reads wait until the host has seen both start, so a write that did not wait for them would start first.
  def test_tool03_a_write_waits_for_every_call_before_it_and_a_later_call_waits_for_the_write
    gate = Thread::Queue.new
    tools = [tool('a') { |_, _| gate.pop(timeout: 5) }, tool('b') { |_, _| gate.pop(timeout: 5) },
             tool('w', kind: :write) { |_, _| 'Written.' }, tool('c') { |_, _| 'Read.' }]
    seen = []

    run_calls(tools, *%w[a b w c].map { call(it, it) }) do |event|
      seen << [event.class, event.call.name] if event in ToolCallStarted | ToolCallFinished
      2.times { gate << true } if event in ToolCallStarted(call: { name: 'b' })
    end

    assert_equal [ToolCallStarted, 'w'], seen[4]
    assert_equal [[ToolCallFinished, 'w'], [ToolCallStarted, 'c'], [ToolCallFinished, 'c']], seen.last(3)
  end
end
