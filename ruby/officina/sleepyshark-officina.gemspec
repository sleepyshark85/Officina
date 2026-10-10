# frozen_string_literal: true

require_relative 'lib/sleepyshark/officina/version'

Gem::Specification.new do |spec|
  spec.name = 'sleepyshark-officina'
  spec.version = Sleepyshark::Officina::VERSION
  spec.authors = ['sleepyshark85']
  spec.summary = 'A purpose-neutral library for building agentic applications.'
  spec.license = 'MIT'
  spec.required_ruby_version = '>= 4.0'
  spec.files = Dir.glob('{lib,sig}/**/*', base: __dir__)
  spec.metadata['rubygems_mfa_required'] = 'true'

  spec.add_dependency 'bigdecimal', '~> 4.0'
  spec.add_dependency 'opentelemetry-api', '~> 1.11'
  # Pre-1.0 and marked experimental, so pinned exactly.
  spec.add_dependency 'opentelemetry-metrics-api', '= 0.9.0'
end
