# frozen_string_literal: true

require 'test_helper'

# A tool's definition: its parts, its handler and what it refuses.
class ToolTest < Minitest::Test
  include Sleepyshark::Officina

  cover 'Sleepyshark::Officina::Tool*'

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
    assert_equal [true, true], [tool.write?, tool.needs_approval?]
  end

  def test_tool02_input_that_cannot_reach_the_handler_is_said_for_the_model
    tool = Tool.new(name: 'search', description: 'Searches.', input: Search, kind: :read) { 'Found.' }

    assert_nil tool.input_problem('{"title":"Dune","maxPrice":3}')
    problem = tool.input_problem('{"maxPrice":-1}')

    assert_equal ["The input does not match the tool's schema:", '/maxPrice: must be at least 0',
                  '/title: is required'], [problem.lines.first.chomp, *problem.lines.drop(1).map(&:chomp).sort]
    assert_match(/\AThe input is not valid JSON: /, tool.input_problem('{"title":'))
  end

  def test_tool01_a_definition_that_cannot_work_is_refused
    {
      'A tool needs a name' => { name: ' ' },
      'kind must be :read or :write, not :other' => { kind: :other },
      'needs a handler' => { handler: nil },
      "is not an object's" => { input: Schema.new('true') }
    }.each do |message, change|
      parts = { name: 'search', description: 'Searches.', input: Search, kind: :read, handler: -> { 'Found.' } }
      parts.merge!(change)
      handler = parts.delete(:handler)

      error = assert_raises(Error) { Tool.new(**parts, &handler) }

      assert_match message, error.message
    end
  end
end
