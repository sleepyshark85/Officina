# frozen_string_literal: true

require 'bookshop'
require 'json'
require 'test_helper'

# What the tools tell the model, which needs no database: the shop's pool connects only on a tool's first call.
class ToolDefinitionsTest < Minitest::Test
  # What .NET defines, by tool name: its description and input schema.
  DOTNET = File.readlines(File.join(__dir__, 'fixtures/dotnet-tools.tsv'), chomp: true)
               .reject { it.start_with?('#') }
               .to_h { |line| line.split("\t", 3).then { |name, *definition| [name, definition] } }.freeze
  WRITES = %w[add_customer cancel_order place_order restock_book].freeze

  def setup
    super
    @shop = Bookshop::Shop.open('postgres://nobody@127.0.0.1:1/nowhere')
    @tools = Bookshop::Tools.all(@shop)
  end

  def teardown
    @shop.close
    super
  end

  def test_tool01_every_tool_has_dotnets_name_description_and_input_schema
    defined = @tools.to_h { [it.name, [it.description, it.input_schema]] }

    assert_equal DOTNET, defined
  end

  def test_app06_the_four_writes_need_approval_and_the_five_reads_do_not
    writes = @tools.select(&:write?).map(&:name)
    approved = @tools.select(&:needs_approval?).map(&:name)

    assert_equal WRITES, writes.sort
    assert_equal WRITES, approved.sort
  end

  def test_app08_the_only_text_inputs_are_values_compared_as_values
    texts = @tools.flat_map { text_properties(JSON.parse(it.input_schema)) }

    assert_equal %w[author email genre name nameOrEmail title], texts.uniq.sort
  end

  private

  # The names of an object schema's string properties, those of its arrays' objects included.
  def text_properties(schema)
    schema.fetch('properties', {}).flat_map do |name, property|
      own = Array(property['type']).include?('string') ? [name] : []
      own + (property['items'] ? text_properties(property['items']) : [])
    end
  end
end
