# frozen_string_literal: true

require 'test_helper'
require 'tmpdir'

# The built-in audit sink: one JSON object per line, appended.
class JsonLinesAuditSinkTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina::JsonLinesAuditSink*'

  def test_aud04_each_entry_is_appended_as_one_json_object_with_its_members_in_camel_case
    entries = [entry(1, kind: :tool_ended, tool: 'search', call_id: 'call_1', input: '{}', outcome: 'ok',
                        detail: 'Found.', duration: 0.25), entry(2, kind: :run_ended, usage: Usage.new(input: 3))]

    lines = written { |sink| entries.each { sink.write(it) } }

    assert_equal({ 'time' => '2026-10-10T07:00:00.500000Z', 'sequence' => 1, 'run' => 'r', 'conversation' => 'c',
                   'agent' => 'shop', 'kind' => 'tool_ended', 'tool' => 'search', 'callId' => 'call_1', 'input' => '{}',
                   'outcome' => 'ok', 'detail' => 'Found.', 'duration' => 0.25 }, lines[0])
    assert_equal({ 'input' => 3, 'output' => 0, 'cache_read' => 0, 'cache_write' => 0 }, lines[1]['usage'])
  end

  def test_aud03_aud04_an_entrys_trace_and_span_are_written_in_camel_case_after_its_kind
    lines = written { |sink| sink.write(entry(1, kind: :run_started, trace_id: 'a' * 32, span_id: 'b' * 16)) }

    assert_equal(%w[time sequence run conversation agent kind traceId spanId], lines[0].keys)
    assert_equal ['a' * 32, 'b' * 16], lines[0].values_at('traceId', 'spanId')
  end

  def test_aud04_members_an_entry_does_not_have_are_left_out
    lines = written { |sink| sink.write(entry(1, kind: :run_started)) }

    assert_equal %w[time sequence run conversation agent kind], lines[0].keys
  end

  def test_aud04_entries_written_at_once_are_each_one_whole_line
    lines = written do |sink|
      Array.new(50) { |at| Thread.new { sink.write(entry(at, kind: :run_started)) } }.each(&:join)
    end

    assert_equal (0...50).to_a, lines.map { it['sequence'] }.sort
  end

  def test_aud04_a_sink_that_cannot_write_raises
    sink = JsonLinesAuditSink.new(File.join(Dir.tmpdir, 'no-such-directory-r05b', 'audit.jsonl'))

    assert_raises(SystemCallError) { sink.write(entry(1, kind: :run_started)) }
  end

  private

  # An entry at 9:00:00.5 in UTC+2.
  def entry(sequence, kind:, **members)
    AuditEntry.new(time: Time.new(2026, 10, 10, 9, 0, 0.5, '+02:00'), sequence:, run: 'r', conversation: 'c',
                   agent: 'shop', kind:, **members)
  end

  # The lines of a new file, parsed, once the block has written to a sink of it.
  def written
    Dir.mktmpdir do |directory|
      path = File.join(directory, 'audit.jsonl')
      yield JsonLinesAuditSink.new(path)
      File.readlines(path).map { JSON.parse(it) }
    end
  end
end
