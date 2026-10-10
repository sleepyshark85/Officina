# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Testing
      # An audit sink that keeps its entries in memory, for a test to read; it can be told to fail.
      class RecordingAuditSink
        # @param fails [#call, nil] given each entry, says whether writing it fails, as a full disk would
        def initialize(fails: nil)
          @fails = fails
          @entries = []
          @lock = Mutex.new
        end

        # @return [Array<AuditEntry>] the entries written so far, in order
        def entries = @lock.synchronize { @entries.dup.freeze }

        # Keeps the entry.
        # @raise [IOError] when +fails+ says so
        def write(entry)
          raise IOError, "Recording sink: entry #{entry.sequence} could not be written" if @fails&.call(entry)

          @lock.synchronize { @entries << entry }
          nil
        end
      end
    end
  end
end
