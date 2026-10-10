# frozen_string_literal: true

module Sleepyshark
  module Officina
    # A run that went wrong, and why: +:model_error+ (a model call failed after its retries), +:unexpected_stop+ (the
    # model stopped in a way the run cannot act on) or +:prefix_mismatch+ (the agent's tools, instructions or model
    # settings differ from those the conversation was started with). +detail+ says what happened. With the +usage+ of
    # all its model calls.
    Failed = Data.define(:reason, :detail, :usage)
  end
end
