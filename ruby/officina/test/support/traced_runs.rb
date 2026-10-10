# frozen_string_literal: true

require_relative 'collector'
require_relative 'fake_clock'
require_relative 'tool_calls'

# What the telemetry tests share, besides the tool calls': a priced model of another provider, steps of replies, a
# human who takes a while, and an agent whose telemetry a collector gathers.
module TracedRuns
  include ToolCalls

  Officina = Sleepyshark::Officina

  # The attributes of every measurement of an unnamed agent on the scripted model.
  SCRIPTED = { 'gen_ai.provider.name' => 'scripted', 'gen_ai.request.model' => 'scripted' }.freeze

  # A human who takes the seconds, on the clock, to give the answer.
  Waiting = Data.define(:clock, :seconds, :approved) do
    def approve(_tool, _call, **)
      clock.advance(seconds)
      Officina::Approval.new(approved:)
    end
  end

  # The steps of a reply that streams the text, reports the usage and ends with the stop, holding the blocks after its
  # text's.
  def reply(text, usage, *blocks, stop: :end)
    [Officina::TextDelta.new(text:), Officina::UsageReported.new(usage:),
     Officina::Reply.new(blocks: [Model.text_block(text), *blocks], stop:)]
  end

  # A model of another provider, with a price: 4, 20, 0.20, 5 and 8 dollars per million input, output, cache read,
  # five-minute and one-hour cache write tokens. Made in the test, so that mutation testing sees it made.
  def acme
    Officina::ModelInfo.new(provider: 'acme', name: 'acme-large', price: Officina::Price.new(
      input: BigDecimal(4), output: BigDecimal(20), cache_read: BigDecimal('0.2'), cache_write: BigDecimal(5),
      cache_write_hour: BigDecimal(8)
    ))
  end

  def usage(input, output, cache_read = 0, cache_write = 0, cache_write_hour = 0)
    Officina::Usage.new(input:, output:, cache_read:, cache_write:, cache_write_hour:)
  end

  # An agent of the model, its telemetry gathered by the collector.
  def traced(collector, model, **parts)
    Officina::Agent.new(model:, instructions: 'You help.', telemetry: collector.telemetry, **parts)
  end
end
