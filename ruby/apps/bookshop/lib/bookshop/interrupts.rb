# frozen_string_literal: true

module Bookshop
  # Turns Ctrl+C into cancelling the reply in progress. The trap only pushes to a queue, as a Mutex raises in trap
  # context; a thread of its own reads the queue and cancels the reply's Cancellation. Between replies Ctrl+C does
  # nothing, as at a shell's prompt: /quit, or the end of the input, leaves.
  class Interrupts
    def initialize
      @signals = Thread::Queue.new
      @lock = Mutex.new
      @reply = nil
    end

    # Watches for Ctrl+C while the block runs, in place of what Ctrl+C did before, which it puts back.
    #
    # @return the block's value
    def watch
      previous = Signal.trap('INT') { @signals << :interrupt }
      watcher = Thread.new { @lock.synchronize { @reply&.cancel } while @signals.pop }
      begin
        yield
      ensure
        Signal.trap('INT', previous)
        # The trap, the queue's only sender, is gone, so nothing can push after this.
        @signals.close
        watcher.join
      end
    end

    # Lets Ctrl+C cancel the reply while the block runs; once it returns, a Ctrl+C read late no longer reaches it.
    #
    # @param reply [Sleepyshark::Officina::Cancellation] the reply's
    # @return the block's value
    def cancelling(reply)
      @lock.synchronize { @reply = reply }
      yield
    ensure
      @lock.synchronize { @reply = nil }
    end
  end
  private_constant :Interrupts
end
