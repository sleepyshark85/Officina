# frozen_string_literal: true

require 'bigdecimal'
require_relative 'fake_clock'

# What the budget tests share: Opus 5.5's price, a search tool that takes time on the agent's clock, and runs of an
# agent on a scripted model with that price.
module Budgets
  Officina = Sleepyshark::Officina
  Model = Officina::Testing::ScriptedModel
  # $4 in, $20 out, $0.20 a cache read; a cache write 1.25 times input for five minutes, twice input for an hour.
  OPUS = Officina::Price.new(input: BigDecimal(4), output: BigDecimal(20), cache_read: BigDecimal('0.20'),
                             cache_write: BigDecimal(5), cache_write_hour: BigDecimal(8))
  OBJECT = Officina::Schema.new('{"type":"object"}')

  # A scripted model with the price, Opus 5.5's unless given.
  def priced(*replies, price: OPUS)
    Model.new(*replies, info: Officina::ModelInfo.new(provider: 'scripted', name: 'scripted', price:))
  end

  # A reply that calls the search tool, reporting the usage.
  def search_call(id, usage) = Model.tool_use(Model.tool_use_block(id, 'search', '{}'), usage:)

  # Runs an agent with the search tool on the model, each search taking the seconds on the agent's clock.
  def run_priced(model, budget: nil, seconds: 0, conversation: Officina::Conversation.new, sink: nil)
    clock = FakeClock.new
    search = Officina::Tool.new(name: 'search', description: 'Searches.', input: OBJECT, kind: :read) do |_, _|
      clock.advance(seconds) || 'Found.'
    end
    Officina::Agent.new(model:, instructions: 'You help.', tools: [search], clock:, audit_sink: sink)
                   .run(conversation, 'Go.', budget:)
  end

  # The output limit of each request the model received.
  def limits(model) = model.requests.map(&:max_output_tokens)
end
