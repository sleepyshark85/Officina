# frozen_string_literal: true

require 'test_helper'
require_relative 'support/budgets'

# A run's budget: each limit stops the run before the next model call once used up, and each call's output limit is
# lowered to what the cost and tokens left allow.
class BudgetTest < Minitest::Test
  include Sleepyshark::Officina
  include Budgets

  cover 'Sleepyshark::Officina*'

  def test_bud01_a_cost_budget_used_up_stops_the_run_before_the_next_call_with_its_reason
    model = priced(search_call('c1', Usage.new(input: 100, output: 50)), Model.text('Unused.'))
    conversation = Conversation.new

    result = run_priced(model, budget: Budget.new(cost: dollars('0.001')), conversation:)

    assert_equal Stopped.new(reason: :budget, detail: 'The cost budget is used up: $0.0014 of $0.001.',
                             usage: Usage.new(input: 100, output: 50), cost: dollars('0.0014'), model_calls: 1,
                             tool_calls: 1), result
    assert_equal 1, model.requests.size
    assert_equal %i[user assistant user], conversation.messages.map(&:role)
  end

  def test_bud01_each_calls_output_limit_is_lowered_to_what_the_cost_and_tokens_left_allow
    model = priced(search_call('c1', Usage.new(input: 200, output: 100)), Model.text('Done.'), Model.text('Free.'))

    run_priced(model, budget: Budget.new(cost: dollars('0.01'), tokens: 1_000))
    run_priced(model)

    # $0.01 buys 500 output tokens, and 1,000 tokens are left; then $0.0072 buys 360, and 700 tokens are left; then
    # nothing limits them.
    assert_equal [500, 360, nil], limits(model)
  end

  def test_bud01_a_token_budget_alone_lowers_the_output_limit_to_the_tokens_left
    model = priced(search_call('c1', Usage.new(input: 200, output: 100)), Model.text('Done.'))

    run_priced(model, budget: Budget.new(tokens: 1_000))

    assert_equal [1_000, 700], limits(model)
  end

  # Each case: the price, the cost budget, the output limits of the requests made, and whether the run stopped for
  # the budget.
  BOUNDARIES = {
    # $0.00002 buys exactly one output token at $20 per million: the call is made, limited to it.
    'what is left buys one output token' => [OPUS, '0.00002', [1], false],
    # Nothing is left, so nothing more may be spent, even where output costs nothing.
    'nothing left, output free' => [OPUS.with(output: BigDecimal(0)), '0', [], true],
    # Output that costs nothing is not limited by what is left.
    'something left, output free' => [OPUS.with(output: BigDecimal(0)), '0.01', [nil], false]
  }.freeze

  def test_bud01_the_cost_budget_at_its_boundaries
    BOUNDARIES.each do |name, (price, cost, requests, stopped)|
      model = priced(Model.text('Y'), price:)

      result = run_priced(model, budget: Budget.new(cost: dollars(cost)))

      assert_equal requests, limits(model), name
      assert_equal stopped, result.is_a?(Stopped), name
    end
  end

  def test_bud01_a_reply_cut_short_by_the_lowered_limit_stops_for_the_budget_and_one_cut_by_the_models_own_does_not
    model = priced(Model.stop(:max_tokens, usage: Usage.new(input: 10, output: 50)),
                   Model.stop(:max_tokens, usage: Usage.new(input: 10, output: 30)))

    budget_cut = run_priced(model, budget: Budget.new(cost: dollars('0.001')))
    own_cut = run_priced(model, budget: Budget.new(cost: dollars('0.01')))

    assert_equal Stopped.new(reason: :budget, detail: 'The cost budget is used up: $0.00104 of $0.001.',
                             usage: Usage.new(input: 10, output: 50), cost: dollars('0.00104'), model_calls: 1),
                 budget_cut
    assert_equal :output_limit, own_cut.reason
  end

  # Each limit, and the reason it stops a run whose one call used 1,500 tokens in a search of ten seconds.
  LIMITS = {
    Budget.new(model_calls: 1) => 'The model call budget is used up: 1 of 1.',
    Budget.new(tokens: 100) => 'The token budget is used up: 1,500 of 100 tokens.',
    Budget.new(time: 5) => 'The time budget is used up: 10 s of 5 s.'
  }.freeze

  def test_bud01_model_call_time_and_token_limits_stop_the_run_before_the_next_call
    LIMITS.each do |budget, reason|
      model = priced(search_call('c1', Usage.new(input: 1_000, output: 500)), Model.text('Unused.'))

      result = run_priced(model, budget:, seconds: 10)

      assert_equal Stopped.new(reason: :budget, detail: reason, usage: Usage.new(input: 1_000, output: 500),
                               cost: dollars('0.014'), model_calls: 1, tool_calls: 1, duration: 10.0), result
      assert_equal 1, model.requests.size
    end
  end

  def test_bud01_a_used_up_budget_stops_before_the_first_call_and_appends_nothing
    model = priced(Model.text('Unused.'))
    conversation = Conversation.new

    result = run_priced(model, budget: Budget.new(cost: BigDecimal(0)), conversation:)

    assert_equal Stopped.new(reason: :budget, detail: 'The cost budget is used up: $0 of $0.'), result
    assert_empty model.requests
    assert_empty conversation.messages
  end

  def test_bud01_a_cost_budget_is_used_up_when_what_is_left_buys_less_than_one_output_token
    # The first call costs $0.0014; the $0.00001 left would buy half an output token at $20 per million.
    model = priced(search_call('c1', Usage.new(input: 100, output: 50)), Model.text('Unused.'))

    result = run_priced(model, budget: Budget.new(cost: dollars('0.00141')))

    assert_equal 'The cost budget is used up: $0.0014 of $0.00141.', result.detail
  end

  def test_bud01_a_cost_budget_needs_a_model_with_a_price
    agent = Agent.new(model: Model.new, instructions: 'You help.')

    error = assert_raises(Error) { agent.run(Conversation.new, 'Go.', budget: Budget.new(cost: BigDecimal(1))) }

    assert_equal 'A cost budget needs a model with a price', error.message
  end

  def test_bud01_a_call_budget_used_up_on_the_last_allowed_call_stops_for_the_budget_not_the_iteration_limit
    model = priced(*Array.new(24) { search_call("c#{it}", Usage.new(input: 1, output: 1)) })

    result = run_priced(model, budget: Budget.new(model_calls: 24))

    assert_equal :budget, result.reason
    assert_equal 'The model call budget is used up: 24 of 24.', result.detail
  end

  def test_bud01_a_reply_cut_by_the_models_own_limit_stops_for_that_limit_even_when_a_call_budget_is_used_up
    model = priced(Model.stop(:max_tokens, usage: Usage.new(input: 10, output: 10)))

    assert_equal :output_limit, run_priced(model, budget: Budget.new(model_calls: 1)).reason
  end

  def test_bud01_a_time_budget_is_used_up_the_moment_it_is_reached
    model = priced(search_call('c1', Usage.new(input: 1, output: 1)), Model.text('Unused.'))

    result = run_priced(model, budget: Budget.new(time: 2), seconds: 2)

    assert_equal 'The time budget is used up: 2 s of 2 s.', result.detail
  end
end
