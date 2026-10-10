# frozen_string_literal: true

module Bookshop
  # Yielded among a reply's events, once a reply, when the session could not be saved: why, in a line for the staff
  # member. The reply goes on, and the next save stores everything.
  SessionNotSaved = Data.define(:message)
  private_constant :SessionNotSaved
end
