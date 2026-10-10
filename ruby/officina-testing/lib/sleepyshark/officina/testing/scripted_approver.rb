# frozen_string_literal: true

module Sleepyshark
  module Officina
    module Testing
      # An approver that gives answers scripted in advance, in order, and records each call it was asked about; the
      # human in a test.
      #
      # @example
      #   approver = ScriptedApprover.new(true, 'Not today.')
      class ScriptedApprover
        # @param answers [Array<Boolean, String>] each +true+ to approve, +false+ to deny, or a denial's reason
        def initialize(*answers)
          @answers = answers.map do |answer|
            answer.is_a?(String) ? Approval.new(approved: false, reason: answer) : Approval.new(approved: answer)
          end
          @asked = []
          @lock = Mutex.new
        end

        # @return [Array<ToolCall>] the calls asked about so far, in order
        def asked = @lock.synchronize { @asked.dup.freeze }

        # Records the call and gives the next answer.
        # @raise [Error] when no answer is left, which denies the call
        def approve(_tool, call, **)
          @lock.synchronize do
            @asked << call
            raise Error, "Scripted approver: no answer left for call #{@asked.size}" if @answers.empty?

            @answers.shift
          end
        end
      end
    end
  end
end
