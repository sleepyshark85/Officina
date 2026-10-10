# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    # The built-in audit sink: appends each entry to a file as one JSON object per line, flushed to disk before #write
    # returns. Safe for concurrent runs within one process. The core uses it only when the host passes it to an agent.
    class JsonLinesAuditSink
      # The members whose names are more than one word, in camel case.
      CAMEL_CASE = { memory_scope: :memoryScope, call_id: :callId, trace_id: :traceId, span_id: :spanId }.freeze
      private_constant :CAMEL_CASE

      # @param path [String] the file, created when the first entry is written
      def initialize(path)
        @path = path
        @lock = Mutex.new
      end

      # Appends the entry: its time in ISO 8601 UTC with microseconds, its cost a JSON number, its members in camel
      # case, those it does not have left out.
      # @param entry [AuditEntry]
      # @return [void]
      # @raise [SystemCallError] when the file cannot be written
      def write(entry)
        append("#{JSON.generate(fields(entry))}\n")
      end

      private

      # mutant:disable -- what no test can see: that the line is on disk before it returns (fsync), and that two runs'
      #   lines never interleave (the lock), which a single write in append mode already ensures on Linux
      def append(line)
        @lock.synchronize do
          File.open(@path, 'a') do |file|
            file.write(line)
            file.fsync
          end
        end
      end

      def fields(entry)
        fields = entry.to_h.merge(time: entry.time.utc.iso8601(6), usage: entry.usage&.to_h, cost: number(entry.cost))
        fields.transform_keys { |key| CAMEL_CASE.fetch(key, key) }.compact
      end

      # A cost as its digits, which JSON.generate would write as a string.
      def number(cost) = cost && JSON::Fragment.new(cost.to_s('F'))
    end
  end
end
