# frozen_string_literal: true

require_relative '../../officina/lib/sleepyshark/officina/version'

Gem::Specification.new do |spec|
  spec.name = 'bookshop'
  spec.version = Sleepyshark::Officina::VERSION
  spec.authors = ['sleepyshark85']
  spec.summary = "Bookshop Assistant, Officina's reference application: a console chatbot over PostgreSQL."
  spec.license = 'MIT'
  spec.required_ruby_version = '>= 4.0'
  spec.files = Dir.glob('{exe,lib,sig}/**/*', base: __dir__)
  spec.bindir = 'exe'
  spec.executables = ['bookshop']
  spec.metadata['rubygems_mfa_required'] = 'true'

  spec.add_dependency 'bigdecimal', '~> 4.0'
  spec.add_dependency 'connection_pool', '~> 3.0'
  # Telemetry to the compose file's dashboard over OTLP/HTTP (R16); the pre-1.0 gems pinned exactly.
  spec.add_dependency 'opentelemetry-exporter-otlp', '= 0.37.0'
  spec.add_dependency 'opentelemetry-exporter-otlp-logs', '= 0.6.0'
  spec.add_dependency 'opentelemetry-exporter-otlp-metrics', '= 0.13.0'
  spec.add_dependency 'opentelemetry-logs-sdk', '= 0.8.0'
  spec.add_dependency 'opentelemetry-metrics-sdk', '= 0.20.0'
  spec.add_dependency 'opentelemetry-sdk', '~> 1.13'
  spec.add_dependency 'pg', '~> 1.7'
  spec.add_dependency 'sleepyshark-officina', Sleepyshark::Officina::VERSION
  spec.add_dependency 'sleepyshark-officina-claude', Sleepyshark::Officina::VERSION
end
