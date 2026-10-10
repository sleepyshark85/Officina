# frozen_string_literal: true

require 'bookshop'
require 'prism'
require 'test_helper'

# The static checks that nothing runs SQL text it was given: they need no database.
class SqlTest < Minitest::Test
  LIB = File.expand_path('../lib', __dir__)
  # pg's methods that send SQL to the server, and which of their arguments is the SQL: a prepared statement's name
  # comes first.
  SQL_ARGUMENT = {
    exec: 0, exec_params: 0, async_exec: 0, async_exec_params: 0, sync_exec: 0, sync_exec_params: 0, query: 0,
    send_query: 0, send_query_params: 0, prepare: 1, send_prepare: 1
  }.freeze
  OPERATIONS = [Bookshop::Catalogue, Bookshop::Customers, Bookshop::Orders].freeze
  # What the operations take: values, compared or stored as values, never run.
  INPUTS = %i[book_id customer_id email filter id limit lines name quantity text].freeze

  def test_app08_every_query_runs_a_constant_of_literal_sql
    queries = Dir[File.join(LIB, '**', '*.rb')].sum do |file|
      tree = Prism.parse_file(file).value
      sql = literal_constants(tree)
      runs = calls(tree).select { SQL_ARGUMENT.key?(it.name) }
      runs.each { assert_includes sql, constant_name(sql_argument(it)), "#{file}: #{it.slice}" }
      runs.size
    end

    assert_operator queries, :>=, 15
  end

  def test_app08_the_operations_take_only_values
    inputs = OPERATIONS.flat_map do |operations|
      operations.public_instance_methods(false).flat_map { operations.instance_method(it).parameters.map(&:last) }
    end

    assert_equal INPUTS, inputs.uniq.sort
    assert_equal %i[author genre in_stock max_price title], Bookshop::BookFilter.members.sort
  end

  private

  def sql_argument(call) = call.arguments&.arguments&.[](SQL_ARGUMENT.fetch(call.name))

  # The constants a file assigns literal SQL: text, or text with other such constants in it, frozen or chomped.
  def literal_constants(tree)
    writes = nodes(tree, Prism::ConstantWriteNode)
    writes.each_with_object([]) do |write, literal|
      literal << write.name if literal?(text_of(write.value), literal)
    end
  end

  def literal?(node, literal)
    case node
    in Prism::StringNode then true
    in Prism::InterpolatedStringNode then node.parts.all? { literal_part?(it, literal) }
    else false
    end
  end

  def literal_part?(part, literal)
    case part
    in Prism::StringNode then true
    in Prism::EmbeddedStatementsNode
      body = part.statements.body
      body.one? && literal.include?(constant_name(body.first))
    else false
    end
  end

  # The text a constant's value is made from: .freeze and .chomp change no SQL.
  def text_of(node)
    node.is_a?(Prism::CallNode) && %i[freeze chomp].include?(node.name) ? text_of(node.receiver) : node
  end

  def constant_name(node)
    node.name if node.is_a?(Prism::ConstantReadNode)
  end

  def calls(tree) = nodes(tree, Prism::CallNode)

  def nodes(node, type)
    (node.is_a?(type) ? [node] : []) + node.compact_child_nodes.flat_map { nodes(it, type) }
  end
end
