# frozen_string_literal: true

# What the tests of tool calls share: tools over one small input, the calls of a reply, and a run of an agent on a
# scripted model that asks for them and then answers.
module ToolCalls
  Officina = Sleepyshark::Officina
  Model = Officina::Testing::ScriptedModel
  Search = Officina::Input.define { string :title, 'Part of the title.' }

  # A run of an agent whose model asked for the calls: its result, its conversation and its model.
  Ran = Data.define(:result, :conversation, :model) do
    # The results the calls got, in call order.
    def results = conversation.messages.fetch(2).blocks.map(&:tool_result)
  end

  def tool(name, kind: :read, needs_approval: false, &)
    Officina::Tool.new(name:, description: "Does #{name}.", input: Search, kind:, needs_approval:, &)
  end

  def call(id, name, input = '{"title":"Dune"}') = Model.tool_use_block(id, name, input)

  # Runs an agent with the tools and the agent's other parts, whose model asks for the calls, then says "Done.". A
  # block that breaks leaves this method too.
  def run_calls(tools, *calls, cancel: nil, **parts, &)
    model = Model.new(Model.tool_use(*calls), Model.text('Done.'))
    conversation = Officina::Conversation.new
    result = Officina::Agent.new(model:, instructions: 'You help.', tools:, **parts).run(conversation, 'Go', cancel:, &)
    Ran.new(result:, conversation:, model:)
  end
end
