# frozen_string_literal: true

module Bookshop
  # The chat agent's approver: hands the staff member's answers, which the console asks for when it shows a call's
  # approval-asked event, to the tool pipeline, which waits for them on its own thread. Each answer names its call, so
  # one left over by a cancelled reply never answers another call.
  class Approvals
    Approval = Sleepyshark::Officina::Approval
    # An answer the console gives about one call.
    Answer = Data.define(:call_id, :approval)
    WITHDRAWN = Approval.new(approved: false, reason: 'the reply was cancelled while waiting for the staff member')
    private_constant :Approval, :Answer, :WITHDRAWN

    def initialize
      # Holds at most a reply's answers and, once it is cancelled, one cancellation per call it waited for: the
      # console clears it after each reply.
      @answers = Thread::Queue.new
      freeze
    end

    # Waits for the console's answer about the call, or until the reply is cancelled, which denies it.
    #
    # @param call [Sleepyshark::Officina::ToolCall]
    # @param cancel [Sleepyshark::Officina::Cancellation]
    # @return [Sleepyshark::Officina::Approval]
    def approve(_tool, call, cancel:)
      cancel.on_cancel { @answers << cancel }
      loop do
        case @answers.pop
        in ^cancel then return WITHDRAWN
        in Answer(call_id: ^(call.id), approval:) then return approval
        else next # left by a reply that was cancelled
        end
      end
    end

    # Gives the staff member's answer to the pipeline waiting about the call.
    #
    # @param call [Sleepyshark::Officina::ToolCall]
    # @param approval [Sleepyshark::Officina::Approval]
    def answer(call, approval)
      @answers << Answer.new(call_id: call.id, approval:)
    end

    # Drops what a reply left: an answer or cancellation nobody waited for. Called once the reply has ended, when
    # nothing waits.
    def clear = @answers.clear
  end
end
