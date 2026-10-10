# frozen_string_literal: true

require 'test_helper'
require_relative 'support/tool_calls'

# A run's entries reach its sink one at a time, in sequence order, though reads finish on threads of their own.
class AuditOrderTest < Minitest::Test
  include Sleepyshark::Officina
  include ToolCalls

  cover 'Sleepyshark::Officina*'

  # Holds the end of the first read in its write long enough for the second read's end to arrive, and notes whether
  # any write began while another was under way.
  class SlowSink
    attr_reader :sequences, :overlapped

    def initialize
      @sequences = Thread::Queue.new
      @writing = Thread::Queue.new
      @overlapped = false
    end

    def write(entry)
      @overlapped ||= !@writing.empty?
      @writing << true
      sleep 0.05 if entry.kind == :tool_ended && entry.call_id == '1'
      @sequences << entry.sequence
      @writing.pop
    end
  end

  def test_aud04_entries_reach_the_sink_one_at_a_time_in_sequence_order
    sink = SlowSink.new
    tools = [tool('first') { |_, _| 'First.' }, tool('second') { |_, _| 'Second.' }]

    run_calls(tools, call('1', 'first'), call('2', 'second'), audit_sink: sink)

    sequences = Array.new(sink.sequences.size) { sink.sequences.pop }

    refute sink.overlapped
    assert_equal (1..sequences.size).to_a, sequences
  end
end
