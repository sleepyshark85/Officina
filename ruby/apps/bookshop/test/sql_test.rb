# frozen_string_literal: true

require 'bookshop'
require 'prism'
require 'test_helper'

# The static checks that nothing runs SQL text it was given: they need no database.
class SqlTest < Minitest::Test
  LIB = File.expand_path('../lib', __dir__)
  # pg's methods that send SQL to the server.
  RUNS_SQL = %i[exec exec_params async_exec async_exec_params sync_exec sync_exec_params query prepare send_query
                send_query_params send_prepare].freeze
  OPERATIONS = [Bookshop::Catalogue, Bookshop::Customers, Bookshop::Orders].freeze
  # What the operations take: values, compared or stored as values, never run.
  INPUTS = %i[book_id customer_id email filter id limit lines name quantity text].freeze

  def test_app08_every_query_runs_a_constant_of_literal_sql
    queries = Dir[File.join(LIB, '**', '*.rb')].sum do |file|
      tree = Prism.parse_file(file).value
      sql = literal_constants(tree)
      runs = calls(tree).select { RUNS_SQL.include?(it.name) }
      runs.each { assert_includes sql, constant_name(it.arguments&.arguments&.first), "#{file}: #{it.slice}" }
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

  # The constants a file assigns literal SQL: text, or text with other such constants in it, frozen or not.
  def literal_constants(tree)
    writes = nodes(tree, Prism::ConstantWriteNode)
    writes.each_with_object([]) do |write, literal|
      literal << write.name if literal?(unfrozen(write.value), literal)
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

  def unfrozen(node) = node.is_a?(Prism::CallNode) && node.name == :freeze ? node.receiver : node

  def constant_name(node)
    node.name if node.is_a?(Prism::ConstantReadNode)
  end

  def calls(tree) = nodes(tree, Prism::CallNode)

  def nodes(node, type)
    (node.is_a?(type) ? [node] : []) + node.compact_child_nodes.flat_map { nodes(it, type) }
  end
end
