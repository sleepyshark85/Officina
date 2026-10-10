# frozen_string_literal: true

module Bookshop
  # The console's session commands, /new, /sessions, /resume <id> and /cost, with the texts .NET and Go show. The
  # commands that change the session in use return the one to go on with.
  class SessionCommands
    # How many sessions /sessions lists.
    LISTED = 20
    private_constant :LISTED

    # @param store [SessionStore]
    # @param clock [#call] returns the current Time, for the sessions' run context
    # @param budgets [Budgets] which /cost shows
    # @param terminal [Terminal] where the commands answer
    def initialize(agent:, store:, clock:, budgets:, terminal:)
      @agent = agent
      @store = store
      @clock = clock
      @budgets = budgets
      @terminal = terminal
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

    # Shows the sessions changed last, the latest first, marking the one in use.
    def list(current)
      listed = @store.list(LISTED)
      return @terminal.write_line('No sessions yet.') if listed.empty?

      @terminal.write_line('Sessions, most recent first:')
      listed.each { @terminal.write_line(line(it, current)) }
    rescue PG::Error => e
      @terminal.write_line("The sessions could not be read: #{e.message.strip}")
    end

    # The stored session with the id, to go on with; or +current+ when there is none, it cannot be read, or another
    # version of the assistant started it, whose next reply would fail on its prefix. Says which. The staff member at
    # the counter stays: the run context names them.
    #
    # @return [Session]
    def resume(id, current)
      stored = id.empty? ? nil : @store.load(id)
      return resumed(stored, current) if stored && continues?(stored)

      @terminal.write_line(refusal(id, stored))
      current
    rescue PG::Error, Sleepyshark::Officina::Error => e
      @terminal.write_line("The session could not be read: #{e.message.strip}")
      current
    end

    # Shows what the session used and cost, against its budget.
    def cost(session)
      spent = "#{Spent.tokens(session.usage)}; cost $#{Spent.dollars(session.cost)}"
      @terminal.write_line("Session #{session.id}: #{spent} of its $#{Spent.budget(@budgets.session)} budget.")
    end

    private

    def line(listing, current)
      mark = listing.id == current.id ? '*' : ' '
      updated = listing.updated.localtime.strftime('%a %-d %b %H:%M')
      "#{mark} #{listing.id}  #{updated}  #{listing.staff_member}  #{listing.title || '(no title yet)'}  " \
        "$#{Spent.dollars(listing.cost)}"
    end

    # A conversation no agent has appended to yet, or this agent's.
    def continues?(stored) = [nil, @agent.fingerprint].include?(stored.conversation.fingerprint)

    def resumed(stored, current)
      session = Session.new(agent: @agent, store: @store, clock: @clock, staff_member: current.staff_member, stored:)
      @terminal.write_line("Resumed session #{session.id}: #{session.length} messages, " \
                           "$#{Spent.dollars(session.cost)} so far.")
      session
    end

    def refusal(id, stored)
      if id.empty? then 'Which session? Type /resume <id>; /sessions lists them.'
      elsif stored.nil? then "There is no session #{id}. Type /sessions to list them."
      else
        "Session #{id} was started with another version of the assistant, so it cannot go on. " \
          'Type /new to start a new session.'
      end
    end
  end
  private_constant :SessionCommands
end
