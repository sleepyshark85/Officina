# frozen_string_literal: true

require 'test_helper'

# Declarations of the schema DSL that are refused when they are defined.
class InputDeclarationTest < Minitest::Test
  cover 'Sleepyshark::Officina::Input*'

  Input = Sleepyshark::Officina::Input

  EITHER = 'tags: give either of: string, integer, number, boolean or a block'
  # Malformed declarations, with what is wrong with them.
  MALFORMED = {
    -> { string :kind, enum: %w[a b], nullable: true } => 'kind: an enum cannot be nullable',
    -> { integer :count, minimum: 0.5 } => 'count: minimum must be an Integer',
    -> { number :price, minimum: 0.5 } => 'price: minimum must be an Integer',
    -> { array :tags } => EITHER,
    -> { array(:tags, of: :string) { string :name } } => EITHER,
    -> { array :tags, of: :object } => 'tags: of: must be one of string, integer, number, boolean',
    lambda {
      string :title
      string :title
    } => 'title: title is declared twice',
    lambda {
      integer :max_price
      integer :maxPrice
    } => 'maxPrice: maxPrice is declared twice'
  }.freeze

  def test_test08_a_malformed_declaration_is_refused_when_it_is_defined
    MALFORMED.each do |declaration, message|
      assert_equal message, assert_raises(ArgumentError) { Input.define(&declaration) }.message
    end
  end

  def test_test08_a_declaration_reaches_only_the_member_methods
    assert_raises(NoMethodError) { Input.define { type 'string', true } }
  end
end
