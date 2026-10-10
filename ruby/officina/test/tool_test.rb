# frozen_string_literal: true

require 'test_helper'

# A tool's definition, its handler and what it refuses; a call of one, and the call's result.
class ToolTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina*'

  Search = Input.define do
    string :title, 'Part of the title.'
    integer :max_price, optional: true, minimum: 0
  end

  def test_tool01_a_tool_from_a_declared_input_gets_its_value_and_describes_itself_with_its_schema
    tool = Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read) do |input, _cancel|
      "#{input.title} under #{input.max_price.inspect}"
    end

    assert_equal 'Dune under nil', tool.invoke('{"title":"Dune"}', Cancellation.new)
    assert_equal Search.schema.to_s, tool.input_schema
    assert_equal [:read, false, false], [tool.kind, tool.write?, tool.needs_approval?]
    assert_predicate tool, :frozen?
  end

  def test_tool01_a_tool_from_a_schema_gets_the_json_value_and_a_result_that_is_not_text_is_sent_as_json
    schema = Schema.new('{"type":"object","properties":{"id":{"type":"integer"}}}')
    tool = Tool.new(name: 'order', description: 'Orders.', input: schema, kind: :write,
                    needs_approval: true) do |input, _|
      { 'ordered' => input['id'], 'note' => nil }
    end

    assert_equal '{"ordered":7,"note":null}', tool.invoke('{"id":7}', Cancellation.new)
    assert_equal [:write, true, true], [tool.kind, tool.write?, tool.needs_approval?]
  end

  def test_tool01_the_handler_gets_the_runs_cancellation
    cancel = Cancellation.new
    tool = Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read) do |_, given|
      given.equal?(cancel)
    end

    assert_equal 'true', tool.invoke('{"title":"Dune"}', cancel)
  end

  def test_tool02_input_that_cannot_reach_the_handler_is_said_for_the_model
    tool = Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read) { 'Found.' }
    problem = tool.input_problem('{"maxPrice":-1}')

    assert_nil tool.input_problem('{"title":"Dune","maxPrice":3}')
    assert_equal ["The input does not match the tool's schema:", '/maxPrice: must be at least 0',
                  '/title: is required'], [problem.lines.first.chomp, *problem.lines.drop(1).map(&:chomp).sort]
    assert_match(/\AThe input is not valid JSON: /, tool.input_problem('{"title":'))
    assert_match(/\AThe input is not valid JSON: .*duplicate key/, tool.input_problem('{"title":"a","title":"b"}'))
  end

  def test_tool01_a_definition_that_cannot_work_is_refused_saying_why
    {
      'A tool needs a name' => { name: " \n" },
      "Tool search's kind must be :read or :write, not :other" => { kind: :other },
      'Tool search needs a handler' => { handler: nil },
      'The input schema of tool search is not a JSON object' => { input: Schema.new('true') }
    }.each do |message, change|
      parts = { name: 'search', description: 'Searches.', input: Search, kind: :read, handler: -> { 'Found.' } }
      handler = parts.merge!(change).delete(:handler)

      assert_equal message, assert_raises(Error) { Tool.new(**parts, &handler) }.message
    end
  end

  def test_tool01_a_tool_keeps_frozen_copies_of_its_strings
    name = +'search'
    description = +'Searches.'

    tool = Tool.new(name:, description:, input: Search, kind: :read) { 'Found.' }
    [name, description].each { |string| string << '!' }

    assert_equal %w[search Searches.], [tool.name, tool.description]
    assert_predicate tool.name, :frozen?
    assert_predicate tool.description, :frozen?
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

  def test_tool05_a_failure_the_handler_returns_is_passed_on_as_it_is
    tool = Tool.new(name: 'order', description: 'Orders.', input: Search, kind: :write) do |_, _|
      ToolFailure.new(message: 'Not enough stock.')
    end

    assert_equal ToolFailure.new(message: 'Not enough stock.'), tool.invoke('{"title":"Dune"}', Cancellation.new)
  end

  def test_tool05_a_tool_failure_keeps_a_frozen_copy_of_its_message
    message = +'Not enough stock.'

    failure = ToolFailure.new(message:)
    message << '!'

    assert_equal 'Not enough stock.', failure.message
    assert_predicate failure.message, :frozen?
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
