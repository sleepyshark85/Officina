# frozen_string_literal: true

require 'test_helper'
require 'sleepyshark/officina/claude'

# The Claude gem, which has nothing more yet.
class ClaudeTest < Minitest::Test
  def test_the_gem_loads
    assert_kind_of Module, Sleepyshark::Officina::Claude
  end
end
