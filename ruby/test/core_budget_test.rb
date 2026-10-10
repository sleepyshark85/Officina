# frozen_string_literal: true

require 'test_helper'

# The core stays small: a budget of code lines (lines of the core's lib/ that are neither blank nor only a comment),
# raised only by a change to its decision in docs/implementations/ruby.md that says why.
class CoreBudgetTest < Minitest::Test
  BUDGET = 2_800
  LIB = File.expand_path('../officina/lib', __dir__)

  def test_the_core_keeps_within_its_line_budget
    lines = Dir.glob('**/*.rb', base: LIB).sum { code_lines(File.join(LIB, it)) }

    assert_operator lines, :<=, BUDGET, "The core has #{lines} code lines, over its budget of #{BUDGET}"
  end

  private

  def code_lines(file) = File.readlines(file).count { !it.strip.empty? && !it.strip.start_with?('#') }
end
