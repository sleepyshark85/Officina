# frozen_string_literal: true

require 'bookshop'
require 'test_helper'

# The budgets a reply runs within, and how the console writes tokens and dollars.
class BudgetsTest < Minitest::Test
  Officina = Sleepyshark::Officina
  # Private to the application, which hands them only to the console.
  Budgets = Bookshop.const_get(:Budgets)
  Spent = Bookshop.const_get(:Spent)

  def test_app14_a_reply_runs_within_the_lower_of_its_budget_and_what_is_left_of_the_sessions
    budgets = Budgets.from({})

    assert_equal [BigDecimal('0.5'), BigDecimal(5)], [budgets.reply, budgets.session]
    assert_equal([BigDecimal('0.5'), BigDecimal('0.5'), BigDecimal('0.25'), BigDecimal(0)],
                 %w[0 4.5 4.75 5.5].map { budgets.for_reply(BigDecimal(it)).cost })
  end

  def test_app14_a_budget_stop_says_which_budget_was_reached
    budgets = Budgets.from({ 'BOOKSHOP_REPLY_BUDGET' => '0.125' })

    assert_equal 'this reply has reached its budget of $0.125.', budgets.reached(BigDecimal('4.875') - 1)
    assert_equal 'this session has reached its budget of $5.00. Type /new to start a new session.',
                 budgets.reached(BigDecimal('4.875'))
  end

  def test_app14_a_reply_budget_that_is_not_an_amount_above_zero_is_refused
    %w[0 -1 abc].each do |setting|
      error = assert_raises(ArgumentError) { Budgets.from({ 'BOOKSHOP_REPLY_BUDGET' => setting }) }

      assert_equal %(BOOKSHOP_REPLY_BUDGET "#{setting}" is not an amount of US dollars above zero), error.message
    end
  end

  def test_app14_tokens_show_all_the_input_and_the_share_read_from_the_cache_rounded_half_away_from_zero
    usage = Officina::Usage.new(input: 999, output: 1234, cache_read: 1, cache_write: 1000)

    assert_equal 'tokens: 2,000 in (0% from cache), 1,234 out', Spent.tokens(usage)
    assert_equal 'tokens: 200 in (1% from cache), 0 out', Spent.tokens(Officina::Usage.new(input: 199, cache_read: 1))
    assert_equal 'tokens: 0 in (0% from cache), 0 out', Spent.tokens(Officina::Usage.new)
  end

  def test_app14_dollars_round_half_away_from_zero_to_four_decimals_and_budgets_keep_two_to_four
    amounts = %w[0.00005 0.000049 12.34565 5].map { BigDecimal(it) }

    assert_equal(%w[0.0001 0.0000 12.3457 5.0000], amounts.map { Spent.dollars(it) })
    assert_equal(%w[5.00 0.50 0.125 0.001], %w[5 0.5 0.125 0.001].map { Spent.budget(BigDecimal(it)) })
  end
end
