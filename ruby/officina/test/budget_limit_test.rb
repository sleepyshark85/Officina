# frozen_string_literal: true

require 'test_helper'
require_relative 'support/budgets'

# A budget's limits at their edges: below zero, and at a midpoint of the digits the reason a run stops shows.
class BudgetLimitTest < Minitest::Test
  include Sleepyshark::Officina
  include Budgets

  cover 'Sleepyshark::Officina*'

  # Each limit below zero, as a host might compute from what is left of a larger budget, and its reason.
  BELOW_ZERO = {
    Budget.new(model_calls: -1) => 'The model call budget is used up: 0 of -1.',
    Budget.new(tokens: -1) => 'The token budget is used up: 0 of -1 tokens.',
    Budget.new(cost: BigDecimal(-1)) => 'The cost budget is used up: $0 of $-1.',
    Budget.new(time: -1) => 'The time budget is used up: 0 s of -1 s.'
  }.freeze

  def test_bud01_a_limit_below_zero_is_used_up_before_the_first_call
    BELOW_ZERO.each do |budget, reason|
      model = priced(Model.text('Unused.'))

      result = run_priced(model, budget:)

      assert_equal Stopped.new(reason: :budget, detail: reason), result
      assert_empty model.requests
    end
  end

  # Dollar limits at a midpoint of the six decimals a reason shows, and the reason, rounded half away from zero as
  # .NET's is.
  MIDPOINTS = {
    '0.0000125' => 'The cost budget is used up: $0 of $0.000013.',
    '0.0000005' => 'The cost budget is used up: $0 of $0.000001.'
  }.freeze

  def test_bud01_a_reason_rounds_dollars_at_a_midpoint_half_away_from_zero
    MIDPOINTS.each do |cost, reason|
      assert_equal reason, run_priced(priced(Model.text('Unused.')), budget: Budget.new(cost: BigDecimal(cost))).detail
    end
  end

  def test_bud01_a_reason_rounds_dollars_half_away_from_zero_whatever_the_threads_bigdecimal_rounding_mode
    mode = BigDecimal.mode(BigDecimal::ROUND_MODE)
    BigDecimal.mode(BigDecimal::ROUND_MODE, :banker)
    model = priced(Model.text('Unused.'))

    result = run_priced(model, budget: Budget.new(cost: BigDecimal('0.0000125')))

    assert_equal 'The cost budget is used up: $0 of $0.000013.', result.detail
  ensure
    BigDecimal.mode(BigDecimal::ROUND_MODE, mode)
  end

  def test_bud01_a_reason_rounds_seconds_at_a_midpoint_half_away_from_zero
    model = priced(search_call('c1', Usage.new(input: 1, output: 1)), Model.text('Unused.'))

    result = run_priced(model, budget: Budget.new(time: 2.25), seconds: 2.25)

    assert_equal 'The time budget is used up: 2.3 s of 2.3 s.', result.detail
  end

  def test_bud01_a_reason_keeps_every_digit_of_a_large_dollar_amount
    budget = Budget.new(cost: BigDecimal('123456789012.1234565'))
    model = priced(Model.text('Unused.'), price: OPUS.with(output: BigDecimal('1e18')))

    assert_equal 'The cost budget is used up: $0 of $123456789012.123457.', run_priced(model, budget:).detail
  end
end
