# frozen_string_literal: true

require 'test_helper'

# The scripted approver and the recording audit sink: the human and the trail's store in a test.
class ScriptedApproverTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina::Testing*'

  def test_test01_the_approver_answers_as_scripted_in_order_and_records_each_call
    approver = Testing::ScriptedApprover.new(true, 'Not today.', false)
    calls = %w[1 2 3].map { ToolCall.new(id: it, name: 'order', input: '{}') }

    answers = calls.map { approver.approve(nil, it, cancel: Cancellation.new) }

    assert_equal [Approval.new(approved: true), Approval.new(approved: false, reason: 'Not today.'),
                  Approval.new(approved: false)], answers
    assert_equal calls, approver.asked
    assert_raises(Error) { approver.approve(nil, calls.first, cancel: Cancellation.new) }
  end

  def test_test01_the_recording_sink_keeps_its_entries_and_fails_when_told
    sink = Testing::RecordingAuditSink.new(fails: ->(entry) { entry.sequence == 2 })
    entries = [1, 2, 3].map do |sequence|
      AuditEntry.new(time: Time.now, sequence:, run: 'r', conversation: 'c', agent: nil, kind: :run_started)
    end

    sink.write(entries[0])
    assert_raises(IOError) { sink.write(entries[1]) }
    sink.write(entries[2])

    assert_equal [1, 3], sink.entries.map(&:sequence)
  end
end
