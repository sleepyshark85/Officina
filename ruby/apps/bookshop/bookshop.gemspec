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

  spec.add_dependency 'sleepyshark-officina', Sleepyshark::Officina::VERSION
end
