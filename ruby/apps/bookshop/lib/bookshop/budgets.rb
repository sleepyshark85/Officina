# frozen_string_literal: true

module Bookshop
  # What the replies of a session may cost, in US dollars: each reply, and the session over all its replies. A reply
  # runs within the lower of its own budget and what is left of the session's.
  Budgets = Data.define(:reply, :session)

  # Each reply's and the session's budget.
  class Budgets
    # Reads BOOKSHOP_REPLY_BUDGET, a reply's budget in US dollars, such as 0.01 to show a budget stop; $0.50 if not
    # set or empty, as Go reads it. A session may spend $5.
    #
    # @param env [#fetch]
    # @raise [SettingError] when the setting is not an amount above zero
    def self.from(env)
      setting = env.fetch('BOOKSHOP_REPLY_BUDGET', '')
      reply = setting.empty? ? BigDecimal('0.50') : BigDecimal(setting, exception: false)
      unless reply&.positive?
        raise SettingError, "BOOKSHOP_REPLY_BUDGET #{setting.inspect} is not an amount of US dollars above zero"
      end

      new(reply:, session: BigDecimal(5))
    end

    # The budget of a reply in a session that has spent +spent+ so far.
    #
    # @param spent [BigDecimal] US dollars
    # @return [Sleepyshark::Officina::Budget]
    def for_reply(spent) = Sleepyshark::Officina::Budget.new(cost: (session - spent).clamp(BigDecimal(0), reply))

    # Why a budget stopped a reply that started when the session had spent +spent+: the session's budget, when what
    # was left of it was the lower, or the reply's own.
    #
    # @param spent [BigDecimal] US dollars
    def reached(spent)
      if session - spent <= reply
        "this session has reached its budget of $#{Spent.budget(session)}. Type /new to start a new session."
      else
        "this reply has reached its budget of $#{Spent.budget(reply)}."
      end
    end
  end
  private_constant :Budgets
end
