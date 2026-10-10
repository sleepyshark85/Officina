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
                      reply('Not saved.', usage(0, 5, 400, 10)), info: ACME)
    save = tool('save', kind: :write, needs_approval: true) { |_, _| 'Saved.' }

    traced(collector, model, name: 'clerk', tools: [save], approver: Testing::ScriptedApprover.new('No.'),
                             clock: FakeClock.new).run(Conversation.new, 'Save.')
    got = collector.points(DIMENSIONS).map { [it.metric, it.attributes, it.sum.round(12), it.count] }

    assert_equal expected_points, got
  end

  def test_evt02_a_denied_calls_span_is_marked_failed_as_a_tool_error
    collector = Collector.new
    model = Model.new(Model.tool_use(call('1', 'save')), Model.text('Not saved.'))

    traced(collector, model, tools: [tool('save', kind: :write, needs_approval: true) { |_, _| 'Saved.' }],
                             approver: Testing::ScriptedApprover.new('No.')).run(Conversation.new, 'Save.')
    save = collector.span('execute_tool save')

    assert_equal ['tool_error', OpenTelemetry::Trace::Status::ERROR, ''],
                 [save.attributes['error.type'], save.status.code, save.status.description]
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
