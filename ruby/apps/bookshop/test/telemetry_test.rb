# frozen_string_literal: true

require 'test_helper'
require_relative 'memory_telemetry'

# The application's telemetry wiring, with the SDK's in-memory exporters in place of OTLP: each reply a span over its
# run, the core's metrics, and log records through Logger with their severity and trace.
class TelemetryTest < Minitest::Test
  Officina = Sleepyshark::Officina
  ScriptedModel = Officina::Testing::ScriptedModel
  # Private to the application, which hands it only to the telemetry.
  TelemetryLogger = Bookshop.const_get(:TelemetryLogger)
  SDK = OpenTelemetry::SDK

  # An exporter whose dashboard does not answer: each export waits until the gate closes, or a second at most.
  class UnansweredExporter
    def initialize(gate) = @gate = gate

    def export(*, **)
      @gate.pop(timeout: 1)
      SDK::Metrics::Export::SUCCESS
    end

    def force_flush(**) = SDK::Metrics::Export::SUCCESS
    def shutdown(**) = SDK::Metrics::Export::SUCCESS
  end

  def setup
    super
    @memory = MemoryTelemetry.new
    @telemetry = @memory.telemetry
  end

  def teardown
    @telemetry.close
    super
  end

  def test_evt02_a_reply_is_a_span_of_the_service_over_its_run_with_the_cores_metrics
    result = reply(ScriptedModel.text('Hello.'))

    reply, run = %w[reply invoke_agent].map { |name| @memory.spans.find { it.name.start_with?(name) } }

    assert_instance_of Officina::Completed, result
    assert_equal [reply.trace_id, reply.span_id], [run.trace_id, run.parent_span_id]
    assert_equal OpenTelemetry::Trace::INVALID_SPAN_ID, reply.parent_span_id
    assert_equal 'bookshop-assistant', reply.resource.attribute_enumerator.to_h['service.name']
    assert_includes @memory.metric_names, 'officina.runs'
  end

  def test_app20_each_reply_is_logged_in_its_trace_a_failure_as_an_error
    usage = Officina::Usage.new(input: 10, output: 5, cache_read: 30, cache_write: 2)
    reply(ScriptedModel.text('Hello.', usage:))
    reply(ScriptedModel.stop(:max_tokens))
    reply([RuntimeError.new('The model is overloaded')])

    replies = @memory.spans.select { it.name == 'reply' }

    assert_equal([['INFO', 9, 'Reply in conversation c1 completed: 42 input tokens, 5 output tokens'],
                  ['INFO', 9, 'Reply in conversation c1 stopped (output_limit): 0 input tokens, 0 output tokens'],
                  ['ERROR', 17, 'Reply in conversation c1 failed (model_error): The model is overloaded']],
                 @memory.logs.map { it.to_h.values_at(:severity_text, :severity_number, :body) })
    assert_equal(replies.map { [it.trace_id, it.span_id] }, @memory.logs.map { [it.trace_id, it.span_id] })
  end

  def test_app20_each_summary_is_a_span_over_its_run_logged_in_its_trace_a_failure_as_a_warning
    summarize(ScriptedModel.text('{"title":"Greeting","summary":"Hi.","changes":[]}', usage: Officina::Usage.new(
      input: 10, output: 5
    )))
    summarize(ScriptedModel.stop(:max_tokens))
    summarize(ScriptedModel.text('{"title":"Greeting"}'))

    summaries = @memory.spans.select { it.name == 'summary' }
    runs = @memory.spans.select { it.name.start_with?('invoke_agent') }

    assert_equal([['INFO', 9, 'Session c1 summarized: 10 input tokens, 5 output tokens'],
                  ['WARN', 13, 'Session c1 could not be summarized: stopped (output_limit)'],
                  ['WARN', 13, 'Session c1 could not be summarized (invalid_output): The output does not match its ' \
                               'schema: /summary: is required; /changes: is required']],
                 @memory.logs.map { it.to_h.values_at(:severity_text, :severity_number, :body) })
    assert_equal(summaries.map { [it.trace_id, it.span_id] }, @memory.logs.map { [it.trace_id, it.span_id] })
    assert_equal(summaries.map { [it.trace_id, it.span_id] }, runs.map { [it.trace_id, it.parent_span_id] })
  end

  def test_app20_logger_records_carry_logger_severities_and_respect_its_level
    logger = TelemetryLogger.new(OpenTelemetry::SDK::Logs::LoggerProvider.new.tap do |provider|
      provider.add_log_record_processor(OpenTelemetry::SDK::Logs::Export::SimpleLogRecordProcessor.new(exported))
    end.logger(name: 'test'))
    logger.debug('one')
    logger.warn { 'two' }
    logger.add(nil, nil, 'three')
    logger.add(Logger::FATAL, 'four')
    logger.error(42)
    logger.level = Logger::ERROR
    logger.info('not sent')

    assert_equal([['DEBUG', 5, 'one'], ['WARN', 13, 'two'], ['UNKNOWN', 0, 'three'], ['FATAL', 21, 'four'],
                  ['ERROR', 17, '42']],
                 exported.emitted_log_records.map { it.to_h.values_at(:severity_text, :severity_number, :body) })
  end

  def test_app20_closing_waits_at_most_its_timeout_for_a_dashboard_that_does_not_answer
    before = Thread.list
    gate = Thread::Queue.new
    exporter = UnansweredExporter.new(gate)
    @telemetry = Bookshop.const_get(:Telemetry).new(
      spans: SDK::Trace::Export::BatchSpanProcessor.new(exporter),
      metrics: SDK::Metrics::Export::PeriodicMetricReader.new(exporter:, export_interval_millis: 60_000),
      logs: SDK::Logs::Export::BatchLogRecordProcessor.new(exporter)
    )
    reply(ScriptedModel.text('Hello.'))

    started = Process.clock_gettime(Process::CLOCK_MONOTONIC)
    @telemetry.close(timeout: 0.1)
    took = Process.clock_gettime(Process::CLOCK_MONOTONIC) - started
    gate.close
    (Thread.list - before).each(&:join)

    assert_in_delta 0.1, took, 0.1
  end

  private

  # Runs an agent of the model's one reply as a reply of conversation c1, and returns its result.
  def reply(steps)
    agent = Officina::Agent.new(name: 'bookshop', model: ScriptedModel.new(steps), instructions: 'Help.',
                                telemetry: @telemetry.officina)
    @telemetry.reply('c1') { agent.run(Officina::Conversation.new, 'Hi.') { nil } }
  end

  # Runs a summarizer agent of the model's one reply as the summary of session c1, and returns its result.
  def summarize(steps)
    agent = Officina::Agent.new(name: 'summarizer', model: ScriptedModel.new(steps), instructions: 'Summarize.',
                                output: Bookshop.const_get(:SessionSummary), telemetry: @telemetry.officina)
    @telemetry.summary('c1') { agent.run(Officina::Conversation.new, 'Staff: Hi.') { nil } }
  end

  def exported = @exported ||= OpenTelemetry::SDK::Logs::Export::InMemoryLogRecordExporter.new
end
