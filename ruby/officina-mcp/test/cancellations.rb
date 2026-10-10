# frozen_string_literal: true

# A cancellation cancelled from its nth check on, so a request is cancelled while it waits, with no thread to time
# it. Only the thread that makes the request checks it.
class CancelledAfter
  def initialize(checks)
    @checks = checks
  end

  def cancelled? = (@checks -= 1) <= 0
end

# A cancellation cancelled once a file exists, such as the one a server writes its process id to as it starts.
CancelledOnceExists = Data.define(:path) do
  def cancelled? = File.exist?(path)
end

# A cancellation that is never cancelled, for a request that must run to its end although it could be cancelled.
NeverCancelled = Data.define do
  def cancelled? = false
end
