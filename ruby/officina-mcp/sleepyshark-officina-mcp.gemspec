# frozen_string_literal: true

require_relative '../officina/lib/sleepyshark/officina/version'

Gem::Specification.new do |spec|
  spec.name = 'sleepyshark-officina-mcp'
  spec.version = Sleepyshark::Officina::VERSION
  spec.authors = ['sleepyshark85']
  spec.summary = "Officina's MCP client, over stdio and Streamable HTTP."
  spec.license = 'MIT'
  spec.required_ruby_version = '>= 4.0'
  spec.files = Dir.glob('{lib,sig}/**/*', base: __dir__)
  spec.metadata['rubygems_mfa_required'] = 'true'

  # 3.0 on: JSON.parse refuses comments and a name given twice, which the reading of servers' responses relies on.
  spec.add_dependency 'json', '~> 3.0'
  spec.add_dependency 'sleepyshark-officina', Sleepyshark::Officina::VERSION
end
