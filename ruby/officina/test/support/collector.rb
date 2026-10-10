# frozen_string_literal: true

require 'opentelemetry-sdk'
require 'opentelemetry-metrics-sdk'

# The OpenTelemetry SDK with an in-memory span exporter and metric reader: it collects the spans and metrics of the
# agents given its telemetry. The SDK is for tests only; the core uses the API alone.
class Collector
  SDK = OpenTelemetry::SDK
  # One data point of a metric: its attributes, its sum (a counter's value) and its count (nil for a counter).
  Point = Data.define(:metric, :attributes, :sum, :count)

  # @return [Sleepyshark::Officina::Telemetry] what an agent takes to report here
  attr_reader :telemetry
  # @return [OpenTelemetry::SDK::Trace::TracerProvider] for a host's own spans
  attr_reader :tracer_provider

  # @param content [Boolean] whether the telemetry carries text
  def initialize(content: false)
    @spans = SDK::Trace::Export::InMemorySpanExporter.new
    @tracer_provider = SDK::Trace::TracerProvider.new
    @tracer_provider.add_span_processor(SDK::Trace::Export::SimpleSpanProcessor.new(@spans))
    @reader = SDK::Metrics::Export::InMemoryMetricPullExporter.new
    meter_provider = SDK::Metrics::MeterProvider.new
    meter_provider.add_metric_reader(@reader)
    @telemetry = Sleepyshark::Officina::Telemetry.new(tracer_provider: @tracer_provider, meter_provider:, content:)
  end

  # @return [Array<OpenTelemetry::SDK::Trace::SpanData>] the spans ended so far, in the order they ended
  def spans = @spans.finished_spans

  # The one span of the name; it raises NoMatchingPatternError when there is none or more than one.
  def span(name)
    spans.select { it.name == name } => [span]
    span
  end

  # The values of the keys in the attributes of the one span of the name.
  def attributes(name, *keys) = span(name).attributes.values_at(*keys)

  # @return [Array<OpenTelemetry::SDK::Metrics::State::MetricData>] the metrics as they stand
  def metrics
    @reader.reset
    @reader.pull
    @reader.metric_snapshots
  end

  # The data points of the metrics, by metric and attributes, without the attributes every measurement has (the
  # agent, provider and model), which +dimensions+ must hold.
  # @return [Array<Point>]
  def points(dimensions = {})
    points = metrics.flat_map do |metric|
      metric.data_points.map do |point|
        raise "#{metric.name} #{point.attributes} lacks #{dimensions}" unless point.attributes >= dimensions

        histogram = metric.instrument_kind == :histogram
        Point.new(metric.name, point.attributes.except(*dimensions.keys), histogram ? point.sum : point.value,
                  (point.count if histogram))
      end
    end
    points.sort_by { [it.metric, it.attributes.sort.to_s] }
  end

  # The data points of the metric, as #points.
  def points_of(metric, dimensions = {}) = points(dimensions).select { it.metric == metric }

  # Everything the spans and metrics carry, as text, one value a line: names, statuses, attributes and events.
  def dump
    texts = spans.flat_map do |span|
      [span.name, span.status.description, *flat(span.attributes),
       *span.events.to_a.flat_map { [it.name, *flat(it.attributes)] }]
    end
    [*texts, *points.flat_map { [it.metric, *flat(it.attributes)] }].join("\n")
  end

  private

  # Each key and value as text, each item of a list on its own.
  def flat(attributes) = attributes.to_a.flatten.map(&:to_s)
end
