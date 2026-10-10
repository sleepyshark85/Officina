# frozen_string_literal: true

require 'test_helper'

# The settings of context management, which an agent's prefix holds, and the provider's support they need.
class ContextManagementTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel

  def test_hist03_context_management_needs_valid_settings
    {
      'The compaction threshold must be positive, not 0' => -> { ContextManagement.new(compact_at: 0) },
      'Tool results are cleared after at least 1 tool call, not 0' => -> { ToolResultClearing.new(after: 0) },
      'Tool result clearing cannot keep -1 tool calls' => -> { ToolResultClearing.new(after: 3, keep: -1) },
      'Tool result clearing cannot clear at least -1 tokens' => lambda {
        ToolResultClearing.new(after: 3, at_least_tokens: -1)
      }
    }.each { |message, make| assert_equal message, assert_raises(Error, &make).message }
  end

  def test_hist02_clearing_keeps_none_and_clears_whatever_there_is_unless_told
    assert_equal ToolResultClearing.new(after: 1, keep: 0, at_least_tokens: 0), ToolResultClearing.new(after: 1)
    assert_equal ContextManagement.new(compact_at: nil, clear_tool_results: nil), ContextManagement.new
    assert_equal '', ContextManagement.new.fingerprint
  end

  def test_hist03_context_management_needs_the_providers_support
    {
      "The model's provider does not compact conversations" => [ContextManagement.new(compact_at: 50_000), {}],
      "The model's provider does not clear old tool results" =>
        [ContextManagement.new(clear_tool_results: ToolResultClearing.new(after: 3)), { compacts: true }]
    }.each do |message, (context_management, support)|
      info = ModelInfo.new(provider: 'scripted', name: 'scripted', **support)

      error = assert_raises(Error) do
        Agent.new(model: Model.new(info:), instructions: 'You help.', context_management:)
      end

      assert_equal message, error.message
    end
  end

  def test_hist03_a_provider_that_does_one_of_compacting_and_clearing_takes_context_management_asking_for_it
    clearing = ContextManagement.new(clear_tool_results: ToolResultClearing.new(after: 3))
    compaction = ContextManagement.new(compact_at: 50_000)

    [[clearing, { clears_tool_results: true }], [compaction, { compacts: true }]].each do |context_management, support|
      model = Model.new(info: ModelInfo.new(provider: 'scripted', name: 'scripted', **support))

      agent = Agent.new(model:, instructions: 'You help.', context_management:)

      assert_same context_management, agent.context_management
    end
  end
end
