# frozen_string_literal: true

require 'bookshop'
require 'test_helper'

# The chat agent's model: its settings enter the prefix fingerprint, so they are .NET's and Go's.
class ChatAgentTest < Minitest::Test
  ChatAgent = Bookshop.const_get(:ChatAgent)

  def test_app17_demo_mode_keeps_claudes_caches_five_minutes_and_otherwise_an_hour
    assert_equal 'claude model=claude-opus-5-5 effort=medium max_tokens=16000 cache=5m thinking=adaptive',
                 ChatAgent.claude(demo: true).settings
    assert_equal 'claude model=claude-opus-5-5 effort=medium max_tokens=16000 cache=1h thinking=adaptive',
                 ChatAgent.claude(demo: false).settings
  end
end
