# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# The core's instruments and attributes, string for string as .NET's and Go's source names them, so one dashboard reads
# every implementation.
class TelemetryNamesTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  ROOT = File.expand_path('../../..', __dir__)
  DOTNET = %w[src/Sleepyshark.Officina/Telemetry/Telemetry.cs src/Sleepyshark.Officina/Runs/RunEngine.cs].freeze
  GO = %w[go/officina/telemetry.go go/officina/run.go].freeze
  # An instrument as .NET makes one: kind, name, unit and description.
  DOTNET_INSTRUMENT = /Create(Histogram|Counter)<\w+>\(\s*"([^"]+)",\s*"([^"]+)",\s*"([^"]+)"\)/
  # As Go does, its description perhaps in pieces joined with +.
  GO_INSTRUMENT = /\.(?:Int64|Float64)(Histogram|Counter)\("([^"]+)",\s*metric\.WithUnit\("([^"]+)"\),\s*
                   metric\.WithDescription\(((?:"[^"]*"\s*\+?\s*)+)\)/x
  KEY = /"((?:gen_ai|officina|error)\.[a-z_.]+)"/

  def test_evt02_the_instruments_have_the_kinds_names_units_and_descriptions_of_dotnets_and_gos
    collector = Collector.new(content: true)
    dotnet = read(DOTNET).scan(DOTNET_INSTRUMENT).map { |kind, *rest| [kind.downcase.to_sym, *rest] }
    go = read(GO).scan(GO_INSTRUMENT).map do |kind, name, unit, pieces|
      [kind.downcase.to_sym, name, unit, joined(pieces)]
    end

    exercise(collector)
    ruby = collector.metrics.map { [it.instrument_kind, it.name, it.unit, it.description] }

    assert_equal 13, dotnet.size
    assert_equal dotnet.sort, go.sort
    assert_equal dotnet.sort, ruby.sort
  end

  def test_evt02_the_spans_events_and_metrics_carry_the_attributes_dotnet_and_go_name
    collector = Collector.new(content: true)
    dotnet, go = [DOTNET, GO].map { read(it).scan(KEY).flatten.uniq.sort }

    exercise(collector)

    assert_equal dotnet, go
    assert_equal dotnet, keys(collector)
  end

  def test_evt02_the_spans_and_metrics_come_from_the_scope_of_officinas_name
    collector = Collector.new

    exercise(collector)

    assert_equal(['Sleepyshark.Officina'],
                 [*collector.spans, *collector.metrics].map { it.instrumentation_scope.name }.uniq)
  end

  private

  def read(paths) = paths.map { File.read(File.join(ROOT, it)) }.join("\n")
  def joined(pieces) = pieces.scan(/"([^"]*)"/).join

  # Every name the collector holds: the keys its spans, their events and the data points carry, the events' names and
  # the metrics'.
  def keys(collector)
    spans = collector.spans
    events = spans.flat_map { it.events.to_a }
    [*spans.flat_map { it.attributes.keys }, *events.flat_map { [it.name, *it.attributes.keys] },
     *collector.points.flat_map { [it.metric, *it.attributes.keys] }].uniq.sort
  end

  # Runs that make every instrument and attribute the core has: a retry, text, usage, an approved write blocked as its
  # attempt cannot be audited, a read that runs, with content on, in a memory scope; then a model call that fails, and
  # one for which the provider cleared tool results and compacted the conversation.
  def exercise(collector)
    model = Model.new([Retried.new, *reply('Saving.', usage(1, 1), call('1', 'save'), call('2', 'search'),
                                           stop: :tool_use)], Model.text('Done.'))
    tools = [tool('save', kind: :write, needs_approval: true) { |_, _| 'Saved.' }, tool('search') { |_, _| 'Found.' }]
    traced(collector, model, name: 'clerk', tools:, approver: Testing::ScriptedApprover.new(true),
                             audit_sink: Testing::RecordingAuditSink.new(fails: lambda { |entry|
                               entry.kind == :tool_started
                             }))
      .run(Conversation.new, 'Save.', memory_scope: 'sam')
    traced(collector, Model.new([RuntimeError.new('down')]), name: 'clerk').run(Conversation.new, 'Again.')
    traced(collector, shortening, name: 'clerk', context_management: ContextManagement.new(
      compact_at: 50_000, clear_tool_results: ToolResultClearing.new(after: 1)
    )).run(Conversation.new, 'Long.')
  end

  def shortening
    edits = [ToolResultsCleared.new(tokens: 1, tool_calls: 1), ConversationCompacted.new(tokens: 2, summary_tokens: 1)]
    Model.new([*edits, *Model.text('Short.')],
              info: ModelInfo.new(provider: 'scripted', name: 'scripted', compacts: true, clears_tool_results: true))
  end
end
