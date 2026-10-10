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
    # Seconds closing waits for each provider to send what is left: an exporter whose dashboard is down retries until
    # then, which delays leaving the console.
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
    def reply(conversation)
      @traces.tracer(SCOPE).in_span('reply') do
        result = yield
        log(conversation, result)
        result
      end
    end

    # Sends what is left and stops exporting, waiting at most a few seconds for each kind.
    def close
      [@traces, @metrics, @logs].each { it.shutdown(timeout: CLOSE_TIMEOUT) }
    end

    private

    def log(conversation, result)
      case result
      in Officina::Failed(reason:, detail:)
        @logger.error("Reply in conversation #{conversation} failed (#{reason}): #{detail}")
      in Officina::Completed(usage:) then ended(conversation, 'completed', usage)
      in Officina::Stopped(reason:, usage:) then ended(conversation, "stopped (#{reason})", usage)
      end
    end

    def ended(conversation, how, usage)
      @logger.info("Reply in conversation #{conversation} #{how}: " \
                   "#{usage.input + usage.cache_read + usage.cache_write} input tokens, #{usage.output} output tokens")
    end
  end
  private_constant :Telemetry
end
