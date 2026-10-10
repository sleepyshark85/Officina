# frozen_string_literal: true

require 'opentelemetry-api'
require 'opentelemetry-metrics-api'

module Sleepyshark
  module Officina
    # Where the core's traces and metrics go: the host's tracer and meter providers, through the OpenTelemetry API; the
    # host chooses the exporter. Each run is one trace, under the span current where the host runs it, if any: a span
    # for the run, with a span per model call and per tool call under it. Names follow the OpenTelemetry generative-AI
    # conventions where they exist, else +officina.+, the same as .NET's and Go's, so one dashboard reads all three.
    # Message text, tool inputs and results appear only when the host opts in, and never a secret.
    #
    # Make one per pair of providers and give it to every agent that reports to them: it makes the core's instruments,
    # which a meter takes only once.
    class Telemetry
      # The name of the core's tracer and meter.
      NAME = 'Sleepyshark.Officina'
      Instrument = Data.define(:name, :unit, :description)

      # An instrument the core makes: its name, unit and description, as .NET's.
      class Instrument
        def histogram(meter) = meter.create_histogram(name, unit:, description:)
        def counter(meter) = meter.create_counter(name, unit:, description:)
      end

      # The instruments, by kind.
      HISTOGRAMS = {
        tokens: Instrument.new('gen_ai.client.token.usage', '{token}',
                               'Tokens per model call, by type: input (all of it, cached or not) and output.'),
        cache_tokens: Instrument.new('officina.model.cache_tokens', '{token}',
                                     'Input tokens per model call read from or written to the cache, ' \
                                     'by officina.cache.type: read or write. Part of the input tokens.'),
        model_duration: Instrument.new('gen_ai.client.operation.duration', 's',
                                       'Duration of a model call, retries included.'),
        cache_hit_ratio: Instrument.new('officina.model.cache_hit_ratio', '1',
                                        "The share of a model call's input tokens read from the cache."),
        cost: Instrument.new('officina.model.cost', '{USD}',
                             "What a model call cost, in US dollars, at the model's price."),
        tool_duration: Instrument.new('officina.tool.duration', 's',
                                      'Duration of a tool call, from its start to its result, approval included.')
      }.freeze
      COUNTERS = {
        retries: Instrument.new('officina.model.retries', '{retry}', 'Model call retries.'),
        tool_calls: Instrument.new('officina.tool.calls', '{call}', 'Tool calls, by outcome.'),
        approvals: Instrument.new('officina.tool.approvals', '{approval}', 'Approvals, by answer.'),
        runs: Instrument.new('officina.runs', '{run}', 'Runs, by result.'),
        audit_failures: Instrument.new('officina.audit.failures', '{entry}', 'Audit entries the sink failed to write.')
      }.freeze
      private_constant :Instrument, :HISTOGRAMS, :COUNTERS

      # @param tracer_provider [OpenTelemetry::Trace::TracerProvider, nil] the API's no-op one unless given: no spans
      # @param meter_provider [OpenTelemetry::Metrics::MeterProvider, nil] the API's no-op one unless given: no metrics
      # @param content [Boolean] whether spans carry the user's message, the model's text and tools' inputs and results,
      #   their secrets redacted
      def initialize(tracer_provider: nil, meter_provider: nil, content: false)
        @tracer = (tracer_provider || OpenTelemetry::Trace::TracerProvider.new).tracer(NAME)
        meter = (meter_provider || OpenTelemetry::Metrics::MeterProvider.new).meter(NAME)
        @histograms = HISTOGRAMS.transform_values { it.histogram(meter) }.freeze
        @counters = COUNTERS.transform_values { it.counter(meter) }.freeze
        @content = content
        freeze
      end

      # Whether spans carry text.
      def content? = @content

      # Starts a span under the parent context; it is not made current. The core's way in, as Ruby has no visibility
      # between classes; a host has no need of it.
      # @param kind [Symbol] +:internal+ or +:client+
      # @param at [Time] its start, from the agent's clock
      # @return [OpenTelemetry::Trace::Span]
      def start_span(name, parent:, kind:, attributes:, at:)
        @tracer.start_span(name, with_parent: parent, kind:, attributes:, start_timestamp: at)
      end

      # Adds to one of the core's counters. The core's way in, as #start_span.
      # @param counter [Symbol] which: +:runs+, +:retries+…
      def add(counter, value, attributes) = @counters.fetch(counter).add(value, attributes:)

      # Records in one of the core's histograms. The core's way in, as #start_span.
      # @param histogram [Symbol] which: +:tokens+, +:cost+…
      def record(histogram, value, attributes) = @histograms.fetch(histogram).record(value, attributes:)
    end
  end
end
