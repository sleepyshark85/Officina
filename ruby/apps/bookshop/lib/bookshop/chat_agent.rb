# frozen_string_literal: true

module Bookshop
  # What the chat agent is told, and on which model. Its instructions are frozen, .NET's and Go's word for word, so a
  # session one implementation saved resumes in another; who and when come as run context.
  module ChatAgent
    INSTRUCTIONS = <<~TEXT.chomp
      You are Bookshop Assistant, working alongside the staff of a small independent bookshop. Staff ask you, in
      plain language, about the catalogue, the stock, customers and their orders, and ask you to make changes for
      them. A message from the operator tells you today's date and which staff member you are talking to.

      How to work:
      - Use the tools for every fact about books, stock, customers and orders. Never guess an id, a price or a stock
        level; look it up. When a name could match several customers, ask which one is meant.
      - Requests often take several steps. Do the lookups first (they may run together), then the change. Before a
        change, say in one short sentence what you are about to do.
      - Changes (adding a customer, placing or cancelling an order, restocking) need the staff member's approval,
        which they give in their own interface. If they decline, accept it and offer an alternative.
      - When a tool returns an error, read it. Business rule failures, such as too few copies in stock, mean nothing
        changed: explain and offer a way forward, such as fewer copies or another book. If the database cannot be
        reached, say so plainly and suggest trying again shortly; do not pretend the change was made.
      - Prices are in pounds sterling. Give totals to the penny.
      - Asked to export a report, such as a customer's order history, look up the data, then write it as a CSV file
        with a header row into the exports folder, /projects/exports, named for its content (for example
        order-history-alice-martin.csv). Writing a file needs the staff member's approval. Tell them the file's name.
      - Your memory belongs to the staff member you are talking to. Keep their preferences and standing notes there,
        such as how they like prices shown, and follow them.

      How to answer:
      - Be brief and concrete: a few sentences, or a short list when there are several items. Name books by title and
        id, customers by name and id, orders by id.
      - Do not show raw JSON or tool names to the staff member.
    TEXT

    # The run context: today's date and who is at the counter, such as "Today is Saturday 10 October 2026. The staff
    # member using the assistant is Sam."
    #
    # @param now [Time] in the shop's time zone
    def self.context(now, staff_member)
      "Today is #{now.strftime('%A %-d %B %Y')}. The staff member using the assistant is #{staff_member}."
    end

    # How the provider shortens a long conversation, .NET's and Go's settings, so a session one implementation saved
    # resumes in another: compaction at Claude's default threshold, and old tool results cleared only when that frees
    # about two broad searches' worth, as each clearing rewrites the cached tail.
    LONG_CONVERSATIONS = Sleepyshark::Officina::ContextManagement.new(
      compact_at: 150_000,
      clear_tool_results: Sleepyshark::Officina::ToolResultClearing.new(after: 20, keep: 5, at_least_tokens: 20_000)
    )

    # Demo mode's: compaction at Claude's minimum, which the demo's four 10–15k-token searches reach, and clearing
    # above 12 tool calls (the provider clears above the threshold, not at it). Clearing counts every tool call, so a
    # lower threshold clears the searches before they can compact; keeping the 10 latest results means a turn of 8
    # lookups never loses what it just fetched.
    DEMO = Sleepyshark::Officina::ContextManagement.new(
      compact_at: 50_000, clear_tool_results: Sleepyshark::Officina::ToolResultClearing.new(after: 12, keep: 10)
    )

    # What the console says at the start of demo mode.
    DEMO_ANNOUNCEMENT = 'Demo mode: compaction from 50,000 input tokens, and old tool results cleared above 12 ' \
                        'tool calls.'

    # @param demo [Boolean] whether compaction and clearing come early enough to see in a short session
    # @return [Sleepyshark::Officina::ContextManagement]
    def self.context_management(demo:) = demo ? DEMO : LONG_CONVERSATIONS

    # Claude Opus 5.5 at medium effort. Staff reply minutes apart, and the instructions and tools serve every session,
    # so its caches last an hour; a demo is one sitting, and its large searches would cost 60% more to cache for an
    # hour, so in demo mode they last five minutes. The API key comes from ANTHROPIC_API_KEY, where the SDK finds it.
    #
    # @param demo [Boolean]
    def self.claude(demo:)
      cache = demo ? '5m' : '1h'
      Sleepyshark::Officina::Claude::Model.new(name: 'claude-opus-5-5', effort: :medium, max_output_tokens: 16_000,
                                               prefix_cache: cache, conversation_cache: cache)
    end
  end
  private_constant :ChatAgent
end
