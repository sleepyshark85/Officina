# frozen_string_literal: true

require_relative 'claude_test_case'

# Typed output through Claude's structured output, its schema adjusted to what the API accepts, on the fake API.
class OutputTest < ClaudeTestCase
  # .NET's hand-written schema with every keyword the adjustment changes, and a property named like one of them; the
  # shared output-written.json holds what .NET sends for it.
  WRITTEN = <<~JSON
    {"type":"object","properties":{
      "count":{"type":"integer","minimum":1,"maximum":9,"exclusiveMinimum":0,"exclusiveMaximum":10,"multipleOf":1},
      "maximum":{"type":"number"},
      "tags":{"type":"array","items":{"type":"string","pattern":"^[a-z]+$","minLength":1},"minItems":1,"maxItems":3},
      "pairs":{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"}}},"minItems":2},
      "note":{"anyOf":[{"type":"null"},{"type":["object","null"],"properties":{"text":{"type":"string"}}}]}},
     "required":["count"]}
  JSON

  def test_out01_a_written_schema_is_closed_and_stripped_of_what_the_api_rejects_as_dotnet_sends_it
    api = serve(text_reply)
    # Generated again from the parsed JSON, which keeps the order of members, to compare it too.
    expected = JSON.generate(JSON.parse(testdata('claude/output-written.json')))

    collect(model(api, effort: :low), hi.with(output_schema: WRITTEN))

    assert_equal expected, JSON.generate(JSON.parse(api.bodies[0])['output_config'])
  end

  def test_out01_gen05_a_structured_reply_comes_back_as_the_typed_output_and_tool_choice_is_never_forced
    order = Input.define do
      string :customer
      array(:lines) { integer :copies, minimum: 1 }
    end
    api = serve(text_reply(text: '{"customer":"Ana","lines":[{"copies":2}]}'))
    agent = Agent.new(model: model(api), instructions: 'Read the order.', output: order)
    # The core validates the minimum; the API rejects it.
    sent_schema = '{"type":"object","properties":{"customer":{"type":"string"},"lines":{"type":"array","items":' \
                  '{"type":"object","properties":{"copies":{"type":"integer"}},"required":["copies"],' \
                  '"additionalProperties":false}}},"required":["customer","lines"],"additionalProperties":false}'

    result = agent.run(Conversation.new, 'Ana wants two copies.')

    assert_equal ['Ana', 2], [result.output.customer, result.output.lines.first.copies]
    sent = JSON.parse(api.bodies[0])

    assert_equal({ 'type' => 'json_schema', 'schema' => JSON.parse(sent_schema) }, sent.dig('output_config', 'format'))
    refute sent.key?('tool_choice')
  end

  def test_out01_an_open_object_is_refused_rather_than_closed_and_nothing_is_sent
    api = serve
    open = '{"type":"object","properties":{"counts":{"type":"object","additionalProperties":{"type":"integer"}}},' \
           '"additionalProperties":false}'

    error = assert_raises(Claude::InvalidRequestError) { collect(model(api), hi.with(output_schema: open)) }

    assert_equal 'The output schema has an open object, which structured output cannot express', error.message
    assert_empty api.bodies
  end
end
