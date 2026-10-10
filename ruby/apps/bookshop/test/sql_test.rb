# frozen_string_literal: true

require 'bookshop'
require 'prism'
require 'test_helper'

# The static checks that nothing runs SQL text it was given: they need no database.
class SqlTest < Minitest::Test
  LIB = File.expand_path('../lib/bookshop', __dir__)
  OPERATIONS = [Bookshop::Catalogue, Bookshop::Customers, Bookshop::Orders].freeze
  # What the operations take: values, compared or stored as values, never run.
  INPUTS = %i[author book_id customer_id email genre id in_stock limit lines max_price name quantity text
              title].freeze

  def test_app08_every_query_runs_a_constant_of_literal_sql
    queries = Dir[File.join(LIB, '*.rb')].sum do |file|
      tree = Prism.parse_file(file).value
      sql = literal_constants(tree)
      runs = calls(tree).select { %i[exec exec_params exec_prepared async_exec query].include?(it.name) }
      runs.each do |run|
        argument = run.arguments&.arguments&.first

        name = argument.name if argument.is_a?(Prism::ConstantReadNode)

        assert_includes sql, name, "#{file}:#{run.location.start_line} runs #{argument&.slice}, not a SQL constant"
      end
      runs.size
    end

    assert_operator queries, :>=, 15
  end

  def test_app08_the_operations_take_only_values
    inputs = OPERATIONS.flat_map do |operations|
      operations.public_instance_methods(false).flat_map { operations.instance_method(it).parameters.map(&:last) }
    end

    assert_equal INPUTS, inputs.uniq.sort
  end

  private

  # The names of the constants a file assigns a string literal without interpolation.
  def literal_constants(node)
    names = node.is_a?(Prism::ConstantWriteNode) && literal?(node.value) ? [node.name] : []
    names + node.compact_child_nodes.flat_map { literal_constants(it) }
  end

  # A string literal: a squiggly heredoc's lines are parts, each a literal unless interpolated.
  def literal?(node)
    node.is_a?(Prism::StringNode) ||
      (node.is_a?(Prism::InterpolatedStringNode) && node.parts.all?(Prism::StringNode))
  end

  def calls(node)
    (node.is_a?(Prism::CallNode) ? [node] : []) + node.compact_child_nodes.flat_map { calls(it) }
  end
end
