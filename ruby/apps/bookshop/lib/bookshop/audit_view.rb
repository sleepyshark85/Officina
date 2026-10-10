# frozen_string_literal: true

module Bookshop
  # What /audit shows: a session's entries from the audit table, grouped by run, each run with a link to its trace on
  # the telemetry dashboard, each entry with its local time, kind, tool and outcome, and each run's end with its tokens
  # and cost. The text is .NET's and Go's, line for line.
  class AuditView
    Usage = Sleepyshark::Officina::Usage
    private_constant :Usage

    # @param table [AuditTable]
    # @param dashboard [String] the dashboard's address, such as http://localhost:18888
    def initialize(table:, dashboard:)
      @table = table
      @dashboard = dashboard.delete_suffix('/')
      freeze
    end

    # The session's audit trail as text, or why it could not be read.
    #
    # @param session [String] the session's id, its conversation's
    def show(session)
      trail(session, @table.entries(session))
    rescue PG::Error => e
      "The audit trail could not be read: #{e.message.strip}"
    end

    private

    def trail(session, records)
      return "No audit entries for session #{session}." if records.empty?

      runs = records.group_by(&:run).values.each_with_index.map do |run, index|
        ["Run #{index + 1}, trace: #{link(run)}", *run.map { line(it) }]
      end
      ["Audit of session #{session}:", *runs.flatten].join("\n")
    end

    def link(run)
      trace = run.filter_map(&:trace_id).first
      trace ? "#{@dashboard}/traces/detail/#{trace}" : 'none recorded'
    end

    def line(record)
      time = record.time.localtime.strftime('%H:%M:%S')
      "  #{time}  #{record.kind.ljust(16)}  #{record.tool.to_s.ljust(20)}  #{outcome(record)}".rstrip
    end

    def outcome(record)
      case record
      in { kind: 'RunEnded', usage: Usage => usage } then "#{record.outcome}  #{spent(record, usage)}"
      in { kind: 'ApprovalAnswered', detail: String => reason } then "#{record.outcome}: #{reason}"
      in { kind: 'ToolEnded', duration: Float => seconds } then "#{record.outcome}  #{milliseconds(seconds)}"
      else record.outcome.to_s
      end
    end

    # A run's tokens and cost.
    def spent(record, usage)
      "tokens: #{thousands(usage.input + usage.cache_read + usage.cache_write)} in " \
        "(#{thousands(usage.cache_read)} cached), #{thousands(usage.output)} out, $#{dollars(record.cost)}"
    end

    def milliseconds(seconds) = "#{thousands((seconds * 1000).round)} ms"

    # The cost to four decimals, such as 0.0123; nothing when it is not known.
    def dollars(cost) = format('%.4f', cost || 0)

    # The count, not negative, with its thousands separated by commas.
    def thousands(count) = count.to_s.reverse.scan(/\d{1,3}/).join(',').reverse
  end
  private_constant :AuditView
end
