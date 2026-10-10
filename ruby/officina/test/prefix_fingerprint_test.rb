# frozen_string_literal: true

require 'test_helper'

# The prefix fingerprint every implementation computes, against the shared fixture: the tools, the instructions and the
# model settings, with an output schema, and with each case of context management.
class PrefixFingerprintTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Model = Testing::ScriptedModel
  PREFIX = JSON.parse(File.read(File.expand_path('../../../testdata/session/prefix.json', __dir__)), freeze: true)

  def test_ctx01_the_fingerprint_is_the_one_every_implementation_computes
    agent = Agent.new(model: Model.new(settings: PREFIX['settings']), instructions: PREFIX['instructions'],
                      tools: shared_tools.reverse)

    assert_equal PREFIX['fingerprint'], agent.fingerprint
  end

  def test_ctx01_out01_the_fingerprint_with_an_output_schema_is_the_one_every_implementation_computes
    output = Input.define do
      string :title, "A title «short», with <b>&amp; 'quotes' + more."
      integer :copies, minimum: 0
      array :changes, of: :string, optional: true
      string :note, nullable: true
    end
    model = Model.new(settings: PREFIX['settings'], info: ModelInfo.new(provider: 's', name: 's', compacts: true,
                                                                        clears_tool_results: true))

    PREFIX['output'].each do |part|
      agent = Agent.new(model:, instructions: PREFIX['instructions'], tools: shared_tools, output:,
                        context_management: (context_management(part) if part['compactAt']))

      assert_equal part['schema'], output.schema.to_s
      assert_equal part['fingerprint'], agent.fingerprint, part.to_s
    end
  end

  def test_ctx01_hist01_the_fingerprint_with_context_management_is_the_one_every_implementation_computes
    model = Model.new(settings: PREFIX['settings'], info: ModelInfo.new(provider: 's', name: 's', compacts: true,
                                                                        clears_tool_results: true))

    PREFIX['contextManagement'].each do |part|
      agent = Agent.new(model:, instructions: PREFIX['instructions'], tools: shared_tools,
                        context_management: context_management(part))

      assert_equal part['fingerprint'], agent.fingerprint, part.to_s
    end
  end

  private

  def shared_tools
    PREFIX['tools'].map do |tool|
      Tool.new(name: tool['name'], description: tool['description'], input: Schema.new(tool['inputSchema']),
               kind: :read) { 'Found.' }
    end
  end

  # The context management of a case of the shared fixture.
  def context_management(part)
    clearing = (if part['clearAfter']
                  ToolResultClearing.new(after: part['clearAfter'], keep: part['clearKeep'],
                                         at_least_tokens: part['clearAtLeastTokens'])
                end)
    ContextManagement.new(compact_at: part['compactAt'], clear_tool_results: clearing)
  end
end
