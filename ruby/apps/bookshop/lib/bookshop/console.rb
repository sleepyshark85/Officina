# frozen_string_literal: true

module Bookshop
  # The staff member's console: asks who is using it, then reads messages and commands, streams each reply with its
  # tool activity, asks approval for changes, and cancels the reply in progress on Ctrl+C, after which the session
  # goes on; Ctrl+C with no reply in progress leaves.
  class Console
    HELP = <<~TEXT.chomp
      Commands:
        /help          Show this help.
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
    # @param audit [AuditView] what /audit shows
    # @param telemetry [Telemetry] which traces and logs each reply
    def initialize(agent:, approvals:, input:, output:, clock:, audit:, telemetry:)
      @agent = agent
      @audit = audit
      @telemetry = telemetry
      @approvals = approvals
      @clock = clock
      @terminal = Terminal.new(input:, output:)
      # Ending the input ends the session, as its end does.
      @interrupts = Interrupts.new(idle: -> { @terminal.end_input })
    end

    # Runs until /quit or the end of the input.
    def run
      @interrupts.watch do
        @terminal.write_line('Bookshop Assistant. Type /help for commands.')
        staff_member = ask_staff_member
        converse(Session.new(agent: @agent, staff_member:, clock: @clock)) if staff_member
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

    def converse(session)
      while (line = @terminal.read('you> '))
        case line.strip
        when '' then next
        when '/quit' then return
        when %r{\A/} then command(line.strip, session)
        else reply(session, line)
        end
      end
    end

    def command(command, session)
      case command
      when '/help' then @terminal.write_line(HELP)
      when '/audit' then @terminal.write_line(@audit.show(session.id))
      when %r{\A/audit\s+\S+\z} then @terminal.write_line(@audit.show(command.delete_prefix('/audit').strip))
      else @terminal.write_line("Unknown command #{command}. Type /help for commands.")
      end
    end

    def reply(session, message)
      cancel = Sleepyshark::Officina::Cancellation.new
      view = ReplyView.new(terminal: @terminal, approvals: @approvals, cancel:)
      result = @telemetry.reply(session.id) do
        @interrupts.cancelling(cancel) { session.reply(message, cancel:) { |event| view.show(event) } }
      end
      view.finish(result)
    ensure
      # The run has ended, so nothing waits for an answer.
      @approvals.clear
    end
  end
  private_constant :Console
end
