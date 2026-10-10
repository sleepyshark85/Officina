# frozen_string_literal: true

# What every test in the workspace loads first.

if ENV['COVERAGE']
  require 'simplecov'
  SimpleCov.start { add_filter %r{/test/} }
end

require 'minitest/autorun'
require 'mutant/minitest/coverage'
require 'pbt'
require 'sleepyshark/officina/testing'
require_relative 'workspace_warnings'

# Ruby's documented way to handle warnings is to override Warning.warn.
Warning.singleton_class.prepend(WorkspaceWarnings)

# Property tests draw one seed per run, which a failure prints; PROPERTY_SEED sets it, to reproduce one or for CI.
Pbt.configure { |config| config.seed = Integer(ENV.fetch('PROPERTY_SEED')) } if ENV.key?('PROPERTY_SEED')

# Minitest's documented way to extend every test is to include a module of lifecycle hooks in Minitest::Test.
Minitest::Test.include(Sleepyshark::Officina::Testing::ThreadLeakCheck)
