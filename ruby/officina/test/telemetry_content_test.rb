# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# Telemetry carries no text unless the host opts in, and never a secret.
class TelemetryContentTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  TEXTS = %w[question-text input-text result-text answer-text].freeze

  def test_evt03_evt04_by_default_telemetry_carries_no_text_and_no_secret
    dump = searched(Collector.new).dump

    assert_empty(TEXTS.select { dump.include?(it) })
    refute_includes dump, 'hunter2'
  end

  def test_evt03_evt04_a_host_that_opts_in_gets_the_text_without_the_secrets
    collector = searched(Collector.new(content: true))
    dump = collector.dump

    assert_equal(TEXTS, TEXTS.select { dump.include?(it) })
    refute_includes dump, 'hunter2'
    assert_equal(['[{"role":"user","parts":[{"type":"text","content":"question-text [redacted]"}]}]',
                  '[{"role":"assistant","parts":[{"type":"text","content":"answer-text [redacted]"}]}]'],
                 collector.span('invoke_agent').attributes.values_at('gen_ai.input.messages', 'gen_ai.output.messages'))
    assert_equal(['{"title":"input-text [redacted]"}', 'result-text [redacted]'],
                 collector.span('execute_tool search').attributes.values_at('gen_ai.tool.call.arguments',
                                                                            'gen_ai.tool.call.result'))
  end

  private

  # A run whose question, the search's input and result, and the answer each hold a text and the secret hunter2.
  def searched(collector)
    model = Model.new(Model.tool_use(call('1', 'search', '{"title":"input-text hunter2"}')),
                      Model.text('answer-text hunter2'))
    traced(collector, model, tools: [tool('search') { |_, _| 'result-text hunter2' }], secrets: ['hunter2'])
      .run(Conversation.new, 'question-text hunter2')
    collector
  end
end
