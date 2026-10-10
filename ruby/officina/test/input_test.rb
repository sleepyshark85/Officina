# frozen_string_literal: true

require 'test_helper'

# The schema DSL: one declaration gives a value class and its JSON schema. input_dotnet_test.rb checks the schemas
# against .NET's bytes.
class InputTest < Minitest::Test
  cover 'Sleepyshark::Officina::Input*'

  Input = Sleepyshark::Officina::Input

  def test_tool01_options_are_written_in_dotnets_key_order_and_names_in_camel_case
    input = Input.define do
      integer :copies, minimum: 0
      number :unit_price, 'Each.', nullable: true, minimum: 1
      boolean :gift, nullable: true
      number :weight
    end

    assert_equal '{"type":"object","properties":{"copies":{"type":"integer","minimum":0},' \
                 '"unitPrice":{"description":"Each.","type":["number","null"],"minimum":1},' \
                 '"gift":{"type":["boolean","null"]},"weight":{"type":"number"}},' \
                 '"required":["copies","unitPrice","gift","weight"],"additionalProperties":false}', input.schema.to_s
  end

  def test_tool01_arrays_of_scalars_and_of_objects_may_be_nullable_and_optional
    assert_equal '{"type":"object","properties":{"tags":{"type":["array","null"],"items":{"type":"integer"}},' \
                 '"parts":{"type":["array","null"],"items":{"type":"object","properties":{"n":{"type":"integer"}},' \
                 '"required":["n"],"additionalProperties":false}}},"required":[],"additionalProperties":false}',
                 arrays.schema.to_s
  end

  def test_tool01_an_array_becomes_a_frozen_array_and_an_absent_one_nil
    tagged = arrays.from_json({ 'tags' => [1, 2] })
    parted = arrays.from_json({ 'parts' => [{ 'n' => 3 }] })

    assert_equal [[1, 2], nil, nil, [3]], [tagged.tags, tagged.parts, parted.tags, parted.parts.map(&:n)]
    assert_predicate tagged.tags, :frozen?
    assert_predicate parted.parts, :frozen?
  end

  def test_tool01_the_value_class_is_a_frozen_data_class_with_the_declared_members_in_order
    order_class = order_input

    assert_operator order_class, :<, Data
    assert_predicate order_class, :frozen?
    assert_equal %i[customer_id note lines], order_class.members
  end

  def test_tool01_valid_json_becomes_a_value_with_nested_values_and_absent_members_nil
    order = order_input.from_json(JSON.parse('{"customerId":4,"lines":[{"bookId":7,"quantity":2}]}'))
    line = order.lines.first

    assert_equal [4, nil, 1], [order.customer_id, order.note, order.lines.size]
    assert_equal [7, 2], [line.book_id, line.quantity]
    assert_predicate order.lines, :frozen?
  end

  def test_tool02_the_declared_schema_validates_input
    assert_equal ['/customerId: must be integer', '/lines/0/quantity: must be at least 1'],
                 order_input.schema.validate({ 'customerId' => '4', 'lines' => [{ 'bookId' => 7, 'quantity' => 0 }] })
  end

  EITHER = 'tags: give either of: string, integer, number, boolean or a block'
  # Malformed declarations, with what is wrong with them.
  MALFORMED = {
    -> { string :kind, enum: %w[a b], nullable: true } => 'kind: an enum cannot be nullable',
    -> { integer :count, minimum: 0.5 } => 'count: minimum must be an Integer',
    -> { number :price, minimum: 0.5 } => 'price: minimum must be an Integer',
    -> { array :tags } => EITHER,
    -> { array(:tags, of: :string) { string :name } } => EITHER,
    -> { array :tags, of: :object } => 'tags: of: must be one of string, integer, number, boolean'
  }.freeze

  def test_test08_a_malformed_declaration_is_refused_when_it_is_defined
    MALFORMED.each do |declaration, message|
      assert_equal message, assert_raises(ArgumentError) { Input.define(&declaration) }.message
    end
  end

  private

  def arrays
    Input.define do
      array :tags, of: :integer, nullable: true, optional: true
      array(:parts, nullable: true, optional: true) { integer :n }
    end
  end

  # Declared in each test, not when the file loads, so mutation testing sees each declaration run.
  def order_input
    Input.define do
      integer :customer_id, "The customer's id."
      string :note, optional: true
      array :lines, 'The books.' do
        integer :book_id
        integer :quantity, minimum: 1
      end
    end
  end
end
