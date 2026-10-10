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

  def test_app20_logger_records_carry_logger_severities_and_respect_its_level
    logger = TelemetryLogger.new(OpenTelemetry::SDK::Logs::LoggerProvider.new.tap do |provider|
      provider.add_log_record_processor(OpenTelemetry::SDK::Logs::Export::SimpleLogRecordProcessor.new(exported))
    end.logger(name: 'test'))
    logger.debug('one')
    logger.warn { 'two' }
    logger.add(nil, nil, 'three')
    logger.add(Logger::FATAL, 'four')
    logger.level = Logger::ERROR
    logger.info('not sent')

    assert_equal([['DEBUG', 5, 'one'], ['WARN', 13, 'two'], ['UNKNOWN', 0, 'three'], ['FATAL', 21, 'four']],
                 exported.emitted_log_records.map { it.to_h.values_at(:severity_text, :severity_number, :body) })
  end

  private

  # Runs an agent of the model's one reply as a reply of conversation c1, and returns its result.
  def reply(steps)
    agent = Officina::Agent.new(name: 'bookshop', model: ScriptedModel.new(steps), instructions: 'Help.',
                                telemetry: @telemetry.officina)
    @telemetry.reply('c1') { agent.run(Officina::Conversation.new, 'Hi.') { nil } }
  end

  def exported = @exported ||= OpenTelemetry::SDK::Logs::Export::InMemoryLogRecordExporter.new
end
