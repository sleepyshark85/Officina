# frozen_string_literal: true

module Bookshop
  # The application's audit sink: each entry becomes a row of the shared schema's audit table, written before #write
  # returns, so a failure raises and the core reports it. It also reads a session's entries back for /audit: a
  # session's id is its conversation's. Thread-safe.
  class AuditTable
    INSERT = <<~SQL
      insert into audit (time, sequence, run, conversation, agent, trace_id, span_id, kind, tool, call_id, input,
                         outcome, detail, duration, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                         cost)
      values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, make_interval(secs => $14::float8), $15, $16,
              $17, $18, $19)
    SQL

    SELECT = <<~SQL
      select time, run, trace_id, kind, tool, outcome, detail, extract(epoch from duration)::float8 as duration,
             input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost
      from audit
      where conversation = $1
      order by id
    SQL

    private_constant :INSERT, :SELECT

    def initialize(database:)
      @database = database
      freeze
    end

    # Inserts the entry as a row; its memory scope is left out until the agent has a memory.
    #
    # @param entry [Sleepyshark::Officina::AuditEntry]
    # @raise [PG::Error] when the database cannot be reached
    def write(entry)
      @database.with { |connection| connection.exec_params(INSERT, values(entry)) }
    end

    # The conversation's entries, in the order they were written.
    #
    # @param conversation [String] its id
    # @return [Array<AuditRecord>]
    # @raise [PG::Error] when the database cannot be reached
    def entries(conversation)
      @database.with do |connection|
        connection.exec_params(SELECT, [conversation]).map { record(it) }
      end
    end

    private

    # The row's values, in the insert's order.
    def values(entry)
      [entry.time, entry.sequence, entry.run, entry.conversation, entry.agent, entry.trace_id, entry.span_id,
       kind(entry.kind), entry.tool, entry.call_id, entry.input, entry.outcome, entry.detail, entry.duration,
       *spent(entry)]
    end

    # The tokens and cost columns, which only a run's end, the entry with usage and cost, fills.
    def spent(entry)
      usage = entry.usage
      [usage&.input, usage&.output, usage&.cache_read, usage&.cache_write, entry.cost]
    end

    # The kind as .NET writes it, such as ToolStarted, so the table reads the same from every implementation.
    def kind(symbol) = symbol.to_s.split('_').map(&:capitalize).join

    def record(row)
      AuditRecord.new(time: row[:time], run: row[:run], trace_id: row[:trace_id], kind: row[:kind], tool: row[:tool],
                      outcome: row[:outcome], detail: row[:detail], duration: row[:duration], usage: usage(row),
                      cost: row[:cost])
    end

    def usage(row)
      return unless row[:input_tokens]

      Sleepyshark::Officina::Usage.new(input: row[:input_tokens], output: row[:output_tokens],
                                       cache_read: row[:cache_read_tokens], cache_write: row[:cache_write_tokens])
    end
  end
  private_constant :AuditTable
end
