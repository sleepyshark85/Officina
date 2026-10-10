# frozen_string_literal: true

module Bookshop
  # The session store: the shared schema's sessions table, one row per conversation keyed by its id, as .NET and Go
  # keep it. The conversation is the core's JSON in a text column, so it reads back byte for byte and resumes with its
  # prefix and cache intact. Thread-safe.
  class SessionStore
    CREATE = <<~SQL
      insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens,
                            cache_write_tokens, cost, updated)
      values ($1, $2, $3, $4, $5, $6, $7, $8, now())
      on conflict (id) do nothing
    SQL

    UPDATE = <<~SQL
      update sessions
      set conversation = $3, input_tokens = $4, output_tokens = $5, cache_read_tokens = $6, cache_write_tokens = $7,
          cost = $8, updated = case when conversation is distinct from $3 then now() else updated end
      where id = $1 and staff_member = $2 and conversation = $9
    SQL

    STORED = 'select conversation from sessions where id = $1'

    LOAD = <<~SQL
      select conversation, staff_member, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost
      from sessions
      where id = $1
    SQL

    LIST = <<~SQL
      select id, staff_member, title, cost, updated
      from sessions
      order by updated desc
      limit $1
    SQL

    private_constant :CREATE, :UPDATE, :STORED, :LOAD, :LIST

    def initialize(database:)
      @database = database
      freeze
    end

    # Saves the session as it is now, with its totals, which replace the stored ones. A save never loses messages:
    # when the stored conversation is no longer +previous+, the session is saved only if the stored one is an earlier
    # state of it, as when a save landed but its answer was lost. The database stamps the time, moving it only when
    # the conversation changed.
    #
    # @param conversation [Sleepyshark::Officina::Conversation]
    # @param staff_member [String] who started the session
    # @param usage [Sleepyshark::Officina::Usage] what all its replies used
    # @param cost [BigDecimal] what they cost, in US dollars
    # @param previous [String, nil] the text this console last saved or loaded; nil for a new session
    # @return [String] the text the conversation is now stored as
    # @raise [SessionChangedError] when another console changed it, or the id is another session's
    # @raise [PG::Error] when the database cannot be reached
    def save(conversation, staff_member:, usage:, cost:, previous:)
      saved = conversation.to_json
      values = [conversation.id, staff_member, saved, usage.input, usage.output, usage.cache_read, usage.cache_write,
                cost]
      written = @database.with do |connection|
        write(connection, values, previous).positive? || write_over_earlier(connection, conversation, values).positive?
      end
      return saved if written

      raise SessionChangedError,
            "Session #{conversation.id} changed elsewhere since it was last saved here, so it was not overwritten."
    end

    # The stored session with the id, nil when there is none.
    #
    # @return [StoredSession, nil]
    # @raise [Sleepyshark::Officina::Error] when its conversation cannot be read as one
    # @raise [PG::Error] when the database cannot be reached
    def load(id)
      row = @database.with { |connection| connection.exec_params(LOAD, [id]).first }
      row && stored(row)
    end

    # The sessions changed last, the latest first.
    #
    # @param count [Integer] how many at most
    # @return [Array<SessionListing>]
    # @raise [PG::Error] when the database cannot be reached
    def list(count)
      @database.with do |connection|
        connection.exec_params(LIST, [count]).map do |row|
          SessionListing.new(id: row[:id], staff_member: row[:staff_member], title: row[:title], cost: row[:cost],
                             updated: row[:updated])
        end
      end
    end

    private

    # Writes the values as a new row, with no +expected+, or over the row holding the +expected+ conversation; returns
    # how many rows it wrote, 1 or 0.
    def write(connection, values, expected)
      return connection.exec_params(CREATE, values).cmd_tuples unless expected

      connection.exec_params(UPDATE, [*values, expected]).cmd_tuples
    end

    # Writes the values over the stored row if its conversation holds the first messages of this one, and no others;
    # returns how many rows it wrote, 1 or 0.
    def write_over_earlier(connection, conversation, values)
      stored = connection.exec_params(STORED, [conversation.id]).first&.fetch(:conversation)
      return 0 unless stored && earlier?(stored, conversation)

      write(connection, values, stored)
    end

    def earlier?(stored, conversation)
      before = Sleepyshark::Officina::Conversation.from_json(stored).messages
      conversation.messages.first(before.size) == before
    rescue Sleepyshark::Officina::Error
      false
    end

    def stored(row)
      usage = Sleepyshark::Officina::Usage.new(input: row[:input_tokens], output: row[:output_tokens],
                                               cache_read: row[:cache_read_tokens],
                                               cache_write: row[:cache_write_tokens])
      StoredSession.new(conversation: Sleepyshark::Officina::Conversation.from_json(row[:conversation]),
                        staff_member: row[:staff_member], usage:, cost: row[:cost], saved: row[:conversation])
    end
  end
  private_constant :SessionStore
end
