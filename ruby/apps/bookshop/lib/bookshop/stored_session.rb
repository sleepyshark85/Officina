# frozen_string_literal: true

module Bookshop
  # A session as the session store holds it: its conversation, the staff member who started it, what its replies
  # used and cost in US dollars, and the text its conversation is stored as, nil until it is first saved.
  StoredSession = Data.define(:conversation, :staff_member, :usage, :cost, :saved)
  private_constant :StoredSession
end
