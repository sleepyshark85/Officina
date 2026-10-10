# frozen_string_literal: true

module Bookshop
  # One staff member's conversation with the chat agent. Each reply runs the agent on it, with the run context when
  # the conversation has none yet or it has changed since (a new day): the context enters the conversation only with
  # a reply that answers it, so one a cancelled reply never sent is sent again.
  class Session
    Appended = Sleepyshark::Officina::ConversationAppended
    private_constant :Appended

    # @param clock [#call] returns the current Time, in the shop's time zone
    def initialize(agent:, staff_member:, clock:)
      @agent = agent
      @staff_member = staff_member
      @clock = clock
      @conversation = Sleepyshark::Officina::Conversation.new
      @context = nil
    end

    # Runs the chat agent on the message, yielding each event of the run.
    #
    # @param cancel [Sleepyshark::Officina::Cancellation]
    # @return [Sleepyshark::Officina::Completed, Sleepyshark::Officina::Stopped, Sleepyshark::Officina::Failed]
    def reply(message, cancel:)
      context = ChatAgent.context(@clock.call, @staff_member)
      @agent.run(@conversation, message, context: (context unless context == @context), cancel:) do |event|
        @context = context if event in Appended(message: { role: :operator })
        yield event
      end
    end
  end
  private_constant :Session
end
