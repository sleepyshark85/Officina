# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# The core's metrics: tokens, cost, cache, durations, tool outcomes, approvals and results, each by agent and model.
class TelemetryMetricsTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  DIMENSIONS = { 'gen_ai.agent.name' => 'clerk', 'gen_ai.provider.name' => 'acme',
                 'gen_ai.request.model' => 'acme-large' }.freeze

  def test_evt02_ctx05_metrics_count_tokens_cost_cache_hit_ratio_tool_outcomes_approvals_and_results
    collector = Collector.new
    model = Model.new(reply('Saving.', usage(100, 20, 300), call('1', 'save'), stop: :tool_use),
                      reply('Not saved.', usage(0, 5, 400, 10)), info: acme)
    save = tool('save', kind: :write, needs_approval: true) { |_, _| 'Saved.' }

    traced(collector, model, name: 'clerk', tools: [save], approver: Testing::ScriptedApprover.new('No.'),
                             clock: FakeClock.new).run(Conversation.new, 'Save.')
    got = collector.points(DIMENSIONS).map { [it.metric, it.attributes, it.sum.round(12), it.count] }

    assert_equal expected_points, got
  end

  def test_evt02_a_hosts_views_get_every_measurement_and_what_one_adds_reaches_no_other
    collector = Collector.new(views: true)
    model = Model.new(reply('Saving.', usage(100, 20, 300), call('1', 'save'), stop: :tool_use),
                      reply('Not saved.', usage(0, 5, 400, 10)), info: acme)
    save = tool('save', kind: :write, needs_approval: true) { |_, _| 'Saved.' }

    traced(collector, model, name: 'clerk', tools: [save], approver: Testing::ScriptedApprover.new('No.'),
                             clock: FakeClock.new).run(Conversation.new, 'Save.')
    got = collector.points(DIMENSIONS).map do |point|
      [point.metric, point.attributes.except('host.view'), point.sum.round(12), point.count]
    end

    assert_equal expected_points, got
    assert_equal(collector.metrics.map { [it.name, it.instrument_kind.to_s] }.sort,
                 collector.points.map { [it.metric, it.attributes['host.view']] }.uniq.sort)
    refute(collector.spans.any? { it.attributes.key?('host.view') })
  end

  def test_evt02_telemetry_is_frozen_with_its_instruments_as_every_run_shares_it
    telemetry = Telemetry.new
    held = telemetry.instance_variables.map { telemetry.instance_variable_get(it) }.grep(Hash)

    assert_predicate telemetry, :frozen?
    assert_equal [true, true], held.map(&:frozen?)
  end

  def test_evt02_a_models_info_keeps_frozen_copies_of_its_names_and_has_no_price_unless_given
    provider = +'acme'
    info = ModelInfo.new(provider:, name: +'acme-large')
    provider << '-changed'

    assert_equal ['acme', 'acme-large', nil], [info.provider, info.name, info.price]
    assert_predicate info.name, :frozen?
  end

  def test_evt02_ctx05_cache_writes_kept_an_hour_cost_their_own_rate_and_five_minute_ones_theirs
    collector = Collector.new

    traced(collector, Model.new(reply('Hi.', usage(0, 0, 0, 300, 100)), info: acme)).run(Conversation.new, 'Hi')

    assert_in_delta 0.0018, collector.span('invoke_agent').attributes['officina.usage.cost'], 1e-12
    assert_equal BigDecimal('0.0008'), acme.price.cost(usage(0, 0, 0, 100, 100))
    assert_equal BigDecimal('0.0005'), acme.price.cost(usage(0, 0, 0, 100))
  end

  def test_evt02_a_model_without_a_price_costs_nothing
    collector = Collector.new

    traced(collector, Model.new(Model.text('Hi.', usage: usage(10, 2)))).run(Conversation.new, 'Hi')

    assert_in_delta 0.0, collector.span('invoke_agent').attributes['officina.usage.cost']
    assert_equal [0.0], collector.points.select { it.metric == 'officina.model.cost' }.map(&:sum)
  end

  private

  def expected_points
    chat = { 'gen_ai.operation.name' => 'chat' }
    save = { 'gen_ai.tool.name' => 'save', 'officina.tool.outcome' => 'error' }
    [['gen_ai.client.operation.duration', chat, 0.0, 2], ['gen_ai.client.token.usage', input(chat), 810, 2],
     ['gen_ai.client.token.usage', { **chat, 'gen_ai.token.type' => 'output' }, 25, 2],
     ['officina.model.cache_hit_ratio', {}, (0.75 + (400.0 / 410)).round(12), 2],
     ['officina.model.cache_tokens', { 'officina.cache.type' => 'read' }, 700, 2],
     ['officina.model.cache_tokens', { 'officina.cache.type' => 'write' }, 10, 2],
     ['officina.model.cost', {}, 0.00109, 2], ['officina.runs', { 'officina.run.result' => 'completed' }, 1, nil],
     ['officina.tool.approvals', { 'gen_ai.tool.name' => 'save', 'officina.tool.approval' => 'denied' }, 1, nil],
     ['officina.tool.calls', save, 1, nil], ['officina.tool.duration', save, 0.0, 1]]
  end

  def input(chat) = { **chat, 'gen_ai.token.type' => 'input' }
end
