# frozen_string_literal: true

require 'test_helper'

# A tool, a call of one, and the call's result.
class ToolTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  def test_tool01_a_tool_with_a_blank_name_is_refused
    error = assert_raises(Error) { Tool.new(name: " \n", description: 'Searches.', input_schema: '{}') }

    assert_equal 'A tool needs a name', error.message
  end

  def test_tool01_a_tool_whose_schema_is_not_a_json_object_is_refused
    ['[]', '"object"', '{"type":', '{"type":"object","type":"array"}'].each do |schema|
      error = assert_raises(Error, schema) { Tool.new(name: 'search', description: 'Searches.', input_schema: schema) }

      assert_equal 'The input schema of tool search is not a JSON object', error.message, schema
    end
  end

  def test_tool01_a_tool_keeps_frozen_copies_of_its_strings
    name = +'search'
    description = +'Searches.'
    schema = +'{ "type": "object" }'

    tool = Tool.new(name:, description:, input_schema: schema)
    [name, description, schema].each { |string| string << '!' }

    assert_equal Tool.new(name: 'search', description: 'Searches.', input_schema: '{ "type": "object" }'), tool
    assert_predicate tool.name, :frozen?
    assert_predicate tool.description, :frozen?
    assert_predicate tool.input_schema, :frozen?
  end

  def test_a_tool_call_keeps_frozen_copies_of_its_strings
    id = +'call_1'
    name = +'search'
    input = +'{"q":"x"}'

    call = ToolCall.new(id:, name:, input:)
    [id, name, input].each { |string| string << '!' }

    assert_equal ToolCall.new(id: 'call_1', name: 'search', input: '{"q":"x"}'), call
    assert_predicate call.id, :frozen?
    assert_predicate call.name, :frozen?
    assert_predicate call.input, :frozen?
  end

  def test_a_tool_result_keeps_frozen_copies_of_its_strings
    call_id = +'call_1'
    content = +'3 copies.'

    result = ToolResult.new(call_id:, content:, error: false)
    [call_id, content].each { |string| string << '!' }

    assert_equal ToolResult.new(call_id: 'call_1', content: '3 copies.', error: false), result
    assert_predicate result.call_id, :frozen?
    assert_predicate result.content, :frozen?
  end

  def test_a_tool_result_says_whether_the_call_failed
    assert_predicate ToolResult.new(call_id: 'call_1', content: 'No such book.', error: true), :error?
    refute_predicate ToolResult.new(call_id: 'call_1', content: '3 copies.', error: false), :error?
  end
end
