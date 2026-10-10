# frozen_string_literal: true

require 'test_helper'

# The entry that closes a run's audit trail, whichever way the run ended.
class AuditRunEndTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  Sink = Testing::RecordingAuditSink

  def test_aud01_the_run_end_records_how_it_ended_with_its_usage
    usage = Usage.new(input: 4, output: 2)
    {
      Model.new([UsageReported.new(usage:), *Model.stop(:refusal, detail: 'cyber')]) => ['stopped: refusal', 'cyber'],
      Model.new([UsageReported.new(usage:), RuntimeError.new('overloaded')]) => ['failed: model_error', 'overloaded']
    }.each do |model, ended|
      sink = Sink.new
      Agent.new(model:, instructions: 'You help.', audit_sink: sink).run(Conversation.new, 'Hi')

      assert_equal([:run_ended, *ended, usage], sink.entries.last.then { [it.kind, it.outcome, it.detail, it.usage] })
    end
  end

  def test_aud01_a_run_the_host_left_is_recorded_as_abandoned
    sink = Sink.new

    Agent.new(model: Model.new(Model.text('Hi')), instructions: 'You help.', audit_sink: sink)
         .run(Conversation.new, 'Hi') { break }

    assert_equal [:run_ended, 'abandoned'], [sink.entries.last.kind, sink.entries.last.outcome]
  end
end
