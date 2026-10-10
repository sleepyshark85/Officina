# frozen_string_literal: true

require 'test_helper'
require_relative 'support/traced_runs'

# What a model call's span says of a retry, the time to first token, a failure and how the reply ended, and the run's
# span of how the run ended.
class TelemetryModelCallTest < Minitest::Test
  include Sleepyshark::Officina
  include TracedRuns

  cover 'Sleepyshark::Officina*'

  # A model that streams its steps, each an event or the seconds it waits on the clock first, then says Hello.
  Paced = Data.define(:clock, :steps) do
    def settings = 'paced'
    def info = Testing::ScriptedModel::INFO

    def stream(_request, cancel:)
      steps.each { it.is_a?(Numeric) ? clock.advance(it) : yield(it) }
      Reply.new(blocks: [Testing::ScriptedModel.text_block('Hello.')], stop: :end) unless cancel.cancelled?
    end
  end

  def test_evt02_a_retry_is_counted_and_the_failed_attempts_tokens_are_kept
    collector = Collector.new
    model = Model.new([UsageReported.new(usage: usage(10, 1)), Retried.new, *Model.text('Hi.', usage: usage(10, 3))])

    result = traced(collector, model).run(Conversation.new, 'Hi')
    span = collector.span('chat scripted').attributes

    assert_equal usage(20, 4), result.usage
    assert_equal [1, 20, 4], span.values_at('officina.model.retries', 'gen_ai.usage.input_tokens',
                                            'gen_ai.usage.output_tokens')
    assert_equal [1], collector.points_of('officina.model.retries').map(&:sum)
  end

  def test_evt02_the_time_to_first_token_is_the_wait_for_the_first_text_of_the_attempt_that_counts
    hello = [TextDelta.new(text: 'Hel'), Retried.new, 2, TextDelta.new(text: 'Hel'), 1, TextDelta.new(text: 'lo.')]

    assert_in_delta 3.0, first_token([1, *hello])
    assert_in_delta 0.0, first_token([TextDelta.new(text: 'Hel'), 1, TextDelta.new(text: 'lo.')])
    assert_nil first_token([1])
  end

  def test_evt02_evt03_a_failed_model_call_marks_its_span_and_the_runs_without_the_secret
    collector = Collector.new

    result = traced(collector, Model.new([TextDelta.new(text: 'Hel'), RuntimeError.new('upstream refused key s3cret')]),
                    secrets: ['s3cret']).run(Conversation.new, 'Hi')

    assert_equal 'upstream refused key [redacted]', result.detail
    assert_equal([[OpenTelemetry::Trace::Status::ERROR, result.detail, 'ModelError', 'failed', 'ModelError'],
                  [OpenTelemetry::Trace::Status::ERROR, result.detail, 'model_error', nil, nil]],
                 ['invoke_agent', 'chat scripted'].map { failure(collector.span(it)) })
    assert_equal(['model_error'],
                 collector.points_of('gen_ai.client.operation.duration').map { it.attributes['error.type'] })
  end

  def test_evt02_a_stopped_runs_reason_is_named_as_dotnet_names_it_and_its_span_is_not_failed
    collector = Collector.new

    traced(collector, Model.new(Model.stop(:max_tokens))).run(Conversation.new, 'Hi')
    run = collector.span('invoke_agent')

    assert_equal ['stopped', 'OutputLimit', nil, OpenTelemetry::Trace::Status::UNSET],
                 [*run.attributes.values_at('officina.run.result', 'officina.run.reason', 'error.type'),
                  run.status.code]
    assert_equal [{ 'officina.run.result' => 'stopped', 'officina.run.reason' => 'OutputLimit' }],
                 collector.points_of('officina.runs', SCRIPTED).map(&:attributes)
  end

  def test_evt02_an_unknown_finish_is_named_by_the_providers_word_and_a_call_without_tokens_has_no_hit_ratio
    collector = Collector.new

    traced(collector, Model.new(Model.stop(:unknown, detail: 'pause_turn'), Model.stop(:unknown, detail: nil)))
      .then { |agent| 2.times { agent.run(Conversation.new, 'Go.') } }

    reasons = collector.spans.filter_map { it.attributes['gen_ai.response.finish_reasons'] }

    assert_equal [['pause_turn'], ['unknown']], reasons
    assert_empty(collector.points.select { it.metric.start_with?('officina.model.cache', 'gen_ai.client.token') })
  end

  def test_evt02_a_run_the_host_left_ends_its_spans_and_is_counted_as_abandoned
    collector = Collector.new
    clock = FakeClock.new

    traced(collector, Paced.new(clock:, steps: [TextDelta.new(text: 'Hel'), 2, TextDelta.new(text: 'lo')]))
      .run(Conversation.new, 'Hi') { break }
    run, call = ['invoke_agent', 'chat scripted'].map { collector.span(it) }

    assert_equal [{ 'officina.run.result' => 'abandoned' }], collector.points_of('officina.runs', SCRIPTED)
                                                                      .map(&:attributes)
    refute_includes run.attributes, 'officina.run.model_calls'
    assert_equal [OpenTelemetry::Trace::Status::UNSET, nil, nil], [call.status.code, *call.attributes.values_at(
      'gen_ai.response.finish_reasons', 'error.type'
    )]
  end

  private

  # The time to first token of a call that streams the steps, on a clock that moves only as they say.
  def first_token(steps)
    collector = Collector.new
    clock = FakeClock.new
    traced(collector, Paced.new(clock:, steps:), clock:).run(Conversation.new, 'Hi')
    collector.span('chat scripted').attributes['officina.model.time_to_first_token']
  end

  def failure(span)
    [span.status.code, span.status.description,
     *span.attributes.values_at('error.type', 'officina.run.result', 'officina.run.reason')]
  end
end
