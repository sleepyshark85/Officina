# frozen_string_literal: true

require 'test_helper'

# An agent's definition, the checks on a run's input, and the prefix fingerprint that binds a conversation to it.
class AgentTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  SHARED = File.expand_path('../../../testdata', __dir__)
  PREFIX_MISMATCH = "The agent's tools, instructions or model settings differ from those the conversation was " \
                    'started with; start a new conversation'

  def test_gen02_an_agent_needs_only_a_model_and_instructions
    agent = Agent.new(model: Model.new(Model.text('Hello')), instructions: 'You help.')

    assert_equal 'Hello', agent.run(Conversation.new, 'Hi').text
  end

  def test_agt01_an_agent_is_frozen_with_a_sorted_copy_of_its_tools
    tools = [tool('search'), tool('place_order')]
    agent = Agent.new(model: Model.new, instructions: +'You help.', tools:)

    assert_predicate agent, :frozen?
    assert_predicate agent.instructions, :frozen?
    assert_equal %w[place_order search], agent.tools.map(&:name)
    assert_predicate agent.tools, :frozen?
    assert_equal %w[search place_order], tools.map(&:name)
  end

  def test_agt01_blank_instructions_are_refused
    error = assert_raises(Error) { Agent.new(model: Model.new, instructions: " \n") }

    assert_equal 'An agent needs instructions', error.message
  end

  def test_agt01_two_tools_with_one_name_are_refused_whatever_their_descriptions
    tools = [tool('search', description: 'Searches the catalogue.'), tool('search', description: 'Searches orders.')]

    error = assert_raises(Error) { Agent.new(model: Model.new, instructions: 'You help.', tools:) }

    assert_equal 'Two tools are named search', error.message
  end

  def test_agt02_a_blank_message_is_refused
    agent = Agent.new(model: Model.new, instructions: 'You help.')

    error = assert_raises(Error) { agent.run(Conversation.new, " \n") }

    assert_equal 'A run needs a message', error.message
  end

  def test_ctx02_a_blank_run_context_is_refused
    agent = Agent.new(model: Model.new, instructions: 'You help.')

    error = assert_raises(Error) { agent.run(Conversation.new, 'Hi', context: " \n") }

    assert_equal 'A run context cannot be blank', error.message
  end

  def test_ctx01_the_fingerprint_is_the_one_every_implementation_computes
    prefix = JSON.parse(File.read(File.join(SHARED, 'session', 'prefix.json')))
    tools = prefix['tools'].map { tool(it['name'], description: it['description'], schema: it['inputSchema']) }

    agent = Agent.new(model: Model.new(settings: prefix['settings']), instructions: prefix['instructions'],
                      tools: tools.reverse)

    assert_equal prefix['fingerprint'], agent.fingerprint
  end

  def test_ctx04_a_changed_tool_fails_the_run_with_a_prefix_mismatch
    model = Model.new
    changed = Agent.new(model:, instructions: 'You help.', tools: [tool('search', description: 'Searches better.')])

    assert_prefix_mismatch changed, model
  end

  def test_ctx04_changed_instructions_fail_the_run_with_a_prefix_mismatch
    model = Model.new
    changed = Agent.new(model:, instructions: 'You help more.', tools: [tool('search')])

    assert_prefix_mismatch changed, model
  end

  def test_ctx04_a_changed_model_setting_fails_the_run_with_a_prefix_mismatch
    model = Model.new(settings: 'other')
    changed = Agent.new(model:, instructions: 'You help.', tools: [tool('search')])

    assert_prefix_mismatch changed, model
  end

  private

  def tool(name, description: 'Searches the catalogue.', schema: '{"type":"object"}')
    Tool.new(name:, description:, input_schema: schema)
  end

  # Runs the changed agent on a conversation that a run of the original bound: it fails before it calls its model, and
  # leaves the conversation as the original's run left it.
  def assert_prefix_mismatch(changed, model)
    conversation = Conversation.new
    original = Agent.new(model: Model.new(Model.text('Hello')), instructions: 'You help.', tools: [tool('search')])
    original.run(conversation, 'Hi')

    result = changed.run(conversation, 'Again')

    assert_equal Failed.new(reason: :prefix_mismatch, detail: PREFIX_MISMATCH, usage: Usage.new), result
    assert_empty model.requests
    assert_equal 2, conversation.messages.size
    assert_equal original.fingerprint, conversation.fingerprint
  end
end
