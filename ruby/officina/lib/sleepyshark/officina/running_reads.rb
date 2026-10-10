# frozen_string_literal: true

module Sleepyshark
  module Officina
    # The read calls of one reply that are running, each on a thread of its own, by call index. Only the tool
    # pipeline's thread starts, joins and stops them.
    class RunningReads
      def initialize(cancel)
        @cancel = cancel
        @threads = {}
      end

      # Runs the block on a thread of its own, for the call at the index.
      def start(index, &)
        @threads[index] = QuietThread.start(&)
      end

      # Waits for every read: their results, by call index. What a read raised is raised.
      def join
        @threads.transform_values(&:value).tap { @threads.clear }
      end

      # Cancels the reads still running and waits for each, so none outlives the run. Called when the pipeline ends
      # early, on an exception already on its way.
      def stop
        return if @threads.empty?

        @cancel.cancel
        join_all(@threads.values)
      end

      private

      # Joins every thread; what the first that failed raised is raised once all have ended.
      def join_all(threads)
        # @type var thread: Thread?
        # @type var rest: Array[Thread]
        thread, *rest = threads
        thread&.join
      ensure
        join_all(rest) if thread
      end
    end
    private_constant :RunningReads
  end
end
