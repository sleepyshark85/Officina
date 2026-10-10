# frozen_string_literal: true

require 'sleepyshark/officina'
require_relative 'testing/fake_mcp_server'
require_relative 'testing/fake_mcp_tool'
require_relative 'testing/thread_leak_check'

module Sleepyshark
  module Officina
    # Officina's test kit: what tests of agents need in place of the real boundaries.
    module Testing
    end
  end
end
