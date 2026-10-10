# frozen_string_literal: true

module Bookshop
  # The staff member's console: asks who is using it, then reads messages and commands, streams each reply with its
  # tool activity, asks approval for changes, and cancels the reply in progress on Ctrl+C, after which the session
  # goes on; Ctrl+C with no reply in progress leaves. Each conversation is a session, saved after every step of a
  # reply; each reply ends with a status line of its tokens and cost, and stops at its own or its session's budget.
  # Each reply is traced and logged; /audit shows a session's audit trail.
  class Console
    HELP = <<~TEXT.chomp
      Commands:
        /help          Show this help.
        /new           Start a new session.
        /sessions      List the latest sessions.
        /resume <id>   Go on with the session with that id.
        /cost          Show this session's tokens and cost.
        /audit [<id>]  Show the audit trail of this session, or of the session with that id.
        /quit          Leave the assistant.
      Anything else is a message to the assistant. Ctrl+C stops a reply in progress.
    TEXT
    private_constant :HELP

    # @param agent [Sleepyshark::Officina::Agent] the chat agent, whose approver is +approvals+
    # @param approvals [Approvals]
    # @param input [IO] the staff member's lines
    # @param output [IO]
    # @param clock [#call] returns the current Time, for the run context
    # @param store [SessionStore] where the sessions are kept
    # @param budgets [Budgets]
    # @param audit [AuditView] what /audit shows
    # @param telemetry [Telemetry] which traces and logs each reply
    # @param summarizer [Summarizer, nil] which summarizes each session left; nil for none
    def initialize(agent:, approvals:, input:, output:, clock:, store:, budgets:, audit:, telemetry:, summarizer:)
      @audit = audit
      @telemetry = telemetry
      @approvals = approvals
      @budgets = budgets
      @terminal = Terminal.new(input:, output:)
      @sessions = SessionCommands.new(agent:, store:, clock:, budgets:, terminal: @terminal, summarizer:)
      # Ending the input ends the session, as its end does.
      @interrupts = Interrupts.new(idle: -> { @terminal.end_input })
    end

    # Runs until /quit or the end of the input, and summarizes each session it leaves: with /new, /resume, /quit or
    # the end of the input.
    def run
      @interrupts.watch do
        @terminal.write_line('Bookshop Assistant. Type /help for commands.')
        staff_member = ask_staff_member
        @sessions.leave(converse(@sessions.start(staff_member))) if staff_member
      end
    ensure
      @terminal.close
    end

    private

    # The staff member's name, asked until it is given; nil at the end of the input.
    def ask_staff_member
      while (name = @terminal.read('Who is using the assistant? Your name: '))
        next if name.strip.empty?

        @terminal.write_line("Hello, #{name.strip}.")
        return name.strip
      end
    end

    # Converses until /quit or the end of the input, and returns the session in use then.
    def converse(session)
      while (line = @terminal.read('you> '))
        case line.strip
        when '' then next
        when '/quit' then return session
        when %r{\A/} then session = command(line.strip, session)
        else reply(session, line)
        end
      end
      session
    end

    # Answers the command and returns the session to go on with.
    def command(command, session)
      case command
      when '/new' then return @sessions.start_new(session)
      when %r{\A/resume(\s|\z)} then return @sessions.resume(argument(command), session)
      when '/sessions' then @sessions.list(session)
      when '/cost' then @sessions.cost(session)
      when '/help' then @terminal.write_line(HELP)
      when %r{\A/audit(\s|\z)} then @terminal.write_line(@audit.show(audited(command, session)))
      else @terminal.write_line("Unknown command #{command}. Type /help for commands.")
      end
      session
    end

    # The id of the session an /audit command names, the session in use's if it names none.
    def audited(command, session) = argument(command).then { it.empty? ? session.id : it }

    # What follows the command's name, trimmed.
    def argument(command) = command.split(/\s+/, 2)[1].to_s.strip

    def reply(session, message)
      cancel = Sleepyshark::Officina::Cancellation.new
      view = ReplyView.new(terminal: @terminal, approvals: @approvals, cancel:,
                           budget_reached: @budgets.reached(session.cost))
      budget = @budgets.for_reply(session.cost)
      result = @telemetry.reply(session.id) do
        @interrupts.cancelling(cancel) { session.reply(message, cancel:, budget:) { |event| view.show(event) } }
      end
      view.finish(result, session_cost: session.cost)
    ensure
      # The run has ended, so nothing waits for an answer.
      @approvals.clear
    end
  end
  private_constant :Console
end
