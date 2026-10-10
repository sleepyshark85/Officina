# frozen_string_literal: true

require 'test_helper'

# An agent's definition and the prefix fingerprint that binds a conversation to it.
class AgentTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  SHARED = File.expand_path('../../../testdata', __dir__)

  def test_gen02_an_agent_needs_only_a_model_and_instructions
    agent = Agent.new(model: Model.new(Model.text('Hello')), instructions: 'You help.')

    assert_equal 'Hello', agent.run(Conversation.new, 'Hi').text
  end

  def test_agt01_an_agent_is_frozen_with_its_tools_sorted_by_name
    agent = Agent.new(model: Model.new, instructions: +'You help.', tools: [tool('search'), tool('place_order')])

    assert_predicate agent, :frozen?
    assert_predicate agent.tools, :frozen?
    assert_predicate agent.instructions, :frozen?
    assert_equal %w[place_order search], agent.tools.map(&:name)
  end

  def test_agt01_a_definition_that_cannot_work_is_refused
    assert_raises(Error) { Agent.new(model: Model.new, instructions: ' ') }
    assert_raises(Error) { Agent.new(model: Model.new, instructions: 'Hi', tools: [tool('search'), tool('search')]) }
    assert_raises(Error) { tool('search', schema: '[]') }
    assert_raises(Error) { tool('search', schema: '{"type":') }
    assert_raises(Error) { tool(' ') }
  end

  def test_ctx01_the_fingerprint_is_the_one_every_implementation_computes
    prefix = JSON.parse(File.read(File.join(SHARED, 'session', 'prefix.json')))
    tools = prefix['tools'].map { tool(it['name'], description: it['description'], schema: it['inputSchema']) }

    agent = Agent.new(model: Model.new(settings: prefix['settings']), instructions: prefix['instructions'],
                      tools: tools.reverse)

    assert_equal prefix['fingerprint'], agent.fingerprint
  end

  def test_ctx04_a_changed_tool_instruction_or_model_setting_fails_the_run_with_a_prefix_mismatch
    conversation = Conversation.new
    first = Agent.new(model: Model.new(Model.text('Hello')), instructions: 'You help.', tools: [tool('search')])
    first.run(conversation, 'Hi')
    changes = {
      tools: [tool('search', description: 'Searches better.')],
      instructions: 'You help more.',
      model: Model.new(settings: 'other')
    }

    changes.each do |part, changed|
      model = changed.is_a?(Model) ? changed : Model.new
      definition = { model:, instructions: 'You help.', tools: [tool('search')], part => changed }
      result = Agent.new(**definition).run(conversation, 'Again')

      assert_equal [:prefix_mismatch, true], [result.reason, result.detail.end_with?('start a new conversation')], part
      assert_empty model.requests, part
    end
    assert_equal 2, conversation.messages.size
    assert_equal first.fingerprint, conversation.fingerprint
  end

  private

  def tool(name, description: 'Searches the catalogue.', schema: '{"type":"object"}')
    Tool.new(name:, description:, input_schema: schema)
  end
end
