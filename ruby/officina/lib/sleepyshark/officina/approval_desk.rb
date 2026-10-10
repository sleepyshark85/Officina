# frozen_string_literal: true

module Sleepyshark
  module Officina
    # Asks the agent's approver about one reply's calls, one at a time, reporting each question and answer.
    class ApprovalDesk
      def initialize(approver:, cancel:, report:)
        @approver = approver
        @cancel = cancel
        @report = report
      end

      # Why the call may not run, said for the model, or nil when the approver approved it.
      # @param call [ToolCall] its input redacted, as the approver and the host see it
      # @param step [ToolCallTrace] the call's, which records the wait and the answer
      def denial(tool, call, step)
        approver = @approver
        return 'The call needs approval, and this run is unattended, so it was denied.' unless approver

        @report.call(:approval_asked, ApprovalAsked.new(call:), span: step.span)
        step.asked
        approval = ask(approver, tool, call)
        step.answered(approval.approved?, cancelled: @cancel.cancelled?)
        @report.call(:approval_answered, ApprovalAnswered.new(call:, approved: approval.approved?),
                     span: step.span, outcome: approval.approved? ? 'approved' : 'denied', detail: approval.reason)
        reason(approval)
      end

      private

      # The approver's answer. One that raises denies the call: the approver is the host's, and the host cannot
      # rescue on the pipeline's thread.
      def ask(approver, tool, call)
        approver.approve(tool, call, cancel: @cancel)
      rescue StandardError => e
        Approval.new(approved: false, reason: "asking for approval failed: #{e}")
      end

      def reason(approval)
        return if approval.approved?
        return 'The call was cancelled while waiting for approval.' if @cancel.cancelled?

        approval.reason ? "The call was denied: #{approval.reason}" : 'The call was denied.'
      end
    end
    private_constant :ApprovalDesk
  end
end
