# frozen_string_literal: true

# A cancellation cancelled from its nth check on, so a request is cancelled while it waits, with no thread to time
# it. Only the thread that makes the request checks it.
class CancelledAfter
  def initialize(checks)
    @checks = checks
  end

  def cancelled? = (@checks -= 1) <= 0
end
