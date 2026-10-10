# frozen_string_literal: true

module Bookshop
  # The console's session commands, /new, /sessions, /resume <id> and /cost, with the texts .NET and Go show. The
  # commands that change the session in use return the one to go on with, and summarize the one they leave.
  class SessionCommands
    # How many sessions /sessions lists.
    LISTED = 20
    private_constant :LISTED

    # @param store [SessionStore]
    # @param clock [#call] returns the current Time, for the sessions' run context
    # @param budgets [Budgets] which /cost shows
    # @param terminal [Terminal] where the commands answer
    # @param summarizer [Summarizer, nil] which summarizes the sessions left; nil for none
    # @param telemetry [Telemetry] which traces and logs each summary
    def initialize(agent:, store:, clock:, budgets:, terminal:, summarizer:, telemetry:)
      @agent = agent
      @store = store
      @clock = clock
      @budgets = budgets
      @terminal = terminal
      @summaries = Summaries.new(summarizer:, store:, terminal:, telemetry:)
      freeze
    end

    # A new session of the staff member, announced as "Session <id>."; saved with its first reply. Its id, 12 random
    # hex digits as .NET makes them, is short enough to type in /resume.
    #
    # @return [Session]
    def start(staff_member, announced: 'Session')
      conversation = Sleepyshark::Officina::Conversation.new(id: SecureRandom.hex(6))
      stored = StoredSession.new(conversation:, staff_member:, usage: Sleepyshark::Officina::Usage.new,
                                 cost: BigDecimal(0), saved: nil)
      @terminal.write_line("#{announced} #{conversation.id}.")
      Session.new(agent: @agent, store: @store, clock: @clock, staff_member:, stored:)
    end

    # Leaves the session in use, and starts a new one of its staff member.
    #
    # @return [Session]
    def start_new(left)
      leave(left)
      start(left.staff_member, announced: 'New session')
    end

    # Summarizes the session as it is left, unless nothing was said in it since this console took it up.
    def leave(session) = @summaries.leave(session)

    # Shows the sessions changed last, the latest first, marking the one in use, after summarizing a few of those
    # left without a summary.
    def list(current)
      listed = @store.list(LISTED)
      return @terminal.write_line('No sessions yet.') if listed.empty?

      listed = @summaries.summarize_left(listed, current)
      @terminal.write_line('Sessions, most recent first:')
      listed.each { show(it, current) }
    rescue PG::Error => e
      @terminal.write_line("The sessions could not be read: #{e.message.strip}")
    end

    # The stored session with the id, to go on with; or +current+ when there is none, it cannot be read, or another
    # version of the assistant started it, whose next reply would fail on its prefix. Says which. The staff member at
    # the counter stays: the run context names them.
    #
    # @return [Session]
    def resume(id, current)
      return refuse('Which session? Type /resume <id>; /sessions lists them.', current) if id.empty?

      stored = @store.load(id)
      return resumed(stored, current) if stored && continues?(stored)

      refuse(refusal(id, stored), current)
    rescue PG::Error, Sleepyshark::Officina::Error => e
      refuse("The session could not be read: #{e.message.strip}", current)
    end

    # Shows what the session used and cost, against its budget.
    def cost(session)
      spent = "#{Spent.tokens(session.usage)}; cost $#{Spent.dollars(session.cost)}"
      @terminal.write_line("Session #{session.id}: #{spent} of its $#{Spent.budget(@budgets.session)} budget.")
    end

    private

    def show(listing, current)
      @terminal.write_line(line(listing, current))
      @terminal.write_line("    #{listing.summary}") if listing.summary
      @terminal.write_line("    Changes: #{listing.changes.join('; ')}") unless listing.changes.empty?
    end

    def line(listing, current)
      mark = listing.id == current.id ? '*' : ' '
      updated = listing.updated.localtime.strftime('%a %-d %b %H:%M')
      "#{mark} #{listing.id}  #{updated}  #{listing.staff_member}  #{listing.title || '(no title yet)'}  " \
        "$#{Spent.dollars(listing.cost)}"
    end

    # A conversation no agent has appended to yet, or this agent's.
    def continues?(stored) = [nil, @agent.fingerprint].include?(stored.conversation.fingerprint)

    # The stored session, told as resumed, leaving the one in use; resuming the one in use reloads it without
    # leaving it.
    def resumed(stored, current)
      again = stored.conversation.id == current.id
      session = Session.new(agent: @agent, store: @store, clock: @clock, staff_member: current.staff_member, stored:,
                            changed: again && current.changed?)
      @terminal.write_line("Resumed session #{session.id}: #{session.length} messages, " \
                           "$#{Spent.dollars(session.cost)} so far.")
      leave(current) unless again
      session
    end

    # Why the session with the id, stored as +stored+ or not at all, cannot go on.
    def refusal(id, stored)
      return "There is no session #{id}. Type /sessions to list them." unless stored

      "Session #{id} was started with another version of the assistant, so it cannot go on. " \
        'Type /new to start a new session.'
    end

    # Says why the session in use goes on, and returns it.
    def refuse(reason, current)
      @terminal.write_line(reason)
      current
    end
  end
  private_constant :SessionCommands
end
