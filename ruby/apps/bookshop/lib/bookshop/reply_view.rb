# frozen_string_literal: true

module Bookshop
  # Shows one reply on the terminal as its run's events come: the text as it streams, each tool call with its input and
  # outcome, and a note when the reply starts again. It asks the staff member about each call that needs approval, and
  # ends with how the reply ended when that is not its text.
  class ReplyView
    Officina = Sleepyshark::Officina
    DECLINED = Officina::Approval.new(approved: false, reason: 'the staff member declined')
    private_constant :Officina, :DECLINED

    # @param cancel [Sleepyshark::Officina::Cancellation] the reply's: once cancelled, an approval prompt stops waiting
    def initialize(terminal:, approvals:, cancel:)
      @terminal = terminal
      @approvals = approvals
      @cancel = cancel
      @labelled = false
    end

    # @param event [Sleepyshark::Officina::run_event]
    def show(event)
      case event
      in Officina::TextDelta(text:) then stream(text)
      in Officina::Retried then @terminal.write_line('[The reply was interrupted and starts again.]')
      in Officina::ToolCallStarted(call:) then @terminal.write_line("  > #{call.name} #{call.input}")
      in Officina::ApprovalAsked(call:) then ask(call)
      in Officina::ToolCallFinished(call:, result:) then @terminal.write_line("  < #{call.name}: #{outcome(result)}")
      else nil # usage and appends show nothing yet
      end
    end

    # Ends the reply with how it ended, unless with its text, already shown.
    #
    # @param result [Sleepyshark::Officina::Completed, Sleepyshark::Officina::Stopped, Sleepyshark::Officina::Failed]
    def finish(result)
      ending = ending(result)
      ending ? @terminal.write_line(ending) : @terminal.end_line
    end

    private

    # The reply's text, labelled where it starts; text between tool calls follows their lines.
    def stream(text)
      unless @labelled
        @terminal.end_line
        @terminal.write('assistant> ')
        @labelled = true
      end
      @terminal.write(text)
    end

    # Shows the call's exact input and gives the staff member's answer to the pipeline, which waits for it. Ctrl+C at
    # the prompt stops the wait at once, and the line being typed is the next message.
    def ask(call)
      return if @cancel.cancelled? # asked just before Ctrl+C, shown just after: no prompt for a withdrawn call

      @terminal.write_line("  ? #{call.name} needs your approval. Its exact input:")
      @terminal.write_line("    #{call.input}")
      answer = @terminal.read('    Approve? [y/N] ', cancel: @cancel)
      return if @cancel.cancelled?

      approved = %w[y yes].include?(answer.to_s.strip.downcase)
      @approvals.answer(call, approved ? Officina::Approval.new(approved: true) : DECLINED)
    end

    # "ok", or the error's first line: the staff member sees only that, while the model gets the whole error.
    def outcome(result) = result.error? ? "error: #{result.content.lines.first&.chomp}" : 'ok'

    def ending(result)
      case result
      in Officina::Completed(text:) then ('[The reply has no text. Please ask again.]' if text.strip.empty?)
      in Officina::Stopped(reason: :cancelled) then '[Cancelled.]'
      in Officina::Stopped(reason:) then "[Stopped: #{reason.to_s.tr('_', ' ')}.]"
      in Officina::Failed(detail:) then "[Failed: #{detail}]"
      end
    end
  end
  private_constant :ReplyView
end
