# frozen_string_literal: true

module Bookshop
  # The application's traces, metrics and logs, through the OpenTelemetry SDK: the core's, through the agent's
  # telemetry, and the application's own, each reply a span that the run's trace hangs from and that the reply's log
  # record joins. Close it to send what is left.
  class Telemetry
    SDK = OpenTelemetry::SDK
    Officina = Sleepyshark::Officina
    # Names the application's tracer and logger, and the service, as .NET and Go do, so the dashboard shows the three
    # implementations as one application.
    SCOPE = 'BookshopAssistant'
    SERVICE = 'bookshop-assistant'
    # Milliseconds between metric exports, as .NET's.
    METRICS_INTERVAL = 5_000
    # The metrics and logs exporters gzip by default, which the dashboard cannot read; the traces exporter does not.
    UNCOMPRESSED = 'none'
    # Seconds closing waits, in all, for the providers to send what is left: an exporter whose dashboard is down
    # retries for its own ten seconds, and the metrics SDK ignores the timeout it is given.
    CLOSE_TIMEOUT = 2
    private_constant :SDK, :Officina, :SCOPE, :SERVICE, :METRICS_INTERVAL, :UNCOMPRESSED, :CLOSE_TIMEOUT

    # Telemetry that exports over OTLP/HTTP to OTEL_EXPORTER_OTLP_ENDPOINT, or http://localhost:4318, the compose
    # file's dashboard. The exporters connect when they first send, so it is made with the dashboard down.
    def self.otlp
      otlp = OpenTelemetry::Exporter::OTLP
      new(spans: SDK::Trace::Export::BatchSpanProcessor.new(otlp::Exporter.new),
          metrics: SDK::Metrics::Export::PeriodicMetricReader.new(
            exporter: otlp::Metrics::MetricsExporter.new(compression: UNCOMPRESSED),
            export_interval_millis: METRICS_INTERVAL
          ),
          logs: SDK::Logs::Export::BatchLogRecordProcessor.new(otlp::Logs::LogsExporter.new(compression: UNCOMPRESSED)))
    end

    # @return [Sleepyshark::Officina::Telemetry] what the agent reports its runs to
    attr_reader :officina

    # @param spans [OpenTelemetry::SDK::Trace::SpanProcessor] where ended spans go
    # @param metrics [OpenTelemetry::SDK::Metrics::Export::MetricReader] what reads the metrics
    # @param logs [OpenTelemetry::SDK::Logs::LogRecordProcessor] where log records go
    def initialize(spans:, metrics:, logs:)
      # This process, as one instance of the service.
      resource = SDK::Resources::Resource.create('service.name' => SERVICE, 'service.instance.id' => SecureRandom.uuid)
      @traces = SDK::Trace::TracerProvider.new(resource:).tap { it.add_span_processor(spans) }
      @metrics = SDK::Metrics::MeterProvider.new(resource:).tap { it.add_metric_reader(metrics) }
      @logs = SDK::Logs::LoggerProvider.new(resource:).tap { it.add_log_record_processor(logs) }
      @officina = Officina::Telemetry.new(tracer_provider: @traces, meter_provider: @metrics)
      @logger = TelemetryLogger.new(@logs.logger(name: SCOPE))
      freeze
    end

    # Runs the block, a reply in the conversation, in a span of its own, current while it runs so that the run's trace
    # is under it, and logs how the reply ended.
    #
    # @param conversation [String] the conversation's id
    # @return [Sleepyshark::Officina::Completed, Sleepyshark::Officina::Stopped, Sleepyshark::Officina::Failed] what
    #   the block returns
    def reply(conversation, &run) = traced('reply', run) { log(conversation, it) }

    # Runs the block, the summary of a session, in a span of its own, as #reply does, and logs how the summary ended:
    # a summary that failed as a warning, since the session goes on without one.
    #
    # @param session [String] the session's id
    # @return [Sleepyshark::Officina::Completed, Sleepyshark::Officina::Stopped, Sleepyshark::Officina::Failed] what
    #   the block returns
    def summary(session, &) = traced('summary', -> { refusal_logged(session, &) }) { log_summary(session, it) }

    # Logs, as a warning in a span of its own, a summary the database failed to store.
    #
    # @param session [String] the session's id
    # @param error [StandardError] what the database raised
    # @return [void]
    def summary_not_saved(session, error)
      @traces.tracer(SCOPE).in_span('summary.save') do
        @logger.warn("Session #{session} summary not saved: #{error.message.strip}")
      end
    end

    # Sends what is left and stops exporting. The three providers shut down at once, and closing returns once they
    # have or the timeout has passed, whichever is first; a shutdown still waiting on its exporter ends with the
    # process.
    #
    # @param timeout [Numeric] seconds to wait at most
    def close(timeout: CLOSE_TIMEOUT)
      deadline = now + timeout
      # A join whose time has passed returns at once.
      [@traces, @metrics, @logs].map { |provider| Thread.new { provider.shutdown(timeout:) } }
                                .each { it.join(deadline - now) }
    end

    private

    def now = Process.clock_gettime(Process::CLOCK_MONOTONIC)

    # Calls the run in a span of the name, current while it runs, and yields its result before the span ends, so what
    # the block logs is in its trace.
    def traced(name, run)
      @traces.tracer(SCOPE).in_span(name) { run.call.tap { yield it } }
    end

    # Runs the block, and logs a run the core refuses, which is raised and not returned, before it goes on.
    def refusal_logged(session)
      yield
    rescue Officina::Error => e
      @logger.warn("Session #{session} could not be summarized: #{e.message}")
      raise
    end

    def log(conversation, result)
      case result
      in Officina::Failed(reason:, detail:)
        @logger.error("Reply in conversation #{conversation} failed (#{reason}): #{detail}")
      in Officina::Completed(usage:) then ended(conversation, 'completed', usage)
      in Officina::Stopped(reason:, usage:) then ended(conversation, "stopped (#{reason})", usage)
      end
    end

    def log_summary(session, result)
      case result
      in Officina::Completed(usage:)
        @logger.info("Session #{session} summarized: #{usage.all_input} input tokens, #{usage.output} output tokens")
      in Officina::Stopped(reason:) then @logger.warn("Session #{session} could not be summarized: stopped (#{reason})")
      in Officina::Failed(reason:, detail:)
        @logger.warn("Session #{session} could not be summarized (#{reason}): #{detail}")
      end
    end

    def ended(conversation, how, usage)
      @logger.info("Reply in conversation #{conversation} #{how}: " \
                   "#{usage.all_input} input tokens, #{usage.output} output tokens")
    end
  end
  private_constant :Telemetry
end
