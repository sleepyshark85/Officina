# frozen_string_literal: true

require 'test_helper'
require_relative 'support/budgets'

# What a run reports it used, whichever way it ends, and the bound on how far its last call overshoots a budget.
class BudgetReportTest < Minitest::Test
  include Sleepyshark::Officina
  include Budgets

  cover 'Sleepyshark::Officina*'

  FIRST = [UsageReported.new(usage: Usage.new(input: 100, output: 20, cache_write: 1_000, cache_write_hour: 1_000)),
           UsageReported.new(usage: Usage.new(output: 5)),
           *Model.tool_use(Model.tool_use_block('c1', 'search', '{}'), Model.tool_use_block('c2', 'search', '{}'))]
          .freeze
  USED = Usage.new(input: 110, output: 30, cache_read: 1_100, cache_write: 1_000, cache_write_hour: 1_000)

  def test_bud02_bud03_a_result_reports_tokens_cost_model_and_tool_calls_and_duration
    model = priced(FIRST, Model.text('Done.', usage: Usage.new(input: 10, output: 5, cache_read: 1_100)))

    result = run_priced(model, seconds: 1)

    # $4 × 110 + $20 × 30 + $0.20 × 1,100 + $8 × 1,000 per million; each search takes a second.
    assert_equal Completed.new(text: 'Done.', usage: USED, cost: dollars('0.00926'), model_calls: 2, tool_calls: 2,
                               duration: 2.0), result
  end

  def test_bud03_aud01_the_run_ended_entry_holds_the_runs_usage_and_cost
    sink = Testing::RecordingAuditSink.new
    model = priced(FIRST, Model.text('Done.', usage: Usage.new(input: 10, output: 5, cache_read: 1_100)))

    run_priced(model, sink:)

    ended = sink.entries.last

    assert_equal :run_ended, ended.kind
    assert_equal USED, ended.usage
    assert_equal dollars('0.00926'), ended.cost
  end

  def test_bud03_a_model_without_a_price_costs_nothing
    model = Model.new(Model.text('Done.', usage: Usage.new(input: 10, output: 5)))

    result = Agent.new(model:, instructions: 'You help.').run(Conversation.new, 'Go.')

    assert_equal BigDecimal(0), result.cost
  end

  # One planned model call: its prompt's tokens, the output it wants, and whether it compacts, which reads the
  # prompt twice.
  CALL = Pbt.tuple(Pbt.integer(min: 1, max: 3_000), Pbt.integer(min: 0, max: 20_000), Pbt.integer(min: 0, max: 5_000),
                   Pbt.integer(min: 0, max: 8_000), Pbt.boolean)
  # A budget's limits of cost in hundredths of a cent, tokens and model calls; 0 for none.
  LIMITS = Pbt.tuple(Pbt.integer(min: 0, max: 500), Pbt.integer(min: 0, max: 60_000), Pbt.integer(min: 0, max: 8))

  def test_test07_a_budget_is_overshot_by_at_most_one_calls_input_or_twice_that_when_it_compacts
    Pbt.assert do
      Pbt.property(LIMITS, Pbt.array(CALL, min: 1, max: 10)) do |(cost, tokens, calls), plan|
        given = ->(limit) { limit unless limit.zero? }
        budget = Budget.new(cost: given[cost]&./(BigDecimal(10_000)), tokens: given[tokens], model_calls: given[calls])
        model = PlannedModel.new(plan, OPUS)

        result = run_priced(model, budget:)

        assert_within_budget budget, result, model.last_input
      end
    end
  end

  private

  # Only the last call can cross a limit, as each starts below all of them; it overshoots by its input at most.
  def assert_within_budget(budget, result, (input, input_cost))
    assert result.is_a?(Completed) || result.reason == :budget, "#{result.inspect} ended otherwise"
    assert_operator result.usage.total, :<=, budget.tokens + input if budget.tokens
    assert_operator result.cost, :<=, budget.cost + input_cost if budget.cost
    assert_operator result.model_calls, :<=, budget.model_calls if budget.model_calls
  end

  # A model that makes the planned calls in order, each but the last asking for the search tool, and keeps each
  # reply within the request's output limit, as a real model does.
  class PlannedModel
    include Sleepyshark::Officina

    attr_reader :settings, :info

    def initialize(plan, price)
      @plan = plan
      @made = 0
      @settings = 'planned'
      @info = ModelInfo.new(provider: 'planned', name: 'planned', price:)
    end

    # The tokens of the last call's input, and what they cost.
    def last_input
      input, cache_read, cache_write, _, compacts = @plan.fetch(@made - 1)
      factor = compacts ? 2 : 1
      price = @info.price
      [(input + cache_read + cache_write) * factor,
       factor * ((price.input * input) + (price.cache_read * cache_read) + (price.cache_write * cache_write)) /
         1_000_000]
    end

    def stream(request, cancel:)
      input, cache_read, cache_write, output, compacts = @plan.fetch(@made)
      @made += 1
      factor = compacts ? 2 : 1
      output = [output, request.max_output_tokens].compact.min
      yield UsageReported.new(usage: Usage.new(input: input * factor, output:, cache_read: cache_read * factor,
                                               cache_write: cache_write * factor))
      reply unless cancel.cancelled?
    end

    private

    def reply
      return Reply.new(blocks: [Testing::ScriptedModel.text_block('Done.')], stop: :end) if @made == @plan.size

      Reply.new(blocks: [Testing::ScriptedModel.tool_use_block("c#{@made}", 'search', '{}')], stop: :tool_use)
    end
  end
end
