# frozen_string_literal: true

module Bookshop
  # The session summarizer: a stateless agent with typed output and no tools, which reads a session's transcript as
  # one user message and writes its title, summary and changes. Its instructions are .NET's and Go's word for word.
  class Summarizer
    Officina = Sleepyshark::Officina

    INSTRUCTIONS = <<~TEXT.chomp
      You summarize a session between a member of staff of a small bookshop and the Bookshop Assistant, a chatbot
      that looks up and changes the shop's catalogue, stock, customers and orders. The user message is the session's
      transcript: the staff member's messages, the assistant's replies, and each tool call with its outcome.

      Write a title, a summary and the list of changes made. Count as changes only write tool calls that succeeded
      (adding a customer, placing or cancelling an order, restocking); a declined or failed call changed nothing.
      Name customers, books and orders with their ids. Write in British English, plainly.
    TEXT

    # The most one summary may spend, in US dollars. It limits output, not input: a long session's summary may cost
    # more.
    BUDGET = Officina::Budget.new(cost: BigDecimal('0.05'))

    private_constant :Officina, :INSTRUCTIONS, :BUDGET

    # Claude Opus 5.5 at low effort, as a short, typed answer needs little. The API key comes from ANTHROPIC_API_KEY,
    # where the SDK finds it.
    def self.claude
      Officina::Claude::Model.new(name: 'claude-opus-5-5', effort: :low, max_output_tokens: 4_000)
    end

    # @param model [Sleepyshark::Officina::_Model] one with a price, as each summary has a cost budget
    # @param clock [#call] returns the current Time
    # @param telemetry [Telemetry] which traces and logs each summary
    def initialize(model:, clock:, telemetry:)
      @agent = Officina::Agent.new(name: 'summarizer', model:, instructions: INSTRUCTIONS, output: SessionSummary,
                                   clock:, telemetry: telemetry.officina)
      @telemetry = telemetry
      freeze
    end

    # Summarizes the conversation in a run of its own, on a new conversation dropped afterwards.
    #
    # @param conversation [Sleepyshark::Officina::Conversation] the session's, which the run leaves as it is
    # @return [Sleepyshark::Officina::Completed, Sleepyshark::Officina::Stopped, Sleepyshark::Officina::Failed] a
    #   completed run's output is a SessionSummary
    # @raise [Sleepyshark::Officina::Error] when the conversation holds nothing to summarize
    def summarize(conversation)
      @telemetry.summary(conversation.id) do
        @agent.run(Officina::Conversation.new, Transcript.of(conversation), budget: BUDGET)
      end
    end
  end
  private_constant :Summarizer
end
