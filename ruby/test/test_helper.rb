# frozen_string_literal: true

# What every test in the workspace loads first, before any test file (the Rakefile's test prelude).

# First, so a warning about any workspace file loaded after it fails the tests. Ruby's documented way to handle
# warnings is to override Warning.warn.
require_relative 'workspace_warnings'
Warning.singleton_class.prepend(WorkspaceWarnings)

if ENV['COVERAGE']
  require 'simplecov'
  SimpleCov.start { skip %r{/test/} }
end

require 'minitest/autorun'
require 'mutant/minitest/coverage'
require 'pbt'
require 'sleepyshark/officina/testing'

# Property tests draw one seed per run, which a failure prints; PROPERTY_SEED sets it, to reproduce one or for CI.
Pbt.configure { |config| config.seed = Integer(ENV.fetch('PROPERTY_SEED')) } if ENV.key?('PROPERTY_SEED')

# Minitest's documented way to extend every test is to include a module of lifecycle hooks in Minitest::Test.
Minitest::Test.include(Sleepyshark::Officina::Testing::ThreadLeakCheck)
