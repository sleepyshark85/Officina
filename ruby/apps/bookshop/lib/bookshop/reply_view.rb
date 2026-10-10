# frozen_string_literal: true

module Bookshop
  # Shows one reply on the terminal as its run's events come: the text as it streams, each tool call with its input and
  # outcome, a note when the reply starts again, one when the provider compacts the conversation or clears old tool
  # results, and one when the session could not be saved. It asks the staff member about each call that needs
  # approval, and ends with how the reply ended when that is not its text, and a status line of the reply's tokens and
  # cost and the session's cost.
  class ReplyView
    Officina = Sleepyshark::Officina
    DECLINED = Officina::Approval.new(approved: false, reason: 'the staff member declined')
    private_constant :Officina, :DECLINED

    # @param cancel [Sleepyshark::Officina::Cancellation] the reply's: once cancelled, an approval prompt stops waiting
    # @param budget_reached [String] which budget a budget stop reached, and what to do
    def initialize(terminal:, approvals:, cancel:, budget_reached:)
      @terminal = terminal
      @approvals = approvals
      @cancel = cancel
      @budget_reached = budget_reached
      @labelled = false
      @compacted = false
    end

    # @param event [Sleepyshark::Officina::run_event, SessionNotSaved]
    def show(event)
      case event
      in Officina::TextDelta(text:) then stream(text)
      in Officina::ToolCallStarted(call:) then @terminal.write_line("  > #{call.name} #{call.input}")
      in Officina::ApprovalAsked(call:) then ask(call)
      in Officina::ToolCallFinished(call:, result:) then @terminal.write_line("  < #{call.name}: #{outcome(result)}")
      else note(event)
      end
    end

    # Ends the reply with how it ended, unless with its text, already shown, and its status line.
    #
    # @param result [Sleepyshark::Officina::Completed, Sleepyshark::Officina::Stopped, Sleepyshark::Officina::Failed]
    # @param session_cost [BigDecimal] what the session has cost, this reply included, in US dollars
    def finish(result, session_cost:)
      ending = ending(result)
      @terminal.write_line(ending) if ending
      @terminal.write_line("[#{Spent.tokens(result.usage)} · reply $#{Spent.dollars(result.cost)} · " \
                           "session $#{Spent.dollars(session_cost)}]")
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

    # A note on what happened to the reply or its conversation besides the model's text and tool calls; the events
    # that show nothing come here too.
    def note(event)
      case event
      in Officina::Retried then @terminal.write_line('[The reply was interrupted and starts again.]')
      in Officina::ConversationCompacted then compacted(event)
      in Officina::ToolResultsCleared then cleared(event)
      in SessionNotSaved(message:) then @terminal.write_line(message)
      else nil # usage and appends show nothing yet
      end
    end

    # Says how much the compaction summarized, and remembers it: a reply without text after one may be its doing.
    def compacted(event)
      @compacted = true
      @terminal.write_line("  ~ Conversation compacted: #{Spent.thousands(event.tokens)} tokens summarized into " \
                           "#{Spent.thousands(event.summary_tokens)}.")
    end

    def cleared(event)
      @terminal.write_line("  ~ Old tool results cleared: #{event.tool_calls} tool calls, " \
                           "#{Spent.thousands(event.tokens)} tokens.")
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
      in Officina::Completed(text:) then without_text if text.strip.empty?
      in Officina::Stopped(reason:) then stopped(reason)
      in Officina::Failed(detail:) then "[Failed: #{detail}]"
      end
    end

    def without_text
      if @compacted
        '[The conversation was compacted and the reply has no text. Please ask again.]'
      else
        '[The reply has no text. Please ask again.]'
      end
    end

    def stopped(reason)
      case reason
      when :cancelled then '[Cancelled.]'
      when :budget then "[Stopped: #{@budget_reached}]"
      else "[Stopped: #{reason.to_s.tr('_', ' ')}.]"
      end
    end
  end
  private_constant :ReplyView
end
