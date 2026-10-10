# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The run asks the approver about the +call+ (its input redacted). It may reach the host before or after the
    # approver is asked, so a host that answers from its events must take the answer in either order.
    ApprovalAsked = Data.define(:call)
  end
end
