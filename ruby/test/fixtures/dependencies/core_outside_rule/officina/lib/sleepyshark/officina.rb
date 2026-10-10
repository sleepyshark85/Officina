# A core that uses more than its rule allows, from line 3 to 7.
require 'json'
require 'opentelemetry/sdk'
require 'minitest'
require 'sleepyshark/officina/mcp'
require_relative '../../../officina-mcp/lib/sleepyshark/officina/mcp'
require ENV.fetch('FEATURE')
require_relative 'officina/version'
require 'opentelemetry'
