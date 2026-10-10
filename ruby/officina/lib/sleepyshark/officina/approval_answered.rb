# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The approver answered about the +call+ (its input redacted), or failed to, which denies it: +approved+ says
    # which.
    ApprovalAnswered = Data.define(:call, :approved)
  end
end
