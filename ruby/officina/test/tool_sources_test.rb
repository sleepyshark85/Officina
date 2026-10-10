# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# A run connects the sources of its tools before its first model call, and audits their connection changes.
class ToolSourcesTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  Sink = Testing::RecordingAuditSink

  # A source standing in for a connection to a server: each connect runs the block, which may raise or cancel, then
  # counts as connected.
  class Source
    attr_reader :name, :connects

    def initialize(name, &on_connect)
      @name = name
      @on_connect = on_connect
      @connects = 0
      @changes = []
    end

    def connect(cancel:)
      @connects += 1
      @on_connect&.call(cancel)
      @changes << Sleepyshark::Officina::ToolSourceChange.new(state: :connected)
    end

    def lose(reason) = @changes << Sleepyshark::Officina::ToolSourceChange.new(state: :disconnected, detail: reason)

    def take_changes = @changes.slice!(0..)
  end

  def test_mcp04_a_source_that_cannot_connect_fails_the_run_before_any_model_call
    down = Source.new('files') { raise 'refused with token-7' }
    later = Source.new('web')
    sink = Sink.new
    model = Model.new(Model.text('Hello'))
    conversation = Conversation.new
    tools = [sourced('files__read', down), sourced('files__write', down), sourced('web__fetch', later)]

    result = Agent.new(model:, instructions: 'You help.', tools:, audit_sink: sink, secrets: ['token-7'])
                  .run(conversation, 'Hi')

    assert_equal [:tool_source_unavailable, 'The tool source files is not available: refused with [redacted]'],
                 [result.reason, result.detail]
    assert_empty model.requests
    assert_empty conversation.messages
    assert_equal [1, 0], [down.connects, later.connects], 'each source once, and none after one that failed'
    assert_equal([[:run_started, nil, nil], [:run_ended, 'failed: tool_source_unavailable', result.detail]],
                 sink.entries.map { [it.kind, it.outcome, it.detail] })
  end

  def test_aud01_source_changes_are_recorded_when_the_run_connects_and_after_each_replys_calls
    files = Source.new('files')
    sink = Sink.new
    read = sourced('files__read', files) do |_, _|
      files.lose('it went away')
      'Read.'
    end

    ran = run_calls([read], call('1', 'files__read'), audit_sink: sink)

    assert_instance_of Completed, ran.result
    assert_equal([[:run_started, nil, nil, nil], [:tool_source, 'files', 'connected', nil],
                  [:tool_started, 'files__read', nil, nil], [:tool_ended, 'files__read', 'ok', 'Read.'],
                  [:tool_source, 'files', 'disconnected', 'it went away'], [:run_ended, nil, 'completed', nil]],
                 sink.entries.map { [it.kind, it.tool, it.outcome, it.detail] })
  end

  def test_agt05_a_run_cancelled_while_its_sources_connect_stops_as_cancelled
    cancel = Cancellation.new
    first = Source.new('files') do |given|
      given.cancel
      raise 'cancelled'
    end
    second = Source.new('web')
    model = Model.new(Model.text('Hello'))

    result = Agent.new(model:, instructions: 'You help.', tools: [sourced('a', first), sourced('b', second)])
                  .run(Conversation.new, 'Hi', cancel:)

    assert_equal [Stopped, :cancelled], [result.class, result.reason]
    assert_equal [1, 0], [first.connects, second.connects]
    assert_empty model.requests
  end

  def test_evt02_a_tool_call_span_names_the_source_of_its_tool
    collector = Collector.new
    files = Source.new('files')

    traced(collector, Model.new(Model.tool_use(call('1', 'files__read')), Model.text('Done.')),
           tools: [sourced('files__read', files) { |_, _| 'Read.' }]).run(Conversation.new, 'Go')

    assert_equal ['files'], collector.attributes('execute_tool files__read', 'officina.tool.source')
  end

  private

  def sourced(name, source, &handler)
    handler ||= ->(_, _) { 'Done.' }
    Tool.new(name:, description: "Does #{name}.", input: Search, kind: :read, source:, &handler)
  end
end
