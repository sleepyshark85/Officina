# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The host's way to stop a run: pass one to Agent#run as +cancel:+ and call #cancel from any thread. Cancelling is
    # cooperative: the run and its model stream stop at their next check. A signal trap must not call #cancel, as it
    # takes a Mutex, which raises in trap context; the trap pushes to a Thread::Queue that a thread of the host reads.
    class Cancellation
      def initialize
        @lock = Mutex.new
        @cancelled = false
        @callbacks = []
      end

      # Asks the run to stop, and calls each callback given to #on_cancel; calling it again changes nothing.
      # @return [void]
      def cancel
        callbacks = @lock.synchronize do
          waiting = @callbacks
          @callbacks = []
          @cancelled = true
          waiting
        end
        callbacks.each(&:call)
      end

      def cancelled?
        @lock.synchronize { @cancelled }
      end

      # Calls the block once the run is cancelled, on the thread that cancels it, or at once when it already is. For
      # what waits and must wake when the run stops; the block must be quick and must not raise.
      # @return [void]
      def on_cancel(&callback)
        already = @lock.synchronize do
          @callbacks << callback unless @cancelled
          @cancelled
        end
        yield if already
      end
    end
  end
end
