# frozen_string_literal: true

module Bookshop
  # What the summarizer writes about a session, its typed output. The descriptions, which the model reads in the
  # schema, are .NET's and Go's. Steep cannot see the members a declaration defines, which the signature names: the
  # assertion after the block says the class is that one.
  # rubocop:disable-next Layout/LeadingCommentSpace -- an RBS type assertion is written `#:`, as Steep reads it
  SessionSummary = Sleepyshark::Officina::Input.define do
    string :title, 'A title of a few words, naming the customers, books or orders the session was about.'
    string :summary, 'One to three sentences on what the staff member asked and what came of it.'
    array :changes, "Each change made to the shop's data, such as a customer added, an order placed or cancelled, " \
                    'or a restock, with its ids. Empty when nothing changed.', of: :string
  end #: singleton(SessionSummary)
  private_constant :SessionSummary
end
