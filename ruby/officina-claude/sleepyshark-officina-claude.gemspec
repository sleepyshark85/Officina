# frozen_string_literal: true

require_relative '../officina/lib/sleepyshark/officina/version'

Gem::Specification.new do |spec|
  spec.name = 'sleepyshark-officina-claude'
  spec.version = Sleepyshark::Officina::VERSION
  spec.authors = ['sleepyshark85']
  spec.summary = "Officina's model adapter for Claude, through the Anthropic SDK."
  spec.license = 'MIT'
  spec.required_ruby_version = '>= 4.0'
  spec.files = Dir.glob('{lib,sig}/**/*', base: __dir__)
  spec.metadata['rubygems_mfa_required'] = 'true'

  spec.add_dependency 'anthropic', '= 1.78.0'
  spec.add_dependency 'sleepyshark-officina', Sleepyshark::Officina::VERSION
end
