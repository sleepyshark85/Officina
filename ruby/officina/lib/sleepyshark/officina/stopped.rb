# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A run that ended before the model's answer, and why: +:cancelled+ (the host cancelled), +:refusal+ (the model
    # declined; +detail+ is its category), +:output_limit+ (the reply reached the output token limit), +:context_full+
    # (the conversation no longer fits the model's context window) or +:iteration_limit+ (the run made as many model
    # calls as it may). With the +usage+ of all its model calls.
    Stopped = Data.define(:reason, :detail, :usage)
  end
end
