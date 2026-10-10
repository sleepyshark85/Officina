# frozen_string_literal: true

module Bookshop
  # Turns Ctrl+C into cancelling the reply in progress, or, with none in progress, into leaving, as .NET's and Go's
  # consoles do. The trap only pushes to a queue, as a Mutex raises in trap context; a thread of its own reads the
  # queue and cancels the reply's Cancellation, or runs +idle+.
  class Interrupts
    # @param idle [#call] what Ctrl+C does with no reply in progress, on the watcher's thread
    def initialize(idle:)
      @idle = idle
      @signals = Thread::Queue.new
      @lock = Mutex.new
      @reply = nil
    end

    # Watches for Ctrl+C while the block runs, in place of what Ctrl+C did before, which it puts back.
    #
    # @return the block's value
    def watch
      previous = Signal.trap('INT') { @signals << :interrupt }
      watcher = Thread.new { interrupt while @signals.pop }
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

    private

    def interrupt
      @lock.synchronize do
        reply = @reply
        reply ? reply.cancel : @idle.call
      end
    end
  end
  private_constant :Interrupts
end
