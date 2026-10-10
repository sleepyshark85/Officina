# frozen_string_literal: true

module Bookshop
  # The session in use: a conversation with the chat agent, saved after every step of a reply and at its end with
  # what the session has spent, so a crash loses at most the step in flight. Each reply runs the agent with the run
  # context when the conversation has none yet or it has changed since (a new day): the context enters the
  # conversation only with a reply that answers it, so one a cancelled reply never sent is sent again. The provider
  # clears old tool results again on every later call, as each sends the whole conversation: the session passes a
  # clearing on only when it differs from the last one.
  class Session
    Officina = Sleepyshark::Officina
    private_constant :Officina

    # @param store [SessionStore]
    # @param clock [#call] returns the current Time, in the shop's time zone
    # @param staff_member [String] who is at the counter, whom the run context names
    # @param stored [StoredSession] the session to go on with, or a new one, not saved yet
    def initialize(agent:, store:, clock:, staff_member:, stored:)
      @agent = agent
      @store = store
      @clock = clock
      @staff_member = staff_member
      @owner = stored.staff_member
      @conversation = stored.conversation
      @usage = stored.usage
      @cost = stored.cost
      @saved = stored.saved
      @context = @conversation.messages.rfind { it.role == :operator }&.text
    end

    # @return [String] who is at the counter
    attr_reader :staff_member
    # @return [Sleepyshark::Officina::Usage] what the session's replies used
    attr_reader :usage
    # @return [BigDecimal] what they cost, in US dollars
    attr_reader :cost

    # @return [String] the session's id, its conversation's
    def id = @conversation.id

    # @return [Integer] how many messages the conversation holds
    def length = @conversation.messages.size

    # Runs the chat agent on the message, yielding each event of the run but a clearing that repeats the session's
    # last, and a SessionNotSaved the first time in the reply that a save fails.
    #
    # @param cancel [Sleepyshark::Officina::Cancellation]
    # @param budget [Sleepyshark::Officina::Budget]
    # @return [Sleepyshark::Officina::Completed, Sleepyshark::Officina::Stopped, Sleepyshark::Officina::Failed]
    def reply(message, cancel:, budget:, &)
      context = ChatAgent.context(@clock.call, @staff_member)
      @spent = Officina::Usage.new
      @told = false
      result = @agent.run(@conversation, message, context: (context unless context == @context), cancel:,
                                                  budget:) do |event|
        repeated = repeated_clearing?(event)
        follow(event, context, &)
        yield event unless repeated
      end
      add(result, &)
    end

    private

    # Adds what the reply spent to the session's totals, and saves the session with them.
    def add(result, &)
      @usage += result.usage
      @cost += result.cost
      save(@usage, @cost, &)
      result
    end

    # Whether the event is a clearing of as many tool calls as the session's last.
    def repeated_clearing?(event) = event.is_a?(Officina::ToolResultsCleared) && event.tool_calls == @cleared

    # Keeps up with the run: what the reply has spent so far, so every save stores the session's whole spend, the
    # context, held once its operator message is appended, and how many tool calls the last clearing cleared.
    def follow(event, context, &)
      case event
      in Officina::UsageReported(usage:) then @spent += usage
      in Officina::ToolResultsCleared(tool_calls:) then @cleared = tool_calls
      in Officina::ConversationAppended(message:)
        @context = context if message.role == :operator
        save(@usage + @spent, @cost + priced(@spent), &)
      else nil
      end
    end

    # The cost of the usage at the model's price, an estimate the run's end replaces with the result's cost. The model
    # has a price: every reply has a cost budget, which the run refuses for a model without one.
    def priced(usage)
      # @type var price: Sleepyshark::Officina::Price
      @agent.model.info => { price: Officina::Price => price }
      price.cost(usage)
    end

    # Saves the conversation with the totals given, which replace the stored ones; a save that fails is told once a
    # reply.
    def save(usage, cost, &)
      @saved = @store.save(@conversation, staff_member: @owner, usage:, cost:, previous: @saved)
    rescue SessionChangedError => e
      tell("#{e.message} Type /resume #{id} to go on from what was saved.", &)
    rescue PG::Error => e
      tell(e.message.strip, &)
    end

    def tell(problem)
      return if @told

      @told = true
      yield SessionNotSaved.new(message: "[The session could not be saved: #{problem}]")
    end
  end
  private_constant :Session
end
