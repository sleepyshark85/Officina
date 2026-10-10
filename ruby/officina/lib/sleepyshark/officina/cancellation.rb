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
      end

      # Asks the run to stop; calling it again changes nothing.
      # @return [void]
      def cancel
        @lock.synchronize { @cancelled = true }
        nil
      end

      def cancelled?
        @lock.synchronize { @cancelled }
      end
    end
  end
end
