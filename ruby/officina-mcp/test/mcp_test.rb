# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/mcp'

# The MCP gem, which has nothing more yet.
class McpTest < Minitest::Test
  def test_the_gem_loads
    assert_kind_of Module, Sleepyshark::Officina::Mcp
  end
end
