# frozen_string_literal: true

require 'bookshop'

# The application's telemetry with the SDK's in-memory exporters in place of OTLP: the spans, metrics and log records
# it collects, for a test to read. It fails a test whose telemetry the SDK complained of, which it only logs.
class MemoryTelemetry
  SDK = OpenTelemetry::SDK

  # The SDK's in-memory metric reader, which a meter provider can shut down: the SDK's own shutdown takes no timeout,
  # which the provider passes.
  class MetricReader < SDK::Metrics::Export::InMemoryMetricPullExporter
    def shutdown(*) = super()
  end

  # @return [Bookshop::Telemetry] what Bookshop.build takes
  attr_reader :telemetry

  def initialize
    @log = StringIO.new
    OpenTelemetry.logger = Logger.new(@log)
    @spans = SDK::Trace::Export::InMemorySpanExporter.new
    @metrics = MetricReader.new
    @logs = SDK::Logs::Export::InMemoryLogRecordExporter.new
    @telemetry = Bookshop.const_get(:Telemetry).new(spans: SDK::Trace::Export::SimpleSpanProcessor.new(@spans),
                                                    metrics: @metrics,
                                                    logs: SDK::Logs::Export::SimpleLogRecordProcessor.new(@logs))
  end

  # The spans ended so far, in the order they ended. Closing the telemetry clears them.
  def spans
    quiet!
    @spans.finished_spans
  end

  # The log records emitted so far. Closing the telemetry clears them.
  def logs
    quiet!
    @logs.emitted_log_records
  end

  # The names of the metrics recorded so far.
  def metric_names
    quiet!
    @metrics.pull
    @metrics.metric_snapshots.map(&:name).uniq.sort
  end

  private

  def quiet!
    raise "The OpenTelemetry SDK logged:\n#{@log.string}" unless @log.string.empty?
  end
end
