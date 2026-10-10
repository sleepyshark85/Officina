# frozen_string_literal: true

require 'json'

module Sleepyshark
  module Officina
    # The built-in audit sink: appends each entry to a file as one JSON object per line, flushed to disk before #write
    # returns. Safe for concurrent runs within one process. The core uses it only when the host passes it to an agent.
    class JsonLinesAuditSink
      # @param path [String] the file, created when the first entry is written
      def initialize(path)
        @path = -path
        @lock = Mutex.new
        freeze
      end

      # Appends the entry: its time in ISO 8601 UTC with microseconds, its members in camel case, those it does not
      # have left out.
      # @param entry [AuditEntry]
      # @return [void]
      # @raise [SystemCallError] when the file cannot be written
      def write(entry)
        line = "#{JSON.generate(fields(entry))}\n"
        @lock.synchronize do
          File.open(@path, 'a') do |file|
            file.write(line)
            file.fsync
          end
        end
        nil
      end

      private

      def fields(entry)
        fields = entry.to_h.merge(time: entry.time.utc.iso8601(6), usage: entry.usage&.to_h)
        fields.transform_keys { |key| key == :call_id ? :callId : key }.compact
      end
    end
  end
end
